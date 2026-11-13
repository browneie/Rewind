using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rewind;

static class DevTools
{
    public static int RenderUi(string dir, string? background = null)
    {
        Directory.CreateDirectory(dir);
        Settings.UseDefaults();
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int code = 0;
        app.Startup += (_, _) =>
        {
            try
            {
                BitmapSource? game = background == null ? null : new BitmapImage(new Uri(Path.GetFullPath(background)));
                int w = game?.PixelWidth ?? 1600, h = game?.PixelHeight ?? 1000;
                var overlay = new OverlayWindow(() => { });
                overlay.ShowPreviewState(w, h);
                var content = (FrameworkElement)overlay.Content;
                overlay.Content = null;
                Save(game == null ? OnBackground(content, w, h) : OnImage(content, game), Path.Combine(dir, "overlay.png"));

                var toast = Toast.BuildCard("Replay saved", "2:00 · Replay 2026-11-13 at 21.04.11.mp4", Sym.Check, Theme.Replay, clickable: true);
                Save(OnBackground(toast, 560, 140), Path.Combine(dir, "notification.png"));

                var settings = SettingsWindow.CreateForPreview();
                Save(settings, Path.Combine(dir, "settings.png"));

                SaveTrayIcons(Path.Combine(dir, "tray-icons.png"));
            }
            catch (Exception e)
            {
                Log.Error("render-ui", e);
                code = 1;
            }
            app.Shutdown(code);
        };
        app.Run();
        return code;
    }

