using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Rewind;

/// Look of the overlay and notifications (the same values as Sources/Rewind/Theme.swift). The layout, accent
/// color, dimming and hotkey hints are in Settings → Overlay; change the rest here and rebuild to see them.
static class Theme
{
    // Size multiplier for everything (mockup "Size").
    public const double Scale = 1.35;
    // Corner roundness of the main panel; tiles use 72% of it, notifications 60% (mockup "Corner roundness").
    public const double Radius = 34;
    // How opaque the dark panel is over the dimmed game, 0–1 (mockup "Panel opacity"). macOS uses 0.64 with a
    // blurred backdrop; Windows can't blur behind one panel of a transparent window, so it's a bit more opaque.
    public const double PanelOpacity = 0.85;
    // How much the game behind the overlay is darkened, 0–0.9. Set in Settings → Overlay.
    public static double Dim => Settings.Shared.OverlayDim;

    // Accent (and Instant Replay) color. Set in Settings → Overlay; presets below, or any color from the wheel.
    public static Color Accent => Hex(Settings.Shared.AccentColor);
    public static Color Replay => Accent;
    public static readonly Color Record = Hex(0xFF4D5E);
    public static readonly Color Screenshot = Hex(0x4DA3FF);
    public const uint DefaultAccent = 0x3DDC84;
    public static readonly (string Name, uint Hex)[] AccentPresets =
    {
        ("Rewind green", 0x3DDC84), ("Classic green", 0x76B900), ("Sky", 0x4DA3FF), ("Violet", 0xA78BFA),
        ("Amber", 0xFFB454), ("Rose", 0xFF6B9D), ("White", 0xF2F2F2),
    };

    public static bool ShowHotkeys => Settings.Shared.ShowHotkeys;

    // Split layout: width of the actions pane (before scaling).
    public const double ActionsWidth = 215;
    // Player width (before scaling) is set in Settings → Overlay → Player size; this is its range.
    public const double MinPlayerSize = 400, MaxPlayerSize = 1000, DefaultPlayerSize = 600;

    public const string Brand = "Rewind";
    public const string ReplayTitle = "Instant Replay";
    public const string RecordTitle = "Record";
    public const string ScreenshotTitle = "Screenshot";
    public const string FolderLabel = "Clips folder";
    public const string SettingsLabel = "Settings";

    public static readonly Color Green = Hex(0x30D158);
    public static readonly Color Orange = Hex(0xFF9F0A);
    public static readonly Color Red = Hex(0xFF453A);
    public static readonly Color Blue = Hex(0x0A84FF);
    public static readonly Color Gray = Hex(0x98989D);

    public static readonly Color PanelBase = Color.FromRgb(18, 18, 22);
    public static readonly Brush Line = Frozen(new SolidColorBrush(Color.FromArgb(23, 255, 255, 255)));
    public static readonly Brush Muted = Frozen(new SolidColorBrush(Color.FromArgb(148, 255, 255, 255)));
    public static double S(double v) => v * Scale;
    // Windows' "Animation effects" setting (off when the user wants less motion).
    public static bool ReduceMotion => !SystemParameters.ClientAreaAnimation;

