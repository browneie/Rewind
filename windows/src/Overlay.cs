using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace Rewind;

static class OverlayController
{
    static OverlayWindow? window;

    public static bool IsVisible => window != null;
    public static void Toggle() { if (IsVisible) Hide(); else Show(); }
    public static void Show() => Show(null);

    /// Shows the overlay; with a path, selects that clip in the overlay's player and plays it.
    public static void Show(string? previewPath) => Open(w => { if (previewPath != null) w.ShowClip(previewPath); });

    /// Shows the overlay with Settings open (the tray menu's Settings… and hotkey warnings come here).
    public static void ShowSettings(SettingsPage page = SettingsPage.Video) => Open(w => w.OpenSettings(page));

    static void Open(Action<OverlayWindow> then)
    {
        ClipLibrary.Refresh();
        if (window != null)
        {
            then(window);
            return;
        }
        // A whole-display capture would record the overlay, so the video holds while it's open.
        Controller.Shared.OverlayChanged(true);
        var w = window = new OverlayWindow(Hide);
        w.Closed += (_, _) =>
        {
            // Resume a moment after it's gone, so no frame still showing it gets recorded.
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            t.Tick += (_, _) => { t.Stop(); if (window == null) Controller.Shared.OverlayChanged(false); };
            t.Start();
        };
        then(w);
        w.Present();
    }

    public static void Hide()
    {
        var w = window;
        if (w == null) return;
        window = null;
        w.FadeOutAndClose();
    }
}

/// What the overlay shows about capture; from the controller, or fixed for --render-ui.
readonly record struct OverlayState(bool ReplayOn, double Buffered, bool Recording, double RecordingSeconds, bool Capturing, string Info);

/// Fullscreen in-game overlay (default Alt+Z), in the layout picked in Settings → Overlay. Settings open inside
/// it too, styled for each layout.
sealed class OverlayWindow : Window
{
    readonly Controller c = Controller.Shared;
    readonly Settings s = Settings.Shared;
    readonly Action close;
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Native.RECT bounds;
    readonly Size screen;
    readonly Grid root = new();
    readonly Border dim;
    readonly ContentControl layoutHost = new() { Focusable = false };
    OverlayLayout layout;
    Action<OverlayState> refresh = _ => { };
    OverlayState? demo;
    ClipPane? pane;
    string? pendingClip;
    SettingsPage? settingsPage;
    SettingsContent? settingsContent;
    // Classic layout: the clip pane is shown under the bar after clicking Gallery.
    Action<bool>? setGallery;
    bool galleryOpen;
    // Sidebar slides in from the right.
    TranslateTransform? slide;

    public OverlayWindow(Action close, Size? screenSize = null)
    {
        this.close = close;
        bounds = Native.MonitorUnderCursor().bounds;
        double dpi = Native.DpiScaleUnderCursor();
        screen = screenSize ?? new Size(bounds.Width / dpi, bounds.Height / dpi);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Opacity = 0;
        Title = "Rewind overlay";
        // The layouts are sized ×1.35 (Theme.S), which puts text and edges between pixels: snapped, they stay sharp.
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        dim = new Border { Background = Theme.Brush(Colors.Black, Theme.Dim) };
        dim.MouseLeftButtonDown += (_, _) => { if (pane is { IsTrimming: true }) pane.CancelTrim(); else close(); };
        root.Children.Add(dim);
        root.Children.Add(layoutHost);
        Build();

        var arrow = new Path
        {
            Data = Geometry.Parse("M0,0 L0,17.5 L4.2,13.4 L7,20 L9.6,18.9 L6.9,12.4 L12.6,12.4 Z"),
            Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 1.1, StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.5 },
        };
        softwareCursor = arrow;
        var cursorLayer = new Canvas { IsHitTestVisible = false };
        cursorLayer.Children.Add(arrow);
        root.Children.Add(cursorLayer);
        Content = root;
        cursorTick.Tick += (_, _) => UpdateCursor();

