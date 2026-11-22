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

    /// "Replay 22.19.19", "Roblox 16.53.58 (trimmed)": the kind or app, and the time from the file name.
    public string DisplayName
    {
        get
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(FilePath);
            int at = name.IndexOf(" at ", StringComparison.Ordinal);
            if (at < 0) return name;
            return $"{(Kind == Kinds.Screenshot ? AppName ?? "Screenshot" : Title)} {name[(at + 4)..]}";
        }
    }

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
    /// "H.264", "HEVC", "AV1", "PNG"…
    public static readonly Dictionary<string, string> Codecs = new(StringComparer.OrdinalIgnoreCase);
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
                var codec = CodecOf(item);
                ui.BeginInvoke(() =>
                {
                    if (thumb != null) Thumbs[item.FilePath] = thumb;
                    if (codec != null) Codecs[item.FilePath] = codec;
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
            if (CodecOf(item) is { } c) Codecs[item.FilePath] = c;
        }
    }

    public static string? CodecOf(ClipItem item)
    {
        if (!item.IsVideo) return System.IO.Path.GetExtension(item.FilePath).TrimStart('.').ToUpperInvariant();
        try { return Mp4Reader.Open(item.FilePath).Video?.Codec.Short(); }
        catch { return null; }
    }

    /// "4m ago · 0:08 · 1600×1000 · HEVC · 11.3 MB": what's known about a clip so far.
    public static string Details(ClipItem item, bool ago = true)
    {
        var parts = new List<string>();
        if (ago) parts.Add(ClipThumb.TimeAgo(item.Date));
        if (Durations.TryGetValue(item.FilePath, out var d)) parts.Add(Format.Duration(d));
        if (Sizes.TryGetValue(item.FilePath, out var s)) parts.Add($"{s.W}×{s.H}");
        if (Codecs.TryGetValue(item.FilePath, out var c)) parts.Add(c);
        try { parts.Add(Controller.SizeText(new FileInfo(item.FilePath).Length)); } catch { }
        return string.Join(" · ", parts);
    }

    /// Shorter, for lists: length or format, codec, size and age.
    public static string RowDetails(ClipItem item)
    {
        var parts = new List<string>();
        if (Durations.TryGetValue(item.FilePath, out var d)) parts.Add(Format.Duration(d));
        if (Codecs.TryGetValue(item.FilePath, out var c)) parts.Add(c);
        try { parts.Add(Controller.SizeText(new FileInfo(item.FilePath).Length)); } catch { }
        parts.Add(ClipThumb.TimeAgo(item.Date));
        return string.Join(" · ", parts);
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
    /// "Replay … (trimmed).mp4", "(HEVC)", "(trimmed, H.264)", or with " 2" etc. if that exists.
    public static string OutputPath(string source, string label = "trimmed")
    {
        var dir = Path.GetDirectoryName(source)!;
        var ext = Path.GetExtension(source) is { Length: > 0 } e ? e : ".mp4";
        var name = Path.GetFileNameWithoutExtension(source);
        var path = Path.Combine(dir, $"{name} ({label}){ext}");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{name} ({label} {n}){ext}");
        return path;
    }

    /// `start` and `end` are in seconds from the start of the clip as it's shown (edit lists applied).
    public static Task<double> TrimAsync(string source, double start, double end, string dest, int[]? audio = null) =>
        Task.Run(() => Trim(source, start, end, dest, audio));

    /// `audio`: which sound tracks to keep, as from ClipAudio.Choose (null keeps them all).
    public static double Trim(string source, double start, double end, string dest, int[]? audio = null)
    {
        var r = Mp4Reader.Open(source);
        var v = r.Video ?? throw new RewindException("This clip has no video.");
        int fps = (int)Math.Clamp(Math.Round((v.Count - 1) / Math.Max(v.MediaDuration, 0.001)), 1, 360);
        var cfg = new CodecConfig { Codec = v.Codec, Width = v.Width, Height = v.Height, Fps = fps, Vps = v.Vps, Sps = v.Sps, Pps = v.Pps, SequenceHeader = v.SequenceHeader };
        if (!cfg.Ready || v.Count == 0)
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
        var samples = new List<(double time, int track, int size, long dts, bool sync, Func<byte[]> load)>();
        for (int i = k; i < e; i++)
        {
            var (off, size) = (v.Offsets[i], v.Sizes[i]);
            samples.Add((v.Presented(i) - t0, vt, size, (long)Math.Round((v.Presented(i) - t0) * Mp4Muxer.VideoTimescale), v.IsSync(i), () => Read(off, size)));
        }

        foreach (var a in ClipAudio.Pick(source, r, audio, end + 0.1))
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
                                  a.AudioConfig, editMediaTime: Math.Max(lead, 0), delaySamples: Math.Max(-lead, 0), name: a.Name);
            double frame = Audio.AacFrame / (double)a.Timescale;
            for (int j = j0; j < a.Count && a.Presented(j) < end + frame; j++)
            {
                var (off, size, data) = (a.Offsets[j], a.Sizes[j], a.Data?[j]);
                samples.Add((Raw(j) - Raw(j0), at, size, a.Dts[j] - a.Dts[j0], true, data != null ? () => data : () => Read(off, size)));
            }
        }

        foreach (var s in samples.OrderBy(s => s.time))
            mp4.Write(s.track, s.size, s.dts, s.sync, s.load);
        mp4.Finish();
        return Math.Min(end, v.Presented(e - 1) + 1.0 / fps) - shownFrom;
    }
}

// MARK: - Views

/// Colors the clip pane and its controls are drawn in: white on the dark overlay, or a layout's own palette.
sealed record PanePalette(Color Text, Color Muted, Color Line, Color Wash, Color Bg, bool Dark)
{
    public static PanePalette Overlay => Theme.Light ? OverlayLight : OverlayDark;
    public static readonly PanePalette OverlayLight = new(Theme.Hex(0x1B1B19), Theme.Hex(0x6B6A64), Color.FromArgb(34, 0, 0, 0), Colors.Black, Theme.Hex(0xF3F1EC), false);
    public static readonly PanePalette ConsoleLight = new(Theme.Hex(0x1B1B19), Theme.Hex(0x6B6A64), Theme.Hex(0xD9D6CE), Colors.Black, Theme.Hex(0xF3F1EC), false);
    public static readonly PanePalette OverlayDark = new(Colors.White, Color.FromArgb(148, 255, 255, 255), Color.FromArgb(23, 255, 255, 255), Colors.White, Theme.PanelBase, true);
    public static readonly PanePalette Console = new(Color.FromRgb(0xEC, 0xEB, 0xE7), Theme.Hex(0x7D7E79), Theme.Hex(0x2A2B29), Colors.White, Theme.Hex(0x0F100F), true);
    public static readonly PanePalette SidebarLight = new(Theme.Hex(0x1B1B19), Theme.Hex(0x6B6A64), Theme.Hex(0xD9D6CE), Colors.Black, Theme.Hex(0xF3F1EC), false);
    public static readonly PanePalette SidebarDark = new(Theme.Hex(0xECEBE7), Theme.Hex(0x8E8D88), Theme.Hex(0x2E2D2A), Colors.White, Theme.Hex(0x171716), true);