    public static readonly FontFamily Font = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    public static readonly FontFamily Symbols = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static Color Hex(uint hex) => Color.FromRgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
    public static Color WithAlpha(this Color c, double a) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);
    public static Color Mix(this Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    public static SolidColorBrush Brush(Color c) => Frozen(new SolidColorBrush(c));
    public static SolidColorBrush Brush(Color c, double alpha) => Frozen(new SolidColorBrush(c.WithAlpha(alpha)));
    public static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public static TextBlock Text(string s, double size, FontWeight? weight = null, Brush? brush = null, FontFamily? family = null) => new()
    {
        Text = s,
        FontSize = S(size),
        FontWeight = weight ?? FontWeights.Normal,
        FontFamily = family ?? Font,
        Foreground = brush ?? Brushes.White,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
}

enum Sym { Replay, ReplayOff, Record, RecordFill, Stop, Camera, Folder, Gear, Close, Check, Error, Warning, Hourglass, Lock, Keyboard, Display, App, Mic,
           Trash, Play, Pause, Back, Scissors, Eye, Gamepad, StepBack, StepForward, Film, MicOff,
           // The rewind arrow with the replay length inside it ("30", "2m").
           ReplayLength }

static class Icons
{
    static string? Glyph(Sym s) => s switch
    {
        Sym.Camera => "",
        Sym.Folder => "",
        Sym.Gear => "",
        Sym.Close => "",
        Sym.Check => "",
        Sym.Error => "",
        Sym.Warning => "",
        Sym.Hourglass => "",
        Sym.Lock => "",
        Sym.Keyboard => "",
        Sym.Display => "",
        Sym.App => "",
        Sym.Mic => "",
        Sym.Trash => "",
        Sym.Play => "",
        Sym.Pause => "",
        Sym.Back => "",
        Sym.Scissors => "",
        Sym.Eye => "",
        Sym.Gamepad => "",
        Sym.StepBack => "",
        Sym.StepForward => "",
        Sym.Film => "",
        Sym.MicOff => "",
        _ => null,
    };

    public static FrameworkElement Make(Sym sym, double size, Brush brush)
    {
        if (Glyph(sym) is { } g)
            return new TextBlock
            {
                Text = g, FontFamily = Theme.Symbols, FontSize = size, Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        return sym switch
        {
            Sym.Replay or Sym.ReplayOff => RewindArrow(size, brush, dot: false),
            Sym.ReplayLength => ReplayIcon(Settings.Shared.ReplaySeconds, size, brush),
            Sym.Record => RecordCircle(size, brush, filled: false),
            Sym.RecordFill => RecordCircle(size, brush, filled: true),
            Sym.Stop => new Border
            {
                Width = size * 0.72, Height = size * 0.72, CornerRadius = new CornerRadius(size * 0.14), Background = brush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
            _ => new Border(),
        };
    }

    /// A rewind arrow with the replay length inside it, like SF Symbols' gobackward.30 but for any length.
    public static FrameworkElement ReplayIcon(int seconds, double size, Brush brush)
    {
        string label = seconds < 60 ? $"{seconds}" : seconds % 60 == 0 ? $"{seconds / 60}m" : Format.Duration(seconds);
        var text = new TextBlock
        {
            Text = label, Foreground = brush, FontFamily = Theme.Font, FontWeight = FontWeights.Black,
            FontSize = size * (label.Length > 2 ? 0.27 : 0.33),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            // The ring's centre sits a little below the middle of the icon.
            Margin = new Thickness(0, size * 0.1, 0, 0),
        };
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(RewindArrow(size, brush, dot: false));
        grid.Children.Add(text);
        return grid;
    }

    public static FrameworkElement RewindArrow(double size, Brush brush, bool dot)
    {
        var canvas = new Canvas { Width = 62, Height = 62 };
        var ring = new Path
        {
            Data = RingGeometry(), Stroke = brush, StrokeThickness = 6.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        };
        var head = new Path
        {
            Data = Geometry.Parse("M 42,25 L 52.5,17.5 L 52.5,32.5 Z"), Fill = brush, Stroke = brush, StrokeThickness = 3,
            StrokeLineJoin = PenLineJoin.Round,
        };
        foreach (var p in new UIElement[] { ring, head })
        {
            p.RenderTransform = new TranslateTransform(-19, -16);
            canvas.Children.Add(p);
        }
        if (dot)
        {
            var e = new Ellipse { Width = 24, Height = 24, Fill = Theme.Brush(Theme.Record) };
            Canvas.SetLeft(e, 38 - 19); Canvas.SetTop(e, 38 - 16);
            canvas.Children.Add(e);
        }
        return new Viewbox { Width = size, Height = size, Child = canvas, Stretch = Stretch.Uniform };
    }

    public static Geometry RingGeometry()
    {
        double start = Math.Atan2(34.0 - 50, 30.5 - 50), sweep = 309.4 * Math.PI / 180;
        var fig = new PathFigure { StartPoint = new Point(50 + 25 * Math.Cos(start), 50 + 25 * Math.Sin(start)), IsClosed = false };
        var pts = new PointCollection();
        for (int i = 1; i <= 120; i++)
        {
            double a = start - sweep * i / 120;
            pts.Add(new Point(50 + 25 * Math.Cos(a), 50 + 25 * Math.Sin(a)));
        }
        fig.Segments.Add(new PolyLineSegment(pts, true));
        var g = new PathGeometry(new[] { fig });
        g.Freeze();
        return g;
    }

    static FrameworkElement RecordCircle(double size, Brush brush, bool filled)
    {
        var grid = new Grid { Width = size, Height = size };
        if (filled)
        {
            grid.Children.Add(new Ellipse { Fill = brush, Margin = new Thickness(size * 0.06) });
            grid.Children.Add(new Ellipse { Fill = Brushes.White, Width = size * 0.36, Height = size * 0.36, Opacity = 0.95 });
        }
        else
        {
            grid.Children.Add(new Ellipse { Stroke = brush, StrokeThickness = size * 0.09, Margin = new Thickness(size * 0.06) });
            grid.Children.Add(new Ellipse { Fill = brush, Width = size * 0.38, Height = size * 0.38 });
        }
        return grid;
    }
}
