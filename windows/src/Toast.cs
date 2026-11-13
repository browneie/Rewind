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

    public static void Error(string title, Exception error) =>
        Show(title, error.Message, Sym.Error, Theme.Red, duration: 6);

    public static void Show(string title, string? subtitle, Sym icon, Color tint, double duration = 3.5, Action? onClick = null)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null) return;
        if (!d.CheckAccess()) { d.BeginInvoke(() => Show(title, subtitle, icon, tint, duration, onClick)); return; }

        current?.Dismiss(fast: true);
        var w = new ToastWindow(title, subtitle, icon, tint, onClick);
        current = w;
        w.Closed += (_, _) => { if (current == w) current = null; };
        w.ShowFor(duration);
    }

    sealed class ToastWindow : Window
    {
        readonly Action? onClick;
        readonly DispatcherTimer timer = new();
        bool dismissing;

        public ToastWindow(string title, string? subtitle, Sym icon, Color tint, Action? onClick)
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

            var content = BuildCard(title, subtitle, icon, tint, onClick != null);
            Content = content;
            content.MouseLeftButtonUp += (_, _) => { this.onClick?.Invoke(); Dismiss(fast: false); };

            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                Native.MakeToolWindow(hwnd, noActivate: true);
                Native.ExcludeFromCapture(hwnd);
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

    public static FrameworkElement BuildCard(string title, string? subtitle, Sym icon, Color tint, bool clickable)
    {
        {
            var iconBox = new Border
            {
                Width = Theme.S(30), Height = Theme.S(30), CornerRadius = new CornerRadius(Theme.S(9)),
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
            if (clickable)
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

            var card = new Border
            {
                Width = Theme.S(330),
                Padding = new Thickness(Theme.S(14), Theme.S(11), Theme.S(14), Theme.S(11)),
                CornerRadius = new CornerRadius(Theme.S(Theme.Radius * 0.6)),
                Background = Theme.Brush(Theme.PanelBase, 0.94),
                BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
                Child = row,
                Effect = new DropShadowEffect { BlurRadius = Theme.S(18), ShadowDepth = Theme.S(5), Direction = 270, Opacity = 0.4, Color = Colors.Black },
                Cursor = clickable ? Cursors.Hand : Cursors.Arrow,
            };
            return new Border { Padding = new Thickness(Theme.S(16)), Child = card, Background = Brushes.Transparent };
        }
    }
}
