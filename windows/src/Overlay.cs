using System.Windows;
using System.Windows.Controls;
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
    public static void Show(string? previewPath)
    {
        ClipLibrary.Refresh();
        if (window != null)
        {
            if (previewPath != null) window.ShowClip(previewPath);
            return;
        }
        // If screen shares may see the overlay, so could Rewind's own display capture: hold the video first.
        Controller.Shared.OverlayChanged(true);
        var w = window = new OverlayWindow(Hide);
        w.Closed += (_, _) =>
        {
            // Resume a moment after it's gone, so no frame still showing it gets recorded.
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            t.Tick += (_, _) => { t.Stop(); if (window == null) Controller.Shared.OverlayChanged(false); };
            t.Start();
        };
        if (previewPath != null) w.ShowClip(previewPath);
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

/// Fullscreen in-game overlay (default Alt+Z), in the layout picked in Settings → Overlay.
sealed class OverlayWindow : Window
{
    readonly Controller c = Controller.Shared;
    readonly Settings s = Settings.Shared;
    readonly Action close;
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Native.RECT bounds;
    readonly Size screen;
    readonly OverlayLayout layout;
    readonly Grid root = new();
    Action<OverlayState> refresh = _ => { };
    OverlayState? demo;
    ClipPane? pane;
    string? pendingClip;
    // Classic layout: the clip pane is shown under the bar after clicking Gallery.
    Action<bool>? setGallery;

    public OverlayWindow(Action close, Size? screenSize = null)
    {
        this.close = close;
        bounds = Native.MonitorUnderCursor().bounds;
        double dpi = Native.DpiScaleUnderCursor();
        screen = screenSize ?? new Size(bounds.Width / dpi, bounds.Height / dpi);
        layout = s.OverlayLayout;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Opacity = 0;
        Title = "Rewind overlay";

        var dim = new Border { Background = Theme.Brush(Colors.Black, Theme.Dim) };
        dim.MouseLeftButtonDown += (_, _) => { if (pane is { IsTrimming: true }) pane.CancelTrim(); else close(); };
        root.Children.Add(dim);
        root.Children.Add(layout switch
        {
            OverlayLayout.Classic => BuildClassic(),
            OverlayLayout.Tiles => BuildTiles(),
            _ => BuildSplit(),
        });

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
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(hwnd, noActivate: false);
            Native.ExcludeFromCapture(hwnd, !s.ShareRewindWindows);
            PlaceOnScreen();
        };
        DpiChanged += (_, _) => PlaceOnScreen();
        tick.Tick += (_, _) => Refresh();
        c.StateChanged += Refresh;
        Closed += (_, _) =>
        {
            tick.Stop();
            cursorTick.Stop();
            c.StateChanged -= Refresh;
            pane?.Stop();
            Mouse.OverrideCursor = null;
        };
        Refresh();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (pane is { IsTrimming: true }) pane.CancelTrim(); else close();
            return;
        }
        if (pane == null || Keyboard.Modifiers != ModifierKeys.None) return;
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

    /// Selects a clip in the player (opening Classic's Gallery for it) and plays it.
    public void ShowClip(string path)
    {
        setGallery?.Invoke(true);
        if (pane != null) pane.Show(path); else pendingClip = path;
    }

    ClipPane MakePane(double? fixedWidth = null, double reservedWidth = 0, double reservedHeight = 0, bool showsClose = true)
    {
        var p = new ClipPane(close, screen, fixedWidth, reservedWidth, reservedHeight, showsClose);
        if (pendingClip != null) { p.Show(pendingClip); pendingClip = null; }
        return p;
    }

    // MARK: Split

    /// Actions in a narrow pane beside a player showing your newest clip.
    FrameworkElement BuildSplit()
    {
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
        chips.Children.Add(new Chip(null, Sym.Gear, () => { close(); SettingsWindow.ShowShared(); })
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
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
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

    // MARK: Tiles

    /// One panel with three big tiles, a player with your newest clip under them, the recent clips and a footer.
    FrameworkElement BuildTiles()
    {
        double tilesWidth = Theme.S(150 * 3 + 14 * 2);
        var replay = new IconTile(Theme.ReplayTitle, Theme.Replay);
        var record = new IconTile(Theme.RecordTitle, Theme.Record);
        var shot = new IconTile(Theme.ScreenshotTitle, Theme.Screenshot, flashOnTap: true);
        replay.SetActions(() => { if (c.ReplayActive) c.SaveReplay(); else c.StartReplay(); }, c.ToggleReplay);
        record.SetActions(c.ToggleRecording, c.ToggleRecording);
        shot.SetActions(c.Screenshot, c.Screenshot);

        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(Logo(26, 8, 13));
        var name = Theme.Text(Theme.Brand, 20, FontWeights.Bold);
        name.Margin = new Thickness(Theme.S(9), 0, 0, 0);
        name.VerticalAlignment = VerticalAlignment.Center;
        brand.Children.Add(name);
        var recSlot = new ContentControl { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Theme.S(12), 0, 0, 0), Focusable = false };
        brand.Children.Add(recSlot);
        var info = Theme.Text("", 12, brush: Theme.Muted);
        info.VerticalAlignment = VerticalAlignment.Center;
        info.Margin = new Thickness(Theme.S(20), 0, Theme.S(12), 0);
        var closeButton = new CircleButton(Sym.Close, close) { ToolTip = "Close (Esc)" };
        var header = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(brand, Dock.Left);
        DockPanel.SetDock(closeButton, Dock.Right);
        DockPanel.SetDock(info, Dock.Right);
        header.Children.Add(brand);
        header.Children.Add(closeButton);
        header.Children.Add(info);

        var tiles = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, Theme.S(18), 0, Theme.S(18)) };
        tiles.Children.Add(replay);
        record.Margin = new Thickness(Theme.S(14), 0, Theme.S(14), 0);
        tiles.Children.Add(record);
        tiles.Children.Add(shot);

        var sound = Theme.Text("", 12, brush: Theme.Muted);
        sound.VerticalAlignment = VerticalAlignment.Center;
        var chips = new StackPanel { Orientation = Orientation.Horizontal };
        chips.Children.Add(new Chip(Theme.FolderLabel, Sym.Folder, () => { close(); c.OpenClipsFolder(); }));
        chips.Children.Add(new Chip(Theme.SettingsLabel, Sym.Gear, () => { close(); SettingsWindow.ShowShared(); }) { Margin = new Thickness(Theme.S(8), 0, 0, 0) });
        var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, Theme.S(18), 0, 0) };
        DockPanel.SetDock(chips, Dock.Left);
        DockPanel.SetDock(sound, Dock.Right);
        footer.Children.Add(chips);
        footer.Children.Add(sound);

        pane = MakePane(fixedWidth: tilesWidth, reservedHeight: Theme.S(420), showsClose: false);
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(tiles);
        stack.Children.Add(pane);
        stack.Children.Add(footer);
        var panel = Surface(stack, Theme.S(Theme.Radius), Theme.S(22));
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.VerticalAlignment = VerticalAlignment.Center;

        refresh = st =>
        {
            replay.Update(Sym.ReplayLength, st.ReplayOn ? "ON" : null,
                st.ReplayOn ? s.Hotkey(HotkeyAction.SaveReplay) : s.Hotkey(HotkeyAction.ToggleReplay),
                st.ReplayOn ? $"Save last {Format.Duration(st.Buffered)}" : "Click to turn on",
                st.ReplayOn ? "Right-click to turn off" : null,
                st.ReplayOn ? st.Buffered / Math.Max(s.ReplaySeconds, 1) : null);
            record.Update(st.Recording ? Sym.Stop : Sym.Record, st.Recording ? "REC" : null, s.Hotkey(HotkeyAction.ToggleRecording),
                st.Recording ? "Click to stop" : "Start recording", null, null);
            shot.Update(Sym.Camera, null, s.Hotkey(HotkeyAction.Screenshot), "Full-res PNG", null, null);
            recSlot.Content = st.Recording ? new RecPill { Text = Format.Duration(st.RecordingSeconds) } : null;
            info.Text = st.Capturing ? st.Info : "Not capturing";
            sound.Text = SoundText();
        };
        return panel;
    }

    // MARK: Classic

    /// ShadowPlay-style: a title strip across the top, square tiles (label, icon, status) in a dark panel, a
    /// Gallery panel to the right with small round buttons, and a hotkey hint bar underneath.
    FrameworkElement BuildClassic()
    {
        var strip = new Grid { Height = Theme.S(40), Background = Theme.Brush(Colors.Black, 0.45), VerticalAlignment = VerticalAlignment.Top };
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
        var hintText = Theme.Text("", 10.5, brush: Theme.Brush(Colors.White, 0.85));
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
        var rounds = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, Theme.S(8), 0, 0) };
        rounds.Children.Add(mic);
        rounds.Children.Add(new ClassicRound(Sym.Folder, () => { close(); c.OpenClipsFolder(); }) { ToolTip = Theme.FolderLabel, Margin = new Thickness(Theme.S(8), 0, Theme.S(8), 0) });
        rounds.Children.Add(new ClassicRound(Sym.Gear, () => { close(); SettingsWindow.ShowShared(); }) { ToolTip = Theme.SettingsLabel });
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
        var keyIcon = Icons.Make(Sym.Keyboard, Theme.S(10), Theme.Brush(Colors.White, 0.85));
        keyIcon.Margin = new Thickness(0, 0, Theme.S(6), 0);
        hintRow.Children.Add(keyIcon);
        hintRow.Children.Add(hintText);
        var hintPanel = ClassicPanel(hintRow, 0);
        hintPanel.Padding = new Thickness(Theme.S(10), Theme.S(5), Theme.S(10), Theme.S(5));
        hintPanel.HorizontalAlignment = HorizontalAlignment.Center;
        hintPanel.Margin = new Thickness(0, Theme.S(8), 0, 0);

        pane = MakePane(reservedHeight: Theme.S(300));
        var galleryPane = ClassicPanel(pane, Theme.S(14));
        galleryPane.HorizontalAlignment = HorizontalAlignment.Center;
        galleryPane.Margin = new Thickness(0, Theme.S(8), 0, 0);
        galleryPane.Visibility = Visibility.Collapsed;

        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, Theme.S(150), 0, 0) };
        column.Children.Add(bar);
        column.Children.Add(hintPanel);
        column.Children.Add(galleryPane);

        bool open = false;
        setGallery = on =>
        {
            if (open == on) return;
            open = on;
            gallery.On = on;
            galleryPane.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            column.Margin = new Thickness(0, Theme.S(on ? 64 : 150), 0, 0);
            if (on && !Theme.ReduceMotion) galleryPane.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        };
        gallery.Clicked = () => setGallery(!open);

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
        var panel = new Border
        {
            Padding = new Thickness(padding),
            CornerRadius = new CornerRadius(radius),
            Background = Theme.Brush(Theme.PanelBase, Theme.PanelOpacity),
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            Child = child,
            Effect = new DropShadowEffect { BlurRadius = Theme.S(40), ShadowDepth = Theme.S(15), Direction = 270, Opacity = 0.45, Color = Colors.Black },
        };
        var sheen = new Border
        {
            CornerRadius = panel.CornerRadius, IsHitTestVisible = false, BorderThickness = new Thickness(1),
            BorderBrush = new LinearGradientBrush(Color.FromArgb(18, 255, 255, 255), Colors.Transparent, 90),
        };
        var g = new Grid();
        g.Children.Add(panel);
        g.Children.Add(sheen);
        return g;
    }

    /// Near-black, square-cornered panel of the Classic layout.
    static Border ClassicPanel(UIElement child, double padding) => new()
    {
        Background = Theme.Brush(Colors.Black, 0.78),
        BorderBrush = Theme.Brush(Colors.White, 0.08),
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
        int mbps = (int)(CaptureMath.Bitrate((width, height), 60, Quality.High, VideoCodec.H264) * EncoderTuning.For(VideoCodec.H264).BitrateFactor / 1_000_000);
        demo = new OverlayState(true, 84, false, 0, true, $"{width}×{height} · 60 fps · H.264 · {mbps} Mbps");
        Refresh();
    }

    /// For --render-ui: Classic with its Gallery open.
    public void OpenGallery() => setGallery?.Invoke(true);

    void Refresh() => refresh(demo ?? new OverlayState(c.ReplayActive, c.BufferedSeconds, c.Recording,
        c.RecordingStart is { } start ? (DateTime.Now - start).TotalSeconds : 0, c.IsCapturing, c.CaptureInfo));
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
            Background = Theme.Brush(Colors.White, 0.07), Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center,
        };
        flash = new Border { CornerRadius = CornerRadius, Background = Brushes.White, Opacity = 0, IsHitTestVisible = false, Margin = new Thickness(-Theme.S(7)) };
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
        disc.Update(icon, Theme.S(15), on ? Theme.Brush(tint, 0.24) : Theme.Brush(Colors.White, Hovering ? 0.12 : 0.08),
            on ? Theme.Brush(tint) : Theme.Brush(Colors.White, 0.9), on, progress);
        Background = on ? Theme.Brush(tint, 0.1) : Theme.Brush(Colors.White, Hovering ? 0.09 : 0.05);
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
        flash = new Border { Background = Brushes.White, Opacity = 0, IsHitTestVisible = false, Margin = new Thickness(-Theme.S(8), -Theme.S(12), -Theme.S(8), -Theme.S(12)) };
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
        disc.Update(icon, Theme.S(17), on ? Theme.Brush(tint, 0.22) : Brushes.Transparent, on ? Theme.Brush(tint) : Brushes.White, on, progress);
        disc.Outline(on ? Theme.Brush(tint, 0.5) : Theme.Brush(Colors.White, 0.9), Theme.S(2.5));
        status.Foreground = on ? Theme.Brush(tint) : Theme.Muted;
        Background = Theme.Brush(Colors.White, Hovering ? 0.06 : 0);
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
        var i = Icons.Make(icon, Theme.S(15), Brushes.White);
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
        Background = Theme.Brush(Colors.White, hover || on ? 0.1 : 0.05);
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
        Background = Theme.Brush(Colors.White, hover ? 0.16 : 0.08);
        BorderBrush = on ? Theme.Brush(Theme.Accent, 0.7) : Brushes.Transparent;
        Child = Icons.Make(icon, Theme.S(11), on ? Theme.Brush(Theme.Accent) : Brushes.White);
    }
}

