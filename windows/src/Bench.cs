using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.IO.Path;

namespace Rewind;

static class Bench
{
    public static int Run(string[] args)
    {
        int at = Array.IndexOf(args, "--bench");
        int seconds = at >= 0 && args.Length > at + 1 && int.TryParse(args[at + 1], out var n) ? n : 30;
        bool desktop = args.Contains("--desktop");
        int o = Array.IndexOf(args, "--out");
        string outDir = o >= 0 && args.Length > o + 1 ? args[o + 1] : Path.Combine(Path.GetTempPath(), "rewind-bench");
        Directory.CreateDirectory(outDir);
        MF.Startup();
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        NoPowerThrottling();

        var settings = Settings.Shared;
        bool mine = args.Contains("--use-settings");
        var sig = mine
            ? settings.CaptureSignature with { SystemAudio = false, Microphone = false }
            : new CaptureSignature("", "", "", false, 60, Quality.High, VideoCodec.H264, Resolution.Native, false, false, false);
        int fi = Array.IndexOf(args, "--fps");
        if (fi >= 0 && args.Length > fi + 1 && int.TryParse(args[fi + 1], out var fps)) sig = sig with { Fps = fps };
        int replaySeconds = mine ? settings.ReplaySeconds : 120;
        bool mix = !mine || settings.MixMicrophone;

        Process? load = null;
        try
        {
            if (!desktop)
            {
                load = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--bench-load") { UseShellExecute = false })!;
                AppWindow? w = null;
                for (int t = 0; t < 80 && w == null; t++)
                {
                    Thread.Sleep(100);
                    w = AppTarget.Windows().FirstOrDefault(x => x.Pid == load.Id);
                }
                if (w == null) { Console.WriteLine("FAIL: the benchmark window didn't open"); return 1; }
                sig = sig with { AppId = w.Key, AppName = "benchmark window", AppRunning = true };
                Thread.Sleep(1000);
            }

            var router = new MediaRouter();
            router.Replay.Duration = replaySeconds;
            router.Replay.SetAudioGroups(ClipWriter.AudioTracks(new[] { AudioTrack.System, AudioTrack.Mic }, mix));
            router.BufferReplay = true;
            var engine = new CaptureEngine
            {
                OnVideo = router.Video,
                OnAudio = router.Audio,
                OnStop = e => Console.WriteLine("capture stopped: " + e.Message),
            };
            var me = Process.GetCurrentProcess();
            engine.StartAsync(sig).GetAwaiter().GetResult();

            using var stop = new CancellationTokenSource();
            var feeder = new Thread(() => FeedAudio(router, stop.Token)) { IsBackground = true, Name = "bench audio" };
            feeder.Start();

            Thread.Sleep(3000);
            var gpu = new GpuCounters(me.Id);
            var wakeups = new WakeupCounter(me.Id);
            me.Refresh();
            var cpu0 = me.TotalProcessorTime;
            var stats0 = engine.Stats;
            long peak = 0;
            var sw = Stopwatch.StartNew();
            for (int k = 0; k < seconds; k++)
            {
                Thread.Sleep(1000);
                gpu.Sample();
                me.Refresh();
                peak = Math.Max(peak, me.PrivateMemorySize64);
            }
            double wall = sw.Elapsed.TotalSeconds;
            me.Refresh();
            double cpu = (me.TotalProcessorTime - cpu0).TotalSeconds;
            string wake = wakeups.Report();
            var st = engine.Stats;
            long privateBytes = me.PrivateMemorySize64, managed = GC.GetTotalMemory(false);
            long video = router.Replay.Bytes, audio = router.Replay.AudioBytes;
            double buffered = router.Replay.BufferedSeconds;

            Console.WriteLine($"Rewind benchmark: {(desktop ? "desktop" : "animated 1280×720 test window")}, {engine.Info}, " +
                              $"replay {Format.Duration(replaySeconds)}, 2 audio tracks ({(mix ? "mixed" : "separate")}){(mine ? ", your settings" : "")}");
            Console.WriteLine($"  frames/s:  {(st.Arrived - stats0.Arrived) / wall:F1} from Windows, {(st.Accepted - stats0.Accepted) / wall:F1} kept, " +
                              $"lost {st.NoSurface - stats0.NoSurface} (no surface) + {st.EncoderDropped - stats0.EncoderDropped} (encoder busy)");
            Console.WriteLine($"  CPU:       {cpu / wall * 100:F1}% of one core ({cpu / wall * 100 / Environment.ProcessorCount:F2}% of all {Environment.ProcessorCount})");
            Console.WriteLine($"  GPU:       {gpu.Report()}");
            Console.WriteLine($"  wake-ups:  {wake}");
            Console.WriteLine($"  RAM:       private {Mb(privateBytes)} (peak {Mb(peak)}), managed heap {Mb(managed)}");
            Console.WriteLine($"  buffer:    {buffered:F0} s held: video {Mb(video)} ({video / Math.Max(buffered, 1) * 60 / 1e6:F0} MB/min), audio {Mb(audio)}");

            var path = Path.Combine(outDir, "bench-clip.mp4");
            me.Refresh();
            var c0 = me.TotalProcessorTime;
            var s2 = Stopwatch.StartNew();
            var snap = router.Replay.Snapshot();
            double length = ClipWriter.WriteAsync(snap, path).GetAwaiter().GetResult();
            double saveWall = s2.Elapsed.TotalSeconds;
            me.Refresh();
            double saveCpu = (me.TotalProcessorTime - c0).TotalSeconds;
            long size = new FileInfo(path).Length;
            Console.WriteLine($"  clip save: {length:F1} s clip in {saveWall * 1000:F0} ms ({saveCpu * 1000:F0} ms CPU), " +
                              $"{Mb(size)} file ({size / Math.Max(length, 0.1) * 60 / 1e6:F0} MB/min)");

            stop.Cancel();
            feeder.Join(500);
            engine.StopAsync().GetAwaiter().GetResult();
            if (!args.Contains("--keep")) File.Delete(path);
            return 0;
        }
        finally
        {
            try { load?.Kill(); } catch { }
        }
    }