        PreviewKeyDown += OnKey;
        // Alt-Tab, or clicking into another app, closes the overlay. Rewind's own dialogs (the folder picker,
        // say) keep it open; so does a game grabbing focus back the moment the overlay appears.
        var shown = System.Diagnostics.Stopwatch.StartNew();
        Deactivated += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible || IsActive || shown.ElapsedMilliseconds < 600) return;
            if (Native.ProcessIsForeground((uint)Environment.ProcessId)) return;
            close();
        }, DispatcherPriority.Background);
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(hwnd, noActivate: false);
            PlaceOnScreen();
        };
        DpiChanged += (_, _) => PlaceOnScreen();
        tick.Tick += (_, _) => Refresh();
        c.StateChanged += Refresh;
        s.PropertyChanged += OnSetting;
        lightTheme = Theme.AppsUseLightTheme();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemTheme;
        Closed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemTheme;
            Theme.Light = false;
            tick.Stop();
            cursorTick.Stop();
            c.StateChanged -= Refresh;
            s.PropertyChanged -= OnSetting;
            pane?.Stop();
            Mouse.OverrideCursor = null;
        };
        Refresh();
    }

    /// Settings changed from inside the overlay: dimming shows at once; a new layout or look when Settings close.
    void OnSetting(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Settings.OverlayDim)) dim.Background = Theme.Brush(Colors.Black, Theme.Dim);

        // A new layout or appearance shows straight away, cross-fading (Settings stay open on the same page).
        if (e.PropertyName is nameof(Settings.OverlayLayout) or nameof(Settings.OverlayAppearance))
            Dispatcher.BeginInvoke(() => Build(animate: true), DispatcherPriority.Background);
    }

    bool? lightTheme;

    /// Windows switched between light and dark: on "Match Windows" the overlay follows it while open.
    void OnSystemTheme(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Color)) return;
        Dispatcher.BeginInvoke(() =>
        {
            bool light = Theme.AppsUseLightTheme();
            if (lightTheme == light) return;
            lightTheme = light;
            if (s.OverlayAppearance == OverlayAppearance.System) Build(animate: true);
        }, DispatcherPriority.Background);
    }

    /// (Re)builds the current layout, with Settings open or not. With `animate`, the old look fades out over
    /// the new one.
    void Build() => Build(animate: false);

    void Build(bool animate)
    {
        if (animate && !Theme.ReduceMotion && demo == null) CrossFade();
        Theme.Light = Theme.WantsLight();
        pane?.Stop();
        pane = null;
        setGallery = null;
        settingsContent = null;
        slide = null;
        layout = s.OverlayLayout == OverlayLayout.Tiles ? OverlayLayout.Console : s.OverlayLayout;
        layoutHost.Content = layout switch
        {
            OverlayLayout.Classic => BuildClassic(),
            OverlayLayout.Console => BuildConsole(),
            OverlayLayout.Sidebar => BuildSidebar(),
            _ => BuildSplit(),
        };
        Refresh();
    }

    public void OpenSettings(SettingsPage page)
    {
        if (pane is { IsBusy: true }) pane.CancelTrim();
        if (settingsPage == page) return;
        bool wasOpen = settingsPage != null;
        settingsPage = page;
        if (layout == OverlayLayout.Classic && wasOpen) { Build(); return; }
        Build();
    }

    public void CloseSettings()
    {
        if (settingsPage == null) return;
        settingsPage = null;
        Build();
    }

    static T? FindParent<T>(DependencyObject? o) where T : DependencyObject
    {
        while (o != null && o is not T)
            o = o is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o);
        return o as T;
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        // A hotkey field in Settings is waiting for keys: let it have them, Esc included.
        if (SettingsContent.IsRecording) return;
        // Renaming a clip: the text box gets every key (Enter saves, Esc cancels).
        if (pane?.IsRenaming == true) return;
        if (e.Key == Key.Escape)
        {
            // An open dropdown list closes first; otherwise Esc closes the overlay, from anywhere in it.
            if (e.OriginalSource is DependencyObject o && FindParent<ComboBox>(o) is { IsDropDownOpen: true }) return;
            if (e.OriginalSource is ComboBoxItem) return;
            e.Handled = true;
            close();
            return;
        }
        if (settingsPage != null || pane == null || Keyboard.Modifiers != ModifierKeys.None) return;
        if (pane.IsTrimming)
        {
            if (e.Key == Key.Enter) { e.Handled = true; pane.ConfirmTrim(); }
            return;
        }
        if (pane.Item?.IsVideo != true) return;
        switch (e.Key)
        {
            case Key.Space: e.Handled = true; pane.TogglePlay(); break;
            case Key.Left: e.Handled = true; pane.Step(-1); break;
            case Key.Right: e.Handled = true; pane.Step(1); break;
        }
    }

    /// Selects a clip in the player (closing Settings, opening Classic's Gallery for it) and plays it.
    public void ShowClip(string path)
    {
        if (settingsPage != null) { pendingClip = path; CloseSettings(); }
        galleryOpen = true;
        setGallery?.Invoke(true);
        if (pane != null) pane.Show(path); else pendingClip = path;
    }

    ClipPane MakePane(double? fixedWidth = null, double reservedWidth = 0, double reservedHeight = 0, bool showsClose = true,
                      PaneStyle style = PaneStyle.Standard, PanePalette? palette = null, double? mediaHeight = null)
    {
        var p = new ClipPane(close, screen, fixedWidth, reservedWidth, reservedHeight, showsClose, style, palette, mediaHeight);
        if (pendingClip != null) { p.Show(pendingClip); pendingClip = null; }
        return p;
    }

    /// A page (or tab) of Settings in Windows' Fluent styles, dark or light.
    SettingsContent MakeSettings(IEnumerable<SettingsPage> pages, bool dark, bool groupTitles = false, bool flat = false)
    {
        var content = new SettingsContent(pages, inOverlay: true, groupTitles, flat);
        SettingsContent.Themed(content, dark);
        if (flat) SettingsContent.Accented(content, Theme.Accent);
        settingsContent = content;
        return content;
    }

    static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false,
    };

    // MARK: Split

    /// Actions in a narrow pane beside a player showing your newest clip.
    FrameworkElement BuildSplit()
    {
        if (settingsPage is { } page) return SettingsSheet(page, new Size(Math.Min(Theme.S(620), screen.Width - 80), Math.Min(Theme.S(500), screen.Height - 100)));

        var replay = new ActionRow(Sym.ReplayLength, Theme.Replay, flashOnTap: false);
        var record = new ActionRow(Sym.Record, Theme.Record, flashOnTap: false);
        var shot = new ActionRow(Sym.Camera, Theme.Screenshot, flashOnTap: true);
        replay.SetActions(() => { if (c.ReplayActive) c.SaveReplay(); else c.StartReplay(); }, c.ToggleReplay);
        record.SetActions(c.ToggleRecording, c.ToggleRecording);
        shot.SetActions(c.Screenshot, c.Screenshot);

        var brand = new StackPanel { Orientation = Orientation.Horizontal };
        brand.Children.Add(Logo(24, 7, 12));
        var name = Theme.Text(Theme.Brand, 17, FontWeights.Bold);
        name.Margin = new Thickness(Theme.S(8), 0, 0, 0);
        name.VerticalAlignment = VerticalAlignment.Center;
        brand.Children.Add(name);
        var recSlot = new ContentControl { VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        var top = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, Theme.S(14)) };
        DockPanel.SetDock(brand, Dock.Left);
        DockPanel.SetDock(recSlot, Dock.Right);
        top.Children.Add(brand);
        top.Children.Add(recSlot);

        var info = Theme.Text("", 10.5, brush: Theme.Muted);
        info.TextWrapping = TextWrapping.Wrap;
        info.MaxHeight = Theme.S(30);
        var sound = Theme.Text("", 10.5, brush: Theme.Muted);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, Theme.S(6), 0, 0) };
        chips.Children.Add(new Chip(Theme.FolderLabel, Sym.Folder, () => { close(); c.OpenClipsFolder(); }));
        chips.Children.Add(new Chip(null, Sym.Gear, () => OpenSettings(SettingsPage.Video))
            { ToolTip = Theme.SettingsLabel, Margin = new Thickness(Theme.S(6), 0, 0, 0) });
        var bottom = new StackPanel();
        bottom.Children.Add(info);
        bottom.Children.Add(sound);
        bottom.Children.Add(chips);

        var rows = new StackPanel();
        rows.Children.Add(replay);
        record.Margin = shot.Margin = new Thickness(0, Theme.S(8), 0, 0);
        rows.Children.Add(record);
        rows.Children.Add(shot);

        var actions = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(top, System.Windows.Controls.Dock.Top);
        DockPanel.SetDock(bottom, System.Windows.Controls.Dock.Bottom);
        actions.Children.Add(top);
        actions.Children.Add(bottom);
        actions.Children.Add(rows);

        pane = MakePane(reservedWidth: Theme.S(Theme.ActionsWidth + 10), reservedHeight: Theme.S(70));
        var left = Surface(actions, Theme.S(Theme.Radius * 0.7), Theme.S(16));
        left.Width = Theme.S(Theme.ActionsWidth);
        var right = Surface(pane, Theme.S(Theme.Radius * 0.7), Theme.S(16));
        right.Margin = new Thickness(Theme.S(10), 0, 0, 0);

        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);

        refresh = st =>
        {
            replay.Update(st.ReplayOn ? "Save replay" : Theme.ReplayTitle, st.ReplayOn,
                st.ReplayOn ? s.Hotkey(HotkeyAction.SaveReplay) : s.Hotkey(HotkeyAction.ToggleReplay),
                st.ReplayOn ? $"Last {Format.Duration(st.Buffered)} ready" : "Click to turn on",
                st.ReplayOn ? "Right-click to turn off" : null,
                st.ReplayOn ? st.Buffered / Math.Max(s.ReplaySeconds, 1) : null);
            record.Update(st.Recording ? "Stop recording" : Theme.RecordTitle, st.Recording, s.Hotkey(HotkeyAction.ToggleRecording),
                st.Recording ? "Recording…" : "Start recording", null, null, st.Recording ? Sym.Stop : Sym.Record);
            shot.Update(Theme.ScreenshotTitle, false, s.Hotkey(HotkeyAction.Screenshot), "Full-res PNG", null, null);
            recSlot.Content = st.Recording ? new RecPill { Text = Format.Duration(st.RecordingSeconds) } : null;
            info.Text = st.Capturing ? st.Info : "Not capturing";
            sound.Text = SoundText();
        };
        return grid;
    }

    /// Settings in a dark panel: the pages on the left, the chosen page on the right. Split shows it over the
    /// layout; Classic in the Gallery's place.
    FrameworkElement SettingsSheet(SettingsPage page, Size size)
    {
        var p = PanePalette.Overlay;
        var nav = new StackPanel();
        var back = new TextLink("← Back to clips", p.MutedBrush, 13, CloseSettings, tip: "Back (Esc)") { Margin = new Thickness(0, 0, 0, 6) };
        nav.Children.Add(back);
        var title = Theme.Text(Theme.SettingsLabel, 17, FontWeights.Bold);
        title.Margin = new Thickness(0, 0, 0, Theme.S(10));
        nav.Children.Add(title);
        var tab = SettingsPages.Holding(page);
        foreach (var t in SettingsPages.Tabs)
            nav.Children.Add(new NavItem(t.Title(), t.Summary(), t == tab, p, bar: false, () => OpenSettings(t.Pages()[0])));
        var navDock = new DockPanel { Width = Theme.S(170), Margin = new Thickness(0, 0, Theme.S(16), 0) };
        navDock.Children.Add(nav);

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, Theme.S(4)) };
        var x = new CircleButton(Sym.Close, close) { ToolTip = "Close" };
        DockPanel.SetDock(x, Dock.Right);
        head.Children.Add(x);
        var pageTitle = Theme.Text(tab.Title(), 15, FontWeights.SemiBold);
        pageTitle.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(pageTitle);
        var foot = Theme.Text("Esc to go back · changes save straight away", 10, brush: p.MutedBrush);
        foot.Margin = new Thickness(0, Theme.S(8), 0, 0);
        var main = new DockPanel();
        DockPanel.SetDock(head, System.Windows.Controls.Dock.Top);
        DockPanel.SetDock(foot, System.Windows.Controls.Dock.Bottom);
        main.Children.Add(head);
        main.Children.Add(foot);
        main.Children.Add(Scroll(MakeSettings(SettingsPages.Holding(page).Pages(), dark: !Theme.Light, groupTitles: SettingsPages.Holding(page).Pages().Length > 1)));

        var body = new DockPanel();
        DockPanel.SetDock(navDock, Dock.Left);
        body.Children.Add(navDock);
        body.Children.Add(main);
        var surface = Surface(body, Theme.S(Theme.Radius * 0.6), Theme.S(18));
        surface.Width = size.Width;
        surface.Height = size.Height;
        surface.HorizontalAlignment = HorizontalAlignment.Center;
        surface.VerticalAlignment = VerticalAlignment.Center;
        refresh = _ => { };
        return surface;
    }

    // MARK: Console

    static PanePalette ConsolePalette => Theme.Light ? PanePalette.ConsoleLight : PanePalette.Console;
    static Brush ConsoleBg => Theme.Brush(ConsolePalette.Bg, 0.97);
    static Brush ConsoleLine => ConsolePalette.LineBrush;
    static Brush ConsoleDim => ConsolePalette.MutedBrush;
    static Brush ConsoleSoft => Theme.Brush(ConsolePalette.Text, 0.75);

    /// One solid panel: actions and capture details on the left; the clip, its details and a filmstrip on the right.
    FrameworkElement BuildConsole()
    {
        const double side = 270;
        double width = Math.Min(1200, screen.Width - 100), height = Math.Min(740, screen.Height - 110);
        var p = ConsolePalette;
        Border Hr() => new() { Height = 1, Background = ConsoleLine };
        Border Vr() => new() { Width = 1, Background = ConsoleLine };

        var left = new DockPanel { Width = side, LastChildFill = true };
        var info = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        Action<OverlayState> fillInfo = st =>
        {
            info.Children.Clear();
            foreach (var l in CaptureLines(st)) info.Children.Add(new TextBlock { Text = l, FontFamily = Theme.Mono, FontSize = 12, Foreground = ConsoleDim, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 6) });
        };
        FrameworkElement main;
        refresh = _ => { };

        if (settingsPage is { } page)
        {
            var settingsTitle = ClipPane.Plain(Theme.SettingsLabel, 15, p.TextBrush);
            settingsTitle.FontWeight = FontWeights.SemiBold;
            var head = Header52(settingsTitle, new TextLink("← Clips", ConsoleSoft, 13, CloseSettings, tip: "Back (Esc)"), 18);
            var nav = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            var tab = SettingsPages.Holding(page);
            foreach (var t in SettingsPages.Tabs)
                nav.Children.Add(new NavItem(t.Title(), t.Summary(), t == tab, p, bar: true, () => OpenSettings(t.Pages()[0])));
            Put(left, head, System.Windows.Controls.Dock.Top); Put(left, Hr(), System.Windows.Controls.Dock.Top);
            Put(left, info, System.Windows.Controls.Dock.Bottom); Put(left, Hr(), System.Windows.Controls.Dock.Bottom);
            left.Children.Add(nav);

            var mainDock = new DockPanel();
            var x = new TextLink("", ConsoleDim, 11, close, tip: "Close") { FontFamily = Theme.Symbols };
            Put(mainDock, Header52(x, ClipPane.Plain(tab.Title(), 15, p.TextBrush), 24), System.Windows.Controls.Dock.Top);
            Put(mainDock, Hr(), System.Windows.Controls.Dock.Top);
            var foot = new Border { Height = 40, Padding = new Thickness(24, 0, 24, 0), Child = ClipPane.Plain("Esc to go back · changes save straight away", 12, ConsoleDim) };
            Put(mainDock, foot, System.Windows.Controls.Dock.Bottom);
            Put(mainDock, Hr(), System.Windows.Controls.Dock.Bottom);
            var content = MakeSettings(SettingsPages.Holding(page).Pages(), dark: !Theme.Light, groupTitles: SettingsPages.Holding(page).Pages().Length > 1);
            content.Margin = new Thickness(24, 6, 24, 12);
            mainDock.Children.Add(Scroll(content));
            main = mainDock;
            refresh = st => fillInfo(st);
        }
        else
        {
            var brandName = ClipPane.Plain(Theme.Brand, 15, p.TextBrush);
            brandName.FontWeight = FontWeights.SemiBold;
            var toggleKey = ClipPane.Plain("", 12, ConsoleDim, Theme.Mono);
            var head = Header52(toggleKey, brandName, 18);
            var replay = new ConsoleRow(Theme.Replay);
            var record = new ConsoleRow(Theme.Record);
            var shot = new ConsoleRow(Theme.Accent, flashOnTap: true);
            replay.SetActions(() => { if (c.ReplayActive) c.SaveReplay(); else c.StartReplay(); }, c.ToggleReplay);
            record.SetActions(c.ToggleRecording, c.ToggleRecording);
            shot.SetActions(c.Screenshot, c.Screenshot);
            var rows = new StackPanel();
            rows.Children.Add(replay); rows.Children.Add(record); rows.Children.Add(shot);

            var footer = new Grid { Height = 42 };
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            var folder = new TextLink(Theme.FolderLabel, Theme.InkBrush, 13, () => { close(); c.OpenClipsFolder(); }) { Margin = new Thickness(18, 0, 18, 0) };
            var settings = new TextLink(Theme.SettingsLabel, Theme.InkBrush, 13, () => OpenSettings(SettingsPage.Video)) { Margin = new Thickness(18, 0, 18, 0) };
            var divider = Vr();
            Grid.SetColumn(divider, 1); Grid.SetColumn(settings, 2);
            footer.Children.Add(folder); footer.Children.Add(divider); footer.Children.Add(settings);

            Put(left, head, System.Windows.Controls.Dock.Top); Put(left, Hr(), System.Windows.Controls.Dock.Top); Put(left, rows, System.Windows.Controls.Dock.Top);
            Put(left, footer, System.Windows.Controls.Dock.Bottom); Put(left, Hr(), System.Windows.Controls.Dock.Bottom); Put(left, info, System.Windows.Controls.Dock.Bottom); Put(left, Hr(), System.Windows.Controls.Dock.Bottom);
            left.Children.Add(new Border());

            double mainW = width - side - 3; // less the panel's border
            double mediaH = Math.Max(height - 52 - 1 - 18 - Theme.S(36) - (94 + 22 + 36), 180);
            pane = MakePane(fixedWidth: mainW - 36, showsClose: true, style: PaneStyle.Console, palette: p, mediaHeight: mediaH);
            pane.Margin = new Thickness(18, 18, 18, 18);
            var mainDock = new DockPanel();
            Put(mainDock, pane.Header, System.Windows.Controls.Dock.Top);
            Put(mainDock, Hr(), System.Windows.Controls.Dock.Top);
            mainDock.Children.Add(pane);
            main = mainDock;

            refresh = st =>
            {
                var toggle = Shown(s.Hotkey(HotkeyAction.ToggleOverlay));
                toggleKey.Text = toggle ?? "";
                replay.Update(st.ReplayOn ? "Save replay" : Theme.ReplayTitle,
                    st.ReplayOn ? $"● {Format.Duration(st.Buffered)} buffered" : "Off · click to turn on",
                    st.ReplayOn ? Theme.Brush(Theme.Accent) : ConsoleDim, st.ReplayOn ? "Right-click to turn off" : null,
                    Shown(st.ReplayOn ? s.Hotkey(HotkeyAction.SaveReplay) : s.Hotkey(HotkeyAction.ToggleReplay)), st.ReplayOn);
                record.Update(st.Recording ? "Stop recording" : Theme.RecordTitle,
                    st.Recording ? $"● {Format.Duration(st.RecordingSeconds)}" : "Not recording",
                    st.Recording ? Theme.Brush(Theme.Record) : ConsoleDim, null, Shown(s.Hotkey(HotkeyAction.ToggleRecording)), st.Recording);
                shot.Update(Theme.ScreenshotTitle, null, ConsoleDim, null, Shown(s.Hotkey(HotkeyAction.Screenshot)), false);
                fillInfo(st);
            };
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(side) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var vr = Vr();
        Grid.SetColumn(vr, 1); Grid.SetColumn(main, 2);
        grid.Children.Add(left); grid.Children.Add(vr); grid.Children.Add(main);
        var panel = new Border
        {
            Width = width, Height = height, Background = ConsoleBg, BorderBrush = ConsoleLine, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Child = grid, ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 12, Direction = 270, Opacity = 0.45, Color = Colors.Black },
        };
        TextElement.SetForeground(panel, p.TextBrush);
        return panel;
    }

    static void Put(DockPanel panel, UIElement child, Dock dock)
    {
        DockPanel.SetDock(child, dock);
        panel.Children.Add(child);
    }

    /// A 52-high header row: `left` and `right`, padded.
    static Border Header52(FrameworkElement right, FrameworkElement left, double pad)
    {
        var row = new DockPanel { LastChildFill = true };
        right.VerticalAlignment = left.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(right, System.Windows.Controls.Dock.Right);
        row.Children.Add(right);
        row.Children.Add(left);
        return new Border { Height = 52, Padding = new Thickness(pad, 0, pad, 0), Child = row };
    }
    static string? Shown(Hotkey h) => Theme.ShowHotkeys && h.IsSet ? h.Display : null;

    /// What's being captured, as short lines: "Display 1", "2048×1280 · 120 fps", "HEVC · 33 Mbps", sound.
    List<string> CaptureLines(OverlayState st)
    {
        if (!st.Capturing || st.Info.Length == 0) return new() { "Not capturing" };
        var parts = st.Info.Split(" · ");
        var lines = new List<string> { parts[0] };
        for (int i = 1; i < parts.Length; i += 2) lines.Add(string.Join(" · ", parts.Skip(i).Take(2)));
        lines.Add(s.SystemAudio ? (s.Microphone ? "System audio + mic" : "System audio") : (s.Microphone ? "Microphone" : "No sound"));
        return lines;
    }

    // MARK: Sidebar

    /// Sidebar's own light and dark colors, following Settings → Overlay → Appearance like every layout.
    static PanePalette SidebarPalette() => Theme.Light ? PanePalette.SidebarLight : PanePalette.SidebarDark;

    /// Puts a picture of the overlay as it looks now over the rebuilt one and fades it out.
    void CrossFade()
    {
        if (layoutHost.ActualWidth < 1 || layoutHost.ActualHeight < 1) return;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(layoutHost.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(layoutHost.ActualHeight * dpi.DpiScaleY),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bmp.Render(layoutHost);
            bmp.Freeze();
            var snapshot = new Image { Source = bmp, Stretch = Stretch.Fill, IsHitTestVisible = false };
            root.Children.Insert(root.Children.IndexOf(layoutHost) + 1, snapshot);
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } };
            fade.Completed += (_, _) => root.Children.Remove(snapshot);
            snapshot.BeginAnimation(OpacityProperty, fade);
        }
        catch (Exception e) { Log.Error("overlay cross-fade", e); }
    }

    /// A full-height panel that slides in from the right edge, in light or dark (Settings → Overlay).
    FrameworkElement BuildSidebar()
    {
        const double width = 470, pad = 30;
        var p = SidebarPalette();
        var panel = new DockPanel { LastChildFill = true };
        Border Line(Thickness t) => new() { BorderBrush = p.LineBrush, BorderThickness = t };

        TextBlock Big(string text)
        {
            var t = ClipPane.Plain(text, 30, p.TextBrush);
            t.FontWeight = FontWeights.Bold;
            return t;
        }
        FrameworkElement CloseX() => new TextLink("", p.MutedBrush, 12, close, tip: "Close (Esc)") { FontFamily = Theme.Symbols, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0) };
        var footInfo = ClipPane.Plain("", 12, p.MutedBrush, Theme.Mono);
        footInfo.HorizontalAlignment = HorizontalAlignment.Right;
        Border Footer(params FrameworkElement[] links)
        {
            var row = new DockPanel { LastChildFill = true };
            foreach (var l in links)
            {
                l.Margin = new Thickness(0, 0, 22, 0);
                DockPanel.SetDock(l, System.Windows.Controls.Dock.Left);
                row.Children.Add(l);
            }
            row.Children.Add(footInfo);
            var b = Line(new Thickness(0, 1, 0, 0));
            b.Padding = new Thickness(pad, 16, pad, 16);
            b.Child = row;
            return b;
        }
        Action<OverlayState> fillFoot = st =>
        {
            var l = CaptureLines(st);
            footInfo.Text = l.Count > 2 ? l[1] + " · " + l[2].Split(" · ")[0] : l.FirstOrDefault() ?? "";
        };

        if (settingsPage is { } page)
        {
            var tab = SettingsPages.Holding(page);
            var head = new DockPanel { Margin = new Thickness(pad, 30, pad, 18) };
            var x = CloseX();
            DockPanel.SetDock(x, System.Windows.Controls.Dock.Right);
            head.Children.Add(x);
            var titles = new StackPanel();
            titles.Children.Add(new TextLink("← Back to clips", p.MutedBrush, 13, CloseSettings, tip: "Back (Esc)") { Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Left });
            titles.Children.Add(Big(Theme.SettingsLabel));
            head.Children.Add(titles);
            var tabs = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var t in SettingsPages.Tabs)
            {
                bool on = t == tab;
                var label = ClipPane.Plain(t.Title(), 13, on ? p.TextBrush : p.MutedBrush);
                if (on) label.FontWeight = FontWeights.SemiBold;
                var b = new Border
                {
                    Child = label, Padding = new Thickness(0, 0, 0, 12), Margin = new Thickness(0, 0, 20, 0), Cursor = Cursors.Hand,
                    BorderBrush = p.TextBrush, BorderThickness = new Thickness(0, 0, 0, on ? 2 : 0), Background = Brushes.Transparent,
                };
                var target = t;
                b.MouseLeftButtonDown += (_, e) => e.Handled = true;
                b.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenSettings(target.Pages()[0]); };
                tabs.Children.Add(b);
            }
            var tabBar = Line(new Thickness(0, 0, 0, 1));
            tabBar.Padding = new Thickness(pad, 0, pad, 0);
            tabBar.Child = tabs;
            var content = MakeSettings(tab.Pages(), p.Dark, groupTitles: tab.Pages().Length > 1);
            content.Margin = new Thickness(pad, 8, pad, 20);
            Put(panel, head, System.Windows.Controls.Dock.Top);
            Put(panel, tabBar, System.Windows.Controls.Dock.Top);
            Put(panel, Footer(new TextLink(Theme.FolderLabel, p.MutedBrush, 13, () => { close(); c.OpenClipsFolder(); })), System.Windows.Controls.Dock.Bottom);
            panel.Children.Add(Scroll(content));
            refresh = fillFoot;
        }
        else
        {
            var head = new DockPanel { Margin = new Thickness(pad, 30, pad, 18) };
            var x = CloseX();
            DockPanel.SetDock(x, System.Windows.Controls.Dock.Right);
            head.Children.Add(x);
            var titles = new StackPanel();
            titles.Children.Add(Big(Theme.Brand));
            var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var dot = new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            var status = ClipPane.Plain("", 13, p.MutedBrush);
            statusRow.Children.Add(dot);
            statusRow.Children.Add(status);
            titles.Children.Add(statusRow);
            head.Children.Add(titles);

            var replay = new SidebarAction(p, Theme.Replay);
            var record = new SidebarAction(p, Theme.Record);
            var shot = new SidebarAction(p, Theme.Accent, flashOnTap: true);
            replay.SetActions(() => { if (c.ReplayActive) c.SaveReplay(); else c.StartReplay(); }, c.ToggleReplay);
            record.SetActions(c.ToggleRecording, c.ToggleRecording);
            shot.SetActions(c.Screenshot, c.Screenshot);
            var actions = new Grid { Height = 64 };
            for (int i = 0; i < 5; i++) actions.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 1 ? new GridLength(1) : new GridLength(1, GridUnitType.Star) });
            int col = 0;
            foreach (var el in new FrameworkElement[] { replay, new Border { Background = p.LineBrush }, record, new Border { Background = p.LineBrush }, shot })
            {
                Grid.SetColumn(el, col++);
                actions.Children.Add(el);
            }
            var actionsBox = Line(new Thickness(0, 1, 0, 1));
            actionsBox.Child = actions;

            pane = MakePane(fixedWidth: width - pad * 2, showsClose: true, style: PaneStyle.Sidebar, palette: p);
            pane.Margin = new Thickness(pad, 22, pad, 0);

            Put(panel, head, System.Windows.Controls.Dock.Top);
            Put(panel, actionsBox, System.Windows.Controls.Dock.Top);
            Put(panel, Footer(new TextLink(Theme.FolderLabel, p.MutedBrush, 13, () => { close(); c.OpenClipsFolder(); }),
                               new TextLink(Theme.SettingsLabel, p.MutedBrush, 13, () => OpenSettings(SettingsPage.Video))), System.Windows.Controls.Dock.Bottom);
            Put(panel, pane, System.Windows.Controls.Dock.Top);
            panel.Children.Add(new Border());

            refresh = st =>
            {
                var parts = new List<string>();
                if (st.Recording) parts.Add($"Recording {Format.Duration(st.RecordingSeconds)}");
                parts.Add(st.ReplayOn ? $"Replay on · last {Format.Duration(st.Buffered)} ready" : "Replay off");
                if (st.Capturing && st.Info.Length > 0) parts.Add(st.Info.Split(" · ")[0]);
                status.Text = string.Join(" · ", parts);
                dot.Fill = st.ReplayOn ? Theme.Brush(Theme.Accent) : Theme.Brush(p.Muted, 0.6);
                replay.Update(st.ReplayOn ? "Save replay" : Theme.ReplayTitle,
                    Shown(st.ReplayOn ? s.Hotkey(HotkeyAction.SaveReplay) : s.Hotkey(HotkeyAction.ToggleReplay)),
                    st.ReplayOn ? "right-click: off" : null, st.ReplayOn);
                record.Update(st.Recording ? "Stop" : Theme.RecordTitle, Shown(s.Hotkey(HotkeyAction.ToggleRecording)), null, st.Recording);
                shot.Update(Theme.ScreenshotTitle, Shown(s.Hotkey(HotkeyAction.Screenshot)), null, false);
                fillFoot(st);
            };
        }

        slide = new TranslateTransform(Theme.ReduceMotion || settingsPage != null || IsVisible ? 0 : width + 40, 0);
        var box = new Border
        {
            Width = width, Background = Theme.Brush(p.Bg), BorderBrush = p.LineBrush, BorderThickness = new Thickness(1, 0, 0, 0),
            Child = panel, HorizontalAlignment = HorizontalAlignment.Right, ClipToBounds = true, RenderTransform = slide,
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 0, Opacity = 0.35, Color = Colors.Black },
        };
        TextElement.SetForeground(box, p.TextBrush);
        return box;
    }

    // MARK: Classic

    /// ShadowPlay-style: a title strip across the top, square tiles (label, icon, status) in a dark panel, a
    /// Gallery panel to the right with small round buttons, and a hotkey hint bar underneath. The Gallery (or
    /// Settings) opens under the bar.
    FrameworkElement BuildClassic()
    {
        var strip = new Grid { Height = Theme.S(40), Background = Theme.Brush(Theme.Shade, Theme.Light ? 0.7 : 0.45), VerticalAlignment = VerticalAlignment.Top };
        var logo = Logo(24, 4, 12);
        logo.HorizontalAlignment = HorizontalAlignment.Left;
        logo.Margin = new Thickness(Theme.S(14), 0, 0, 0);
        var title = Theme.Text(Theme.Brand, 15, FontWeights.SemiBold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.VerticalAlignment = VerticalAlignment.Center;
        var closeButton = new CircleButton(Sym.Close, close) { ToolTip = "Close (Esc)", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, Theme.S(14), 0) };
        strip.Children.Add(logo);
        strip.Children.Add(title);
        strip.Children.Add(closeButton);

        ClassicHover hovered = ClassicHover.None;
        var hintText = Theme.Text("", 10.5, brush: Theme.Brush(Theme.Wash, 0.85));
        void UpdateHint()
        {
            string Line(HotkeyAction a, string what) { var h = s.Hotkey(a); return h.IsSet ? $"Press {h.Display} to {what}" : $"No hotkey set to {what}"; }
            hintText.Text = hovered switch
            {
                ClassicHover.Replay => c.ReplayActive ? Line(HotkeyAction.SaveReplay, $"save the last {Format.Duration(s.ReplaySeconds)}") : Line(HotkeyAction.ToggleReplay, "turn on Instant Replay"),
                ClassicHover.Record => Line(HotkeyAction.ToggleRecording, c.Recording ? "stop recording" : "start recording"),
                _ => Line(HotkeyAction.Screenshot, "save a screenshot to the Gallery"),
            };
        }

        var replay = new ClassicTile(Theme.ReplayTitle, Theme.Replay, h => { hovered = h ? ClassicHover.Replay : ClassicHover.None; UpdateHint(); });
        var record = new ClassicTile(Theme.RecordTitle, Theme.Record, h => { hovered = h ? ClassicHover.Record : ClassicHover.None; UpdateHint(); });
        var shot = new ClassicTile(Theme.ScreenshotTitle, Theme.Screenshot, h => { hovered = h ? ClassicHover.Screenshot : ClassicHover.None; UpdateHint(); }, flashOnTap: true);
        replay.SetActions(() => { if (c.ReplayActive) c.SaveReplay(); else c.StartReplay(); }, c.ToggleReplay);
        record.SetActions(c.ToggleRecording, c.ToggleRecording);
        shot.SetActions(c.Screenshot, c.Screenshot);
        var tileRow = new StackPanel { Orientation = Orientation.Horizontal };
        tileRow.Children.Add(replay);
        record.Margin = new Thickness(Theme.S(6), 0, Theme.S(6), 0);
        tileRow.Children.Add(record);
        tileRow.Children.Add(shot);
        var tilesPanel = ClassicPanel(tileRow, Theme.S(6));

        var gallery = new ClassicWide(Sym.Film, "Gallery");
        var mic = new ClassicRound(Sym.Mic, () => s.Microphone = !s.Microphone);
        var gear = new ClassicRound(Sym.Gear, () => { if (settingsPage != null) CloseSettings(); else OpenSettings(SettingsPage.Video); }) { ToolTip = Theme.SettingsLabel, On = settingsPage != null };
        var rounds = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, Theme.S(8), 0, 0) };
        rounds.Children.Add(mic);
        rounds.Children.Add(new ClassicRound(Sym.Folder, () => { close(); c.OpenClipsFolder(); }) { ToolTip = Theme.FolderLabel, Margin = new Thickness(Theme.S(8), 0, Theme.S(8), 0) });
        rounds.Children.Add(gear);
        var galleryStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        galleryStack.Children.Add(gallery);
        galleryStack.Children.Add(rounds);
        var galleryPanel = ClassicPanel(galleryStack, Theme.S(10));
        galleryPanel.Width = Theme.S(150);
        galleryPanel.Margin = new Thickness(Theme.S(8), 0, 0, 0);

        var bar = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(galleryPanel, 1);
        bar.Children.Add(tilesPanel);
        bar.Children.Add(galleryPanel);

        var hintRow = new StackPanel { Orientation = Orientation.Horizontal };
        var keyIcon = Icons.Make(Sym.Keyboard, Theme.S(10), Theme.Brush(Theme.Wash, 0.85));
        keyIcon.Margin = new Thickness(0, 0, Theme.S(6), 0);
        hintRow.Children.Add(keyIcon);
        hintRow.Children.Add(hintText);
        var hintPanel = ClassicPanel(hintRow, 0);
        hintPanel.Padding = new Thickness(Theme.S(10), Theme.S(5), Theme.S(10), Theme.S(5));
        hintPanel.HorizontalAlignment = HorizontalAlignment.Center;
        hintPanel.Margin = new Thickness(0, Theme.S(8), 0, 0);

        Border galleryPane;
        if (settingsPage is { } page)
        {
            // Settings in the Gallery's place, as wide as the bar above.
            galleryPane = ClassicSettings(page, new Size(Math.Min(Theme.S(620), screen.Width - 80), Math.Min(Theme.S(440), screen.Height - Theme.S(300))));
            galleryPane.HorizontalAlignment = HorizontalAlignment.Center;
            galleryPane.Margin = new Thickness(0, Theme.S(8), 0, 0);
        }
        else
        {
            pane = MakePane(reservedHeight: Theme.S(300), style: PaneStyle.Classic);
            galleryPane = ClassicPanel(pane, Theme.S(14));
            galleryPane.HorizontalAlignment = HorizontalAlignment.Center;
            galleryPane.Margin = new Thickness(0, Theme.S(8), 0, 0);
        }
        bool showPane = settingsPage != null || galleryOpen;
        galleryPane.Visibility = showPane ? Visibility.Visible : Visibility.Collapsed;

        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, Theme.S(showPane ? 64 : 150), 0, 0) };
        column.Children.Add(bar);
        column.Children.Add(hintPanel);
        column.Children.Add(galleryPane);
        gallery.On = galleryOpen && settingsPage == null;

        setGallery = on =>
        {
            if (settingsPage != null) { if (on) { galleryOpen = true; CloseSettings(); } return; }
            if (galleryOpen == on && galleryPane.Visibility == (on ? Visibility.Visible : Visibility.Collapsed)) return;
            galleryOpen = on;
            gallery.On = on;
            galleryPane.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            column.Margin = new Thickness(0, Theme.S(on ? 64 : 150), 0, 0);
            if (on && !Theme.ReduceMotion) galleryPane.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        };
        gallery.Clicked = () =>
        {
            if (settingsPage != null) { galleryOpen = true; CloseSettings(); }
            else setGallery(!galleryOpen);
        };

        var rootGrid = new Grid();
        rootGrid.Children.Add(column);
        rootGrid.Children.Add(strip);

        refresh = st =>
        {
            replay.Update(Sym.ReplayLength, st.ReplayOn ? $"On · {Format.Duration(st.Buffered)}" : "Off", st.ReplayOn,
                st.ReplayOn ? st.Buffered / Math.Max(s.ReplaySeconds, 1) : null);
            record.Update(st.Recording ? Sym.Stop : Sym.Record, st.Recording ? $"Recording · {Format.Duration(st.RecordingSeconds)}" : "Not recording", st.Recording, null);
            shot.Update(Sym.Camera, "Full-res PNG", false, null);
            mic.On = s.Microphone;
            mic.SetIcon(s.Microphone ? Sym.Mic : Sym.MicOff);
            mic.ToolTip = s.Microphone ? "Microphone on (click to turn off)" : "Microphone off (click to turn on)";
            UpdateHint();
        };
        return rootGrid;
    }

    enum ClassicHover { None, Replay, Record, Screenshot }

    /// Classic's Settings, in a square panel like the bar above it: the tabs along the top (the chosen one
    /// underlined in the accent color), flat sections below.
    Border ClassicSettings(SettingsPage page, Size size)
    {
        var tab = SettingsPages.Holding(page);
        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, Theme.S(10)) };
        var title = Theme.Text(Theme.SettingsLabel.ToUpperInvariant(), 12, FontWeights.Bold);
        title.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(title, Dock.Left);
        head.Children.Add(title);
        var x = new TextLink("", Theme.InkBrush, Theme.S(10), close, tip: "Close (Esc)") { FontFamily = Theme.Symbols };
        DockPanel.SetDock(x, Dock.Right);
        head.Children.Add(x);
        var back = new TextLink("Back to clips", Theme.Muted, Theme.S(10.5), CloseSettings, tip: "Back (Esc)") { Margin = new Thickness(0, 0, Theme.S(16), 0) };
        DockPanel.SetDock(back, Dock.Right);
        head.Children.Add(back);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var t in SettingsPages.Tabs)
        {
            bool on = t == tab;
            var label = Theme.Text(t.Title(), 11.5, on ? FontWeights.SemiBold : FontWeights.Normal, on ? Theme.InkBrush : Theme.Muted);
            var item = new Border
            {
                Child = label, Padding = new Thickness(0, 0, 0, Theme.S(7)), Margin = new Thickness(0, 0, Theme.S(22), -1),
                BorderBrush = on ? Theme.Brush(Theme.Accent) : Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 2),
                Background = Brushes.Transparent, Cursor = Cursors.Hand,
            };
            var first = t.Pages()[0];
            item.MouseEnter += (_, _) => { if (!on) label.Foreground = Theme.InkBrush; };
            item.MouseLeave += (_, _) => { if (!on) label.Foreground = Theme.Muted; };
            item.MouseLeftButtonDown += (_, e) => e.Handled = true;
            item.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (!on) OpenSettings(first); };
            tabs.Children.Add(item);
        }
        var tabBar = new Border { Child = tabs, BorderBrush = Theme.Brush(Theme.Wash, 0.1), BorderThickness = new Thickness(0, 0, 0, 1) };

        var foot = Theme.Text("Esc to go back · changes save straight away", 9.5, brush: Theme.Muted);
        foot.Margin = new Thickness(0, Theme.S(10), 0, 0);
        var pages = tab.Pages();
        var content = MakeSettings(pages, dark: !Theme.Light, groupTitles: pages.Length > 1, flat: true);
        content.Margin = new Thickness(0, Theme.S(4), Theme.S(6), 0);

        var body = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(tabBar, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        body.Children.Add(head);
        body.Children.Add(tabBar);
        body.Children.Add(foot);
        body.Children.Add(Scroll(content));
        var panel = ClassicPanel(body, Theme.S(16));
        panel.Width = size.Width;
        panel.Height = size.Height;
        return panel;
    }

    // MARK: Shared pieces

    string SoundText() => $"Sound: {(s.SystemAudio ? "system" : "off")}{(s.Microphone ? " + mic" : "")}";

    static FrameworkElement Logo(double size, double radius, double glyph) => new Border
    {
        Width = Theme.S(size), Height = Theme.S(size), CornerRadius = new CornerRadius(Theme.S(radius)),
        Background = new LinearGradientBrush(Theme.Accent, Theme.Accent.Mix(Colors.Black, 0.45), 45),
        Child = Icons.RewindArrow(Theme.S(glyph * 1.3), Brushes.Black, dot: false),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// The overlay's dark rounded panel.
    static Grid Surface(UIElement child, double radius, double padding)
    {
        // The fill and its shadow are a layer of their own behind the content: an effect on the panel itself would
        // draw everything inside it (text included) through a soft intermediate bitmap.
        var fill = new Border
        {
            CornerRadius = new CornerRadius(radius),
            Background = Theme.Brush(Theme.PanelBase, Theme.Light ? 0.96 : Theme.PanelOpacity),
            Effect = new DropShadowEffect { BlurRadius = Theme.S(40), ShadowDepth = Theme.S(15), Direction = 270, Opacity = 0.45, Color = Colors.Black },
        };
        var panel = new Border
        {
            Padding = new Thickness(padding),
            CornerRadius = fill.CornerRadius,
            Background = Brushes.Transparent, // still catches clicks, so they don't close the overlay
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            Child = child,
        };
        var sheen = new Border
        {
            CornerRadius = panel.CornerRadius, IsHitTestVisible = false, BorderThickness = new Thickness(1),
            BorderBrush = new LinearGradientBrush(Color.FromArgb(18, 255, 255, 255), Colors.Transparent, 90),
        };
        var g = new Grid();
        g.Children.Add(fill);
        g.Children.Add(panel);
        g.Children.Add(sheen);
        return g;
    }

    /// Near-black, square-cornered panel of the Classic layout.
    static Border ClassicPanel(UIElement child, double padding) => new()
    {
        Background = Theme.Brush(Theme.Shade, Theme.Light ? 0.9 : 0.78),
        BorderBrush = Theme.Brush(Theme.Wash, 0.08),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(padding),
        Child = child,
    };

    readonly DispatcherTimer cursorTick = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
    readonly Path softwareCursor;

    public bool SoftwareCursorShown => softwareCursor.Visibility == Visibility.Visible;

    /// Fullscreen games hide the mouse pointer and often lock it inside their window. While the overlay is up,
    /// unlock it, and if Windows still won't draw it, draw our own.
    void UpdateCursor()
    {
        if (Native.CursorClipped()) Native.ClipCursor(IntPtr.Zero);
        bool hidden = !Native.CursorShowing();
        if (hidden)
        {
            var p = Native.CursorPos();
            var local = PointFromScreen(new Point(p.X, p.Y));
            Canvas.SetLeft(softwareCursor, local.X);
            Canvas.SetTop(softwareCursor, local.Y);
        }
        var v = hidden ? Visibility.Visible : Visibility.Collapsed;
        if (softwareCursor.Visibility != v) softwareCursor.Visibility = v;
    }

    void PlaceOnScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Width, bounds.Height, Native.SWP_SHOWWINDOW);
    }

    public void Present()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Mouse.OverrideCursor = Cursors.Arrow;
        Show();
        PlaceOnScreen();
        Activate();
        // Take focus from the game, so it lets go of the mouse and our window gets the clicks and Esc.
        Native.ForceForeground(hwnd);
        Focus();
        Native.ClipCursor(IntPtr.Zero);
        // Taking focus from a game can leave our thread with the game's "pointer hidden" count; undo that.
        int n = Native.ShowCursor(true);
        while (n < 0) n = Native.ShowCursor(true);
        if (n > 0) Native.ShowCursor(false);
        UpdateCursor();
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        if (slide is { X: > 0 })
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        tick.Start();
        cursorTick.Start();
    }

    public void FadeOutAndClose()
    {
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
        a.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, a);
    }

    /// Fixed state for --render-ui screenshots.
    public void ShowPreviewState(int width, int height)
    {
        tick.Stop();
        c.StateChanged -= Refresh;
        if (slide != null) { slide.BeginAnimation(TranslateTransform.XProperty, null); slide.X = 0; }
        int mbps = (int)(CaptureMath.Bitrate((width, height), 60, Quality.High, VideoCodec.H264) * EncoderTuning.For(VideoCodec.H264).BitrateFactor / 1_000_000);
        demo = new OverlayState(true, 84, false, 0, true, $"Display 1 · {width}×{height} · 60 fps · H.264 · {mbps} Mbps");
        Refresh();
    }

    /// For --render-ui: Classic with its Gallery open.
    public void OpenGallery() { galleryOpen = true; setGallery?.Invoke(true); }

    void Refresh() => refresh(demo ?? new OverlayState(c.ReplayActive, c.BufferedSeconds, c.Recording,
        c.RecordingStart is { } start ? (DateTime.Now - start).TotalSeconds : 0, c.IsCapturing, c.CaptureInfo));
}