/// Big round icon button with title and hotkey hint (Tiles layout).
sealed class IconTile : ClickBorder
{
    readonly Color tint;
    readonly bool flashOnTap;
    readonly IconDisc disc;
    readonly TextBlock caption, hint, badge;
    readonly Border hintBox, badgeBox, flash;
    readonly ScaleTransform scale = new(1, 1);
    string captionText = "", hoverCaptionText = "";
    Sym icon = Sym.Record;
    double? progress;
    bool on;

    public IconTile(string title, Color tint, bool flashOnTap = false)
    {
        this.tint = tint; this.flashOnTap = flashOnTap;
        Width = Theme.S(150);
        CornerRadius = new CornerRadius(Theme.S(Theme.Radius * 0.72));
        BorderThickness = new Thickness(1);
        Padding = new Thickness(Theme.S(12), Theme.S(18), Theme.S(12), Theme.S(14));

        disc = new IconDisc(tint, 72, 3) { RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale };
        var titleText = Theme.Text(title, 14, FontWeights.SemiBold);
        titleText.HorizontalAlignment = HorizontalAlignment.Center;
        titleText.Margin = new Thickness(0, Theme.S(9), 0, 0);
        caption = Theme.Text("", 11.5, brush: Theme.Muted);
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        caption.Margin = new Thickness(0, Theme.S(9), 0, 0);
        hint = Theme.Text("", 11, FontWeights.SemiBold, family: Theme.Mono);
        hintBox = new Border
        {
            Child = hint, Padding = new Thickness(Theme.S(6), Theme.S(2), Theme.S(6), Theme.S(2)),
            CornerRadius = new CornerRadius(Theme.S(5)), Background = Theme.Brush(Colors.White, 0.07), Opacity = 0.75,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, Theme.S(9), 0, 0),
        };

