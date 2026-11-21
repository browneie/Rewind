using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Rewind;

/// The pages of Settings: Console lists them, Sidebar groups them into tabs, the window shows them all.
enum SettingsPage { Capture, Video, Audio, Replay, Saving, Overlay, Hotkeys, General }

/// Sidebar's tabs; each holds one or more pages.
enum SettingsTab { Recording, Replay, Overlay, Hotkeys, General }

static class SettingsPages
{
    public static readonly SettingsPage[] All = Enum.GetValues<SettingsPage>();
    public static readonly SettingsTab[] Tabs = Enum.GetValues<SettingsTab>();

    public static string Title(this SettingsPage p) => p switch
    {
        SettingsPage.Replay => "Instant Replay",
        _ => p.ToString(),
    };

    /// Current value in a few characters, for Console's list of pages.
    public static string Summary(this SettingsPage p)
    {
        var s = Settings.Shared;
        return p switch
        {
            SettingsPage.Capture => s.CaptureAppId.Length == 0 ? Displays.Connected(s.DisplayId)?.Short ?? "Display" : s.CaptureAppName,
            SettingsPage.Video => $"{s.Fps} fps · {s.Codec.Short()}",
            SettingsPage.Audio => s.SystemAudio ? (s.Microphone ? "System + mic" : "System") : (s.Microphone ? "Mic" : "Off"),
            SettingsPage.Replay => Format.Duration(s.ReplaySeconds),
            SettingsPage.Saving => s.AutoCleanup ? $"{s.CleanupDays} days" : "Keep all",
            SettingsPage.Overlay => s.OverlayLayout.Name(),
            SettingsPage.Hotkeys => s.Hotkey(HotkeyAction.ToggleOverlay) is { IsSet: true } h ? h.Display : "—",
            _ => "v" + Updater.CurrentVersion,
        };
    }

    public static string Title(this SettingsTab t) => t.ToString();

    /// Current values in a few characters, for the list of categories.
    public static string Summary(this SettingsTab t)
    {
        var s = Settings.Shared;
        return t switch
        {
            SettingsTab.Recording => $"{s.Fps} fps · {s.Codec.Short()}",
            SettingsTab.Replay => SettingsPage.Replay.Summary(),
            SettingsTab.Overlay => $"{s.OverlayLayout.Name()} · {s.OverlayAppearance.Short()}",
            SettingsTab.Hotkeys => SettingsPage.Hotkeys.Summary(),
            _ => SettingsPage.General.Summary(),
        };
    }

    public static SettingsPage[] Pages(this SettingsTab t) => t switch
    {
        SettingsTab.Recording => new[] { SettingsPage.Capture, SettingsPage.Video, SettingsPage.Audio },
        SettingsTab.Replay => new[] { SettingsPage.Replay, SettingsPage.Saving },
        SettingsTab.Overlay => new[] { SettingsPage.Overlay },
        SettingsTab.Hotkeys => new[] { SettingsPage.Hotkeys },
        _ => new[] { SettingsPage.General },
    };

    public static SettingsTab Holding(SettingsPage p) => Tabs.First(t => t.Pages().Contains(p));
}

/// The separate Settings window (all pages, scrolling). The overlay shows the same pages itself.
sealed class SettingsWindow : Window
{
    static SettingsWindow? shared;

    public static void ShowShared()
    {
        if (shared == null)
        {
            shared = new SettingsWindow();
            shared.Closed += (_, _) => shared = null;
            shared.Show();
        }
        if (shared.WindowState == WindowState.Minimized) shared.WindowState = WindowState.Normal;
        shared.Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(shared).Handle);
    }

    public static FrameworkElement CreateForPreview()
    {
        var w = new SettingsWindow { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
        w.Show();
        w.UpdateLayout();
        var page = w.content;
        ((ScrollViewer)w.Content).Content = null;
        var bg = w.TryFindResource("ApplicationBackgroundBrush") as Brush ?? SystemColors.WindowBrush;
        var host = new Border { Background = bg, Child = page, Width = w.ActualWidth, Resources = w.Resources };
        foreach (var d in System.Windows.Application.Current.Resources.MergedDictionaries) host.Resources.MergedDictionaries.Add(d);
        w.Close();
        TextElement.SetForeground(host, w.TryFindResource("TextFillColorPrimaryBrush") as Brush ?? SystemColors.ControlTextBrush);
        return host;
    }

    readonly SettingsContent content;

    SettingsWindow()
    {
        var s = Settings.Shared;
        Title = $"Rewind Settings · v{Updater.CurrentVersion}";
        Width = 580;
        Height = 760;
        MinWidth = 480;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        ThemeMode = ThemeMode.System;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/Rewind.ico")); } catch { }

        content = new SettingsContent(SettingsPages.All, inOverlay: false) { Margin = new Thickness(20, 12, 20, 24) };
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        Deactivated += (_, _) => content.CancelRecording();

    }
}