/// A page in the list of Settings pages: its name and current value; selected with a bar (Console) or a fill.
sealed class NavItem : Border
{
    public NavItem(string title, string detail, bool on, PanePalette p, bool bar, Action open)
    {
        var row = new DockPanel { LastChildFill = true };
        var d = ClipPane.Plain(detail, bar ? 12 : 10.5 * Theme.Scale, p.MutedBrush, Theme.Mono);
        d.MaxWidth = bar ? 120 : Theme.S(80);
        d.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(d, Dock.Right);
        row.Children.Add(d);
        var t = ClipPane.Plain(title, bar ? 14 : 12 * Theme.Scale, on ? p.TextBrush : Theme.Brush(p.Text, 0.85));
        if (on) t.FontWeight = FontWeights.SemiBold;
        row.Children.Add(t);
        Child = row;
        Padding = bar ? new Thickness(18, 11, 18, 11) : new Thickness(Theme.S(10), Theme.S(6), Theme.S(10), Theme.S(6));
        CornerRadius = new CornerRadius(bar ? 0 : Theme.S(8));
        BorderBrush = Theme.Brush(Theme.Accent);
        BorderThickness = new Thickness(bar && on ? 2 : 0, 0, 0, 0);
        Brush idle = on ? p.WashBrush(bar ? 0.035 : 0.1) : Brushes.Transparent;
        Background = idle;
        Cursor = Cursors.Hand;
        MouseEnter += (_, _) => { if (!on) Background = p.WashBrush(0.05); };
        MouseLeave += (_, _) => Background = idle;
        MouseLeftButtonDown += (_, e) => e.Handled = true;
        MouseLeftButtonUp += (_, e) => { e.Handled = true; if (!on) open(); };
    }
}