    static string Mb(long bytes) => $"{bytes / 1e6:F0} MB";

    static void NoPowerThrottling()
    {
        var state = new PowerThrottlingState { Version = 1, ControlMask = 1 /* EXECUTION_SPEED */, StateMask = 0 };
        SetProcessInformation(Process.GetCurrentProcess().Handle, 4 /* ProcessPowerThrottling */, ref state, Marshal.SizeOf<PowerThrottlingState>());
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll")]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

    static void FeedAudio(MediaRouter router, CancellationToken stop)
    {
        const int chunk = 480;
        long start = Clock.Now, frame = 0;
        while (!stop.IsCancellationRequested)
        {
            long due = start + frame * Clock.Second / Audio.Rate;
            if (Clock.Now < due) { Thread.Sleep(5); continue; }
            var sys = new float[chunk * 2];
            var mic = new float[chunk * 2];
            for (int i = 0; i < chunk; i++)
            {
                double t = (frame + i) / (double)Audio.Rate;
                sys[2 * i] = sys[2 * i + 1] = (float)(0.2 * Math.Sin(2 * Math.PI * 440 * t));
                mic[2 * i] = mic[2 * i + 1] = (float)(0.1 * Math.Sin(2 * Math.PI * 220 * t));
            }
            router.Audio(new AudioChunk(due, sys), AudioTrack.System);
            router.Audio(new AudioChunk(due, mic), AudioTrack.Mic);
            frame += chunk;
        }
    }

    sealed class GpuCounters
    {
        readonly IntPtr query, engines, memory;
        readonly Dictionary<string, double> sums = new();
        int samples;
        double memSum;

        public GpuCounters(int pid)
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0) return;
            PdhAddEnglishCounter(query, $@"\GPU Engine(pid_{pid}_*)\Utilization Percentage", IntPtr.Zero, out engines);
            PdhAddEnglishCounter(query, $@"\GPU Process Memory(pid_{pid}_*)\Dedicated Usage", IntPtr.Zero, out memory);
            PdhCollectQueryData(query);
        }

        public void Sample()
        {
            if (query == IntPtr.Zero || PdhCollectQueryData(query) != 0) return;
            foreach (var (name, v) in Read(engines))
            {
                int i = name.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
                var type = i >= 0 ? name[(i + 8)..] : "?";
                sums[type] = sums.GetValueOrDefault(type) + v;
            }
            memSum += Read(memory).Sum(x => x.value);
            samples++;
        }