/// Settings controls for some pages (or all of them), built in code. Used by the Settings window and by the
/// overlay, which adds Windows' Fluent styles around it with `Themed`.
sealed class SettingsContent : StackPanel
{
    readonly Settings s = Settings.Shared;
    readonly Controller c = Controller.Shared;
    readonly StackPanel root;
    readonly List<Action> refreshers = new();
    readonly HashSet<SettingsPage> pages;
    readonly bool inOverlay;
    bool building;

    ComboBox appPicker = new(), fallbackPicker = new();
    TextBlock captureFooter = new(), audioFooter = new(), estimate = new(), capturingNow = new();
    CheckBox mixMic = new();
    FrameworkElement capturingRow = new Border();
    HotkeyField? recordingField;

    /// A hotkey field is waiting for keys, so the overlay shouldn't act on them (Esc included).
    public static bool IsRecording { get; private set; }

    /// `groupTitles` puts each page's name above its sections (Sidebar tabs that hold several pages).
    public SettingsContent(IEnumerable<SettingsPage> pages, bool inOverlay, bool groupTitles = false)
    {
        this.pages = pages.ToHashSet();
        this.inOverlay = inOverlay;
        this.groupTitles = groupTitles;
        root = this;
        Build();

        PreviewKeyDown += OnPreviewKeyDown;
        // Screens plugged in or out show up in the menus right away.
        EventHandler displaysChanged = (_, _) => Dispatcher.BeginInvoke(() => { FillApps(); FillFallback(); Refresh(); });
        bool attached = false;
        Loaded += (_, _) =>
        {
            if (attached) return;
            attached = true;
            s.Changed += Refresh;
            c.StateChanged += Refresh;
            SystemEvents.DisplaySettingsChanged += displaysChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (!attached) return;
            attached = false;
            CancelRecording();
            s.Changed -= Refresh;
            c.StateChanged -= Refresh;
            SystemEvents.DisplaySettingsChanged -= displaysChanged;
        };
        Refresh();
    }

    readonly bool groupTitles;
    SettingsPage? titled;