/// Console's action row: title, a status line and the hotkey; a bar in the tint when on.
sealed class ConsoleRow : ClickBorder
{
    readonly Color tint;
    readonly bool flashOnTap;
    readonly TextBlock title, sub, key;
    readonly Border bar, flash;
    string subText = "";
    string? hoverSub;
    bool on;

    public ConsoleRow(Color tint, bool flashOnTap = false)
    {
        this.tint = tint; this.flashOnTap = flashOnTap;
        title = ClipPane.Plain("", 15, Theme.InkBrush);
        sub = ClipPane.Plain("", 12, Brushes.Gray, Theme.Mono);
        sub.Margin = new Thickness(0, 5, 0, 0);
        key = ClipPane.Plain("", 12, Theme.Muted, Theme.Mono);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(title);
        texts.Children.Add(sub);
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(18, 16, 18, 16) };
        DockPanel.SetDock(key, Dock.Right);
        row.Children.Add(key);
        row.Children.Add(texts);
        bar = new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, Background = Theme.Brush(tint) };
        flash = new Border { Background = Theme.InkBrush, Opacity = 0, IsHitTestVisible = false };
        var grid = new Grid();
        grid.Children.Add(row);
        grid.Children.Add(bar);
        grid.Children.Add(flash);
        Child = grid;
        BorderBrush = Theme.Line;
        BorderThickness = new Thickness(0, 0, 0, 1);
    }

    public void Update(string title, string? sub, Brush subBrush, string? hoverSub, string? key, bool on)
    {
        this.title.Text = title;
        subText = sub ?? "";
        this.hoverSub = hoverSub;
        this.sub.Foreground = subBrush;
        this.sub.Visibility = sub == null ? Visibility.Collapsed : Visibility.Visible;
        this.key.Text = key ?? "";
        this.on = on;
        Restyle();
    }

    protected override void Fired() { if (flashOnTap) Flash(flash); }

    protected override void Restyle()
    {
        sub.Text = Hovering ? hoverSub ?? subText : subText;
        bar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Background = Theme.Brush(Theme.Wash, on ? 0.035 : Hovering ? 0.03 : 0);
    }
}