    public Brush TextBrush => Theme.Brush(Text);
    public Brush MutedBrush => Theme.Brush(Muted);
    public Brush LineBrush => Theme.Brush(Line);
    public Brush WashBrush(double alpha) => Theme.Brush(Wash, alpha);
}

/// How a layout shows the clip pane: Split and Classic's header and thumbnails, Console's header row and
/// filmstrip, or Sidebar's details under the player and "Earlier" list.
enum PaneStyle { Standard, Console, Sidebar, Classic }

/// Plain text that acts as a button: brighter on hover.
sealed class TextLink : TextBlock
{
    public TextLink(string text, Brush brush, double size, Action action, bool underline = false, string? tip = null)
    {
        Text = text;
        Foreground = brush;
        FontFamily = Theme.Font;
        FontSize = size;
        Opacity = 0.8;
        Cursor = Cursors.Hand;
        ToolTip = tip;
        VerticalAlignment = VerticalAlignment.Center;
        if (underline) TextDecorations = System.Windows.TextDecorations.Underline;
        Background = Brushes.Transparent;
        MouseEnter += (_, _) => { if (IsEnabled) Opacity = 1; };
        MouseLeave += (_, _) => Opacity = IsEnabled ? 0.8 : 0.4;
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 0.8 : 0.4;
        MouseLeftButtonDown += (_, e) => e.Handled = true;
        MouseLeftButtonUp += (_, e) => { e.Handled = true; if (IsEnabled) action(); };
    }
}

/// The overlay's player: the selected clip (newest by default) with Trim, Explorer and Recycle Bin, and the
/// recent clips. Every layout has one, drawn in the layout's style.
sealed class ClipPane : StackPanel
{
    const string PlayHint = "Space to play or pause · ← → to step a frame";
    /// The clip selected last, so it stays selected when the overlay is rebuilt or reopened.
    static string? lastSelected;
    readonly Action close;
    readonly Size screen;
    readonly double width, reservedHeight;
    readonly double? fixedMediaHeight;
    readonly bool showsClose;
    readonly PaneStyle style;
    readonly PanePalette p;
    readonly DockPanel header = new() { LastChildFill = true };
    readonly Grid stageHost = new();
    readonly TextBlock hint;
    readonly StackPanel info = new();
    readonly Panel thumbs;
    // Thumbnails keep one size whatever the player size.
    readonly double gap = Theme.S(7), thumbW = Theme.S(88);
    ClipView? view;
    string? pending;

    public ClipItem? Item => view?.Item;
    public bool IsTrimming => view?.IsTrimming == true;
    public bool IsBusy => view is { IsTrimming: true } or { IsSaving: true };
    /// Console shows the header in its own row; the other styles keep it in the pane.
    public FrameworkElement Header => header;

    /// `fixedWidth` for Console and Sidebar; otherwise Settings → Player size, kept on screen. `reservedWidth` and
    /// `reservedHeight` are the room the rest of the layout takes, so the pane always fits.
    public ClipPane(Action close, Size screen, double? fixedWidth, double reservedWidth, double reservedHeight, bool showsClose,
                    PaneStyle style = PaneStyle.Standard, PanePalette? palette = null, double? mediaHeight = null)
    {
        this.close = close;
        this.screen = screen;
        this.reservedHeight = reservedHeight;
        this.showsClose = showsClose;
        this.style = style;
        p = palette ?? PanePalette.Overlay;
        fixedMediaHeight = mediaHeight;
        width = fixedWidth ?? Math.Min(Theme.S(Settings.Shared.PlayerSize),
            Math.Max(screen.Width - reservedWidth - Theme.S(90), Theme.S(Theme.MinPlayerSize)));
        hint = Theme.Text(" ", 10, brush: p.MutedBrush);
        hint.Margin = new Thickness(0, Theme.S(10), 0, 0);
        hint.Width = width;
        thumbs = style == PaneStyle.Sidebar ? new StackPanel() : new StackPanel { Orientation = Orientation.Horizontal };
        switch (style)
        {
            case PaneStyle.Standard:
                header.Margin = new Thickness(0, 0, 0, Theme.S(10));
                thumbs.Margin = new Thickness(0, Theme.S(10), 0, 0);
                Children.Add(header);
                Children.Add(stageHost);
                Children.Add(hint);
                Children.Add(thumbs);
                break;
            case PaneStyle.Console:
                thumbs.Margin = new Thickness(0, 18, 0, 0);
                Children.Add(stageHost);
                Children.Add(thumbs);
                break;
            case PaneStyle.Classic:
                header.Margin = new Thickness(0, 0, 0, Theme.S(10));
                thumbs.Margin = new Thickness(0, Theme.S(12), 0, 0);
                Children.Add(header);
                Children.Add(stageHost);
                Children.Add(thumbs);
                break;
            case PaneStyle.Sidebar:
                thumbs.Margin = new Thickness(0, 20, 0, 0);
                Children.Add(stageHost);
                Children.Add(info);
                Children.Add(thumbs);
                break;
        }
        ClipLibrary.Changed += OnLibrary;
        // While the pane is on screen, Copy's "Fitting… %" follows along.
        Loaded += (_, _) => { ClipSharer.Changed -= RefreshChrome; ClipSharer.Changed += RefreshChrome; };
        Unloaded += (_, _) => ClipSharer.Changed -= RefreshChrome;
        OnLibrary();
    }

    /// Selects a clip and plays it; waits for the library if the file isn't there yet.
    public void Show(string path)
    {
        if (File.Exists(path)) Select(ItemFor(path), play: true);
        else pending = path;
    }

