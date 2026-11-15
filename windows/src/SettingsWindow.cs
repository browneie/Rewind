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
        var page = w.root;
        ((ScrollViewer)w.Content).Content = null;
        var bg = w.TryFindResource("ApplicationBackgroundBrush") as Brush ?? SystemColors.WindowBrush;
        var host = new Border { Background = bg, Child = page, Width = w.ActualWidth, Resources = w.Resources };
        foreach (var d in System.Windows.Application.Current.Resources.MergedDictionaries) host.Resources.MergedDictionaries.Add(d);
        w.Close();
        TextElement.SetForeground(host, w.TryFindResource("TextFillColorPrimaryBrush") as Brush ?? SystemColors.ControlTextBrush);
        return host;
    }

    readonly Settings s = Settings.Shared;
    readonly Controller c = Controller.Shared;
    readonly StackPanel root = new() { Margin = new Thickness(20, 12, 20, 24) };
    readonly List<Action> refreshers = new();
    bool building;

    ComboBox appPicker = null!, fallbackPicker = null!;
    TextBlock captureFooter = null!, audioFooter = null!, estimate = null!, capturingNow = null!;
    CheckBox mixMic = null!;
    FrameworkElement capturingRow = null!;
    HotkeyField? recordingField;

    SettingsWindow()
    {
        Title = $"Rewind Settings · v{Updater.CurrentVersion}";
        Width = 580;
        Height = 760;
        MinWidth = 480;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        ThemeMode = ThemeMode.System;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/Rewind.ico")); } catch { }

        Build();
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        SourceInitialized += (_, _) => Native.ExcludeFromCapture(new WindowInteropHelper(this).Handle, !s.ShareRewindWindows);
        refreshers.Add(() => Native.ExcludeFromCapture(new WindowInteropHelper(this).Handle, !s.ShareRewindWindows));
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => recordingField?.Cancel();
        s.Changed += Refresh;
        c.StateChanged += Refresh;
        // Screens plugged in or out show up in the menus right away.
        EventHandler displaysChanged = (_, _) => Dispatcher.BeginInvoke(() => { FillApps(); FillFallback(); Refresh(); });
        SystemEvents.DisplaySettingsChanged += displaysChanged;
        Closed += (_, _) =>
        {
            recordingField?.Cancel();
            s.Changed -= Refresh;
            c.StateChanged -= Refresh;
            SystemEvents.DisplaySettingsChanged -= displaysChanged;
        };
        Refresh();
    }

    void Build()
    {
        building = true;

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
            if (s.Fps >= 120)
                notes.Add("120 fps makes Windows capture and convert twice as many frames as 60 fps, which uses more of the GPU your game is using. 60 fps is easier on frame rates.");
            if (s.Codec == VideoCodec.Av1)
                notes.Add(VideoEncoder.HasHardwareEncoder(VideoCodec.Av1)
                    ? "AV1 clips play in browsers, Discord and VLC. Windows' own players need the free AV1 Video Extension, and older phones can't play them."
                    : "This PC's graphics card can't record AV1, so recording won't start. Choose H.264 or HEVC.");
            videoFooter.Text = string.Join("\n\n", notes);
            videoFooter.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        // AV1 is offered only on graphics cards that can encode it (or kept visible if it was chosen on another card).
        var codecs = Enum.GetValues<VideoCodec>()
            .Where(v => v != VideoCodec.Av1 || s.Codec == VideoCodec.Av1 || VideoEncoder.HasHardwareEncoder(VideoCodec.Av1));
        Section("Video", new[]
        {
            fallbackRow,
            Row("Resolution", Combo(Enum.GetValues<Resolution>().Select(r => (r.Label(), r)), s.Resolution, v => s.Resolution = v)),
            Row("Frame rate", FrameRateBox()),
            Row("Quality", Combo(Enum.GetValues<Quality>().Select(q => (q.Label(), q)), s.Quality, v => s.Quality = v)),
            bitrateRow,
            Row("Codec", Combo(codecs.Select(v => (v.Label(), v)), s.Codec, v => s.Codec = v)),
            Toggle("Show mouse cursor", s.ShowCursor, v => s.ShowCursor = v),
        }, videoFooter);

        mixMic = new CheckBox { Content = "Mix microphone into the main track", IsChecked = s.MixMicrophone };
        mixMic.Click += (_, _) => s.MixMicrophone = mixMic.IsChecked == true;
        audioFooter = Footer("");
        Section("Audio", new[]
        {
            Toggle("Record system audio", s.SystemAudio, v => s.SystemAudio = v),
            Toggle("Record microphone", s.Microphone, v => s.Microphone = v),
            Pad(mixMic),
        }, audioFooter);

        var lengths = new[] { 15, 30, 60, 120, 180, 300, 600, 900, 1200 }
            .Select(n => (n < 60 ? $"{n} seconds" : $"{n / 60} minute{(n == 60 ? "" : "s")}", n)).ToList();
        if (!lengths.Any(l => l.n == s.ReplaySeconds)) lengths.Add(($"{s.ReplaySeconds} seconds", s.ReplaySeconds));
        estimate = Footer("");
        Section("Instant Replay", new[]
        {
            Row("Replay length", Combo(lengths, s.ReplaySeconds, v => s.ReplaySeconds = v)),
            Toggle("Turn on Instant Replay when Rewind starts", s.StartReplayOnLaunch, v => s.StartReplayOnLaunch = v),
        }, estimate);

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

        BuildOverlaySection();

        var hotkeyRows = HotkeyActions.All.Select(a => Row(a.Label(), new HotkeyField(this, a))).ToList();
        var reset = new Button { Content = "Reset to defaults", HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) => s.ResetHotkeys();
        hotkeyRows.Add(Pad(reset));
        Section("Hotkeys", hotkeyRows.ToArray(), Footer("Click a shortcut, then press the new keys. Press Esc to remove it, or click it again to keep it. " +
            "Hotkeys work while a game has focus. If one is taken by another app (the NVIDIA or AMD overlay uses some of the same keys), pick a different one."));

        Section("Screen sharing", new[]
        {
            Toggle("Let screen shares see the overlay and Settings", s.ShareRewindWindows, v => s.ShareRewindWindows = v),
        }, Footer("Friends watching your Discord, OBS or Teams screen share see the overlay and this window. " +
                  "Your own clips still leave the overlay out (the video holds while it's open). Notifications are never shared."));

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
        uninstall.SetResourceReference(ForegroundProperty, "SystemFillColorCriticalBrush");
        uninstall.Click += (_, _) => Uninstaller.ConfirmAndRun(this);
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
        Closed += (_, _) => u.Changed -= changed;
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
            sizeRow.IsEnabled = s.OverlayLayout != OverlayLayout.Tiles;
            sizeLabel.Opacity = sizeRow.IsEnabled ? 1 : 0.5;
        });
        var preview = new Button { Content = "Preview overlay", HorizontalAlignment = HorizontalAlignment.Left };
        preview.Click += (_, _) => OverlayController.Show();

        Section("Overlay", new[]
        {
            Row("Layout", Combo(Enum.GetValues<OverlayLayout>().Select(l => (l.Label(), l)), s.OverlayLayout, v => s.OverlayLayout = v)),
            Row("Accent color", accent),
            Row("Presets", presets),
            Row("Dim the game", dim),
            Row(sizeLabel, sizeRow),
            Toggle("Show hotkeys in the overlay", s.ShowHotkeys, v => s.ShowHotkeys = v),
            Pad(preview),
        }, Footer("The accent color is used for Instant Replay, highlights and notifications. Click the swatch for the color wheel, or type a hex code. " +
                  "Changes apply the next time the overlay opens."));
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
        if (dlg.ShowDialog(this) == true) s.OutputDirectory = dlg.FolderName;
    }


    sealed class HotkeyField : Button
    {
        readonly SettingsWindow owner;
        readonly HotkeyAction action;
        bool recording;

        public HotkeyField(SettingsWindow owner, HotkeyAction action)
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


    void Section(string title, FrameworkElement[] rows, TextBlock? footer = null)
    {
        var header = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(4, 16, 0, 8) };
        root.Children.Add(header);
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