/// Sidebar's action segment: title and hotkey; filled in the text color when on.
sealed class SidebarAction : ClickBorder
{
    readonly PanePalette p;
    readonly Color tint;
    readonly bool flashOnTap;
    readonly TextBlock title, key;
    readonly Border flash;
    string? keyText, hoverKey;
    bool on;

    public SidebarAction(PanePalette palette, Color tint, bool flashOnTap = false)
    {
        p = palette; this.tint = tint; this.flashOnTap = flashOnTap;
        title = ClipPane.Plain("", 14, p.TextBrush);
        title.FontWeight = FontWeights.SemiBold;
        title.HorizontalAlignment = HorizontalAlignment.Center;
        key = ClipPane.Plain("", 11, p.MutedBrush, Theme.Mono);
        key.HorizontalAlignment = HorizontalAlignment.Center;
        key.Margin = new Thickness(0, 4, 0, 0);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(title);
        stack.Children.Add(key);
        flash = new Border { Background = p.WashBrush(1), Opacity = 0, IsHitTestVisible = false };
        var grid = new Grid();
        grid.Children.Add(stack);
        grid.Children.Add(flash);
        Child = grid;
    }

    public void Update(string title, string? key, string? hoverKey, bool on)
    {
        this.title.Text = title;
        keyText = key;
        this.hoverKey = hoverKey;
        this.on = on;
        Restyle();
    }

