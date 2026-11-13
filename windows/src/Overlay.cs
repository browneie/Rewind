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

    /// Opens the overlay, or with `previewPath` straight into that clip's preview.
    public static void Show(string? previewPath)
    {
        if (window != null)
        {
            if (previewPath != null) window.OpenPreview(previewPath);
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
        w.Present();
        ClipLibrary.Refresh();
        if (previewPath != null) w.OpenPreview(previewPath);
    }

    public static void Hide()
    {
        var w = window;
        if (w == null) return;
        window = null;
        w.FadeOutAndClose();
    }
}

sealed class OverlayWindow : Window
{
    readonly Controller c = Controller.Shared;
    readonly Settings s = Settings.Shared;
    readonly Action close;
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Native.RECT bounds;
    readonly IconTile replayTile, recordTile, screenshotTile;
    readonly TextBlock info, sound;
    readonly StackPanel headerLeft, home;
    readonly Border panel;
    ClipPreviewPane? preview;
    RecPill? recPill;

    public OverlayWindow(Action close)
    {
        this.close = close;
        bounds = Native.MonitorUnderCursor().bounds;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Opacity = 0;
        Title = "Rewind overlay";

        var root = new Grid();
        var dim = new Border { Background = Theme.Brush(Colors.Black, Theme.Dim) };
        dim.MouseLeftButtonDown += (_, _) => close();
        root.Children.Add(dim);

        var logo = new Border
        {
            Width = Theme.S(26), Height = Theme.S(26), CornerRadius = new CornerRadius(Theme.S(8)),
            Background = new LinearGradientBrush(Theme.Accent, Theme.Accent.Mix(Colors.Black, 0.45), 45),
            Child = Icons.RewindArrow(Theme.S(17), Brushes.Black, dot: false),
        };
        var brand = Theme.Text(Theme.Brand, 20, FontWeights.Bold);
        brand.Margin = new Thickness(Theme.S(9), 0, 0, 0);
        brand.VerticalAlignment = VerticalAlignment.Center;
        headerLeft = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        headerLeft.Children.Add(logo);
        headerLeft.Children.Add(brand);

        info = Theme.Text("", 12, brush: Theme.Muted);
        info.VerticalAlignment = VerticalAlignment.Center;
        info.Margin = new Thickness(Theme.S(20), 0, Theme.S(12), 0);
        var closeButton = new CircleButton(Sym.Close, close) { ToolTip = "Close (Esc)" };

        var header = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(headerLeft, Dock.Left);
        DockPanel.SetDock(closeButton, Dock.Right);
        DockPanel.SetDock(info, Dock.Right);
        header.Children.Add(headerLeft);
        header.Children.Add(closeButton);
        header.Children.Add(info);

        replayTile = new IconTile(Theme.ReplayTitle, Theme.Replay,
            left: () => { if (c.ReplayActive) c.SaveReplay(); else c.StartReplay(); },
            right: () => c.ToggleReplay());
        recordTile = new IconTile(Theme.RecordTitle, Theme.Record, left: () => c.ToggleRecording(), right: () => c.ToggleRecording());
        screenshotTile = new IconTile(Theme.ScreenshotTitle, Theme.Screenshot, left: () => c.Screenshot(), right: () => c.Screenshot(), flashOnTap: true);
        var tiles = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, Theme.S(18), 0, Theme.S(18)) };
        tiles.Children.Add(replayTile);
        recordTile.Margin = new Thickness(Theme.S(14), 0, Theme.S(14), 0);
        tiles.Children.Add(recordTile);
        tiles.Children.Add(screenshotTile);

        var chips = new StackPanel { Orientation = Orientation.Horizontal };
        chips.Children.Add(new Chip(Theme.FolderLabel, Sym.Folder, () => { close(); c.OpenClipsFolder(); }));
        var settingsChip = new Chip(Theme.SettingsLabel, Sym.Gear, () => { close(); SettingsWindow.ShowShared(); });
        settingsChip.Margin = new Thickness(Theme.S(8), 0, 0, 0);
        chips.Children.Add(settingsChip);
        sound = Theme.Text("", 12, brush: Theme.Muted);
        sound.VerticalAlignment = VerticalAlignment.Center;
        var footer = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(chips, Dock.Left);
        DockPanel.SetDock(sound, Dock.Right);
        footer.Children.Add(chips);
        footer.Children.Add(sound);

        home = new StackPanel();
        home.Children.Add(header);
        home.Children.Add(tiles);
        home.Children.Add(new RecentClipsRow(Theme.S(150 * 3 + 14 * 2), item => OpenPreview(item.FilePath)));
        home.Children.Add(footer);

        panel = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(Theme.S(22)),
            CornerRadius = new CornerRadius(Theme.S(Theme.Radius)),
            Background = Theme.Brush(Theme.PanelBase, Theme.PanelOpacity),
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            Child = home,
            Effect = new DropShadowEffect { BlurRadius = Theme.S(40), ShadowDepth = Theme.S(15), Direction = 270, Opacity = 0.45, Color = Colors.Black },
        };
        var sheen = new Border
        {
            CornerRadius = panel.CornerRadius, IsHitTestVisible = false, BorderThickness = new Thickness(1),
            BorderBrush = new LinearGradientBrush(Color.FromArgb(18, 255, 255, 255), Colors.Transparent, 90),
        };
        var panelGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panelGrid.Children.Add(panel);
        panelGrid.Children.Add(sheen);
        root.Children.Add(panelGrid);

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
            preview?.Stop();
            Mouse.OverrideCursor = null;
        };
        Refresh();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Escape when preview is { IsTrimming: true }: preview.CancelTrim(); break;
            case Key.Escape: close(); break;
            case Key.Space when preview != null: preview.TogglePlay(); break;
            case Key.Left when preview != null: preview.Step(-1); break;
            case Key.Right when preview != null: preview.Step(1); break;
            case Key.Enter when preview is { IsTrimming: true }: preview.ConfirmTrim(); break;
            default: e.Handled = false; break;
        }
    }

    public void OpenPreview(string path) => OpenPreview(path, null);

    public void OpenPreview(string path, Size? screenSize)
    {
        if (!File.Exists(path)) return;
        preview?.Stop();
        var item = ClipLibrary.Items.FirstOrDefault(i => string.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase))
                   ?? new ClipItem(path, File.GetCreationTime(path));
        var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var screen = screenSize ?? new Size(bounds.Width * m.M11, bounds.Height * m.M22);
        preview = new ClipPreviewPane(item, screen, ShowHome, close);
        panel.Child = preview;
    }

    void ShowHome()
    {
        preview?.Stop();
        preview = null;
        panel.Child = home;
        ClipLibrary.Refresh();
    }

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

    public void ShowPreviewState(int width, int height)
    {
        tick.Stop();
        c.StateChanged -= Refresh;
        replayTile.Update(Sym.Replay, "ON", s.Hotkey(HotkeyAction.SaveReplay).Display, "Save last 1:24", "Right-click to turn off", 0.7);
        recordTile.Update(Sym.Stop, "REC", s.Hotkey(HotkeyAction.ToggleRecording).Display, "Click to stop", null, null);
        recPill = new RecPill { Margin = new Thickness(Theme.S(12), 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Text = "0:42" };
        headerLeft.Children.Add(recPill);
        int mbps = (int)(CaptureMath.Bitrate((width, height), 60, Quality.High, VideoCodec.H264) * EncoderTuning.For(VideoCodec.H264).BitrateFactor / 1_000_000);
        info.Text = $"{width}×{height} · 60 fps · H.264 · {mbps} Mbps";
        replayTile.SnapProgress(0.7);
    }

    void Refresh()
    {
        bool on = c.ReplayActive;
        replayTile.Update(Sym.Replay, on ? "ON" : null,
            hint: on ? s.Hotkey(HotkeyAction.SaveReplay).Display : s.Hotkey(HotkeyAction.ToggleReplay).Display,
            caption: on ? $"Save last {Format.Duration(c.BufferedSeconds)}" : "Click to turn on",
            hoverCaption: on ? "Right-click to turn off" : null,
            progress: on ? c.BufferedSeconds / Math.Max(s.ReplaySeconds, 1) : null);

        bool rec = c.Recording;
        recordTile.Update(rec ? Sym.Stop : Sym.Record, rec ? "REC" : null, s.Hotkey(HotkeyAction.ToggleRecording).Display,
            rec ? "Click to stop" : "Start recording", null, null);
        screenshotTile.Update(Sym.Camera, null, s.Hotkey(HotkeyAction.Screenshot).Display, "Full-res PNG", null, null);

        if (rec && c.RecordingStart is { } start)
        {
            if (recPill == null)
            {
                recPill = new RecPill { Margin = new Thickness(Theme.S(12), 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                headerLeft.Children.Add(recPill);
            }
            recPill.Text = Format.Duration((DateTime.Now - start).TotalSeconds);
        }
        else if (recPill != null)
        {
            headerLeft.Children.Remove(recPill);
            recPill = null;
        }
        info.Text = c.IsCapturing ? c.CaptureInfo : "Not capturing";
        sound.Text = $"Sound: {(s.SystemAudio ? "system" : "off")}{(s.Microphone ? " + mic" : "")}";
    }
}

sealed class IconTile : Border
{
    readonly Color tint;
    readonly Action left, right;
    readonly bool flashOnTap;
    readonly Ellipse circle = new();
    readonly ProgressRing ring;
    readonly Grid iconHost = new();
    readonly Grid iconBox;
    readonly TextBlock caption, hint, badge;
    readonly Border hintBox, badgeBox, flash;
    readonly ScaleTransform scale = new(1, 1);
    string captionText = "", hoverCaptionText = "";
    Sym? currentIcon;
    (Sym, bool)? shownIcon;
    bool on, hover, pressed;

    public IconTile(string title, Color tint, Action left, Action right, bool flashOnTap = false)
    {
        this.tint = tint; this.left = left; this.right = right; this.flashOnTap = flashOnTap;
        Width = Theme.S(150);
        CornerRadius = new CornerRadius(Theme.S(Theme.Radius * 0.72));
        BorderThickness = new Thickness(1);
        Padding = new Thickness(Theme.S(12), Theme.S(18), Theme.S(12), Theme.S(14));
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;

        ring = new ProgressRing(tint) { Margin = new Thickness(Theme.S(1.5)) };
        iconBox = new Grid
        {
            Width = Theme.S(72), Height = Theme.S(72), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale,
        };
        iconBox.Children.Add(circle);
        iconBox.Children.Add(ring);
        iconBox.Children.Add(iconHost);

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
            Visibility = Theme.ShowHotkeys ? Visibility.Visible : Visibility.Collapsed,
        };

        var stack = new StackPanel();
        stack.Children.Add(iconBox);
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

        MouseEnter += (_, _) => { hover = true; Restyle(); };
        MouseLeave += (_, _) => { hover = false; pressed = false; Restyle(); };
        MouseDown += (_, e) =>
        {
            if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
            pressed = true; Restyle(); CaptureMouse(); e.Handled = true;
        };
        MouseUp += (_, e) =>
        {
            if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
            bool inside = new Rect(RenderSize).Contains(e.GetPosition(this));
            pressed = false; ReleaseMouseCapture(); Restyle(); e.Handled = true;
            if (!inside) return;
            if (this.flashOnTap)
            {
                flash.Opacity = 0.25;
                flash.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 0, TimeSpan.FromMilliseconds(350)));
            }
            (e.ChangedButton == MouseButton.Left ? this.left : this.right)();
        };
    }

    public void Update(Sym icon, string? badgeText, string hint, string caption, string? hoverCaption, double? progress)
    {
        on = badgeText != null;
        currentIcon = icon;
        this.hint.Text = hint;
        captionText = caption;
        hoverCaptionText = hoverCaption ?? caption;
        badge.Text = badgeText ?? "";
        badgeBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ring.Visibility = progress.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (progress is { } p) ring.AnimateTo(Math.Clamp(p, 0, 1));
        Restyle();
    }

    public void SnapProgress(double v)
    {
        ring.BeginAnimation(ProgressRing.ValueProperty, null);
        ring.Value = v;
    }

    void Restyle()
    {
        caption.Text = hover ? hoverCaptionText : captionText;
        circle.Fill = on ? Theme.Brush(tint, 0.24) : Theme.Brush(Colors.White, hover ? 0.11 : 0.07);
        if (currentIcon is { } icon && (icon, on) != shownIcon)
        {
            shownIcon = (icon, on);
            iconHost.Children.Clear();
            iconHost.Children.Add(Icons.Make(icon, Theme.S(icon is Sym.Camera ? 30 : 32),
                on ? Theme.Brush(tint) : Theme.Brush(Colors.White, 0.9)));
        }
        Background = on
            ? new LinearGradientBrush(tint.WithAlpha(0.16), Color.FromArgb(13, 255, 255, 255), new Point(0, 0), new Point(0.5, 0.5))
            : Theme.Brush(Colors.White, hover ? 0.09 : 0.05);
        BorderBrush = on ? Theme.Brush(tint, 0.6) : Theme.Line;
        double target = pressed ? 0.94 : hover ? 1.04 : 1;
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(pressed ? 80 : 180))
        {
            EasingFunction = pressed ? new QuadraticEase { EasingMode = EasingMode.EaseOut } : new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut },
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

    public ProgressRing(Color tint)
    {
        pen = new Pen(Theme.Brush(tint), Theme.S(3)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        IsHitTestVisible = false;
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public void AnimateTo(double v)
    {
        if (Math.Abs(v - Value) < 0.0005) return;
        BeginAnimation(ValueProperty, new DoubleAnimation(v, TimeSpan.FromMilliseconds(900)));
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
        MouseLeftButtonUp += (_, e) => { e.Handled = true; action(); };
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
    public Chip(string title, Sym icon, Action action) : base(action)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var i = Icons.Make(icon, Theme.S(12), Brushes.White);
        i.Margin = new Thickness(0, 0, Theme.S(6), 0);
        row.Children.Add(i);
        var t = Theme.Text(title, 12, FontWeights.Medium);
        t.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(t);
        Child = row;
        Padding = new Thickness(Theme.S(12), Theme.S(7), Theme.S(12), Theme.S(7));
        CornerRadius = new CornerRadius(Theme.S(20));
    }
}