    static ClipItem ItemFor(string path) =>
        ClipLibrary.Items.FirstOrDefault(i => string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase))
        ?? new ClipItem(path, File.GetCreationTime(path));

    void OnLibrary()
    {
        if (pending != null && File.Exists(pending))
        {
            var p = pending;
            pending = null;
            Select(ItemFor(p), play: true);
        }
        // Loads the last selected (or newest) clip, paused, when nothing is selected or the selection was removed.
        else if (Item == null || (!ClipLibrary.Items.Contains(Item) && !File.Exists(Item.FilePath)))
        {
            var last = lastSelected != null && File.Exists(lastSelected) ? ItemFor(lastSelected) : null;
            if (last != null && Item == null) Select(last, play: false);
            else if (ClipLibrary.Items.Count > 0) Select(ClipLibrary.Items[0], play: false);
            else ShowEmpty();
        }
        else RefreshChrome();
        RebuildThumbs();
    }

    /// Like clicking a thumbnail: the selected clip plays or pauses, another one opens.
    void Select(ClipItem item, bool play)
    {
        if (Item == item)
        {
            if (play) view!.TogglePlay();
            return;
        }
        if (view is { IsSaving: true }) return; // finish converting first
        view?.Stop();
        lastSelected = item.FilePath;
        var (duration, size, _, _) = ClipLibrary.Probe(item);
        if (duration is { } d) ClipLibrary.Durations[item.FilePath] = d;
        if (size is { } sz) ClipLibrary.Sizes[item.FilePath] = sz;
        if (!ClipLibrary.Codecs.ContainsKey(item.FilePath) && ClipLibrary.CodecOf(item) is { } c) ClipLibrary.Codecs[item.FilePath] = c;
        view = new ClipView(item, width, MediaHeight(item), play, RefreshChrome, p, style);
        stageHost.Children.Clear();
        stageHost.Children.Add(view);
        RefreshChrome();
        RebuildThumbs();
    }

    void ShowEmpty()
    {
        view?.Stop();
        view = null;
        stageHost.Children.Clear();
        var message = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = Icons.Make(Sym.Film, Theme.S(26), Theme.Muted);
        icon.Margin = new Thickness(0, 0, 0, Theme.S(6));
        message.Children.Add(icon);
        message.Children.Add(Theme.Text("Clips and screenshots you save show up here.", 11.5, brush: Theme.Muted));
        stageHost.Children.Add(ClipView.Stage(width, MediaHeight(null), message, p));
        RefreshChrome();
    }

    /// Height for the pane's width, following the clip's shape within a sane range.
    double MediaHeight(ClipItem? item)
    {
        if (fixedMediaHeight is { } fixedH) return fixedH;
        var natural = item != null && ClipLibrary.Sizes.TryGetValue(item.FilePath, out var s) ? s : (W: 16, H: 10);
        double aspect = natural.W > 0 && natural.H > 0 ? natural.W / (double)natural.H : 1.6;
        if (style == PaneStyle.Sidebar) return Math.Min(Math.Max(width / aspect, width * 9 / 21), width * 3 / 4);
        // Room left after the rest of the layout, the pane's header, transport, hint line and thumbnails.
        double room = screen.Height - reservedHeight - Theme.S(190) - thumbW * 9 / 16;
        return Math.Max(Math.Min(Math.Min(Math.Max(width / aspect, width * 9 / 21), width * 3 / 4), room), Theme.S(140));
    }

    void RefreshChrome()
    {
        switch (style)
        {
            case PaneStyle.Console: ConsoleHeader(); break;
            case PaneStyle.Sidebar: SidebarInfo(); break;
            case PaneStyle.Classic: ClassicHeader(); break;
            default: StandardHeader(); break;
        }
        hint.Text = Item?.IsVideo == true && !IsBusy ? PlayHint : " ";
        if (style == PaneStyle.Classic && view != null) view.ToolTip = Item?.IsVideo == true ? PlayHint : null;
        hint.Visibility = IsBusy ? Visibility.Collapsed : Visibility.Visible;
        thumbs.Visibility = ClipLibrary.Items.Count > 0 && !(style != PaneStyle.Standard && IsBusy) ? Visibility.Visible : Visibility.Collapsed;
    }

    void StandardHeader()
    {
        header.Children.Clear();
        var item = Item;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (item != null && view != null)
        {
            if (item.IsVideo)
            {
                var trim = new Chip(view.IsSaving ? "Saving…" : "Trim", Sym.Scissors, view.BeginTrim);
                if (view.IsTrimming || view.IsSaving) { trim.IsEnabled = false; trim.Opacity = 0.5; }
                actions.Children.Add(trim);
            }
            actions.Children.Add(new Chip(ClipSharer.IsBusy(item.FilePath) ? $"{(int)(ClipSharer.Progress * 100)}%" : null, Sym.Copy,
                    () => ClipSharer.Copy(item.FilePath))
                { ToolTip = "Copy to paste into Discord or anywhere", Margin = new Thickness(Theme.S(8), 0, 0, 0) });
            actions.Children.Add(new Chip(null, Sym.Folder, () => { var path = item.FilePath; close(); Controller.Reveal(path); })
                { ToolTip = "Show in Explorer", Margin = new Thickness(Theme.S(8), 0, 0, 0) });
            actions.Children.Add(new Chip(null, Sym.Trash, () => Delete(item))
                { ToolTip = "Move to Recycle Bin", Margin = new Thickness(Theme.S(8), 0, 0, 0) });
        }
        if (showsClose) actions.Children.Add(new CircleButton(Sym.Close, close) { ToolTip = "Close (Esc)", Margin = new Thickness(Theme.S(8), 0, 0, 0) });
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);

        if (item != null)
        {
            var icon = Icons.Make(item.Icon, Theme.S(13), Theme.Brush(item.Tint));
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(0, 0, Theme.S(8), 0);
            DockPanel.SetDock(icon, Dock.Left);
            header.Children.Add(icon);
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, Theme.S(8), 0) };
            titles.Children.Add(Theme.Text(item.Title, 13, FontWeights.SemiBold));
            titles.Children.Add(Theme.Text(ClipLibrary.Details(item), 10.5, brush: Theme.Muted));
            header.Children.Add(titles);
        }
        else
        {
            var title = Theme.Text("Recent clips", 13, FontWeights.SemiBold);
            title.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(title);
        }
    }

    void Delete(ClipItem item)
    {
        if (view is { IsSaving: true }) return;
        view?.Stop();
        lastSelected = null;
        ClipLibrary.Trash(item);
    }

    /// Text-button actions for Console and Sidebar.
    List<FrameworkElement> Links(ClipItem item, Brush brush, bool underline)
    {
        var list = new List<FrameworkElement>();
        if (item.IsVideo && view != null)
        {
            var trim = new TextLink(view.IsSaving ? "Saving…" : "Trim", brush, 13, view.BeginTrim, underline);
            trim.IsEnabled = !IsBusy;
            list.Add(trim);
        }
        list.Add(new TextLink(ClipSharer.ButtonTitle(item.FilePath), brush, 13, () => ClipSharer.Copy(item.FilePath), underline,
            "Copy to paste into Discord or anywhere"));
        list.Add(new TextLink("Show in Explorer", brush, 13, () => { var path = item.FilePath; close(); Controller.Reveal(path); }, underline));
        var del = new TextLink("Delete", brush, 13, () => Delete(item), underline, "Move to the Recycle Bin");
        del.IsEnabled = view is not { IsSaving: true };
        list.Add(del);
        return list;
    }

    void ConsoleHeader()
    {
        header.Children.Clear();
        header.Height = 52;
        header.Margin = new Thickness(18, 0, 18, 0);
        var item = Item;
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (item != null)
            foreach (var l in Links(item, Theme.Brush(Theme.Hex(0xB5B6B1)), underline: false))
            {
                l.Margin = new Thickness(20, 0, 0, 0);
                right.Children.Add(l);
            }
        var x = new TextLink("", p.MutedBrush, 11, close, tip: "Close (Esc)") { FontFamily = Theme.Symbols, Margin = new Thickness(20, 0, 0, 0) };
        right.Children.Add(x);
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (item != null)
        {
            left.Children.Add(Plain(item.DisplayName, 15, p.TextBrush));
            var d = Plain(ClipLibrary.Details(item), 12, p.MutedBrush, Theme.Mono);
            d.Margin = new Thickness(20, 0, 10, 0);
            left.Children.Add(d);
        }
        else left.Children.Add(Plain("Recent clips", 15, p.TextBrush));
        header.Children.Add(left);
    }

    /// Classic: the clip's name and details on one line, plain text actions on the right.
    void ClassicHeader()
    {
        header.Children.Clear();
        var item = Item;
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (item != null && view != null)
        {
            // Square icon buttons (the shape of Classic's panels); the names are in the tooltips.
            Chip Square(string? title, Sym icon, Action action, string tip)
            {
                var b = new Chip(title, icon, action) { ToolTip = tip, CornerRadius = new CornerRadius(0), Margin = new Thickness(Theme.S(6), 0, 0, 0) };
                return b;
            }
            if (item.IsVideo)
            {
                var trim = Square(view.IsSaving ? "Saving…" : null, Sym.Scissors, view.BeginTrim, "Trim");
                if (IsBusy) { trim.IsEnabled = false; trim.Opacity = 0.5; }
                right.Children.Add(trim);
            }
            right.Children.Add(Square(ClipSharer.IsBusy(item.FilePath) ? $"{(int)(ClipSharer.Progress * 100)}%" : null, Sym.Copy,
                () => ClipSharer.Copy(item.FilePath), "Copy to paste into Discord or anywhere"));
            right.Children.Add(Square(null, Sym.Folder, () => { var path = item.FilePath; close(); Controller.Reveal(path); }, "Show in Explorer"));
            var del = Square(null, Sym.Trash, () => Delete(item), "Move to the Recycle Bin");
            if (view.IsSaving) { del.IsEnabled = false; del.Opacity = 0.5; }
            right.Children.Add(del);
        }
        if (showsClose)
            right.Children.Add(new TextLink("", p.TextBrush, Theme.S(9), close, tip: "Close (Esc)")
                { FontFamily = Theme.Symbols, Margin = new Thickness(Theme.S(18), 0, 0, 0) });
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (item != null)
        {
            var name = Theme.Text(item.DisplayName, 12, FontWeights.SemiBold, p.TextBrush);
            name.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(name);
            var d = Theme.Text(ClipLibrary.Details(item), 10, brush: p.MutedBrush);
            d.VerticalAlignment = VerticalAlignment.Center;
            d.Margin = new Thickness(Theme.S(12), 0, Theme.S(10), 0);
            left.Children.Add(d);
        }
        else left.Children.Add(Theme.Text("Gallery", 12, FontWeights.SemiBold, p.TextBrush));
        header.Children.Add(left);
    }

    void SidebarInfo()
    {
        info.Children.Clear();
        var item = Item;
        if (item == null || IsBusy) return;
        var name = Plain(item.DisplayName, 14, p.TextBrush);
        name.FontWeight = FontWeights.SemiBold;
        name.Margin = new Thickness(0, 10, 0, 0);
        info.Children.Add(name);
        var details = Plain(ClipLibrary.Details(item), 12, p.MutedBrush, Theme.Mono);
        details.Margin = new Thickness(0, 3, 0, 0);
        info.Children.Add(details);
        var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var l in Links(item, p.MutedBrush, underline: true))
        {
            if (links.Children.Count > 0) l.Margin = new Thickness(18, 0, 0, 0);
            links.Children.Add(l);
        }
        info.Children.Add(links);
    }

    public static TextBlock Plain(string text, double size, Brush brush, FontFamily? family = null) => new()
    {
        Text = text, FontSize = size, Foreground = brush, FontFamily = family ?? Theme.Font,
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
    };

    void RebuildThumbs()
    {
        thumbs.Children.Clear();
        switch (style)
        {
            case PaneStyle.Console:
            {
                double w = 150, h = 94;
                int fits = Math.Max(1, (int)((width + 2) / (w + 2)));
                foreach (var item in ClipLibrary.Items.Take(fits))
                    thumbs.Children.Add(new ConsoleThumb(item, w, h, item == Item, p, i => Select(i, play: true)) { Margin = new Thickness(0, 0, 2, 0) });
                break;
            }
            case PaneStyle.Classic:
            {
                double w = Theme.S(104), h = w * 9 / 16, g = Theme.S(6);
                int fits = Math.Max(1, (int)((width + g) / (w + g)));
                foreach (var item in ClipLibrary.Items.Take(fits))
                    thumbs.Children.Add(new ConsoleThumb(item, w, h, item == Item, p, i => Select(i, play: true), mono: false) { Margin = new Thickness(0, 0, g, 0) });
                break;
            }
            case PaneStyle.Sidebar:
            {
                var others = ClipLibrary.Items.Where(i => i != Item).Take(3).ToList();
                if (others.Count == 0) break;
                var title = Plain("Earlier", 12, p.MutedBrush);
                title.Margin = new Thickness(0, 0, 0, 8);
                thumbs.Children.Add(title);
                foreach (var item in others) thumbs.Children.Add(new SidebarClipRow(item, width, p, i => Select(i, play: true)));
                break;
            }
            default:
            {
                int fits = Math.Max(1, (int)((width + gap) / (thumbW + gap)));
                foreach (var item in ClipLibrary.Items.Take(fits))
                    thumbs.Children.Add(new ClipThumb(item, thumbW, item == Item, i => Select(i, play: true))
                        { Margin = new Thickness(0, 0, gap, 0) });
                break;
            }
        }
        RefreshChrome();
    }

    public void TogglePlay() => view?.TogglePlay();
    public void Step(int frames) => view?.Step(frames);
    public void CancelTrim() => view?.CancelTrim();
    public void ConfirmTrim() => view?.ConfirmTrim();

    public void Stop()
    {
        ClipLibrary.Changed -= OnLibrary;
        view?.Stop();
    }
}

