using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Rewind;

static class Toast
{
    static ToastWindow? current;
    // While held (the launch update check) or while a pinned notice is up (the update offer), other notices
    // wait; the most recent one shows once the way is clear.
    static bool held, pinnedUp;
    static Action? waiting;

    public static void Error(string title, Exception error) =>
        Show(title, error.Message, Sym.Error, Theme.Red, duration: 6);

    /// Keeps other notices back until Release() or `seconds` pass, whichever comes first.
    public static void Hold(double seconds)
    {
        held = true;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); Release(); };
        t.Start();
    }

    public static void Release()
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess()) { d.BeginInvoke(Release); return; }
        if (!held) return;
        held = false;
        ShowWaiting();
    }

    static void ShowWaiting()
    {
        if (held || pinnedUp || waiting is not { } next) return;
        waiting = null;
        next();
    }

    /// `closable` adds an ✕ that dismisses the notice without running `onClick`.
    /// `pinned` notices can't be replaced: others wait until it's clicked, closed or times out.
    /// `button` adds a small button (like "Copy") that runs its own action instead of `onClick`.
    public static void Show(string title, string? subtitle, Sym icon, Color tint, double duration = 3.5, Action? onClick = null,
                            bool closable = false, bool pinned = false, (string Title, Action Action)? button = null)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null) return;
        if (!d.CheckAccess()) { d.BeginInvoke(() => Show(title, subtitle, icon, tint, duration, onClick, closable, pinned, button)); return; }

        if (!pinned && (held || pinnedUp))
        {
            waiting = () => Show(title, subtitle, icon, tint, duration, onClick, closable, button: button);
            return;
        }
        var old = current;
        current = null;
        if (old != null) { old.Replaced = true; old.Dismiss(fast: true); }
        pinnedUp = pinned;
        var w = new ToastWindow(title, subtitle, icon, tint, onClick, closable, button) { Pinned = pinned };
        current = w;
        w.Closed += (_, _) =>
        {
            if (current == w) current = null;
            if (w.Pinned && !w.Replaced) { pinnedUp = false; ShowWaiting(); }
        };
        w.ShowFor(duration);
    }

    sealed class ToastWindow : Window
    {
        readonly Action? onClick;
        readonly DispatcherTimer timer = new();
        bool dismissing;
        public bool Pinned, Replaced;

        public ToastWindow(string title, string? subtitle, Sym icon, Color tint, Action? onClick, bool closable,
                           (string Title, Action Action)? button)
        {
            this.onClick = onClick;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Opacity = 0;
            Left = -10000; Top = -10000;
            UseLayoutRounding = true; // sizes are ×1.35 (Theme.S); snapped to pixels, text and edges stay sharp
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            var content = BuildCard(title, subtitle, icon, tint, onClick != null, closable ? () => Dismiss(fast: false) : null,
                button is { } tb ? (tb.Title, () => { tb.Action(); Dismiss(fast: false); }) : null);
            Content = content;
            content.MouseLeftButtonUp += (_, _) => { this.onClick?.Invoke(); Dismiss(fast: false); };

            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                Native.MakeToolWindow(hwnd, noActivate: true);
            };
            SizeChanged += (_, _) => Place();
            DpiChanged += (_, _) => Place();
        }

        void Place()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var (_, work) = Native.MonitorUnderCursor();
            var dpi = VisualTreeHelper.GetDpi(this);
            int w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, work.Right - w, work.Top, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void ShowFor(double seconds)
        {
            Show();
            Place();
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
            timer.Interval = TimeSpan.FromSeconds(seconds);
            timer.Tick += (_, _) => Dismiss(fast: false);
            timer.Start();
        }

        public void Dismiss(bool fast)
        {
            if (dismissing) return;
            dismissing = true;
            timer.Stop();
            if (fast) { Close(); return; }
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            a.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, a);
        }
    }

    /// Notifications are always dark, even while a light overlay is open.
    public static FrameworkElement BuildCard(string title, string? subtitle, Sym icon, Color tint, bool clickable, Action? close = null,
                                             (string Title, Action Action)? button = null)
    {
        bool light = Theme.Light;
        Theme.Light = false;
        try { return BuildDarkCard(title, subtitle, icon, tint, clickable, close, button); }
        finally { Theme.Light = light; }
    }

    static FrameworkElement BuildDarkCard(string title, string? subtitle, Sym icon, Color tint, bool clickable, Action? close,
                                          (string Title, Action Action)? button)
    {
        // The Classic layout's look (ShadowPlay-style): square, near-black, with an edge in the notice's color.
        bool classic = Settings.Shared.OverlayLayout == OverlayLayout.Classic;
        {
            var iconBox = new Border
            {
                Width = Theme.S(30), Height = Theme.S(30), CornerRadius = new CornerRadius(Theme.S(classic ? 15 : 9)),
                Background = Theme.Brush(tint, 0.22), VerticalAlignment = VerticalAlignment.Center,
                Child = Icons.Make(icon, Theme.S(icon is Sym.Replay or Sym.ReplayOff or Sym.Record or Sym.RecordFill ? 18 : 15), Theme.Brush(tint)),
            };
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Theme.S(12), 0, 0, 0) };
            texts.Children.Add(Theme.Text(title, 13, FontWeights.SemiBold));
            if (!string.IsNullOrEmpty(subtitle))
            {
                var sub = Theme.Text(subtitle, 11.5, brush: Theme.Muted);
                sub.TextWrapping = TextWrapping.Wrap;
                sub.MaxHeight = Theme.S(11.5) * 2.7;
                sub.Margin = new Thickness(0, Theme.S(2), 0, 0);
                texts.Children.Add(sub);
            }
            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(iconBox, Dock.Left);
            row.Children.Add(iconBox);
            if (button is { } btn)
            {
                var pill = new Border
                {
                    CornerRadius = new CornerRadius(classic ? 0 : Theme.S(11)), Background = classic ? Theme.Brush(tint) : Theme.Brush(Colors.White, 0.14),
                    Padding = new Thickness(Theme.S(10), Theme.S(4), Theme.S(10), Theme.S(4)),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Theme.S(8), 0, 0, 0), Cursor = Cursors.Hand,
                    Child = Theme.Text(classic ? btn.Title.ToUpperInvariant() : btn.Title, 11, FontWeights.SemiBold, classic ? Brushes.Black : null),
                };
                var idle = pill.Background;
                pill.MouseEnter += (_, _) => pill.Background = classic ? Theme.Brush(tint, 0.85) : Theme.Brush(Colors.White, 0.22);
                pill.MouseLeave += (_, _) => pill.Background = idle;
                pill.MouseLeftButtonUp += (_, e) => { e.Handled = true; btn.Action(); };
                DockPanel.SetDock(pill, Dock.Right);
                row.Children.Add(pill);
            }
            if (close != null)
            {
                var x = new Border
                {
                    Width = Theme.S(22), Height = Theme.S(22), CornerRadius = new CornerRadius(classic ? 0 : Theme.S(11)),
                    Background = Theme.Brush(Colors.White, 0.1), VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(Theme.S(8), 0, 0, 0), Cursor = Cursors.Hand, ToolTip = "Not now",
                    Child = new TextBlock
                    {
                        Text = "", FontFamily = Theme.Symbols, FontSize = Theme.S(9), FontWeight = FontWeights.Bold,
                        Foreground = Theme.Brush(Colors.White, 0.8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                x.MouseLeftButtonUp += (_, e) => { e.Handled = true; close(); };
                DockPanel.SetDock(x, Dock.Right);
                row.Children.Add(x);
            }
            else if (clickable)
            {
                var chevron = new TextBlock
                {
                    Text = "", FontFamily = Theme.Symbols, FontSize = Theme.S(10), Foreground = Theme.Brush(Colors.White, 0.35),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Theme.S(8), 0, 0, 0),
                };
                DockPanel.SetDock(chevron, Dock.Right);
                row.Children.Add(chevron);
            }
            row.Children.Add(texts);

            var radius = new CornerRadius(classic ? 0 : Theme.S(Theme.Radius * 0.6));
            // The shadow is its own layer: an effect on the card would draw its text through a soft bitmap.
            var fill = new Border
            {
                CornerRadius = radius,
                Background = classic ? Theme.Brush(Theme.Hex(0x141617), 0.96) : Theme.Brush(Theme.PanelBase, 0.94),
                Effect = new DropShadowEffect { BlurRadius = Theme.S(18), ShadowDepth = Theme.S(5), Direction = 270, Opacity = 0.4, Color = Colors.Black },
            };
            var card = new Border
            {
                Width = Theme.S(330),
                Padding = new Thickness(Theme.S(classic ? 16 : 14), Theme.S(11), Theme.S(14), Theme.S(11)),
                CornerRadius = radius,
                Background = Brushes.Transparent,
                BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
                Child = row,
                Cursor = clickable ? Cursors.Hand : Cursors.Arrow,
            };
            var layers = new Grid { HorizontalAlignment = HorizontalAlignment.Center }; // as wide as the card, never stretched
            layers.Children.Add(fill);
            layers.Children.Add(card);
            if (classic)
                layers.Children.Add(new Border { Width = Theme.S(3), HorizontalAlignment = HorizontalAlignment.Left, Background = Theme.Brush(tint), IsHitTestVisible = false });
            return new Border { Padding = new Thickness(Theme.S(16)), Child = layers, Background = Brushes.Transparent };
        }
    }
}
