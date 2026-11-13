using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Rewind;

/// A saved replay, recording or screenshot in the clips folder.
sealed record ClipItem(string FilePath, DateTime Date)
{
    public enum Kinds { Replay, Recording, Screenshot, App }

    string Name => System.IO.Path.GetFileName(FilePath);
    public string? AppName => Cleanup.AppPrefix(Name);

    public Kinds Kind =>
        Name.StartsWith("Screenshot ") || System.IO.Path.GetExtension(Name).Equals(".png", StringComparison.OrdinalIgnoreCase) ? Kinds.Screenshot
        : Name.StartsWith("Recording ") ? Kinds.Recording
        : Name.StartsWith("Replay ") ? Kinds.Replay
        : Kinds.App;

    public bool IsVideo => Kind != Kinds.Screenshot;

    public string Title => Kind switch
    {
        Kinds.Replay => "Replay",
        Kinds.Recording => "Recording",
        Kinds.Screenshot => AppName is { } a ? $"{a} screenshot" : "Screenshot",
        _ => AppName ?? "Clip",
    };

    public Sym Icon => Kind switch
    {
        Kinds.Replay => Sym.Replay,
        Kinds.Recording => Sym.Record,
        Kinds.Screenshot => Sym.Camera,
        _ => Sym.Gamepad,
    };

    public Color Tint => Kind switch
    {
        Kinds.Replay => Theme.Replay,
        Kinds.Recording => Theme.Record,
        Kinds.Screenshot => Theme.Screenshot,
        _ => Theme.Accent,
    };
}

/// The most recent clips, with thumbnails, lengths and sizes loaded in the background.
static class ClipLibrary
{
    public const int Limit = 5;
    public static List<ClipItem> Items { get; private set; } = new();
    public static readonly Dictionary<string, BitmapSource> Thumbs = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, double> Durations = new(StringComparer.OrdinalIgnoreCase);
    public static readonly Dictionary<string, (int W, int H)> Sizes = new(StringComparer.OrdinalIgnoreCase);
    public static event Action? Changed;
    static readonly Dictionary<string, DateTime> loaded = new(StringComparer.OrdinalIgnoreCase);
    static int generation;

    public static void RefreshIfShown()
    {
        if (OverlayController.IsVisible) Refresh();
    }