/// Console's filmstrip tile: a square-cornered thumbnail with its length, and how long ago it was saved.
sealed class ConsoleThumb : StackPanel
{
    /// `mono: false` for Classic, which uses the regular font throughout.
    public ConsoleThumb(ClipItem item, double w, double h, bool selected, PanePalette p, Action<ClipItem> open, bool mono = true)
    {
        var family = mono ? Theme.Mono : Theme.Font;
        var picture = new Grid { Width = w, Height = h, ClipToBounds = true, Background = Theme.Brush(Theme.Wash, 0.05) };
        if (ClipLibrary.Thumbs.TryGetValue(item.FilePath, out var img))
            picture.Children.Add(new Image { Source = img, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        string tag = item.IsVideo ? (ClipLibrary.Durations.TryGetValue(item.FilePath, out var d) ? Format.Duration(d) : "")
                                  : (ClipLibrary.Codecs.TryGetValue(item.FilePath, out var c) ? c : "PNG");
        if (tag.Length > 0)
            picture.Children.Add(new Border
            {
                Background = Theme.Brush(Colors.Black, 0.75), Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Child = ClipPane.Plain(tag, mono ? 11 : Theme.S(8.5), Brushes.White, family),
            });
        var frame = new Border
        {
            BorderThickness = new Thickness(2), IsHitTestVisible = false,
            BorderBrush = selected ? Theme.Brush(Theme.Accent) : Brushes.Transparent,
        };
        picture.Children.Add(frame);
        Children.Add(picture);
        var when = ClipPane.Plain(ClipThumb.TimeAgo(item.Date), mono ? 11 : Theme.S(8.5), selected ? p.TextBrush : p.MutedBrush, family);
        when.Margin = new Thickness(0, 5, 0, 0);
        Children.Add(when);
        Width = w;
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
        ToolTip = Path.GetFileName(item.FilePath);
        MouseEnter += (_, _) => { if (!selected) frame.BorderBrush = Theme.Brush(Theme.Wash, 0.35); };
        MouseLeave += (_, _) => { if (!selected) frame.BorderBrush = Brushes.Transparent; };
        MouseLeftButtonDown += (_, e) => e.Handled = true;
        MouseLeftButtonUp += (_, e) => { e.Handled = true; open(item); };
    }
}

/// Sidebar's "Earlier" row: thumbnail, name and a short line of details.
sealed class SidebarClipRow : Border
{
    public SidebarClipRow(ClipItem item, double width, PanePalette p, Action<ClipItem> open)
    {
        var picture = new Grid { Width = 84, Height = 52, ClipToBounds = true, Background = p.WashBrush(0.06) };
        if (ClipLibrary.Thumbs.TryGetValue(item.FilePath, out var img))
            picture.Children.Add(new Image { Source = img, Stretch = Stretch.UniformToFill });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        var name = ClipPane.Plain(item.DisplayName, 14, p.TextBrush);
        name.FontWeight = FontWeights.SemiBold;
        texts.Children.Add(name);
        var details = ClipPane.Plain(ClipLibrary.RowDetails(item), 12, p.MutedBrush, Theme.Mono);
        details.Margin = new Thickness(0, 3, 0, 0);
        texts.Children.Add(details);
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(picture, Dock.Left);
        row.Children.Add(picture);
        row.Children.Add(texts);
        Child = row;
        Width = width;
        Padding = new Thickness(0, 8, 0, 8);
        BorderThickness = new Thickness(0, 1, 0, 0);
        BorderBrush = p.LineBrush;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        ToolTip = Path.GetFileName(item.FilePath);
        MouseEnter += (_, _) => Background = p.WashBrush(0.04);
        MouseLeave += (_, _) => Background = Brushes.Transparent;
        MouseLeftButtonDown += (_, e) => e.Handled = true;
        MouseLeftButtonUp += (_, e) => { e.Handled = true; open(item); };
    }
}

sealed class ClipThumb : StackPanel
{
    public ClipThumb(ClipItem item, double width, bool selected, Action<ClipItem> open)
    {
        double height = width * 9 / 16;
        var radius = new CornerRadius(Theme.S(7));
        var picture = new Grid { Width = width, Height = height, ClipToBounds = true };
        picture.Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius.TopLeft, radius.TopLeft);
        picture.Children.Add(new Border { Background = Theme.Brush(Theme.Wash, 0.06) });
        if (ClipLibrary.Thumbs.TryGetValue(item.FilePath, out var img))
            picture.Children.Add(new Image { Source = img, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        else
            picture.Children.Add(Icons.Make(item.Icon, Theme.S(14), Theme.Muted));
        var hoverShade = new Border { Background = Theme.Brush(Colors.Black, 0.3), Visibility = Visibility.Collapsed };
        hoverShade.Child = Icons.Make(item.IsVideo ? Sym.Play : Sym.Eye, Theme.S(14), Brushes.White);
        picture.Children.Add(hoverShade);
        picture.Children.Add(new Border
        {
            Width = Theme.S(14), Height = Theme.S(14), CornerRadius = new CornerRadius(Theme.S(7)), Background = Theme.Brush(Colors.Black, 0.55),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(Theme.S(3)),
            Child = Icons.Make(item.Icon, Theme.S(7.5), Theme.Brush(item.Tint)),
        });
        if (ClipLibrary.Durations.TryGetValue(item.FilePath, out var dur))
            picture.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(Theme.S(7)), Background = Theme.Brush(Colors.Black, 0.6), Padding = new Thickness(Theme.S(3.5), Theme.S(1), Theme.S(3.5), Theme.S(1)),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(Theme.S(3)),
                Child = Theme.Text(Format.Duration(dur), 8, FontWeights.Bold, Brushes.White, Theme.Mono),
            });
        Brush idle = selected ? Theme.Brush(item.Tint) : Theme.Line;
        var frame = new Border { CornerRadius = radius, BorderThickness = new Thickness(selected ? Theme.S(1.5) : 1), BorderBrush = idle, IsHitTestVisible = false };
        var tile = new Grid();
        tile.Children.Add(picture);
        tile.Children.Add(frame);
        Children.Add(tile);
        var when = Theme.Text(TimeAgo(item.Date), 9.5, brush: selected ? Theme.InkBrush : Theme.Muted);
        when.Margin = new Thickness(0, Theme.S(4), 0, 0);
        Children.Add(when);

        Width = width;
        Cursor = Cursors.Hand;
        ToolTip = Path.GetFileName(item.FilePath);
        Background = Brushes.Transparent;
        MouseEnter += (_, _) => { if (!selected) { hoverShade.Visibility = Visibility.Visible; frame.BorderBrush = Theme.Brush(item.Tint, 0.6); } };
        MouseLeave += (_, _) => { hoverShade.Visibility = Visibility.Collapsed; frame.BorderBrush = idle; };
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

/// One clip in the pane: the video with play, frame steps, a timeline and trimming, or a screenshot.
sealed class ClipView : StackPanel
{
    public ClipItem Item { get; }
    public bool IsTrimming { get; private set; }
    public bool IsSaving { get; private set; }
    readonly Action changed;
    readonly bool autoplay;
    readonly PanePalette p;
    readonly Image? picture;
    readonly TimelineBar? timeline;
    readonly TextBlock? clock;
    readonly StackPanel? trimButtons;
    readonly Chip? confirmButton;
    readonly CircleButton? playButton;
    readonly SaveAsRow? saveAs;
    readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly double mediaW, mediaH, fps = 60, leadIn;
    ClipPlayer? player;
    bool playing, stopped;

    /// The black rounded frame media sits in (square-cornered in Console and Sidebar).
    public static Grid Stage(double w, double h, UIElement content, PanePalette? palette = null)
    {
        double r = palette == null || palette == PanePalette.Overlay ? Theme.S(12) : 0;
        var stage = new Grid { Width = w, Height = h, Background = Brushes.Black };
        stage.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        stage.Children.Add(content);
        return new Grid { Children = { stage, new Border { Width = w, Height = h, CornerRadius = new CornerRadius(r), BorderThickness = new Thickness(1), BorderBrush = Theme.Line, IsHitTestVisible = false } } };
    }

    public ClipView(ClipItem item, double w, double h, bool autoplay, Action changed, PanePalette? palette = null,
                    PaneStyle style = PaneStyle.Standard)
    {
        Item = item;
        this.changed = changed;
        this.autoplay = autoplay;
        p = palette ?? PanePalette.Overlay;
        mediaW = w;
        mediaH = h;
        var (_, _, probedFps, probedLeadIn) = ClipLibrary.Probe(item);
        if (probedFps > 0) fps = probedFps;
        leadIn = probedLeadIn;

        if (!item.IsVideo)
        {
            var image = new Image { Stretch = Stretch.Uniform };
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(item.FilePath);
                bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked (it may be moved to the bin)
                bmp.DecodePixelWidth = (int)Math.Min(w * 2, 3840);
                bmp.EndInit();
                bmp.Freeze();
                image.Source = bmp;
            }
            catch { }
            Children.Add(Stage(w, h, image, p));
            return;
        }

        // Until the first frame arrives, Explorer's thumbnail stands in for it.
        picture = new Image { Stretch = Stretch.Uniform, Source = ClipLibrary.Thumbs.TryGetValue(item.FilePath, out var thumb) ? thumb : null };
        var stage = Stage(w, h, picture, p);
        stage.Cursor = Cursors.Hand;
        stage.MouseLeftButtonUp += (_, e) => { e.Handled = true; TogglePlay(); };
        stage.MouseLeftButtonDown += (_, e) => e.Handled = true;
        Children.Add(stage);

        timeline = new TimelineBar(p) { Height = Theme.S(24), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Theme.S(10), 0, Theme.S(10), 0) };
        timeline.Seek += t => { Pause(); if (player != null) player.Position = t; UpdateClock(); };
        playButton = new CircleButton(Sym.Play, TogglePlay, p) { ToolTip = "Play or pause (Space)" };
        var stepBack = new CircleButton(Sym.StepBack, () => Step(-1), p) { ToolTip = "Back a frame (←)", Margin = new Thickness(Theme.S(6), 0, 0, 0) };
        var stepFwd = new CircleButton(Sym.StepForward, () => Step(1), p) { ToolTip = "Forward a frame (→)", Margin = new Thickness(Theme.S(6), 0, 0, 0) };
        clock = Theme.Text("0:00", 10.5, FontWeights.SemiBold, p.MutedBrush, Theme.Mono);
        clock.VerticalAlignment = VerticalAlignment.Center;
        trimButtons = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(Theme.S(10), 0, 0, 0) };
        confirmButton = new Chip("Trim", Sym.Scissors, ConfirmTrim, p) { Margin = new Thickness(0, 0, Theme.S(6), 0), ToolTip = "Save the part between the handles (Enter)" };
        trimButtons.Children.Add(confirmButton);
        trimButtons.Children.Add(new Chip("Cancel", Sym.Close, CancelTrim, p) { ToolTip = "Cancel (Esc)" });
        var transport = new DockPanel { Margin = new Thickness(0, Theme.S(8), 0, 0), Width = w };
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

        saveAs = new SaveAsRow(w, p, UpdateConfirm, square: style != PaneStyle.Standard) { Visibility = Visibility.Collapsed, Margin = new Thickness(0, Theme.S(8), 0, 0) };
        Children.Add(saveAs);

        ticker.Tick += (_, _) => UpdateClock();
        Loaded += (_, _) => StartPlayer();
        Unloaded += (_, _) => Stop();
    }