    protected override void Fired() { if (flashOnTap) Flash(flash); }

    protected override void Restyle()
    {
        var k = Hovering ? hoverKey ?? keyText : keyText;
        key.Text = k ?? "";
        key.Visibility = k == null ? Visibility.Collapsed : Visibility.Visible;
        title.Foreground = on ? Theme.Brush(p.Bg) : p.TextBrush;
        key.Foreground = on ? Theme.Brush(tint) : p.MutedBrush;
        Background = on ? p.TextBrush : p.WashBrush(Hovering ? 0.05 : 0);
    }
}

/// A border that tells left and right clicks apart and tracks hover and press, for the overlay's action buttons.
abstract class ClickBorder : Border
{
    protected bool Hovering, Pressed;
    Action left = () => { }, right = () => { };

    protected ClickBorder()
    {
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
        MouseEnter += (_, _) => { Hovering = true; HoverChanged(true); Restyle(); };
        MouseLeave += (_, _) => { Hovering = false; Pressed = false; HoverChanged(false); Restyle(); };
        MouseDown += (_, e) =>
        {
            if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
            Pressed = true; Restyle(); CaptureMouse(); e.Handled = true;
        };
        MouseUp += (_, e) =>
        {
            if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
            bool inside = new Rect(RenderSize).Contains(e.GetPosition(this));
            Pressed = false; ReleaseMouseCapture(); Restyle(); e.Handled = true;
            if (!inside) return;
            Fired();
            (e.ChangedButton == MouseButton.Left ? left : right)();
        };
    }