    public static void Refresh()
    {
        var dir = Settings.Shared.OutputDirectory;
        var busy = Controller.Shared.Router.Recorder?.Path;
        var prefixes = Settings.Shared.AppPrefixes.ToList();
        var ui = System.Windows.Application.Current.Dispatcher;
        int gen = ++generation;
        // Shell thumbnails want an STA thread.
        var t = new Thread(() =>
        {
            var found = Recent(dir, busy, prefixes);
            ui.BeginInvoke(() => { if (gen == generation) { Items = found; Changed?.Invoke(); } });
            foreach (var item in found)
            {
                if (gen != generation) return;
                lock (loaded) if (loaded.TryGetValue(item.FilePath, out var d) && d == item.Date) continue;
                var (thumb, duration, size) = Load(item);
                ui.BeginInvoke(() =>
                {
                    if (thumb != null) Thumbs[item.FilePath] = thumb;
                    if (duration is { } dur) Durations[item.FilePath] = dur;
                    if (size is { } sz) Sizes[item.FilePath] = sz;
                    lock (loaded) loaded[item.FilePath] = item.Date;
                    Changed?.Invoke();
                });
            }
        }) { IsBackground = true, Name = "Rewind clips" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// For --render-ui: loads a folder's clips right away instead of in the background.
    public static void LoadNow(string dir)
    {
        Items = Recent(dir, null, Settings.Shared.AppPrefixes);
        foreach (var item in Items)
        {
            var (thumb, duration, size) = Load(item);
            if (thumb != null) Thumbs[item.FilePath] = thumb;
            if (duration is { } d) Durations[item.FilePath] = d;
            if (size is { } s) Sizes[item.FilePath] = s;
        }
    }

    public static List<ClipItem> Recent(string dir, string? busy, IEnumerable<string> prefixes)
    {
        if (!Directory.Exists(dir)) return new();
        var list = prefixes.ToList();
        return Directory.EnumerateFiles(dir)
            .Select(p => new FileInfo(p))
            .Where(f => (f.Attributes & FileAttributes.Hidden) == 0 && Cleanup.IsRewindFile(f.Name, list) &&
                        !string.Equals(f.FullName, busy, StringComparison.OrdinalIgnoreCase))
            .Select(f => new ClipItem(f.FullName, f.CreationTime))
            .OrderByDescending(c => c.Date)
            .Take(Limit)
            .ToList();
    }

    static (BitmapSource? thumb, double? duration, (int, int)? size) Load(ClipItem item)
    {
        var thumb = ShellThumbnail(item.FilePath, 360);
        var (duration, size, _, _) = Probe(item);
        return (thumb, duration, size);
    }

    /// Length, pixel size, frame rate, and for trimmed clips how far into the file the clip starts (the frames
    /// before that are only there for the decoder), read from the file's header.
    public static (double? Duration, (int W, int H)? Size, double Fps, double LeadIn) Probe(ClipItem item)
    {
        try
        {
            if (item.IsVideo)
            {
                var v = Mp4Reader.Open(item.FilePath).Video;
                if (v == null) return (null, null, 60, 0);
                double fps = v.MediaDuration > 0 ? Math.Clamp((v.Count - 1) / v.MediaDuration, 1, 360) : 60;
                double leadIn = Math.Max(v.EditMediaTime / (double)v.Timescale - v.DelaySeconds, 0);
                return (v.PresentedDuration, (v.Width, v.Height), fps, leadIn);
            }
            var frame = BitmapDecoder.Create(new Uri(item.FilePath), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return (null, (frame.PixelWidth, frame.PixelHeight), 0, 0);
        }
        catch { return (null, null, 60, 0); }
    }

    public static void Trash(ClipItem item)
    {
        if (Cleanup.Recycle(item.FilePath))
            Toast.Show("Moved to the Recycle Bin", System.IO.Path.GetFileName(item.FilePath), Sym.Trash, Theme.Gray);
        else
            Toast.Show("Couldn't move to the Recycle Bin", System.IO.Path.GetFileName(item.FilePath), Sym.Error, Theme.Red);
        Refresh();
    }

    // MARK: Shell thumbnails (what Explorer shows)

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap); }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeSize { public int Cx, Cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

    static BitmapSource? ShellThumbnail(string path, int size)
    {
        try
        {
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory) < 0) return null;
            try
            {
                if (factory.GetImage(new NativeSize { Cx = size, Cy = size }, 0x1 | 0x8 /* bigger OK, thumbnail only */, out var hbmp) < 0) return null;
                try
                {
                    var img = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    img.Freeze();
                    return img;
                }
                finally { DeleteObject(hbmp); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch { return null; }
    }
}

/// Cuts a time range out of a clip without re-encoding. Decoding has to start on a keyframe, so the new file
/// begins there and an edit list says where the clip really starts. Rewind's preview and most players honor
/// that; Windows' Photos and Media Player apps don't, and play from the keyframe.
static class ClipTrimmer
{
    /// "Replay … (trimmed).mp4", or "(trimmed 2)" etc. if that exists.
    public static string OutputPath(string source)
    {
        var dir = Path.GetDirectoryName(source)!;
        var ext = Path.GetExtension(source) is { Length: > 0 } e ? e : ".mp4";
        var name = Path.GetFileNameWithoutExtension(source);
        var path = Path.Combine(dir, $"{name} (trimmed){ext}");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{name} (trimmed {n}){ext}");
        return path;
    }

    /// `start` and `end` are in seconds from the start of the clip as it's shown (edit lists applied).
    public static Task<double> TrimAsync(string source, double start, double end, string dest) => Task.Run(() => Trim(source, start, end, dest));

    static double Trim(string source, double start, double end, string dest)
    {
        var r = Mp4Reader.Open(source);
        var v = r.Video ?? throw new RewindException("This clip has no video.");
        if (v.Sps == null || v.Pps == null || (v.Codec == VideoCodec.Hevc && v.Vps == null) || v.Count == 0)
            throw new RewindException("This clip's video can't be read.");
        if (end <= start) throw new RewindException("Choose a part to keep first.");

        // k: the keyframe decoding has to start from; f: the frame on screen at `start`; e: one past the last frame kept.
        int k = 0;
        for (int i = 0; i < v.Count && v.Presented(i) <= start + 1e-4; i++) if (v.IsSync(i)) k = i;
        int f = k;
        while (f + 1 < v.Count && v.Presented(f + 1) <= start + 1e-4) f++;
        int e = f + 1;
        while (e < v.Count && v.Presented(e) < end - 1e-4) e++;
        double t0 = v.Presented(k), shownFrom = v.Presented(f);

        int fps = (int)Math.Clamp(Math.Round((v.Count - 1) / Math.Max(v.MediaDuration, 0.001)), 1, 360);
        var cfg = new CodecConfig { Codec = v.Codec, Width = v.Width, Height = v.Height, Fps = fps, Vps = v.Vps, Sps = v.Sps, Pps = v.Pps };
        using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] Read(long off, int size)
        {
            var buf = new byte[size];
            src.Position = off;
            src.ReadExactly(buf);
            return buf;
        }

        using var mp4 = new Mp4Muxer(dest, fastStart: true);
        int vt = mp4.AddVideo(cfg, editMediaTime: (long)Math.Round((shownFrom - t0) * Mp4Muxer.VideoTimescale));
        var samples = new List<(double time, int track, int size, long dts, bool sync, long offset)>();
        for (int i = k; i < e; i++)
            samples.Add((v.Presented(i) - t0, vt, v.Sizes[i], (long)Math.Round((v.Presented(i) - t0) * Mp4Muxer.VideoTimescale), v.IsSync(i), v.Offsets[i]));

        foreach (var a in r.AudioTracks)
        {
            if (a.Count == 0 || a.AudioConfig == null) continue;
            // Audio starts at the packet nearest the keyframe as players that ignore edit lists see them, so those
            // stay in sync too; the edit list then lines both up at the cut for the rest.
            double raw = (v.Dts[k] - v.Dts[0]) / (double)v.Timescale;
            double Raw(int j) => (a.Dts[j] - a.Dts[0]) / (double)a.Timescale;
            int j0 = 0;
            for (int j = 1; j < a.Count; j++)
                if (Math.Abs(Raw(j) - raw) < Math.Abs(Raw(j0) - raw)) j0 = j;
            long lead = (long)Math.Round((shownFrom - a.Presented(j0)) * a.Timescale);
            int at = mp4.AddAudio(a.SampleRate > 0 ? a.SampleRate : Audio.Rate, a.Channels > 0 ? a.Channels : Audio.Channels, Audio.Bitrate,
                                  a.AudioConfig, editMediaTime: Math.Max(lead, 0), delaySamples: Math.Max(-lead, 0));
            double frame = Audio.AacFrame / (double)a.Timescale;
            for (int j = j0; j < a.Count && a.Presented(j) < end + frame; j++)
                samples.Add((Raw(j) - Raw(j0), at, a.Sizes[j], a.Dts[j] - a.Dts[j0], true, a.Offsets[j]));
        }

        foreach (var s in samples.OrderBy(s => s.time))
            mp4.Write(s.track, s.size, s.dts, s.sync, () => Read(s.offset, s.size));
        mp4.Finish();
        return Math.Min(end, v.Presented(e - 1) + 1.0 / fps) - shownFrom;
    }
}

// MARK: - Views

/// Row of the latest clips under the overlay's tiles.
sealed class RecentClipsRow : StackPanel
{
    readonly double width;
    readonly Action<ClipItem> open;
    readonly Grid row = new();

    public RecentClipsRow(double width, Action<ClipItem> open)
    {
        this.width = width;
        this.open = open;
        Margin = new Thickness(0, 0, 0, Theme.S(18));
        var label = Theme.Text("Recent clips", 12, FontWeights.SemiBold, Theme.Muted);
        label.Margin = new Thickness(0, 0, 0, Theme.S(8));
        Children.Add(label);
        Children.Add(row);
        Rebuild();
        Loaded += (_, _) => { ClipLibrary.Changed -= Rebuild; ClipLibrary.Changed += Rebuild; Rebuild(); };
        Unloaded += (_, _) => ClipLibrary.Changed -= Rebuild;
    }

    void Rebuild()
    {
        row.Children.Clear();
        row.ColumnDefinitions.Clear();
        double gap = Theme.S(8), thumbW = (width - gap * (ClipLibrary.Limit - 1)) / ClipLibrary.Limit;
        if (ClipLibrary.Items.Count == 0)
        {
            var empty = Theme.Text("Clips and screenshots you save show up here.", 11.5, brush: Theme.Muted);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(new Border
            {
                Width = width, Height = thumbW * 9 / 16 + Theme.S(20), CornerRadius = new CornerRadius(Theme.S(10)),
                Background = Theme.Brush(Colors.White, 0.04), Child = empty,
            });
            return;
        }
        for (int i = 0; i < ClipLibrary.Limit; i++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(thumbW + (i < ClipLibrary.Limit - 1 ? gap : 0)) });
        for (int i = 0; i < ClipLibrary.Items.Count; i++)
        {
            var thumb = new ClipThumb(ClipLibrary.Items[i], thumbW, open) { HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(thumb, i);
            row.Children.Add(thumb);
        }
    }
}

sealed class ClipThumb : StackPanel
{
    public ClipThumb(ClipItem item, double width, Action<ClipItem> open)
    {
        double height = width * 9 / 16;
        var radius = new CornerRadius(Theme.S(8));
        var picture = new Grid { Width = width, Height = height, ClipToBounds = true };
        picture.Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius.TopLeft, radius.TopLeft);
        picture.Children.Add(new Border { Background = Theme.Brush(Colors.White, 0.06) });
        if (ClipLibrary.Thumbs.TryGetValue(item.FilePath, out var img))
            picture.Children.Add(new Image { Source = img, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        else
            picture.Children.Add(Icons.Make(item.Icon, Theme.S(16), Theme.Muted));
        var hoverShade = new Border { Background = Theme.Brush(Colors.Black, 0.3), Visibility = Visibility.Collapsed };
        hoverShade.Child = Icons.Make(item.IsVideo ? Sym.Play : Sym.Eye, Theme.S(16), Brushes.White);
        picture.Children.Add(hoverShade);
        var badge = new Border
        {
            Width = Theme.S(16), Height = Theme.S(16), CornerRadius = new CornerRadius(Theme.S(8)), Background = Theme.Brush(Colors.Black, 0.55),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(Theme.S(4)),
            Child = Icons.Make(item.Icon, Theme.S(8.5), Theme.Brush(item.Tint)),
        };
        picture.Children.Add(badge);
        if (ClipLibrary.Durations.TryGetValue(item.FilePath, out var dur))
            picture.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(Theme.S(8)), Background = Theme.Brush(Colors.Black, 0.6), Padding = new Thickness(Theme.S(4), Theme.S(1), Theme.S(4), Theme.S(1)),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(Theme.S(4)),
                Child = Theme.Text(Format.Duration(dur), 9, FontWeights.Bold, family: Theme.Mono),
            });
        var frame = new Border { CornerRadius = radius, BorderThickness = new Thickness(1), BorderBrush = Theme.Line, IsHitTestVisible = false };
        var tile = new Grid();
        tile.Children.Add(picture);
        tile.Children.Add(frame);
        Children.Add(tile);
        var when = Theme.Text(TimeAgo(item.Date), 10.5, brush: Theme.Muted);
        when.Margin = new Thickness(0, Theme.S(5), 0, 0);
        Children.Add(when);

        Cursor = Cursors.Hand;
        ToolTip = Path.GetFileName(item.FilePath);
        Background = Brushes.Transparent;
        MouseEnter += (_, _) => { hoverShade.Visibility = Visibility.Visible; frame.BorderBrush = Theme.Brush(item.Tint, 0.8); };
        MouseLeave += (_, _) => { hoverShade.Visibility = Visibility.Collapsed; frame.BorderBrush = Theme.Line; };
        MouseLeftButtonUp += (_, e) => { e.Handled = true; open(item); };
        MouseLeftButtonDown += (_, e) => e.Handled = true;
    }

    public static string TimeAgo(DateTime date)
    {
        var s = (DateTime.Now - date).TotalSeconds;
        if (s < 60) return "Just now";
        if (s < 3600) return $"{(int)(s / 60)}m ago";
        if (s < 86400) return $"{(int)(s / 3600)}h ago";
        return $"{(int)(s / 86400)}d ago";
    }
}

/// The overlay's preview mode: video player or screenshot, with Back, Trim, Show in Explorer and Recycle Bin.
sealed class ClipPreviewPane : StackPanel
{
    public ClipItem Item { get; }
    public bool IsTrimming { get; private set; }
    readonly Action back, closeOverlay;
    readonly Image? picture;
    readonly TimelineBar? timeline;
    readonly TextBlock? clock, hint;
    readonly Chip? trimChip;
    readonly StackPanel? trimButtons;
    readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(50) };
    CircleButton? playButton;
    ClipPlayer? player;
    bool playing, saving, stopped;
    double fps = 60, leadIn, mediaW, mediaH;