    void UpdateConfirm()
    {
        if (confirmButton == null) return;
        bool convert = Settings.Shared.ExportFormat != ExportFormat.Original;
        confirmButton.Label = convert ? "Convert" : "Trim";
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
            ShowProblem("Couldn't play this clip. Show it in Explorer to open it in another player.");
            return;
        }
        player.Opened += () =>
        {
            picture!.Source = player.Frame;
            timeline!.Duration = player.Duration;
            if (autoplay) Play();
            else player.Position = 0; // shows the first frame, paused
        };
        player.Ended += () => { playing = false; UpdatePlayButton(); };
        player.Failed += ShowProblem;
        CompositionTarget.Rendering += OnRendering;
        ticker.Start();
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
        playButton.Child = Icons.Make(playing ? Sym.Pause : Sym.Play, Theme.S(10), p.TextBrush);
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
        if (player == null || timeline == null || IsSaving || IsTrimming || timeline.Duration <= 0) return;
        Pause();
        IsTrimming = true;
        timeline.BeginTrim();
        trimButtons!.Visibility = Visibility.Visible;
        List<ClipSound> sounds;
        try { sounds = ClipAudio.Sources(Mp4Reader.Open(Item.FilePath)); }
        catch (Exception e) { Log.Error("clip sounds", e); sounds = new(); }
        saveAs!.ShowSounds(sounds);
        saveAs.ShowChoices();
        saveAs.Visibility = Visibility.Visible;
        UpdateConfirm();
        UpdateClock();
        changed();
    }

    public void CancelTrim()
    {
        if (!IsTrimming) return;
        IsTrimming = false;
        timeline!.EndTrim();
        trimButtons!.Visibility = Visibility.Collapsed;
        saveAs!.Visibility = Visibility.Collapsed;
        UpdateClock();
        changed();
    }

    public async void ConfirmTrim()
    {
        if (!IsTrimming || timeline == null) return;
        double start = timeline.TrimStart, end = timeline.TrimEnd, duration = timeline.Duration;
        var s = Settings.Shared;
        var format = s.ExportFormat;
        int quality = s.ExportQuality;
        int[]? audio = saveAs!.AudioChoice;
        bool sound = audio is not { Length: 0 };
        string with = audio is { Length: > 0 } ? " with " + saveAs.ChosenNames : sound ? "" : " without sound";
        CancelTrim();
        IsSaving = true;
        var source = Item.FilePath;
        long before = 0;
        try { before = new FileInfo(source).Length; } catch { }
        if (format != ExportFormat.Original)
        {
            saveAs!.ShowProgress(format, 0);
            saveAs.Visibility = Visibility.Visible;
        }
        changed();
        try
        {
            if (format == ExportFormat.Original)
            {
                var dest = ClipTrimmer.OutputPath(source, sound ? "trimmed" : "trimmed, no sound");
                double length = await ClipTrimmer.TrimAsync(source, start, end, dest, audio);
                ClipLibrary.Refresh();
                Toast.Show("Trimmed clip saved" + with, $"{Format.Duration(length)} · {Path.GetFileName(dest)}", Sym.Scissors, Theme.Accent,
                    onClick: () => OverlayController.Show(dest));
                Finish();
                OverlayController.Show(dest);
            }
            else
            {
                bool whole = start < 0.05 && end > duration - 0.05;
                var dest = ClipConverter.OutputPath(source, format, whole, sound);
                var ui = Dispatcher;
                await ClipConverter.ConvertAsync(source, start, end, format, quality, dest,
                    pr => ui.BeginInvoke(() => saveAs?.ShowProgress(format, pr)), audio);
                ClipLibrary.Refresh();
                long after = 0;
                try { after = new FileInfo(dest).Length; } catch { }
                Toast.Show($"Saved as {format.Label()}{with}", $"{Controller.SizeText(after)} (was {Controller.SizeText(before)}) · {Path.GetFileName(dest)}",
                    Sym.Sync, Theme.Accent, onClick: () => OverlayController.Show(dest));
                Finish();
                OverlayController.Show(dest);
            }
        }
        catch (Exception e)
        {
            Log.Error(format == ExportFormat.Original ? "trim" : "convert", e);
            Toast.Error(format == ExportFormat.Original ? "Couldn't trim the clip" : "Couldn't convert the clip", e);
            Finish();
        }

        void Finish()
        {
            if (!IsSaving) return;
            IsSaving = false;
            if (saveAs != null) saveAs.Visibility = Visibility.Collapsed;
            changed();
        }
    }
}