    public void SetActions(Action left, Action right) { this.left = left; this.right = right; }
    protected virtual void HoverChanged(bool hovering) { }
    protected virtual void Fired() { }
    protected abstract void Restyle();

    protected static void Flash(Border flash)
    {
        if (Theme.ReduceMotion) return;
        flash.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 0, TimeSpan.FromMilliseconds(350)));
    }
}

/// Round icon with a progress ring (for Instant Replay's buffer).
sealed class IconDisc : Grid
{
    readonly Ellipse circle = new();
    readonly Grid iconHost = new();
    public ProgressRing Ring { get; }
    (Sym, bool)? shown;

    public IconDisc(Color tint, double size, double ringWidth)
    {
        Width = Height = Theme.S(size);
        Ring = new ProgressRing(tint, Theme.S(ringWidth)) { Margin = new Thickness(Theme.S(ringWidth / 2)) };
        Children.Add(circle);
        Children.Add(Ring);
        Children.Add(iconHost);
    }

    public void Update(Sym icon, double glyphSize, Brush fill, Brush glyph, bool on, double? progress)
    {
        circle.Fill = fill;
        Ring.Visibility = progress.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (progress is { } p) Ring.AnimateTo(Math.Clamp(p, 0, 1));
        // The replay icon is redrawn each time, as it shows the replay length.
        if (icon == Sym.ReplayLength || (icon, on) != shown)
        {
            shown = (icon, on);
            iconHost.Children.Clear();
            iconHost.Children.Add(Icons.Make(icon, glyphSize, glyph));
        }
    }

    public void Outline(Brush? stroke, double width)
    {
        circle.Stroke = stroke;
        circle.StrokeThickness = width;
    }
}

/// One action in the Split layout's left pane: round icon, title, caption and hotkey.
sealed class ActionRow : ClickBorder
{
    readonly Color tint;
    readonly bool flashOnTap;
    readonly IconDisc disc;
    readonly TextBlock title, caption, hint;
    readonly Border hintBox, flash;
    readonly Sym defaultIcon;
    string captionText = "";
    string? hoverCaption;
    bool on;
    Sym icon;
    double? progress;

    public ActionRow(Sym icon, Color tint, bool flashOnTap)
    {
        this.tint = tint; this.flashOnTap = flashOnTap; defaultIcon = this.icon = icon;
        CornerRadius = new CornerRadius(Theme.S(12));
        BorderThickness = new Thickness(1);
        Padding = new Thickness(Theme.S(7));
        disc = new IconDisc(tint, 36, 2.2) { VerticalAlignment = VerticalAlignment.Center };
        title = Theme.Text("", 12, FontWeights.SemiBold);
        caption = Theme.Text("", 10, brush: Theme.Muted);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Theme.S(9), 0, Theme.S(4), 0) };
        texts.Children.Add(title);
        texts.Children.Add(caption);
        hint = Theme.Text("", 9.5, family: Theme.Mono);
        hintBox = new Border
        {
            Child = hint, Padding = new Thickness(Theme.S(5), Theme.S(2), Theme.S(5), Theme.S(2)), CornerRadius = new CornerRadius(Theme.S(4)),
            Background = Theme.Brush(Theme.Wash, 0.07), Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center,
        };
        flash = new Border { CornerRadius = CornerRadius, Background = Theme.InkBrush, Opacity = 0, IsHitTestVisible = false, Margin = new Thickness(-Theme.S(7)) };
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(disc, Dock.Left);
        DockPanel.SetDock(hintBox, Dock.Right);
        row.Children.Add(disc);
        row.Children.Add(hintBox);
        row.Children.Add(texts);
        var grid = new Grid();
        grid.Children.Add(row);
        grid.Children.Add(flash);
        Child = grid;
    }

    public void Update(string title, bool on, Hotkey hint, string caption, string? hoverCaption, double? progress, Sym? icon = null)
    {
        this.title.Text = title;
        this.on = on;
        captionText = caption;
        this.hoverCaption = hoverCaption;
        this.progress = progress;
        this.icon = icon ?? defaultIcon;
        this.hint.Text = hint.Display;
        hintBox.Visibility = Theme.ShowHotkeys && hint.IsSet ? Visibility.Visible : Visibility.Collapsed;
        Restyle();
    }

    protected override void Fired() { if (flashOnTap) Flash(flash); }

    protected override void Restyle()
    {
        caption.Text = Hovering ? hoverCaption ?? captionText : captionText;
        disc.Update(icon, Theme.S(15), on ? Theme.Brush(tint, 0.24) : Theme.Brush(Theme.Wash, Hovering ? 0.12 : 0.08),
            on ? Theme.Brush(tint) : Theme.Brush(Theme.Wash, 0.9), on, progress);
        Background = on ? Theme.Brush(tint, 0.1) : Theme.Brush(Theme.Wash, Hovering ? 0.09 : 0.05);
        BorderBrush = on ? Theme.Brush(tint, 0.6) : Theme.Line;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new ScaleTransform(Pressed ? 0.97 : 1, Pressed ? 0.97 : 1);
    }
}