    public ClipPreviewPane(ClipItem item, Size screen, Action back, Action closeOverlay)
    {
        Item = item;
        this.back = back;
        this.closeOverlay = closeOverlay;
        var (duration, size, probedFps, probedLeadIn) = ClipLibrary.Probe(item);
        if (duration is { } d0) ClipLibrary.Durations[item.FilePath] = d0;
        if (size is { } s0) ClipLibrary.Sizes[item.FilePath] = s0;
        if (probedFps > 0) fps = probedFps;
        leadIn = probedLeadIn;
        var (w, h) = MediaSize(screen);

        // Header
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Icons.Make(item.Icon, Theme.S(15), Theme.Brush(item.Tint));
        icon.Margin = new Thickness(0, 0, Theme.S(7), 0);
        titleRow.Children.Add(icon);
        titleRow.Children.Add(Theme.Text(item.Title, 16, FontWeights.SemiBold));
        var details = Theme.Text(Details(), 11.5, brush: Theme.Muted);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Theme.S(12), 0, Theme.S(16), 0) };
        titles.Children.Add(titleRow);
        titles.Children.Add(details);
        var backButton = new CircleButton(Sym.Back, back) { ToolTip = "Back to the overlay" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (item.IsVideo)
        {
            trimChip = new Chip("Trim", Sym.Scissors, BeginTrim) { Margin = new Thickness(0, 0, Theme.S(8), 0) };
            actions.Children.Add(trimChip);
        }
        actions.Children.Add(new Chip("Show in Explorer", Sym.Folder, () => { var p = item.FilePath; closeOverlay(); Controller.Reveal(p); }) { Margin = new Thickness(0, 0, Theme.S(8), 0) });
        actions.Children.Add(new Chip("Move to Recycle Bin", Sym.Trash, () => { Stop(); back(); ClipLibrary.Trash(item); }) { Margin = new Thickness(0, 0, Theme.S(12), 0) });
        actions.Children.Add(new CircleButton(Sym.Close, closeOverlay) { ToolTip = "Close (Esc)" });
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, Theme.S(14)) };
        DockPanel.SetDock(backButton, Dock.Left);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(backButton);
        header.Children.Add(actions);
        header.Children.Add(titles);
        Children.Add(header);

        // Media
        var stage = new Grid { Width = w, Height = h, Background = Brushes.Black };
        stage.Clip = new RectangleGeometry(new Rect(0, 0, w, h), Theme.S(14), Theme.S(14));
        if (item.IsVideo)
        {
            mediaW = w;
            mediaH = h;
            picture = new Image { Stretch = Stretch.Uniform };
            stage.Children.Add(picture);
            stage.MouseLeftButtonUp += (_, e) => { e.Handled = true; TogglePlay(); };
            stage.MouseLeftButtonDown += (_, e) => e.Handled = true;
            Loaded += (_, _) => StartPlayer();
        }
        else
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(item.FilePath);
                bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked (it may be moved to the bin)
                bmp.DecodePixelWidth = (int)Math.Min(w * 2, 3840);
                bmp.EndInit();
                bmp.Freeze();
                stage.Children.Add(new Image { Source = bmp, Stretch = Stretch.Uniform });
            }
            catch { }
        }
        Children.Add(new Grid { Children = { stage, new Border { Width = w, Height = h, CornerRadius = new CornerRadius(Theme.S(14)), BorderThickness = new Thickness(1), BorderBrush = Theme.Line, IsHitTestVisible = false } } });

        if (!item.IsVideo) return;

        // Transport: play/pause, frame steps, timeline, time, and the trim controls.
        timeline = new TimelineBar { Height = Theme.S(28), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Theme.S(10), 0, Theme.S(10), 0) };
        timeline.Seek += t => { Pause(); if (player != null) player.Position = t; UpdateClock(); };
        playButton = new CircleButton(Sym.Play, TogglePlay) { ToolTip = "Play or pause (Space)" };
        var stepBack = new CircleButton(Sym.StepBack, () => Step(-1)) { ToolTip = "Back a frame (←)", Margin = new Thickness(Theme.S(6), 0, 0, 0) };
        var stepFwd = new CircleButton(Sym.StepForward, () => Step(1)) { ToolTip = "Forward a frame (→)", Margin = new Thickness(Theme.S(6), 0, 0, 0) };
        clock = Theme.Text("0:00", 11, FontWeights.SemiBold, Theme.Muted, Theme.Mono);
        clock.VerticalAlignment = VerticalAlignment.Center;
        trimButtons = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(Theme.S(10), 0, 0, 0) };
        trimButtons.Children.Add(new Chip("Trim", Sym.Scissors, ConfirmTrim) { Margin = new Thickness(0, 0, Theme.S(6), 0) });
        trimButtons.Children.Add(new Chip("Cancel", Sym.Close, CancelTrim));
        var transport = new DockPanel { Margin = new Thickness(0, Theme.S(12), 0, 0), Width = w };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(playButton); left.Children.Add(stepBack); left.Children.Add(stepFwd);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(clock); right.Children.Add(trimButtons);
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        transport.Children.Add(left);
        transport.Children.Add(right);
        transport.Children.Add(timeline);
        Children.Add(transport);
        hint = Theme.Text("Space to play or pause · ← → to step a frame", 11, brush: Theme.Muted);
        hint.Margin = new Thickness(0, Theme.S(10), 0, 0);
        hint.Width = w;
        Children.Add(hint);

        ticker.Tick += (_, _) => UpdateClock();
        ticker.Start();
        Unloaded += (_, _) => Stop();
    }

    (double w, double h) MediaSize(Size screen)
    {
        double maxW = Math.Min(Theme.S(640), screen.Width * 0.7), maxH = screen.Height * 0.6;
        var natural = ClipLibrary.Sizes.TryGetValue(Item.FilePath, out var s) ? s : (W: 16, H: 9);
        double aspect = natural.W > 0 && natural.H > 0 ? natural.W / (double)natural.H : 16 / 9.0;
        double w = maxW, h = maxW / aspect;
        if (h > maxH) { h = maxH; w = maxH * aspect; }
        return (Math.Max(w, Theme.S(320)), h);
    }

    string Details()
    {
        var parts = new List<string> { Item.Date.ToString("MMM d, yyyy 'at' h:mm tt") };
        if (ClipLibrary.Durations.TryGetValue(Item.FilePath, out var d)) parts.Add(Format.Duration(d));
        if (ClipLibrary.Sizes.TryGetValue(Item.FilePath, out var s)) parts.Add($"{s.W}×{s.H}");
        try { parts.Add(Controller.SizeText(new FileInfo(Item.FilePath).Length)); } catch { }
        return string.Join(" · ", parts);
    }

    void StartPlayer()
    {
        if (player != null || stopped) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double? length = ClipLibrary.Durations.TryGetValue(Item.FilePath, out var d) ? d : null;
        try { player = new ClipPlayer(Item.FilePath, (int)(mediaW * dpi.DpiScaleX), (int)(mediaH * dpi.DpiScaleY), leadIn, length); }
        catch (Exception e)
        {
            Log.Error("clip player", e);
            ShowProblem("Couldn't play this clip.");
            return;
        }
        player.Opened += () =>
        {
            picture!.Source = player.Frame;
            timeline!.Duration = player.Duration;
            Play();
        };
        player.Ended += () => { playing = false; UpdatePlayButton(); };
        player.Failed += ShowProblem;
        CompositionTarget.Rendering += OnRendering;
    }

    void OnRendering(object? sender, EventArgs e) => player?.Update();

    void ShowProblem(string message)
    {
        if (picture?.Parent is not Grid stage) return;
        var text = Theme.Text(message, 12, brush: Theme.Muted);
        text.TextWrapping = TextWrapping.Wrap;
        text.TextAlignment = TextAlignment.Center;
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.Margin = new Thickness(Theme.S(30));
        stage.Children.Add(text);
    }

    public void TogglePlay() { if (playing) Pause(); else Play(); }

    void Play()
    {
        if (player == null || IsTrimming) return;
        if (player.Duration > 0 && player.Position >= player.Duration - 0.05) player.Position = 0;
        player.Play();
        playing = true;
        UpdatePlayButton();
    }

    void Pause()
    {
        if (player == null) return;
        player.Pause();
        playing = false;
        UpdatePlayButton();
    }

    public void Step(int frames)
    {
        if (player == null) return;
        Pause();
        player.Step(frames, fps);
        UpdateClock();
    }

    void UpdatePlayButton()
    {
        if (playButton == null) return;
        playButton.Child = Icons.Make(playing ? Sym.Pause : Sym.Play, Theme.S(10), Brushes.White);
    }

    void UpdateClock()
    {
        if (player == null || timeline == null || clock == null) return;
        double pos = player.Position;
        timeline.Position = pos;
        clock.Text = IsTrimming
            ? $"{Format.Duration(timeline.TrimEnd - timeline.TrimStart)} kept"
            : $"{Format.Duration(pos)} / {Format.Duration(timeline.Duration)}";
    }

    public void Stop()
    {
        stopped = true;
        ticker.Stop();
        CompositionTarget.Rendering -= OnRendering;
        player?.Dispose();
        player = null;
    }

    // MARK: Trimming

    public void BeginTrim()
    {
        if (player == null || timeline == null || saving || IsTrimming || timeline.Duration <= 0) return;
        Pause();
        IsTrimming = true;
        timeline.BeginTrim();
        trimButtons!.Visibility = Visibility.Visible;
        trimChip!.IsEnabled = false;
        trimChip.Opacity = 0.5;
        hint!.Text = "Drag the yellow handles, then click Trim (Enter). The original clip is kept.";
        UpdateClock();
    }

    public void CancelTrim()
    {
        if (!IsTrimming) return;
        IsTrimming = false;
        timeline!.EndTrim();
        trimButtons!.Visibility = Visibility.Collapsed;
        trimChip!.IsEnabled = true;
        trimChip.Opacity = 1;
        hint!.Text = "Space to play or pause · ← → to step a frame";
        UpdateClock();
    }

    public async void ConfirmTrim()
    {
        if (!IsTrimming || timeline == null) return;
        double start = timeline.TrimStart, end = timeline.TrimEnd;
        CancelTrim();
        saving = true;
        trimChip!.IsEnabled = false;
        trimChip.Opacity = 0.5;
        var dest = ClipTrimmer.OutputPath(Item.FilePath);
        try
        {
            double length = await ClipTrimmer.TrimAsync(Item.FilePath, start, end, dest);
            ClipLibrary.Refresh();
            Toast.Show("Trimmed clip saved", $"{Format.Duration(length)} · {Path.GetFileName(dest)}", Sym.Scissors, Theme.Accent,
                onClick: () => OverlayController.Show(dest));
            OverlayController.Show(dest);
        }
        catch (Exception e)
        {
            Log.Error("trim", e);
            Toast.Error("Couldn't trim the clip", e);
        }
        finally
        {
            saving = false;
            trimChip.IsEnabled = true;
            trimChip.Opacity = 1;
        }
    }
}