/// Under the player while trimming: what to save the trimmed part as, and how hard to compress it. While
/// converting, a progress bar.
sealed class SaveAsRow : StackPanel
{
    readonly Settings s = Settings.Shared;
    readonly PanePalette p;
    readonly Action changed;
    readonly WrapPanel choices = new();
    readonly StackPanel formats = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(Theme.S(8), 0, Theme.S(12), 0) };
    readonly StackPanel qualityRow = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly QualitySlider slider;
    readonly TextBlock word, note;
    readonly Grid progress = new();
    readonly Border bar, fill;
    readonly TextBlock progressText;

    // Which of the clip's sounds go into the saved part. Each new trim starts with the clip's sound as it is.
    readonly DockPanel soundRow = new() { LastChildFill = true, Margin = new Thickness(0, 0, 0, Theme.S(7)) };
    readonly WrapPanel soundChips = new();
    readonly TextBlock soundNote;
    List<ClipSound> sounds = new();
    readonly HashSet<int> soundsOn = new();
    readonly bool square;

    /// The tracks to save (see ClipAudio.Choose): null keeps the clip's sound as it is.
    public int[]? AudioChoice => sounds.Count == 0 ? null : ClipAudio.Choose(sounds, soundsOn);
    public string ChosenNames => string.Join(" + ", sounds.Where(x => soundsOn.Contains(x.Track)).Select(x => x.Name));

    /// `square`: square corners, for the layouts whose panels are square (Console, Sidebar, Classic).
    public SaveAsRow(double width, PanePalette palette, Action changed, bool square = false)
    {
        p = palette;
        this.changed = changed;
        this.square = square;
        Width = width;
        var label = RowLabel("Save as");
        var first = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, Theme.S(6)) };
        first.Children.Add(label);
        first.Children.Add(formats);
        formats.Margin = new Thickness(0, 0, Theme.S(12), 0);

        var soundLabel = RowLabel("Sound");
        soundLabel.VerticalAlignment = VerticalAlignment.Top;
        soundLabel.Margin = new Thickness(0, Theme.S(5), 0, 0);
        DockPanel.SetDock(soundLabel, Dock.Left);
        soundRow.Children.Add(soundLabel);
        soundNote = Theme.Text("", 9.5, brush: p.MutedBrush);
        soundNote.VerticalAlignment = VerticalAlignment.Center;
        soundNote.Margin = new Thickness(Theme.S(4), 0, 0, Theme.S(4));
        soundRow.Children.Add(soundChips);
        Children.Add(soundRow);
        choices.Children.Add(first);
        slider = new QualitySlider(p) { Width = Theme.S(140), Height = Theme.S(20), VerticalAlignment = VerticalAlignment.Center };
        slider.Changed += v => { s.ExportQuality = v; ShowWord(); };
        word = Theme.Text("", 10.5, FontWeights.SemiBold, p.TextBrush);
        word.VerticalAlignment = VerticalAlignment.Center;
        word.Margin = new Thickness(Theme.S(8), 0, 0, 0);
        qualityRow.Children.Add(slider);
        qualityRow.Children.Add(word);
        qualityRow.Margin = new Thickness(0, 0, 0, Theme.S(6));
        choices.Children.Add(qualityRow);
        note = Theme.Text("", 9.5, brush: p.MutedBrush);
        note.TextWrapping = TextWrapping.Wrap;
        note.TextTrimming = TextTrimming.None;

        fill = new Border { Background = Theme.Brush(Theme.Accent), CornerRadius = new CornerRadius(Theme.S(2)), HorizontalAlignment = HorizontalAlignment.Left };
        bar = new Border { Height = Theme.S(4), CornerRadius = new CornerRadius(Theme.S(2)), Background = p.WashBrush(0.12), Child = fill, VerticalAlignment = VerticalAlignment.Center };
        progressText = Theme.Text("", 10.5, brush: p.MutedBrush, family: Theme.Mono);
        progressText.HorizontalAlignment = HorizontalAlignment.Right;
        progress.ColumnDefinitions.Add(new ColumnDefinition());
        progress.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        progressText.Margin = new Thickness(Theme.S(10), 0, 0, 0);
        Grid.SetColumn(progressText, 1);
        progress.Children.Add(bar);
        progress.Children.Add(progressText);

        Children.Add(choices);
        Children.Add(note);
        Children.Add(progress);
        ShowChoices();
    }

    /// "Sound" and "Save as" are as wide as each other, so the chips after them line up.
    TextBlock RowLabel(string text)
    {
        var t = Theme.Text(text, 10.5, FontWeights.SemiBold, p.MutedBrush);
        t.VerticalAlignment = VerticalAlignment.Center;
        t.Width = Theme.S(52);
        return t;
    }

    CornerRadius Round(double r) => new(square ? 0 : Theme.S(r));

    /// A new trim: the clip's sounds, the ones in its own mix switched on.
    public void ShowSounds(List<ClipSound> list)
    {
        sounds = list;
        soundsOn.Clear();
        foreach (var x in list) if (x.On) soundsOn.Add(x.Track);
        ShowSoundChips();
    }

    void ShowSoundChips()
    {
        soundRow.Visibility = sounds.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        soundChips.Children.Clear();
        foreach (var x in sounds)
        {
            bool on = soundsOn.Contains(x.Track);
            var ink = on ? p.TextBrush : p.MutedBrush;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = Icons.Make(x.IsMic ? (on ? Sym.Mic : Sym.MicOff) : (on ? Sym.Volume : Sym.Mute), Theme.S(10.5), on ? Theme.Brush(Theme.Accent) : p.MutedBrush);
            icon.Margin = new Thickness(0, 0, Theme.S(5), 0);
            row.Children.Add(icon);
            var name = Theme.Text(x.Name, 10, FontWeights.SemiBold, ink);
            name.VerticalAlignment = VerticalAlignment.Center;
            name.MaxWidth = Theme.S(120);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            if (!on) name.TextDecorations = TextDecorations.Strikethrough;
            row.Children.Add(name);
            var chip = new Border
            {
                CornerRadius = Round(10), Padding = new Thickness(Theme.S(7), Theme.S(3), Theme.S(8), Theme.S(3)),
                Margin = new Thickness(0, 0, Theme.S(4), Theme.S(4)), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                Background = on ? Theme.Brush(Theme.Accent, 0.22) : p.WashBrush(0.04),
                BorderBrush = on ? Theme.Brush(Theme.Accent, 0.9) : p.LineBrush,
                Child = row,
                ToolTip = on ? $"Leave {x.Name} out of the saved clip" : $"Put {x.Name} in the saved clip",
            };
            var idle = chip.Background;
            chip.MouseEnter += (_, _) => chip.Background = on ? Theme.Brush(Theme.Accent, 0.32) : p.WashBrush(0.1);
            chip.MouseLeave += (_, _) => chip.Background = idle;
            int track = x.Track;
            chip.MouseLeftButtonDown += (_, e) => e.Handled = true;
            chip.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (!soundsOn.Remove(track)) soundsOn.Add(track);
                ShowSoundChips();
                changed();
            };
            soundChips.Children.Add(chip);
        }
        var choice = AudioChoice;
        soundNote.Text = choice == null ? "as recorded" : choice.Length == 0 ? "no sound" : choice.Length == 1 ? "only this" : "mixed when saved";
        soundChips.Children.Add(soundNote);
    }

    public void ShowChoices()
    {
        choices.Visibility = note.Visibility = Visibility.Visible;
        soundRow.Visibility = sounds.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        progress.Visibility = Visibility.Collapsed;
        formats.Children.Clear();
        foreach (var f in ClipConverter.Formats())
        {
            bool on = s.ExportFormat == f;
            var pill = new Border
            {
                CornerRadius = Round(10), Padding = new Thickness(Theme.S(7), Theme.S(2.5), Theme.S(7), Theme.S(2.5)),
                Margin = new Thickness(0, 0, Theme.S(3), 0), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
                Background = on ? Theme.Brush(Theme.Accent, 0.3) : p.WashBrush(0.08),
                BorderBrush = on ? Theme.Brush(Theme.Accent) : Brushes.Transparent,
                Child = Theme.Text(f.Label(), 10, FontWeights.SemiBold, p.TextBrush),
                ToolTip = f == ExportFormat.Original ? "Keep the original video, no re-encoding"
                    : f == ExportFormat.Av1 && !VideoEncoder.HasHardwareEncoder(VideoCodec.Av1) ? "This PC can't encode AV1. " + ClipConverter.Av1Needs
                    : $"Re-encode as {f.Label()}",
            };
            var choice = f;
            pill.MouseLeftButtonDown += (_, e) => e.Handled = true;
            pill.MouseLeftButtonUp += (_, e) => { e.Handled = true; s.ExportFormat = choice; ShowChoices(); changed(); };
            formats.Children.Add(pill);
        }
        bool convert = s.ExportFormat != ExportFormat.Original;
        qualityRow.Visibility = convert ? Visibility.Visible : Visibility.Collapsed;
        slider.Value = s.ExportQuality;
        ShowWord();
        note.Text = s.ExportFormat == ExportFormat.Av1 && !VideoEncoder.HasHardwareEncoder(VideoCodec.Av1)
            ? "This PC's graphics card can't encode AV1, so converting will fail. " + ClipConverter.Av1Needs
            : convert
            ? "Drag the handles (or leave them at the ends for the whole clip), then click Convert. Re-encodes into a new file; the original is kept."
            : "Drag the handles, then click Trim. Saves without re-encoding; the original is kept.";
    }

    void ShowWord() => word.Text = ClipConverter.Word(s.ExportQuality);

    public void ShowProgress(ExportFormat format, double fraction)
    {
        choices.Visibility = note.Visibility = soundRow.Visibility = Visibility.Collapsed;
        progress.Visibility = Visibility.Visible;
        double w = Math.Max(Width - Theme.S(200), Theme.S(60));
        bar.Width = w;
        fill.Width = Math.Clamp(fraction, 0, 1) * w;
        progressText.Text = $"Converting to {format.Label()}… {(int)(fraction * 100)}%";
    }
}