        var stack = new StackPanel();
        stack.Children.Add(disc);
        stack.Children.Add(titleText);
        stack.Children.Add(caption);
        stack.Children.Add(hintBox);

        badge = new TextBlock { FontSize = Theme.S(10), FontWeight = FontWeights.Black, FontFamily = Theme.Font, Foreground = Theme.Brush(tint) };
        badgeBox = new Border
        {
            Child = badge, Padding = new Thickness(Theme.S(7), Theme.S(4), Theme.S(7), Theme.S(4)),
            CornerRadius = new CornerRadius(Theme.S(20)), Background = Theme.Brush(tint, 0.25),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -Theme.S(8), -Theme.S(2), 0), Visibility = Visibility.Collapsed,
        };
        flash = new Border
        {
            CornerRadius = CornerRadius, Background = Brushes.White, Opacity = 0, IsHitTestVisible = false,
            Margin = new Thickness(-Theme.S(12), -Theme.S(18), -Theme.S(12), -Theme.S(14)),
        };
        var grid = new Grid();
        grid.Children.Add(stack);
        grid.Children.Add(badgeBox);
        grid.Children.Add(flash);
        Child = grid;
    }

    public void Update(Sym icon, string? badgeText, Hotkey hint, string caption, string? hoverCaption, double? progress)
    {
        on = badgeText != null;
        this.icon = icon;
        this.progress = progress;
        this.hint.Text = hint.Display;
        hintBox.Visibility = Theme.ShowHotkeys && hint.IsSet ? Visibility.Visible : Visibility.Collapsed;
        captionText = caption;
        hoverCaptionText = hoverCaption ?? caption;
        badge.Text = badgeText ?? "";
        badgeBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Restyle();
    }

    protected override void Fired() { if (flashOnTap) Flash(flash); }

    protected override void Restyle()
    {
        caption.Text = Hovering ? hoverCaptionText : captionText;
        disc.Update(icon, Theme.S(icon is Sym.Camera ? 30 : 32), on ? Theme.Brush(tint, 0.24) : Theme.Brush(Colors.White, Hovering ? 0.11 : 0.07),
            on ? Theme.Brush(tint) : Theme.Brush(Colors.White, 0.9), on, progress);
        Background = on
            ? new LinearGradientBrush(tint.WithAlpha(0.16), Color.FromArgb(13, 255, 255, 255), new Point(0, 0), new Point(0.5, 0.5))
            : Theme.Brush(Colors.White, Hovering ? 0.09 : 0.05);
        BorderBrush = on ? Theme.Brush(tint, 0.6) : Theme.Line;
        double target = Pressed ? 0.94 : Hovering ? 1.04 : 1;
        if (Theme.ReduceMotion) { scale.ScaleX = scale.ScaleY = 1; return; }
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(Pressed ? 80 : 180))
        {
            EasingFunction = Pressed ? new QuadraticEase { EasingMode = EasingMode.EaseOut } : new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
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

    public HoverButton(Action action)
    {
        Cursor = Cursors.Hand;
        Background = Theme.Brush(Colors.White, IdleAlpha);
        MouseEnter += (_, _) => Background = Theme.Brush(Colors.White, HoverAlpha);
        MouseLeave += (_, _) => Background = Theme.Brush(Colors.White, IdleAlpha);
        MouseLeftButtonUp += (_, e) => { e.Handled = true; if (IsEnabled) action(); };
        MouseLeftButtonDown += (_, e) => e.Handled = true;
    }
}

sealed class CircleButton : HoverButton
{
    public CircleButton(Sym icon, Action action) : base(action)
    {
        Width = Theme.S(28); Height = Theme.S(28);
        CornerRadius = new CornerRadius(Theme.S(14));
        Child = Icons.Make(icon, Theme.S(10), Brushes.White);
        VerticalAlignment = VerticalAlignment.Center;
    }
}

sealed class Chip : HoverButton
{
    readonly TextBlock? label;

    /// A pill button; with no title, just the icon.
    public Chip(string? title, Sym icon, Action action) : base(action)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var i = Icons.Make(icon, Theme.S(12), Brushes.White);
        row.Children.Add(i);
        if (title != null)
        {
            i.Margin = new Thickness(0, 0, Theme.S(6), 0);
            label = Theme.Text(title, 12, FontWeights.Medium);
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