        public string Report()
        {
            if (samples == 0 || sums.Count == 0) return "n/a";
            var parts = sums.Where(kv => kv.Value / samples >= 0.05).OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key} {kv.Value / samples:F1}%").ToList();
            return (parts.Count > 0 ? string.Join(", ", parts) : "all engines under 0.1%") + $"; dedicated GPU memory {memSum / samples / 1e6:F0} MB";
        }

        static List<(string name, double value)> Read(IntPtr counter)
        {
            var list = new List<(string, double)>();
            if (counter == IntPtr.Zero) return list;
            uint size = 0;
            PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out uint count, IntPtr.Zero);
            if (size == 0) return list;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out count, buf) != 0) return list;
                for (int i = 0; i < count; i++)
                {
                    var item = buf + i * 24; // PDH_FMT_COUNTERVALUE_ITEM_W: { wchar* name; { DWORD status; double value } }
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    list.Add((name, BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + 16))));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return list;
        }

        const uint PDH_FMT_DOUBLE = 0x00000200;
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
    }

    sealed class WakeupCounter(int pid)
    {
        readonly long start = Total(pid);
        readonly Stopwatch clock = Stopwatch.StartNew();

        public string Report()
        {
            long now = Total(pid);
            return start < 0 || now < 0 ? "n/a" : $"{(now - start) / clock.Elapsed.TotalSeconds:F0}/s";
        }

        static unsafe long Total(int pid)
        {
            int size = 1 << 20;
            for (int attempt = 0; attempt < 6; attempt++, size *= 2)
            {
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    int status = NtQuerySystemInformation(5 /* SystemProcessInformation */, buf, size, out _);
                    if (status == unchecked((int)0xC0000004)) continue;
                    if (status != 0) return -1;
                    byte* p = (byte*)buf;
                    while (true)
                    {
                        // 64-bit SYSTEM_PROCESS_INFORMATION is 256 bytes, then 80-byte SYSTEM_THREAD_INFORMATIONs.
                        uint next = *(uint*)p, threads = *(uint*)(p + 4);
                        if (*(long*)(p + 80) == pid)
                        {
                            long sum = 0;
                            for (int t = 0; t < threads; t++) sum += *(uint*)(p + 256 + t * 80 + 64);
                            return sum;
                        }
                        if (next == 0) return -1;
                        p += next;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return -1;
        }

        [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returned);
    }


    public static int LoadWindow()
    {
        NoPowerThrottling();
        var app = new System.Windows.Application();
        app.Startup += (_, _) =>
        {
            var rnd = new Random(1);
            var noise = new WriteableBitmap(256, 256, 96, 96, PixelFormats.Bgr32, null);
            var px = new int[256 * 256];
            for (int i = 0; i < px.Length; i++) px[i] = rnd.Next(0x202020, 0x9F9F9F);
            noise.WritePixels(new Int32Rect(0, 0, 256, 256), px, 256 * 4, 0);
            var scroll = new TranslateTransform();
            var tiles = new Rectangle
            {
                Width = 1280 + 512, Height = 720 + 512, Opacity = 0.35, RenderTransform = scroll,
                Fill = new ImageBrush(noise) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 256, 256), ViewportUnits = BrushMappingMode.Absolute },
            };
            var canvas = new Canvas
            {
                Width = 1280, Height = 720, ClipToBounds = true,
                Background = new LinearGradientBrush(Color.FromRgb(30, 40, 70), Color.FromRgb(70, 30, 50), 30),
            };
            canvas.Children.Add(tiles);
            var balls = new List<(Ellipse e, double fx, double fy, double phase)>();
            for (int i = 0; i < 24; i++)
            {
                var c = Color.FromRgb((byte)rnd.Next(80, 255), (byte)rnd.Next(80, 255), (byte)rnd.Next(80, 255));
                var e = new Ellipse
                {
                    Width = rnd.Next(40, 160), Height = rnd.Next(40, 160),
                    Fill = new RadialGradientBrush(c, Color.FromArgb(0, c.R, c.G, c.B)),
                };
                canvas.Children.Add(e);
                balls.Add((e, 0.2 + rnd.NextDouble(), 0.2 + rnd.NextDouble(), rnd.NextDouble() * 6.28));
            }
            var counter = new TextBlock { FontSize = 64, Foreground = Brushes.White, FontWeight = FontWeights.Bold };
            Canvas.SetLeft(counter, 40); Canvas.SetTop(counter, 40);
            canvas.Children.Add(counter);

            var clock = Stopwatch.StartNew();
            long frameNo = 0;
            CompositionTarget.Rendering += (_, _) =>
            {
                double t = clock.Elapsed.TotalSeconds;
                scroll.X = -(t * 90 % 256); scroll.Y = -(t * 55 % 256);
                foreach (var (e, fx, fy, ph) in balls)
                {
                    Canvas.SetLeft(e, 600 + 560 * Math.Sin(t * fx + ph) - e.Width / 2);
                    Canvas.SetTop(e, 360 + 320 * Math.Cos(t * fy + ph * 1.3) - e.Height / 2);
                }
                counter.Text = $"Rewind benchmark · frame {++frameNo}";
            };
            var w = new Window
            {
                Title = "Rewind benchmark", Content = canvas, SizeToContent = SizeToContent.WidthAndHeight,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Left = 40, Top = 40, ShowActivated = false,
                Topmost = true,
            };
            w.Show();
        };
        return app.Run();
    }
}