/// The quality slider: a thin track filled in the accent color up to a round knob.
sealed class QualitySlider : FrameworkElement
{
    readonly PanePalette p;
    int value;
    bool dragging;
    public event Action<int>? Changed;
    public int Value { get => value; set { this.value = Math.Clamp(value, 0, 100); InvalidateVisual(); } }

    public QualitySlider(PanePalette palette)
    {
        p = palette;
        Cursor = Cursors.Hand;
        ToolTip = "From a much smaller file up to the original's quality";
    }

    double Knob => Theme.S(6);
    double X(int v) => Knob + (ActualWidth - 2 * Knob) * v / 100.0;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, mid = ActualHeight / 2, track = Theme.S(4);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, ActualHeight));
        dc.DrawRoundedRectangle(p.WashBrush(0.15), null, new Rect(Knob, mid - track / 2, Math.Max(w - 2 * Knob, 0), track), track / 2, track / 2);
        dc.DrawRoundedRectangle(Theme.Brush(Theme.Accent), null, new Rect(Knob, mid - track / 2, Math.Max(X(value) - Knob, 0), track), track / 2, track / 2);
        dc.DrawEllipse(p.Dark ? Brushes.White : p.TextBrush, null, new Point(X(value), mid), Knob, Knob);
    }

    void Apply(double x)
    {
        int v = (int)Math.Round(Math.Clamp((x - Knob) / Math.Max(ActualWidth - 2 * Knob, 1), 0, 1) * 100);
        if (v == value) return;
        Value = v;
        Changed?.Invoke(v);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { e.Handled = true; dragging = true; CaptureMouse(); Apply(e.GetPosition(this).X); }
    protected override void OnMouseMove(MouseEventArgs e) { if (dragging) Apply(e.GetPosition(this).X); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { e.Handled = true; dragging = false; ReleaseMouseCapture(); }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        Value = value + Math.Sign(e.Delta) * 5;
        Changed?.Invoke(value);
    }
}