    public static int ProbeOverlayCursor()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int code = 1;
        app.Startup += async (_, _) =>
        {
            System.Diagnostics.Process? game = null;
            try
            {
                game = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--fake-game") { UseShellExecute = false })!;
                for (int i = 0; i < 50 && !AppTarget.Windows().Any(w => w.Pid == game.Id); i++) await Task.Delay(100);
                await Task.Delay(1200);
                string State() => $"pointer {(Native.CursorShowing() ? "visible" : "hidden")}, {(Native.CursorClipped() ? "locked to the game window" : "free")}";
                Console.WriteLine("game running:   " + State());
                OverlayController.Show();
                await Task.Delay(600);
                var p0 = Native.CursorPos();
                for (int i = 1; i <= 10; i++) { Native.SetCursorPos(p0.X + i * 8, p0.Y + i * 5); await Task.Delay(30); }
                await Task.Delay(300);
                var overlay = System.Windows.Application.Current.Windows.OfType<OverlayWindow>().FirstOrDefault();
                bool soft = overlay?.SoftwareCursorShown ?? false;
                bool front = overlay != null && Native.GetForegroundWindow() == new WindowInteropHelper(overlay).Handle;
                Console.WriteLine($"overlay open:   {State()}, Rewind's own pointer {(soft ? "drawn" : "not needed")}, overlay {(front ? "has focus" : "does NOT have focus")}");
                bool usable = (Native.CursorShowing() || soft) && !Native.CursorClipped();
                Console.WriteLine(usable ? "PASS: a pointer is visible and can move over the overlay" : "FAIL: no usable pointer over the overlay");
                code = usable ? 0 : 1;
                OverlayController.Hide();
                await Task.Delay(400);
            }
            catch (Exception e) { Console.WriteLine("FAIL: " + e.Message); }
            finally
            {
                try { game?.Kill(); } catch { }
                Native.ClipCursor(IntPtr.Zero);
                app.Shutdown(code);
            }
        };
        app.Run();
        return code;
    }

    public static int FakeGame()
    {
        var app = new System.Windows.Application();
        app.Startup += (_, _) =>
        {
            var w = new Window
            {
                Title = "Rewind cursor test", Width = 800, Height = 600, Left = 0, Top = 0, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Background = new SolidColorBrush(Color.FromRgb(40, 70, 40)),
                Content = new TextBlock { Text = "Rewind cursor test (closes by itself)", Foreground = Brushes.White, FontSize = 28, Margin = new Thickness(40) },
            };
            w.SourceInitialized += (_, _) =>
            {
                var src = HwndSource.FromHwnd(new WindowInteropHelper(w).Handle);
                src.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
                {
                    if (msg == 0x0020 /* WM_SETCURSOR */) { Native.SetCursor(IntPtr.Zero); handled = true; return new IntPtr(1); }
                    return IntPtr.Zero;
                });
            };
            w.Loaded += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                Native.ForceForeground(hwnd);
                while (Native.ShowCursor(false) >= 0) { }
                var r = Native.VisibleBounds(hwnd);
                Native.SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
                IntPtr rect = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
                System.Runtime.InteropServices.Marshal.StructureToPtr(r, rect, false);
                Native.ClipCursor(rect);
                System.Runtime.InteropServices.Marshal.FreeHGlobal(rect);
            };
            w.Show();
        };
        return app.Run();
    }

    public static int ProbeCapture()
    {
        MF.Startup();
        Probe(configure: false, where: "main thread");
        Task.Run(() => Probe(configure: true, where: "thread pool")).Wait();
        return 0;
    }

    static void Probe(bool configure, string where)
    {
        {
            using var gpu = Gpu.Create();
            var d = Displays.Find("")!;
            var item = Gpu.ItemForMonitor(d.Handle);
            int frames = 0;
            using var pool = Windows.Graphics.Capture.Direct3D11CaptureFramePool.CreateFreeThreaded(gpu.WinRTDevice,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            pool.FrameArrived += (p, _) => { using var f = p.TryGetNextFrame(); if (f != null) Interlocked.Increment(ref frames); };
            using var session = pool.CreateCaptureSession(item);
            string notes = "";
            if (configure)
            {
                try { session.IsCursorCaptureEnabled = true; } catch (Exception e) { notes += " cursor:" + e.Message; }
                try { session.IsBorderRequired = false; } catch (Exception e) { notes += " border:" + e.Message; }
                try { session.IncludeSecondaryWindows = true; } catch (Exception e) { notes += " secondary:" + e.Message; }
            }
            session.StartCapture();
            Thread.Sleep(2000);
            Console.WriteLine($"{where}, {(configure ? "with" : "without")} session settings: {item.Size.Width}x{item.Size.Height} on {gpu.AdapterName}, {frames} frames in 2 s{notes}");
        }
    }

    static void SaveTrayIcons(string path)
    {
        var states = Enum.GetValues<Tray.State>();
        using var strip = new System.Drawing.Bitmap(states.Length * 2 * 56 + 8, 2 * 56 + 8);
        using (var g = System.Drawing.Graphics.FromImage(strip))
        {
            for (int row = 0; row < 2; row++)
            {
                bool light = row == 1;
                using var bg = new System.Drawing.SolidBrush(light ? System.Drawing.Color.FromArgb(238, 238, 238) : System.Drawing.Color.FromArgb(32, 32, 32));
                g.FillRectangle(bg, 0, row * 56, strip.Width, 56 + 8);
                int x = 8;
                foreach (var st in states)
                    foreach (var px in new[] { 16, 32 })
                    {
                        using var ic = Tray.Draw(st, light, px);
                        g.DrawIcon(ic, x + (48 - px) / 2, row * 56 + 8 + (48 - px) / 2);
                        x += 56;
                    }
            }
        }
        strip.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    static FrameworkElement OnImage(FrameworkElement content, BitmapSource image)
    {
        var grid = new Grid
        {
            Width = image.PixelWidth, Height = image.PixelHeight,
            Background = new ImageBrush(image) { Stretch = Stretch.Fill },
        };
        grid.Children.Add(content);
        return grid;
    }

    static FrameworkElement OnBackground(FrameworkElement content, double w, double h)
    {
        var bg = new LinearGradientBrush(Color.FromRgb(70, 92, 60), Color.FromRgb(38, 42, 70), 35);
        var grid = new Grid { Width = w, Height = h, Background = bg };
        for (int i = 0; i < 14; i++)
            grid.Children.Add(new Border
            {
                Width = 60 + i * 23 % 140, Height = 40 + i * 37 % 120, Background = new SolidColorBrush(Color.FromArgb(70, (byte)(120 + i * 9), (byte)(150 - i * 5), 90)),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(i * 113 % w, i * 71 % h, 0, 0),
            });
        grid.Children.Add(content);
        return grid;
    }

    public static void Save(FrameworkElement e, string path)
    {
        e.Measure(new Size(double.IsNaN(e.Width) ? double.PositiveInfinity : e.Width, double.IsNaN(e.Height) ? double.PositiveInfinity : e.Height));
        e.Arrange(new Rect(e.DesiredSize));
        e.UpdateLayout();
        int w = (int)Math.Ceiling(e.ActualWidth), h = (int)Math.Ceiling(e.ActualHeight);
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(e);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