/// The preview's timeline: click or drag to seek; while trimming, two yellow handles mark the part to keep.
sealed class TimelineBar : FrameworkElement
{
    public event Action<double>? Seek;
    public double Duration { get => duration; set { duration = value; InvalidateVisual(); } }
    public double Position { get => position; set { position = value; InvalidateVisual(); } }
    public double TrimStart { get; private set; }
    public double TrimEnd { get; private set; }
    double duration, position;
    bool trimming;
    enum Drag { None, Seek, Start, End }
    Drag drag;
    static readonly Color Yellow = Color.FromRgb(255, 204, 0);

    public TimelineBar() { Cursor = Cursors.Hand; }

    public void BeginTrim() { trimming = true; TrimStart = 0; TrimEnd = duration; InvalidateVisual(); }
    public void EndTrim() { trimming = false; InvalidateVisual(); }

    double X(double t) => duration <= 0 ? 0 : Math.Clamp(t / duration, 0, 1) * ActualWidth;
    double T(double x) => duration <= 0 ? 0 : Math.Clamp(x / Math.Max(ActualWidth, 1), 0, 1) * duration;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, mid = h / 2, track = Theme.S(4);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit area
        dc.DrawRoundedRectangle(Theme.Brush(Colors.White, 0.15), null, new Rect(0, mid - track / 2, w, track), track / 2, track / 2);
        dc.DrawRoundedRectangle(Theme.Brush(Colors.White, 0.7), null, new Rect(0, mid - track / 2, X(position), track), track / 2, track / 2);
        if (trimming)
        {
            double a = X(TrimStart), b = X(TrimEnd);
            dc.DrawRectangle(Theme.Brush(Colors.Black, 0.55), null, new Rect(0, 0, a, h));
            dc.DrawRectangle(Theme.Brush(Colors.Black, 0.55), null, new Rect(b, 0, Math.Max(w - b, 0), h));
            var pen = new Pen(Theme.Brush(Yellow), Theme.S(2));
            dc.DrawRectangle(null, pen, new Rect(a, 1, Math.Max(b - a, 1), h - 2));
            foreach (var x in new[] { a, b })
                dc.DrawRoundedRectangle(Theme.Brush(Yellow), null, new Rect(x - Theme.S(3), 0, Theme.S(6), h), Theme.S(2), Theme.S(2));
        }
        else
        {
            dc.DrawEllipse(Brushes.White, null, new Point(X(position), mid), Theme.S(6), Theme.S(6));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        double x = e.GetPosition(this).X;
        drag = Drag.Seek;
        if (trimming)
        {
            double grab = Theme.S(10);
            if (Math.Abs(x - X(TrimStart)) < grab) drag = Drag.Start;
            else if (Math.Abs(x - X(TrimEnd)) < grab) drag = Drag.End;
        }
        CaptureMouse();
        Apply(x);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (drag != Drag.None) Apply(e.GetPosition(this).X);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        drag = Drag.None;
        ReleaseMouseCapture();
    }

    void Apply(double x)
    {
        double t = T(x), gap = Math.Min(0.2, duration / 10);
        switch (drag)
        {
            case Drag.Start: TrimStart = Math.Min(t, TrimEnd - gap); Seek?.Invoke(TrimStart); break;
            case Drag.End: TrimEnd = Math.Max(t, TrimStart + gap); Seek?.Invoke(TrimEnd); break;
            case Drag.Seek: Seek?.Invoke(trimming ? Math.Clamp(t, TrimStart, TrimEnd) : t); break;
        }
        InvalidateVisual();
    }
}