/// The preview's timeline: click or drag to seek; while trimming, two handles mark the part to keep.
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

    readonly PanePalette p;
    public TimelineBar(PanePalette? palette = null) { p = palette ?? PanePalette.Overlay; Cursor = Cursors.Hand; }

    public void BeginTrim() { trimming = true; TrimStart = 0; TrimEnd = duration; InvalidateVisual(); }
    public void EndTrim() { trimming = false; InvalidateVisual(); }

    double X(double t) => duration <= 0 ? 0 : Math.Clamp(t / duration, 0, 1) * ActualWidth;
    double T(double x) => duration <= 0 ? 0 : Math.Clamp(x / Math.Max(ActualWidth, 1), 0, 1) * duration;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, mid = h / 2, track = Theme.S(4);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // hit area
        dc.DrawRoundedRectangle(p.WashBrush(0.15), null, new Rect(0, mid - track / 2, w, track), track / 2, track / 2);
        dc.DrawRoundedRectangle(p.WashBrush(0.7), null, new Rect(0, mid - track / 2, X(position), track), track / 2, track / 2);
        if (trimming)
        {
            // The part kept is a thicker bar in the accent color; the parts cut fade back. Each end has a handle
            // with a grip, and a thin playhead shows where the preview is.
            double a = X(TrimStart), b = X(TrimEnd), kept = Theme.S(6), hw = Theme.S(9);
            var accent = Theme.Brush(Theme.Accent);
            dc.DrawRoundedRectangle(p.WashBrush(0.08), null, new Rect(0, mid - track / 2, w, track), track / 2, track / 2);
            dc.DrawRectangle(Theme.Brush(Theme.Accent, 0.85), null, new Rect(a, mid - kept / 2, Math.Max(b - a, 0), kept));
            double px = X(Math.Clamp(position, TrimStart, TrimEnd));
            dc.DrawRectangle(p.Dark ? Brushes.White : p.TextBrush, null, new Rect(px - Theme.S(1), mid - kept, Theme.S(2), kept * 2));
            var grip = new Pen(Theme.Brush(Colors.Black, 0.45), Theme.S(1));
            foreach (var x in new[] { a, b })
            {
                var r = new Rect(Math.Clamp(x - hw / 2, 0, Math.Max(w - hw, 0)), 0, hw, h);
                dc.DrawRoundedRectangle(accent, new Pen(Theme.Brush(Colors.Black, 0.35), 1), r, Theme.S(3), Theme.S(3));
                double cx = r.X + r.Width / 2;
                for (int i = -1; i <= 1; i += 2)
                    dc.DrawLine(grip, new Point(cx + i * Theme.S(1.5), mid - Theme.S(4)), new Point(cx + i * Theme.S(1.5), mid + Theme.S(4)));
            }
        }
        else
        {
            dc.DrawEllipse(p.TextBrush, null, new Point(X(position), mid), Theme.S(6), Theme.S(6));
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