/// Classic layout tile: label on top, big icon, status under it. Accent outline when on or hovered.
sealed class ClassicTile : ClickBorder
{
    readonly Color tint;
    readonly bool flashOnTap;
    readonly Action<bool> hover;
    readonly IconDisc disc;
    readonly TextBlock status;
    readonly Border flash;
    bool on;
    Sym icon = Sym.Record;
    double? progress;

    public ClassicTile(string title, Color tint, Action<bool> hover, bool flashOnTap = false)
    {
        this.tint = tint; this.hover = hover; this.flashOnTap = flashOnTap;
        Width = Theme.S(104);
        Padding = new Thickness(Theme.S(8), Theme.S(12), Theme.S(8), Theme.S(12));
        BorderThickness = new Thickness(Theme.S(1.5));
        var label = Theme.Text(title, 11, FontWeights.SemiBold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        disc = new IconDisc(tint, 40, 2.5) { Margin = new Thickness(0, Theme.S(8), 0, Theme.S(8)) };
        status = Theme.Text("", 9.5);
        status.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = new StackPanel();
        stack.Children.Add(label);
        stack.Children.Add(disc);
        stack.Children.Add(status);
        flash = new Border { Background = Theme.InkBrush, Opacity = 0, IsHitTestVisible = false, Margin = new Thickness(-Theme.S(8), -Theme.S(12), -Theme.S(8), -Theme.S(12)) };
        var grid = new Grid();
        grid.Children.Add(stack);
        grid.Children.Add(flash);
        Child = grid;
    }

    public void Update(Sym icon, string status, bool on, double? progress)
    {
        this.icon = icon; this.on = on; this.progress = progress;
        this.status.Text = status;
        Restyle();
    }

    protected override void HoverChanged(bool hovering) => hover(hovering);
    protected override void Fired() { if (flashOnTap) Flash(flash); }

    protected override void Restyle()
    {
        disc.Update(icon, Theme.S(17), on ? Theme.Brush(tint, 0.22) : Brushes.Transparent, on ? Theme.Brush(tint) : Theme.InkBrush, on, progress);
        disc.Outline(on ? Theme.Brush(tint, 0.5) : Theme.Brush(Theme.Wash, 0.9), Theme.S(2.5));
        status.Foreground = on ? Theme.Brush(tint) : Theme.Muted;
        Background = Theme.Brush(Theme.Wash, Hovering ? 0.06 : 0);
        BorderBrush = on || Hovering ? Theme.Brush(Theme.Accent) : Brushes.Transparent;
        disc.RenderTransformOrigin = new Point(0.5, 0.5);
        disc.RenderTransform = new ScaleTransform(Pressed ? 0.93 : 1, Pressed ? 0.93 : 1);
    }
}

/// Classic layout: the wide Gallery button.
sealed class ClassicWide : Border
{
    bool on, hover;
    public Action Clicked { get; set; } = () => { };
    public bool On { get => on; set { on = value; Restyle(); } }

    public ClassicWide(Sym icon, string title)
    {
        Cursor = Cursors.Hand;
        Padding = new Thickness(0, Theme.S(9), 0, Theme.S(9));
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var i = Icons.Make(icon, Theme.S(15), Theme.InkBrush);
        i.Margin = new Thickness(0, 0, Theme.S(8), 0);
        row.Children.Add(i);
        var t = Theme.Text(title, 11.5, FontWeights.SemiBold);
        t.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(t);
        Child = row;
        MouseEnter += (_, _) => { hover = true; Restyle(); };
        MouseLeave += (_, _) => { hover = false; Restyle(); };
        MouseLeftButtonDown += (_, e) => e.Handled = true;
        MouseLeftButtonUp += (_, e) => { e.Handled = true; Clicked(); };
        Restyle();
    }

    void Restyle()
    {
        Background = Theme.Brush(Theme.Wash, hover || on ? 0.1 : 0.05);
        BorderBrush = on ? Theme.Brush(Theme.Accent) : Theme.Line;
        BorderThickness = new Thickness(on ? Theme.S(1.5) : 1);
    }
}

/// Classic layout: small round icon button (mic, folder, settings).
sealed class ClassicRound : Border
{
    bool on, hover;
    Sym icon;
    public bool On { get => on; set { on = value; Restyle(); } }

    public ClassicRound(Sym icon, Action action)
    {
        this.icon = icon;
        Width = Height = Theme.S(30);
        CornerRadius = new CornerRadius(Theme.S(15));
        BorderThickness = new Thickness(1);
        Cursor = Cursors.Hand;
        MouseEnter += (_, _) => { hover = true; Restyle(); };
        MouseLeave += (_, _) => { hover = false; Restyle(); };
        MouseLeftButtonDown += (_, e) => e.Handled = true;
        MouseLeftButtonUp += (_, e) => { e.Handled = true; action(); };
        Restyle();
    }

    public void SetIcon(Sym icon) { this.icon = icon; Restyle(); }

    void Restyle()
    {
        Background = Theme.Brush(Theme.Wash, hover ? 0.16 : 0.08);
        BorderBrush = on ? Theme.Brush(Theme.Accent, 0.7) : Brushes.Transparent;
        Child = Icons.Make(icon, Theme.S(11), on ? Theme.Brush(Theme.Accent) : Theme.InkBrush);
    }
}

sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    readonly Pen pen;

    public ProgressRing(Color tint, double width)
    {
        pen = new Pen(Theme.Brush(tint), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        IsHitTestVisible = false;
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public void AnimateTo(double v)
    {
        if (Math.Abs(v - Value) < 0.0005) return;
        if (Theme.ReduceMotion) { Snap(v); return; }
        BeginAnimation(ValueProperty, new DoubleAnimation(v, TimeSpan.FromMilliseconds(900)));
    }

    public void Snap(double v)
    {
        BeginAnimation(ValueProperty, null);
        Value = v;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double v = Math.Clamp(Value, 0, 1);
        double r = Math.Min(ActualWidth, ActualHeight) / 2 - pen.Thickness / 2;
        if (r <= 0 || v <= 0.001) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        if (v >= 0.999) { dc.DrawEllipse(null, pen, c, r, r); return; }
        double a = v * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise, true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

sealed class RecPill : Border
{
    readonly TextBlock text;

    public RecPill()
    {
        var dot = new Ellipse { Width = Theme.S(7), Height = Theme.S(7), Fill = Theme.Brush(Theme.Record), VerticalAlignment = VerticalAlignment.Center };
        if (!Theme.ReduceMotion)
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.2, TimeSpan.FromMilliseconds(500)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        text = Theme.Text("", 12, FontWeights.Bold, Theme.Brush(Theme.Record), Theme.Mono);
        text.Margin = new Thickness(Theme.S(6), 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot);
        row.Children.Add(text);
        Child = row;
        Padding = new Thickness(Theme.S(10), Theme.S(4), Theme.S(10), Theme.S(4));
        CornerRadius = new CornerRadius(Theme.S(20));
        Background = Theme.Brush(Theme.Record, 0.22);
    }

    public string Text { set => text.Text = value; }
}

class HoverButton : Border
{
    protected readonly double IdleAlpha = 0.09, HoverAlpha = 0.16;

    public HoverButton(Action action, PanePalette? palette = null)
    {
        var wash = (palette ?? PanePalette.Overlay).Wash;
        Cursor = Cursors.Hand;
        Background = Theme.Brush(wash, IdleAlpha);
        MouseEnter += (_, _) => Background = Theme.Brush(wash, HoverAlpha);
        MouseLeave += (_, _) => Background = Theme.Brush(wash, IdleAlpha);
        MouseLeftButtonUp += (_, e) => { e.Handled = true; if (IsEnabled) action(); };
        MouseLeftButtonDown += (_, e) => e.Handled = true;
    }
}

sealed class CircleButton : HoverButton
{
    public CircleButton(Sym icon, Action action, PanePalette? palette = null) : base(action, palette)
    {
        Width = Theme.S(28); Height = Theme.S(28);
        CornerRadius = new CornerRadius(Theme.S(14));
        Child = Icons.Make(icon, Theme.S(10), (palette ?? PanePalette.Overlay).TextBrush);
        VerticalAlignment = VerticalAlignment.Center;
    }
}

sealed class Chip : HoverButton
{
    readonly TextBlock? label;

    /// A pill button; with no title, just the icon.
    public Chip(string? title, Sym icon, Action action, PanePalette? palette = null) : base(action, palette)
    {
        var ink = (palette ?? PanePalette.Overlay).TextBrush;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var i = Icons.Make(icon, Theme.S(12), ink);
        row.Children.Add(i);
        if (title != null)
        {
            i.Margin = new Thickness(0, 0, Theme.S(6), 0);
            label = Theme.Text(title, 12, FontWeights.Medium, ink);
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
        }
        Child = row;
        Padding = new Thickness(Theme.S(title == null ? 9 : 12), Theme.S(7), Theme.S(title == null ? 9 : 12), Theme.S(7));
        CornerRadius = new CornerRadius(Theme.S(20));
        VerticalAlignment = VerticalAlignment.Center;
    }

    public string Label { set { if (label != null) label.Text = value; } }
}