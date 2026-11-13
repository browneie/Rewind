using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Rewind;

static class QualityTest
{
    const int W = 1280, H = 720, Fps = 60, Frames = 360;

    interface ISource { string Name { get; } void Render(int frame, int[] bgra); }

    public static int Run(string[] args)
    {
        int at = Array.IndexOf(args, "--quality-test");
        string dir = at >= 0 && args.Length > at + 1 && !args[at + 1].StartsWith("--") ? args[at + 1] : Path.Combine(Path.GetTempPath(), "rewind-quality");
        Directory.CreateDirectory(dir);
        MF.Startup();
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var configs = args.Contains("--all")
            ? new List<(string name, EncoderTuning tuning)>
            {
                ("original (VBR)", EncoderTuning.Original),
                ("best preset", new EncoderTuning(QualityVsSpeed: 100)),
                ("best preset, 85% bitrate", new EncoderTuning(QualityVsSpeed: 100, BitrateFactor: 0.85)),
                ("best preset, 70% bitrate", new EncoderTuning(QualityVsSpeed: 100, BitrateFactor: 0.70)),
                ("unconstrained VBR", new EncoderTuning(RateControl: 2)),
                ("constant quality 70", new EncoderTuning(RateControl: 3, Quality: 70)),
                ("constant quality 80", new EncoderTuning(RateControl: 3, Quality: 80)),
                ("constant quality 90", new EncoderTuning(RateControl: 3, Quality: 90)),
            }
            : new List<(string name, EncoderTuning tuning)>
            {
                ("original (VBR)", EncoderTuning.Original),
                ("best preset", new EncoderTuning(QualityVsSpeed: 100)),
                ("best, 2 s keyframes", new EncoderTuning(QualityVsSpeed: 100, KeyframeSeconds: 2)),
                ("best, 2 s keys, 95% bitrate", new EncoderTuning(QualityVsSpeed: 100, KeyframeSeconds: 2, BitrateFactor: 0.95)),
                ("best, 2 s keys, 90% bitrate", new EncoderTuning(QualityVsSpeed: 100, KeyframeSeconds: 2, BitrateFactor: 0.90)),
                ("best, 2 s keys, 80% bitrate", new EncoderTuning(QualityVsSpeed: 100, KeyframeSeconds: 2, BitrateFactor: 0.80)),
                ("in use", null!),
            };
        if (args.Contains("--quick")) configs = configs.Take(2).ToList();
        var sources = new ISource[] { new GameSource(), new DesktopSource() };
        var codecs = args.Contains("--h264-only") ? new[] { VideoCodec.H264 } : new[] { VideoCodec.H264, VideoCodec.Hevc };

        Console.WriteLine($"{W}×{H} at {Fps} fps, {Frames} frames per test; quality settings: High. Δ is against the current settings.");
        Console.WriteLine($"{"footage",-8} {"codec",-6} {"settings",-26} {"MB/min",7} {"Δsize",7} {"PSNR",7} {"worst",7} {"SSIM",7}");
        foreach (var codec in codecs)
            foreach (var src in sources)
            {
                (double size, double psnr, double worst, double ssim)? baseline = null;
                foreach (var (name, tuning) in configs)
                {
                    try
                    {
                        int bitrate = CaptureMath.Bitrate((W, H), Fps, Quality.High, codec);
                        var path = Path.Combine(dir, $"q-{src.Name}-{codec}-{configs.FindIndex(c => c.name == name)}.mp4");
                        var (bytes, report) = Encode(src, codec, bitrate, tuning ?? EncoderTuning.For(codec), path);
                        var (psnr, worst, ssim, decoded) = Measure(path, src);
                        double mbPerMin = bytes / 1e6 / (Frames / (double)Fps) * 60;
                        baseline ??= (mbPerMin, psnr, worst, ssim);
                        double dSize = (mbPerMin / baseline.Value.size - 1) * 100;
                        Console.WriteLine($"{src.Name,-8} {codec.Short(),-6} {name,-26} {mbPerMin,7:F1} {dSize,6:+0;-0;0}% {psnr,7:F2} {worst,7:F2} {ssim,7:F4}" +
                                          (decoded != Frames ? $"  (decoded {decoded}/{Frames})" : "") +
                                          (report.Contains("refused") ? $"  [{report}]" : ""));
                        if (!args.Contains("--keep")) File.Delete(path);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"{src.Name,-8} {codec.Short(),-6} {name,-26} failed: {e.Message}");
                    }
                }
            }
        return 0;
    }

    static (long bytes, string report) Encode(ISource src, VideoCodec codec, int bitrate, EncoderTuning tuning, string path)
    {
        using var gpu = Gpu.Create();
        using var conv = new FrameConverter(gpu, W, H, Fps);
        using var tex = MakeTexture(gpu);
        var frames = new List<EncodedFrame>();
        var enc = new VideoEncoder(gpu, W, H, Fps, codec, bitrate, f => { lock (frames) frames.Add(f); }, tuning);
        string report = enc.TuningReport;
        var buf = new int[W * H];
        for (int i = 0; i < Frames; i++)
        {
            src.Render(i, buf);
            gpu.Context.UpdateSubresource(buf, tex, 0, W * 4);
            IMFSample? s;
            while ((s = conv.ConvertOwned(tex)) == null) Thread.Sleep(1);
            enc.Encode(s, i * Clock.Second / Fps);
            while (enc.Pending > 2) Thread.Sleep(1);
        }
        enc.Dispose();
        if (frames.Count == 0) throw new RewindException("the encoder produced nothing");
        using (var mp4 = new Mp4Muxer(path, fastStart: true))
        {
            int t = mp4.AddVideo(frames[0].Config);
            foreach (var f in frames) mp4.Write(t, f.Data, Ticks.ToVideo(f.Pts), f.IsKey);
            mp4.Finish();
        }
        return (new FileInfo(path).Length, report);
    }

    static ID3D11Texture2D MakeTexture(Gpu gpu) => gpu.Device.CreateTexture2D(new Texture2DDescription
    {
        Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
        SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Default,
        BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
    });

    static (double psnr, double worst, double ssim, int frames) Measure(string path, ISource src)
    {
        using var gpu = Gpu.Create();
        using var conv = new FrameConverter(gpu, W, H, Fps);
        using var tex = MakeTexture(gpu);
        using var staging = gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.NV12,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null);
        using (var t = MediaFactory.MFCreateMediaType())
        {
            t.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            t.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            reader.SetCurrentMediaType(0, t);
        }
        var buf = new int[W * H];
        var reference = new byte[W * H];
        var decoded = new byte[W * H];
        double mseSum = 0, worst = double.MaxValue, ssimSum = 0;
        int n = 0;
        while (n < Frames)
        {
            using var s = reader.ReadSample(0, 0, out _, out var flags, out _);
            if (s != null)
            {
                using (var mb = s.ConvertToContiguousBuffer())
                {
                    mb.Lock(out var p, out _, out int len);
                    if (len < W * H) throw new RewindException("unexpected decoded frame size");
                    Marshal.Copy(p, decoded, 0, W * H);
                    mb.Unlock();
                }
                src.Render(n, buf);
                gpu.Context.UpdateSubresource(buf, tex, 0, W * 4);
                using (var nv12 = conv.ConvertOwned(tex)!)
                using (var b = nv12.GetBufferByIndex(0))
                using (var dx = b.QueryInterface<IMFDXGIBuffer>())
                using (var t = new ID3D11Texture2D(dx.GetResource(typeof(ID3D11Texture2D).GUID)))
                {
                    gpu.Context.CopySubresourceRegion(staging, 0, 0, 0, 0, t, dx.SubresourceIndex, null);
                    var map = gpu.Context.Map(staging, 0, MapMode.Read, MapFlags.None);
                    for (int y = 0; y < H; y++) Marshal.Copy(map.DataPointer + (int)(y * map.RowPitch), reference, y * W, W);
                    gpu.Context.Unmap(staging, 0);
                }
                double mse = 0;
                for (int i = 0; i < reference.Length; i++) { int d = reference[i] - decoded[i]; mse += d * d; }
                mse /= reference.Length;
                double psnr = mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255 / mse);
                worst = Math.Min(worst, psnr);
                mseSum += mse;
                ssimSum += Ssim(reference, decoded);
                n++;
            }
            if (((int)flags & 2) != 0) break;
        }
        double avgMse = mseSum / Math.Max(n, 1);
        return (avgMse == 0 ? 99 : 10 * Math.Log10(255.0 * 255 / avgMse), worst, ssimSum / Math.Max(n, 1), n);
    }

    static double Ssim(byte[] a, byte[] b)
    {
        const double c1 = 6.5025, c2 = 58.5225;
        double sum = 0; int blocks = 0;
        for (int by = 0; by + 8 <= H; by += 8)
            for (int bx = 0; bx + 8 <= W; bx += 8)
            {
                double ma = 0, mb = 0, va = 0, vb = 0, cov = 0;
                for (int y = 0; y < 8; y++)
                {
                    int row = (by + y) * W + bx;
                    for (int x = 0; x < 8; x++) { ma += a[row + x]; mb += b[row + x]; }
                }
                ma /= 64; mb /= 64;
                for (int y = 0; y < 8; y++)
                {
                    int row = (by + y) * W + bx;
                    for (int x = 0; x < 8; x++)
                    {
                        double da = a[row + x] - ma, db = b[row + x] - mb;
                        va += da * da; vb += db * db; cov += da * db;
                    }
                }
                va /= 63; vb /= 63; cov /= 63;
                sum += (2 * ma * mb + c1) * (2 * cov + c2) / ((ma * ma + mb * mb + c1) * (va + vb + c2));
                blocks++;
            }
        return sum / blocks;
    }


    sealed class GameSource : ISource
    {
        public string Name => "game";
        readonly int[] world = new int[1024 * 1024];

        public GameSource()
        {
            var rnd = new Random(7);
            var grid = new double[4][,];
            for (int o = 0; o < 4; o++)
            {
                int cells = 8 << o;
                grid[o] = new double[cells + 1, cells + 1];
                for (int y = 0; y <= cells; y++) for (int x = 0; x <= cells; x++) grid[o][y, x] = rnd.NextDouble();
            }
            for (int y = 0; y < 1024; y++)
                for (int x = 0; x < 1024; x++)
                {
                    double v = 0, amp = 0.5;
                    for (int o = 0; o < 4; o++, amp /= 2)
                    {
                        int cells = 8 << o;
                        double fx = x / 1024.0 * cells, fy = y / 1024.0 * cells;
                        int ix = (int)fx, iy = (int)fy;
                        double tx = fx - ix, ty = fy - iy;
                        var g = grid[o];
                        double top = g[iy, ix] * (1 - tx) + g[iy, ix + 1] * tx, bot = g[iy + 1, ix] * (1 - tx) + g[iy + 1, ix + 1] * tx;
                        v += (top * (1 - ty) + bot * ty) * amp;
                    }
                    double grain = rnd.NextDouble() * 0.08;
                    int r = (int)(60 + 120 * v + 40 * grain), gg = (int)(80 + 110 * v + 30 * grain), b = (int)(40 + 50 * v);
                    world[y * 1024 + x] = (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(gg, 0, 255) << 8) | Math.Clamp(b, 0, 255);
                }
        }

        public void Render(int i, int[] f)
        {
            double t = i / (double)Fps;
            double angle = 0.25 * Math.Sin(t * 0.6), zoom = 1 + 0.15 * Math.Sin(t * 0.4);
            double ca = Math.Cos(angle) / zoom, sa = Math.Sin(angle) / zoom;
            double px = i * 5.0, py = i * 2.2;
            for (int y = 0; y < H; y++)
            {
                double dy = y - H / 2.0;
                double u = -W / 2.0 * ca - dy * sa + px, v = -W / 2.0 * sa + dy * ca + py;
                int row = y * W;
                for (int x = 0; x < W; x++, u += ca, v += sa)
                    f[row + x] = world[(((int)v) & 1023) * 1024 + (((int)u) & 1023)];
            }
            var rnd = new Random(i);
            for (int k = 0; k < 500; k++)
            {
                int sx = rnd.Next(W - 3), sy = rnd.Next(H - 3), c = rnd.Next(2) == 0 ? 0xFFE080 : 0xFF8040;
                for (int y = sy; y < sy + 3; y++) for (int x = sx; x < sx + 3; x++) f[y * W + x] = c;
            }
            for (int k = 0; k < 10; k++)
            {
                int cx = (int)(640 + 520 * Math.Sin(t * (0.7 + k * 0.13) + k)), cy = (int)(360 + 300 * Math.Cos(t * (0.5 + k * 0.11) + k * 2));
                int rad = 25 + k * 4, col = ((200 - k * 12) << 16) | ((60 + k * 15) << 8) | (180 - k * 10);
                for (int y = Math.Max(cy - rad, 0); y < Math.Min(cy + rad, H); y++)
                    for (int x = Math.Max(cx - rad, 0); x < Math.Min(cx + rad, W); x++)
                        if ((x - cx) * (x - cx) + (y - cy) * (y - cy) < rad * rad) f[y * W + x] = col;
            }
            for (int y = H - 90; y < H - 20; y++)
                for (int x = 20; x < 380; x++)
                    f[y * W + x] = (x / 12 + y / 12) % 2 == 0 ? 0x202020 : (x < 20 + 340 * (i % 120) / 120 && y > H - 50 ? 0xE04040 : 0x303030);
        }
    }

    sealed class DesktopSource : ISource
    {
        public string Name => "desktop";
        readonly int[] page = new int[900 * 4000];

        public DesktopSource()
        {
            var rnd = new Random(3);
            Array.Fill(page, 0xFFFFFF);
            for (int line = 0; line < 4000 / 22; line++)
            {
                int x = 30;
                while (x < 860)
                {
                    int word = rnd.Next(14, 70);
                    for (int y = line * 22 + 6; y < line * 22 + 16; y++)
                        for (int xx = x; xx < Math.Min(x + word, 870); xx += 1)
                            if (rnd.Next(5) != 0) page[y * 900 + xx] = 0x303030;
                    x += word + rnd.Next(6, 10);
                }
            }
        }

        public void Render(int i, int[] f)
        {
            Array.Fill(f, 0xF3F3F3);
            for (int y = 0; y < 40; y++) for (int x = 0; x < W; x++) f[y * W + x] = 0x202834;
            int scroll = i;
            for (int y = 0; y < 600; y++)
            {
                int src = ((y + scroll) % 4000) * 900, dst = (60 + y) * W + 100;
                Array.Copy(page, src, f, dst, 900);
            }
            for (int y = 60; y < 660; y++) for (int x = 1040; x < 1240; x++) f[y * W + x] = (y / 40) % 2 == 0 ? 0xE8EEF8 : 0xFFFFFF;
            int cx = 300 + (int)(200 * Math.Sin(i / 40.0)), cy = 400 + (int)(100 * Math.Cos(i / 50.0));
            for (int y = 0; y < 18; y++) for (int x = 0; x <= y / 2; x++) f[Math.Min(cy + y, H - 1) * W + Math.Min(cx + x, W - 1)] = 0x000000;
        }
    }
}
