using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Rewind;

/// Rewind's own color picker: a hue/saturation wheel, a brightness slider, a hex field and the presets.
sealed class ColorWheelPicker : StackPanel
{
    const double Size = 190;
    readonly Action<uint> changed;
    readonly Ellipse marker = new() { Width = 18, Height = 18, Stroke = Brushes.White, StrokeThickness = 2.5, IsHitTestVisible = false };
    readonly Ellipse shade = new() { Width = Size, Height = Size, Fill = Brushes.Black, IsHitTestVisible = false };
    readonly Border swatch = new() { Width = 72, Height = 48, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
    readonly TextBox hexBox = new() { Width = 90, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    readonly Border brightTrack = new() { Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1) };
    readonly Ellipse brightThumb = new() { Width = 18, Height = 18, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), StrokeThickness = 1, HorizontalAlignment = HorizontalAlignment.Left };
    readonly StackPanel presets = new() { Orientation = Orientation.Horizontal };
    double hue, sat, bri = 1;
    uint hex;

    public ColorWheelPicker(uint start, Action<uint> changed)
    {
        this.changed = changed;
        Margin = new Thickness(16);
        var line = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
        swatch.BorderBrush = line;
        brightTrack.BorderBrush = line;

        var wheel = new Grid { Width = Size, Height = Size, Cursor = Cursors.Cross, Background = Brushes.Transparent };
        wheel.Children.Add(new Image { Source = WheelBitmap(), Width = Size, Height = Size });
        wheel.Children.Add(shade);
        wheel.Children.Add(new Ellipse { Stroke = line, StrokeThickness = 1, IsHitTestVisible = false });
        var markerHost = new Canvas { IsHitTestVisible = false };
        markerHost.Children.Add(marker);
        wheel.Children.Add(markerHost);
        wheel.MouseLeftButtonDown += (_, e) => { wheel.CaptureMouse(); PickWheel(e.GetPosition(wheel)); };
        wheel.MouseMove += (_, e) => { if (wheel.IsMouseCaptured) PickWheel(e.GetPosition(wheel)); };
        wheel.MouseLeftButtonUp += (_, _) => wheel.ReleaseMouseCapture();

        var side = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        side.Children.Add(swatch);
        side.Children.Add(Caption("Hex", 10));
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal };
        hexRow.Children.Add(new TextBlock { Text = "#", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Opacity = 0.6 });
        hexRow.Children.Add(hexBox);
        side.Children.Add(hexRow);
        hexBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyHexText(); };
        hexBox.LostKeyboardFocus += (_, _) => ApplyHexText();

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(wheel);
        top.Children.Add(side);
        Children.Add(top);

        Children.Add(Caption("Brightness", 14));
        var bright = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        bright.Children.Add(brightTrack);
        bright.Children.Add(brightThumb);
        bright.MouseLeftButtonDown += (_, e) => { bright.CaptureMouse(); PickBrightness(e.GetPosition(bright).X, bright.ActualWidth); };
        bright.MouseMove += (_, e) => { if (bright.IsMouseCaptured) PickBrightness(e.GetPosition(bright).X, bright.ActualWidth); };
        bright.MouseLeftButtonUp += (_, _) => bright.ReleaseMouseCapture();
        bright.SizeChanged += (_, _) => PlaceBrightness();
        Children.Add(bright);

        Children.Add(Caption("Presets", 14));
        Children.Add(presets);
        Set(start, notify: false);
    }

    static TextBlock Caption(string text, double top) => new() { Text = text, FontSize = 12, Opacity = 0.65, Margin = new Thickness(0, top, 0, 4) };

    /// Hue around the circle (clockwise from the right), saturation out from the middle, full brightness.
    static BitmapSource WheelBitmap()
    {
        int n = (int)Size * 2;
        var px = new byte[n * n * 4];
        double r = n / 2.0;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                double dx = x + 0.5 - r, dy = y + 0.5 - r, d = Math.Sqrt(dx * dx + dy * dy);
                if (d > r) continue;
                double a = Math.Atan2(dy, dx);
                if (a < 0) a += 2 * Math.PI;
                var c = FromHsv(a / (2 * Math.PI), Math.Min(d / r, 1), 1);
                double alpha = Math.Clamp(r - d, 0, 1); // soft edge
                int i = (y * n + x) * 4;
                px[i] = (byte)(c.B * alpha); px[i + 1] = (byte)(c.G * alpha); px[i + 2] = (byte)(c.R * alpha); px[i + 3] = (byte)(255 * alpha);
            }
        var bmp = BitmapSource.Create(n, n, 192, 192, PixelFormats.Pbgra32, null, px, n * 4);
        bmp.Freeze();
        return bmp;
    }

    void PickWheel(Point p)
    {
        double dx = p.X - Size / 2, dy = p.Y - Size / 2;
        double a = Math.Atan2(dy, dx);
        if (a < 0) a += 2 * Math.PI;
        hue = a / (2 * Math.PI);
        sat = Math.Min(Math.Sqrt(dx * dx + dy * dy) / (Size / 2), 1);
        Commit();
    }

    void PickBrightness(double x, double width)
    {
        bri = Math.Clamp((x - 9) / Math.Max(width - 18, 1), 0, 1);
        Commit();
    }

    void Commit()
    {
        var c = FromHsv(hue, sat, bri);
        hex = (uint)(c.R << 16 | c.G << 8 | c.B);
        Show();
        changed(hex);
    }

    void Set(uint value, bool notify = true)
    {
        hex = value & 0xFFFFFF;
        var (h, s, v) = ToHsv(Theme.Hex(hex));
        // Keep the hue where it was for greys, so dragging saturation back doesn't jump.
        if (s > 0.001) hue = h;
        sat = s;
        bri = v;
        Show();
        if (notify) changed(hex);
    }

    void ApplyHexText()
    {
        var cleaned = hexBox.Text.Trim().TrimStart('#');
        if (cleaned.Length == 6 && uint.TryParse(cleaned, System.Globalization.NumberStyles.HexNumber, null, out uint v)) Set(v);
        else hexBox.Text = $"{hex:X6}";
    }

    void Show()
    {
        swatch.Background = new SolidColorBrush(Theme.Hex(hex));
        if (!hexBox.IsKeyboardFocused) hexBox.Text = $"{hex:X6}";
        shade.Opacity = 1 - bri;
        marker.Fill = new SolidColorBrush(FromHsv(hue, sat, bri));
        double a = hue * 2 * Math.PI, radius = sat * Size / 2;
        Canvas.SetLeft(marker, Size / 2 + Math.Cos(a) * radius - 9);
        Canvas.SetTop(marker, Size / 2 + Math.Sin(a) * radius - 9);
        brightTrack.Background = new LinearGradientBrush(Colors.Black, FromHsv(hue, sat, 1), 0);
        PlaceBrightness();
        presets.Children.Clear();
        foreach (var (name, value) in Theme.AccentPresets)
        {
            bool chosen = value == hex;
            var dot = new Ellipse
            {
                Width = 20, Height = 20, Fill = new SolidColorBrush(Theme.Hex(value)), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand,
                Stroke = new SolidColorBrush(chosen ? Color.FromArgb(230, 128, 128, 128) : Color.FromArgb(40, 128, 128, 128)), StrokeThickness = chosen ? 2 : 1,
                ToolTip = name,
            };
            dot.MouseLeftButtonUp += (_, _) => Set(value);
            presets.Children.Add(dot);
        }
    }

    void PlaceBrightness()
    {
        double w = Math.Max(brightTrack.ActualWidth, 260);
        brightThumb.Margin = new Thickness(bri * (w - 18), 0, 0, 0);
    }

    static Color FromHsv(double h, double s, double v)
    {
        h = (h % 1 + 1) % 1 * 6;
        int i = (int)Math.Floor(h);
        double f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        var (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    static (double h, double s, double v) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = (g - b) / d % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
            if (h < 0) h += 1;
        }
        return (h, max == 0 ? 0 : d / max, max);
    }
}