    bool Has(SettingsPage p)
    {
        if (!pages.Contains(p)) return false;
        if (groupTitles && titled != p)
        {
            titled = p;
            root.Children.Add(new TextBlock { Text = p.Title(), FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, root.Children.Count == 0 ? 8 : 28, 0, 0) });
        }
        return true;
    }

    public void CancelRecording() => recordingField?.Cancel();

    /// Adds Windows' Fluent control styles (light or dark) to an element outside the Settings window.
    public static void Themed(FrameworkElement host, bool dark)
    {
        try
        {
            host.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/PresentationFramework.Fluent;component/Themes/Fluent.{(dark ? "Dark" : "Light")}.xaml"),
            });
        }
        catch (Exception e) { Log.Error("fluent theme", e); }
        host.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
    }

    void Build()
    {
        building = true;

        current = SettingsPage.Capture;
        appPicker = new ComboBox { MinWidth = 240 };
        appPicker.DropDownOpened += (_, _) => FillApps();
        appPicker.SelectionChanged += (_, _) =>
        {
            if (building || appPicker.SelectedItem is not ComboBoxItem { Tag: CaptureChoice choice }) return;
            if (choice.DisplayId != null)
            {
                s.CaptureAppId = "";
                s.CaptureAppName = "";
                s.DisplayId = choice.DisplayId;
                return;
            }
            s.CaptureAppName = choice.Name;
            s.CaptureAppId = choice.Key;
        };
        FillApps();
        captureFooter = Footer("");
        Section("What to capture", new[] { Row("Capture", appPicker) }, captureFooter);

        var gameToggle = Toggle("Game capture (experimental)", s.GameCapture, v => s.GameCapture = v);
        gameToggle.IsEnabled = ObsGameCapture.Available;
        Section("Game capture", new[] { gameToggle }, Footer(ObsGameCapture.Available
            ? "When you capture one app, Rewind takes each frame straight from the game with OBS Studio's game capture hook, " +
              "like OBS's Game Capture: exclusive fullscreen works, and nothing on top of the game is recorded. Rewind keeps " +
              "recording the window until the first game frame arrives. Games with kernel anti-cheat (Valorant, Fortnite, CS2, " +
              "Easy Anti-Cheat and BattlEye games and more) are never hooked. Server screenshare checks will see OBS's hook in the game."
            : "Game capture uses OBS Studio's game capture hook. Install OBS Studio (winget install OBSProject.OBSStudio) to turn it on" +
              (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64 ? "." : " (it also needs the x64 version of Rewind).")));

        current = SettingsPage.Video;
        fallbackPicker = new ComboBox { MinWidth = 240 };
        fallbackPicker.DropDownOpened += (_, _) => FillFallback();
        fallbackPicker.SelectionChanged += (_, _) =>
        {
            if (!building && fallbackPicker.SelectedItem is ComboBoxItem { Tag: string id }) s.DisplayId = id;
        };
        FillFallback();
        var fallbackRow = Row("Fallback display", fallbackPicker);
        refreshers.Add(() => fallbackRow.Visibility = s.CaptureAppId.Length > 0 ? Visibility.Visible : Visibility.Collapsed);
        var bitrateRow = Row("Bitrate", BitrateBox());
        var videoFooter = Footer("");
        refreshers.Add(() =>
        {
            bitrateRow.Visibility = s.Quality == Quality.Custom ? Visibility.Visible : Visibility.Collapsed;
            var notes = new List<string>();
            int hz = Displays.RefreshRate(s.DisplayId);
            if (hz > 0 && s.Fps > hz)
                notes.Add($"This screen only refreshes {hz} times a second, so Windows can't capture more than {hz} fps. Above that, clips just get bigger, not smoother.");
            if (s.Fps >= 120)
            {
                double x = s.Fps / 60.0;
                string times = x == Math.Round(x) ? $"{(int)x}×" : $"{x:0.0}×";
                notes.Add($"At {s.Fps} fps Windows captures and converts {times} as many frames as at 60 fps, which uses more of the GPU your game is using. 60 fps is easier on frame rates.");
            }
            if (s.Codec == VideoCodec.Av1)
                notes.Add(VideoEncoder.HasHardwareEncoder(VideoCodec.Av1)
                    ? "AV1 clips play in browsers, Discord and VLC. Windows' own players need the free AV1 Video Extension, and older phones can't play them."
                    : "This PC's graphics card can't record AV1, so recording won't start. Choose H.264 or HEVC.");
            videoFooter.Text = string.Join("\n\n", notes);
            videoFooter.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        // AV1 is always listed; the note below says when this PC's graphics card can't encode it.
        var codecs = Enum.GetValues<VideoCodec>();
        Section("Video", new[]
        {
            fallbackRow,
            Row("Resolution", Combo(Enum.GetValues<Resolution>().Select(r => (r.Label(), r)), s.Resolution, v => s.Resolution = v)),
            Row("Frame rate", FrameRateBox()),
            Row("Quality", Combo(Enum.GetValues<Quality>().Select(q => (q.Label(), q)), s.Quality, v => s.Quality = v)),
            bitrateRow,
            Row("Codec", Combo(codecs.Select(v => (v == VideoCodec.Av1 && !VideoEncoder.HasHardwareEncoder(VideoCodec.Av1) ? "AV1 (not supported by this graphics card)" : v.Label(), v)), s.Codec, v => s.Codec = v)),
            Toggle("Show mouse cursor", s.ShowCursor, v => s.ShowCursor = v),
        }, videoFooter);

        current = SettingsPage.Audio;
        mixMic = new CheckBox { Content = "Mix microphone into the main track", IsChecked = s.MixMicrophone };
        mixMic.Click += (_, _) => s.MixMicrophone = mixMic.IsChecked == true;
        audioFooter = Footer("");
        Section("Audio", new[]
        {
            Toggle("Record system audio", s.SystemAudio, v => s.SystemAudio = v),
            Toggle("Record microphone", s.Microphone, v => s.Microphone = v),
            Pad(mixMic),
        }, audioFooter);

        current = SettingsPage.Replay;
        var lengths = new[] { 15, 30, 60, 120, 180, 300, 600, 900, 1200 }
            .Select(n => (n < 60 ? $"{n} seconds" : $"{n / 60} minute{(n == 60 ? "" : "s")}", n)).ToList();
        if (!lengths.Any(l => l.n == s.ReplaySeconds)) lengths.Add(($"{s.ReplaySeconds} seconds", s.ReplaySeconds));
        estimate = Footer("");
        Section("Instant Replay", new[]
        {
            Row("Replay length", Combo(lengths, s.ReplaySeconds, v => s.ReplaySeconds = v)),
            Toggle("Turn on Instant Replay when Rewind starts", s.StartReplayOnLaunch, v => s.StartReplayOnLaunch = v),
        }, estimate);

        current = SettingsPage.Saving;
        var folder = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280, Margin = new Thickness(0, 0, 10, 0) };
        folder.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        refreshers.Add(() =>
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            folder.Text = s.OutputDirectory.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + s.OutputDirectory[home.Length..] : s.OutputDirectory;
            folder.ToolTip = s.OutputDirectory;
        });
        var choose = new Button { Content = "Choose…" };
        choose.Click += (_, _) => ChooseFolder();
        var saveRow = new StackPanel { Orientation = Orientation.Horizontal };
        saveRow.Children.Add(folder);
        saveRow.Children.Add(choose);
        var keepFor = Combo(Cleanup.DayOptions.Select(n => (n == 1 ? "1 day" : $"{n} days", n)), s.CleanupDays, v => s.CleanupDays = v);
        var saveFooter = Footer("");
        refreshers.Add(() =>
        {
            keepFor.IsEnabled = s.AutoCleanup;
            saveFooter.Text = s.AutoCleanup
                ? $"Replays, recordings and screenshots older than {s.CleanupDays} {(s.CleanupDays == 1 ? "day" : "days")} are moved to the Recycle Bin, so you can still get them back. Other files in the folder are never touched."
                : "Clips are kept until you delete them.";
        });
        Section("Saving", new[]
        {
            Row("Save to", saveRow),
            Toggle("Delete old clips automatically", s.AutoCleanup, v => s.AutoCleanup = v),
            Row("Keep clips for", keepFor),
        }, saveFooter);

        var shareLimit = Combo(ClipSharer.PresetsMB.Select(n => (ClipSharer.Label(n), n)).Append(("Custom", -1)),
            s.ShareLimitCustom ? -1 : s.ShareLimitMB, v =>
            {
                if (v == -1)
                {
                    s.ShareLimitCustom = true;
                    if (s.ShareLimitMB == 0) s.ShareLimitMB = 25;
                }
                else
                {
                    s.ShareLimitCustom = false;
                    s.ShareLimitMB = v;
                }
            });
        var shareCustomRow = Row("Custom limit", ShareLimitBox());
        refreshers.Add(() => shareCustomRow.Visibility = s.ShareLimitCustom ? Visibility.Visible : Visibility.Collapsed);
        Section("Copy", new[]
        {
            Row("Size limit for Copy", shareLimit),
            shareCustomRow,
        }, Footer("Copy puts a clip on the clipboard to paste into Discord and the like. Bigger clips are shrunk to fit first; set this to your upload limit."));

        current = SettingsPage.Overlay;
        BuildOverlaySection();

        current = SettingsPage.Hotkeys;
        var hotkeyRows = HotkeyActions.All.Select(a => Row(a.Label(), new HotkeyField(this, a))).ToList();
        var reset = new Button { Content = "Reset to defaults", HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) => s.ResetHotkeys();
        hotkeyRows.Add(Pad(reset));
        Section("Hotkeys", hotkeyRows.ToArray(), Footer("Click a shortcut, then press the new keys. Press Esc to remove it, or click it again to keep it. " +
            "Hotkeys work while a game has focus. If one is taken by another app (the NVIDIA or AMD overlay uses some of the same keys), pick a different one."));

        current = SettingsPage.General;
        capturingNow = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 300, TextAlignment = TextAlignment.Right };
        capturingNow.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        capturingRow = Row("Capturing now", capturingNow);
        Section("General", new[]
        {
            Toggle("Open Rewind when Windows starts", LoginItem.Enabled, v =>
            {
                try { LoginItem.Set(v); }
                catch (Exception e) { Toast.Error("Couldn't change the startup setting", e); }
            }),
            capturingRow,
        }, Footer("Settings are kept in %APPDATA%\\Rewind."));

        BuildUpdatesSection();

        var uninstall = new Button { Content = "Uninstall Rewind…", HorizontalAlignment = HorizontalAlignment.Left };
        uninstall.SetResourceReference(Control.ForegroundProperty, "SystemFillColorCriticalBrush");
        uninstall.Click += (_, _) => Uninstaller.ConfirmAndRun(Window.GetWindow(this));
        Section("Uninstall", new[] { Pad(uninstall) },
            Footer("Permanently deletes Rewind, its settings and logs, and its \"Open Rewind when Windows starts\" entry. Downloaded Rewind installers go to the Recycle Bin. Your clips are kept."));

        building = false;
    }

    /// Version, automatic checks and the Check for Updates button.
    void BuildUpdatesSection()
    {
        var u = Updater.Shared;
        var version = new TextBlock { Text = Updater.CurrentVersion };
        version.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var action = new Button { Content = "Check for Updates" };
        var whatsNew = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var link = new Hyperlink(new Run("What's new"));
        link.Click += (_, _) => { if (u.Offer?.PageUrl is { } page) Process.Start(new ProcessStartInfo(page.ToString()) { UseShellExecute = true }); };
        whatsNew.Inlines.Add(link);
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 260, TextAlignment = TextAlignment.Right };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(action);
        buttons.Children.Add(whatsNew);
        var row = new DockPanel { Margin = new Thickness(16, 10, 12, 10), LastChildFill = false };
        DockPanel.SetDock(buttons, Dock.Left);
        DockPanel.SetDock(status, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(status);
        action.Click += (_, _) =>
        {
            if (u.State == Updater.Phase.Available && u.Offer is { } r) u.Install(r);
            else u.Check(userInitiated: true);
        };

        void Show()
        {
            var available = u.State == Updater.Phase.Available && u.Offer != null;
            action.Content = available ? $"Install Rewind {u.Offer!.Version}" : "Check for Updates";
            action.IsEnabled = u.State is not (Updater.Phase.Checking or Updater.Phase.Downloading or Updater.Phase.Installing);
            if (available) action.SetResourceReference(StyleProperty, "AccentButtonStyle"); else action.ClearValue(StyleProperty);
            whatsNew.Visibility = available && u.Offer!.PageUrl != null ? Visibility.Visible : Visibility.Collapsed;
            status.Text = u.State switch
            {
                Updater.Phase.Checking => "Checking…",
                Updater.Phase.UpToDate => "Rewind is up to date",
                Updater.Phase.Available => $"Version {u.Offer?.Version} is available",
                Updater.Phase.Downloading => "Downloading…",
                Updater.Phase.Installing => "Installing…",
                Updater.Phase.Failed => u.Message,
                _ => "",
            };
            if (u.State == Updater.Phase.Failed) status.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
            else status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        }
        Action changed = () => Dispatcher.BeginInvoke(Show);
        u.Changed += changed;
        Unloaded += (_, _) => u.Changed -= changed;
        Loaded += (_, _) => { u.Changed -= changed; u.Changed += changed; Show(); };
        Show();

        Section("Updates", new[]
        {
            Row("Version", version),
            Toggle("Check for updates when Rewind starts", s.CheckForUpdates, v => s.CheckForUpdates = v),
            row,
        }, Footer("Updates come from Rewind's GitHub releases. Each installer is checked against Rewind's signing certificate before it runs, then Rewind reopens by itself."));
    }

    void Refresh()
    {
        building = true;
        captureFooter.Text = s.CaptureAppId.Length == 0
            ? $"Records everything on {Displays.Connected(s.DisplayId)?.Short ?? "the display"}{(s.SystemAudio ? ", with all system audio" : "")}."
            : $"Records only {s.CaptureAppName}'s window and sound. While it isn't running, Rewind records the fallback display below and switches over when it opens.";

        mixMic.IsEnabled = s.Microphone && s.SystemAudio;
        audioFooter.Text = !(s.Microphone && s.SystemAudio) ? "" : s.MixMicrophone
            ? "Game sound and your voice share one track, so clips play correctly everywhere (VLC, browsers, Discord, phones)."
            : "Game sound and your voice are saved as separate tracks, handy for editing. Some players only play one of them.";
        audioFooter.Visibility = audioFooter.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var size = CaptureMath.OutputSize(CaptureMath.NativePixelSize(s.DisplayId), s.Resolution, s.Codec);
        int bitrate = CaptureMath.Bitrate(size, s.Fps, s.Quality, s.Codec, s.CustomMbps);
        if (s.Quality != Quality.Custom) bitrate = (int)(bitrate * EncoderTuning.For(s.Codec).BitrateFactor);
        int tracks = (s.SystemAudio ? 1 : 0) + (s.Microphone ? 1 : 0);
        if (tracks > 1 && s.MixMicrophone) tracks = 1;
        double bytes = bitrate / 8.0 * s.ReplaySeconds + tracks * Audio.Bitrate / 8.0 * s.ReplaySeconds;
        estimate.Text = $"{size.W}×{size.H} at ≈{bitrate / 1_000_000} Mbps. The replay buffer uses about {FormatBytes(bytes)} of RAM.";

        capturingRow.Visibility = c.IsCapturing ? Visibility.Visible : Visibility.Collapsed;
        capturingNow.Text = c.CaptureInfo;
        foreach (var r in refreshers) r();
        building = false;
    }

    static string FormatBytes(double b) => b >= 1e9 ? $"{b / 1e9:F1} GB" : $"{b / 1e6:F0} MB";

    FrameworkElement FrameRateBox()
    {
        var box = new ComboBox { IsEditable = true, Width = 110, ToolTip = $"Type any frame rate from {Settings.MinFps} to {Settings.MaxFps}" };
        foreach (var f in new[] { 24, 30, 48, 60, 90, 120, 144, 165, 240 }) box.Items.Add(f.ToString());
        box.Text = s.Fps.ToString();
        void Commit()
        {
            var digits = new string((box.Text ?? "").Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int v) && v > 0) s.Fps = v;
            box.Text = s.Fps.ToString();
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        box.SelectionChanged += (_, _) =>
        {
            if (!building && box.SelectedItem is string pick) { box.Text = pick; Commit(); }
        };
        refreshers.Add(() => { if (!box.IsKeyboardFocusWithin) box.Text = s.Fps.ToString(); });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(box);
        row.Children.Add(new TextBlock { Text = "fps", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        return row;
    }


    FrameworkElement BitrateBox()
    {
        // Wide enough for "150" next to the ✕ Windows adds inside a focused text box.
        var box = new TextBox { Width = 92, TextAlignment = TextAlignment.Right, MaxLength = 3, Text = s.CustomMbps.ToString() };
        void Commit()
        {
            if (int.TryParse(new string(box.Text.Where(char.IsDigit).ToArray()), out int v)) s.CustomMbps = v;
            box.Text = s.CustomMbps.ToString();
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        refreshers.Add(() => { if (!box.IsKeyboardFocusWithin) box.Text = s.CustomMbps.ToString(); });
        Button Step(string glyph, int by, string tip)
        {
            var b = new Button { Content = new TextBlock { Text = glyph, FontFamily = Theme.Symbols, FontSize = 10 }, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(4, 0, 0, 0), ToolTip = tip };
            b.Click += (_, _) => s.CustomMbps += by;
            return b;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(box);
        row.Children.Add(Step("\ue738", -1, "1 Mbps less"));
        row.Children.Add(Step("\ue710", 1, "1 Mbps more"));
        row.Children.Add(new TextBlock { Text = "Mbps", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = $"{CaptureMath.MinMbps} to {CaptureMath.MaxMbps} Mbps" });
        return row;
    }

    FrameworkElement ShareLimitBox()
    {
        var box = new TextBox { Width = 92, TextAlignment = TextAlignment.Right, MaxLength = 5, Text = s.ShareLimitMB.ToString() };
        void Commit()
        {
            if (int.TryParse(new string(box.Text.Where(char.IsDigit).ToArray()), out int v)) s.ShareLimitMB = Math.Max(v, 1);
            box.Text = s.ShareLimitMB.ToString();
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        refreshers.Add(() => { if (!box.IsKeyboardFocusWithin) box.Text = s.ShareLimitMB.ToString(); });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(box);
        row.Children.Add(new TextBlock { Text = "MB", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            ToolTip = $"1 to {Settings.MaxShareLimitMB} MB" });
        return row;
    }

    void BuildOverlaySection()
    {
        var accent = new AccentColorButton(() => s.AccentColor, v => s.AccentColor = v);
        var presets = new StackPanel { Orientation = Orientation.Horizontal };
        void ShowPresets()
        {
            presets.Children.Clear();
            foreach (var (name, hex) in Theme.AccentPresets)
            {
                bool chosen = s.AccentColor == hex;
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 18, Height = 18, Margin = new Thickness(7, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = name,
                    Fill = new SolidColorBrush(Theme.Hex(hex)), StrokeThickness = chosen ? 2 : 1,
                    Stroke = new SolidColorBrush(chosen ? Color.FromArgb(230, 128, 128, 128) : Color.FromArgb(50, 128, 128, 128)),
                };
                dot.MouseLeftButtonUp += (_, _) => s.AccentColor = hex;
                presets.Children.Add(dot);
            }
        }
        refreshers.Add(() => { accent.Show(s.AccentColor); ShowPresets(); });

        var dim = new Slider { Minimum = 0, Maximum = 0.9, Width = 180, Value = s.OverlayDim, SmallChange = 0.05, LargeChange = 0.1 };
        dim.ValueChanged += (_, _) => { if (!building) s.OverlayDim = dim.Value; };
        var size = new Slider { Minimum = Theme.MinPlayerSize, Maximum = Theme.MaxPlayerSize, Width = 160, Value = s.PlayerSize, TickFrequency = 10, IsSnapToTickEnabled = true, SmallChange = 10, LargeChange = 50 };
        size.ValueChanged += (_, _) => { if (!building) s.PlayerSize = size.Value; };
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = "Width of the player in the Split layout and Classic's Gallery" };
        sizeRow.Children.Add(new TextBlock { Text = "\ue7f4", FontFamily = Theme.Symbols, FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        sizeRow.Children.Add(size);
        sizeRow.Children.Add(new TextBlock { Text = "\ue7f4", FontFamily = Theme.Symbols, FontSize = 18, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        var sizeLabel = new TextBlock { Text = "Player size", VerticalAlignment = VerticalAlignment.Center };
        refreshers.Add(() =>
        {
            if (!dim.IsMouseCaptureWithin) dim.Value = s.OverlayDim;
            if (!size.IsMouseCaptureWithin) size.Value = s.PlayerSize;
            sizeRow.IsEnabled = s.OverlayLayout is OverlayLayout.Classic or OverlayLayout.Panes;
            sizeLabel.Opacity = sizeRow.IsEnabled ? 1 : 0.5;
        });
        var appearanceRow = Row("Appearance", Combo(Enum.GetValues<OverlayAppearance>().Select(a => (a.Label(), a)), s.OverlayAppearance, v => s.OverlayAppearance = v));
        var preview = new Button { Content = "Preview overlay", HorizontalAlignment = HorizontalAlignment.Left };
        preview.Click += (_, _) => OverlayController.Show();

        var rows = new List<FrameworkElement>
        {
            Row("Layout", Combo(Labels.Layouts.Select(l => (l.Label(), l)), s.OverlayLayout, v => s.OverlayLayout = v)),
            appearanceRow,
            Row("Accent color", accent),
            Row("Presets", presets),
            Row("Dim the game", dim),
            Row(sizeLabel, sizeRow),
            Toggle("Show hotkeys in the overlay", s.ShowHotkeys, v => s.ShowHotkeys = v),
        };
        if (!inOverlay) rows.Add(Pad(preview));
        Section("Overlay", rows.ToArray(), Footer("The accent color is used for Instant Replay, highlights and notifications. Click the swatch for the color wheel, or type a hex code. " +
                  (inOverlay ? "A new accent color shows when you go back to your clips." : "Changes apply the next time the overlay opens.")));
    }

    /// A display (DisplayId set) or an app (Key set).
    sealed record CaptureChoice(string Key, string Name, string? DisplayId);

    void FillApps()
    {
        bool was = building;
        building = true;
        appPicker.Items.Clear();
        foreach (var d in Displays.All())
            appPicker.Items.Add(new ComboBoxItem { Content = IconLabel(null, d.Label), Tag = new CaptureChoice("", "", d.Id) });
        var chosenDisplay = s.CaptureAppId.Length > 0 ? null : Displays.Connected(s.DisplayId)?.Id;
        if (s.CaptureAppId.Length == 0 && chosenDisplay == null)
        {
            chosenDisplay = s.DisplayId;
            appPicker.Items.Add(new ComboBoxItem { Content = IconLabel(null, "Disconnected display"), Tag = new CaptureChoice("", "", s.DisplayId) });
        }
        appPicker.Items.Add(new Separator());
        var apps = AppTarget.Pickable();
        foreach (var a in apps)
            appPicker.Items.Add(new ComboBoxItem { Content = IconLabel(a.ExePath, a.Name), Tag = new CaptureChoice(a.Key, a.Name, null) });
        if (s.CaptureAppId.Length > 0 && apps.All(a => a.Key != s.CaptureAppId))
        {
            appPicker.Items.Add(new Separator());
            appPicker.Items.Add(new ComboBoxItem { Content = IconLabel(null, $"{s.CaptureAppName} (not running)"), Tag = new CaptureChoice(s.CaptureAppId, s.CaptureAppName, null) });
        }
        appPicker.SelectedItem = appPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is CaptureChoice c &&
            (chosenDisplay != null ? c.DisplayId == chosenDisplay : c.DisplayId == null && c.Key == s.CaptureAppId));
        building = was;
    }

    void FillFallback()
    {
        bool was = building;
        building = true;
        fallbackPicker.Items.Clear();
        foreach (var d in Displays.All()) fallbackPicker.Items.Add(new ComboBoxItem { Content = d.Label, Tag = d.Id });
        var current = Displays.Find(s.DisplayId)?.Id;
        fallbackPicker.SelectedItem = fallbackPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current);
        building = was;
    }

    static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    static FrameworkElement IconLabel(string? exe, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        ImageSource? img = null;
        if (exe != null && !IconCache.TryGetValue(exe, out img))
        {
            try
            {
                using var ic = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ic != null)
                {
                    img = Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    img.Freeze();
                }
            }
            catch { }
            IconCache[exe] = img;
        }
        if (img != null) row.Children.Add(new Image { Source = img, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) });
        else if (exe == null) row.Children.Add(new TextBlock { Text = "", FontFamily = Theme.Symbols, FontSize = 14, Margin = new Thickness(1, 1, 9, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    void ChooseFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose where to save clips", InitialDirectory = s.OutputDirectory };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) s.OutputDirectory = dlg.FolderName;
    }


    sealed class HotkeyField : Button
    {
        readonly SettingsContent owner;
        readonly HotkeyAction action;
        bool recording;

        public HotkeyField(SettingsContent owner, HotkeyAction action)
        {
            this.owner = owner; this.action = action;
            SetResourceReference(StyleProperty, typeof(Button)); // subclasses don't get the theme's Button style
            MinWidth = 150;
            Click += (_, _) => { if (recording) Cancel(); else Start(); };
            owner.refreshers.Add(() => { if (!recording) ShowCurrent(); });
            ShowCurrent();
        }

        void ShowCurrent()
        {
            var hk = owner.s.Hotkey(action);
            Content = hk.Display;
            Opacity = hk.IsSet ? 1 : 0.6;
        }

        public void Clear()
        {
            recording = false;
            if (owner.recordingField == this) owner.recordingField = null;
            owner.s.ClearHotkey(action);
            ShowCurrent();
            HotkeyManager.Shared.Resume();
        }

        void Start()
        {
            owner.recordingField?.Cancel();
            recording = true;
            owner.recordingField = this;
            Content = "Press a shortcut…";
            Opacity = 1;
            HotkeyManager.Shared.Suspend();
        }

        public void Cancel()
        {
            if (!recording) return;
            recording = false;
            if (owner.recordingField == this) owner.recordingField = null;
            ShowCurrent();
            HotkeyManager.Shared.Resume();
        }

        public void Take(Key key, ModifierKeys mods)
        {
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            uint m = 0;
            if (mods.HasFlag(ModifierKeys.Control)) m |= Hotkey.Ctrl;
            if (mods.HasFlag(ModifierKeys.Alt)) m |= Hotkey.Alt;
            if (mods.HasFlag(ModifierKeys.Shift)) m |= Hotkey.Shift;
            if (mods.HasFlag(ModifierKeys.Windows)) m |= Hotkey.Win;
            if (m == 0 && !Hotkey.IsFunctionKey(vk)) { Native.MessageBeep(0x30); return; }
            var hk = Hotkey.Make(vk, m);
            recording = false;
            owner.recordingField = null;
            owner.s.SetHotkey(hk, action);
            ShowCurrent();
            HotkeyManager.Shared.Resume();
        }
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (recordingField == null) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key == Key.Escape) { recordingField.Clear(); return; }
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None)
            return;
        recordingField.Take(key, Keyboard.Modifiers);
    }


    SettingsPage current;

    void Section(string title, FrameworkElement[] rows, TextBlock? footer = null)
    {
        if (!Has(current)) return;
        // In the overlay the page already has this name above it.
        bool repeat = inOverlay && string.Equals(title, current.Title(), StringComparison.OrdinalIgnoreCase);
        var header = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(4, repeat ? 10 : 16, 0, 8) };
        if (!repeat) root.Children.Add(header);
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0)
            {
                var line = new Border { Height = 1, Margin = new Thickness(16, 0, 0, 0) };
                line.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
                var row = rows[i];
                line.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = row });
                stack.Children.Add(line);
            }
            stack.Children.Add(rows[i]);
        }
        var card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        root.Children.Add(card);
        if (footer != null) root.Children.Add(footer);
    }

    static TextBlock Footer(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6, 4, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    static FrameworkElement Row(string label, FrameworkElement control) =>
        Row(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, control);

    static FrameworkElement Row(TextBlock label, FrameworkElement control)
    {
        var g = new DockPanel { Margin = new Thickness(16, 8, 12, 8), LastChildFill = false, MinHeight = 32 };
        DockPanel.SetDock(label, Dock.Left);
        DockPanel.SetDock(control, Dock.Right);
        control.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 16, 0);
        g.Children.Add(label);
        g.Children.Add(control);
        return g;
    }

    static FrameworkElement Pad(FrameworkElement e)
    {
        e.Margin = new Thickness(16, 10, 12, 10);
        return e;
    }

    FrameworkElement Toggle(string label, bool value, Action<bool> set)
    {
        var cb = new CheckBox { Content = label, IsChecked = value };
        cb.Click += (_, _) => set(cb.IsChecked == true);
        return Pad(cb);
    }

    ComboBox Combo<T>(IEnumerable<(string label, T value)> items, T current, Action<T> set)
    {
        var box = new ComboBox { MinWidth = 220 };
        foreach (var (label, value) in items) box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, current)) ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
        box.SelectionChanged += (_, _) =>
        {
            if (!building && box.SelectedItem is ComboBoxItem { Tag: T v }) set(v);
        };
        return box;
    }
}

static class LoginItem
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Rewind";

    public static bool Enabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string v && Environment.ProcessPath is { } exe &&
                   v.Contains(exe, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(Name, throwOnMissingValue: false);
    }
}