/// Settings row control: a swatch with its hex that opens the color wheel.
sealed class AccentColorButton : Button
{
    readonly Border swatch = new() { Width = 34, Height = 18, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1) };
    readonly TextBlock hexText = new() { FontFamily = new FontFamily("Cascadia Mono, Consolas"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), Opacity = 0.75 };
    readonly Popup popup = new() { Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };

    public AccentColorButton(Func<uint> get, Action<uint> set)
    {
        SetResourceReference(StyleProperty, typeof(Button)); // subclasses don't get the theme's Button style
        swatch.BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(swatch);
        row.Children.Add(hexText);
        row.Children.Add(new TextBlock { Text = "", FontFamily = Theme.Symbols, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 });
        Content = row;
        popup.PlacementTarget = this;
        Click += (_, _) =>
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 0),
                Background = TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? SystemColors.WindowBrush,
                BorderBrush = TryFindResource("SurfaceStrokeColorFlyoutBrush") as Brush ?? SystemColors.ActiveBorderBrush,
                Child = new ColorWheelPicker(get(), set),
            };
            TextElement.SetForeground(card, TryFindResource("TextFillColorPrimaryBrush") as Brush ?? SystemColors.ControlTextBrush);
            popup.Child = card;
            popup.IsOpen = true;
        };
        Show(get());
    }

    public void Show(uint hex)
    {
        swatch.Background = new SolidColorBrush(Theme.Hex(hex));
        hexText.Text = $"#{hex:X6}";
    }
}
