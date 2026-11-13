using Vortice.Direct3D11;
using Vortice.Mathematics;
using Vortice.MediaFoundation;

namespace Rewind;

static class SelfTest
{
    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        MF.Startup();
        bool ok = RunCodec(dir, VideoCodec.H264, 1280, 720, 60, seconds: 5, full: true);
        ok &= RunCodec(dir, VideoCodec.Hevc, 1920, 1080, 60, seconds: 4, full: false);
        Console.WriteLine(ok ? "selftest passed" : "selftest FAILED");
        return ok ? 0 : 1;
    }

    static bool RunCodec(string dir, VideoCodec codec, int w, int h, int fps, int seconds, bool full)
    {
        string tag = codec == VideoCodec.H264 ? "" : "-hevc";
        string clipSep = Path.Combine(dir, $"selftest-replay{tag}.mp4");
        string clipMix = Path.Combine(dir, $"selftest-replay{tag}-mixed.mp4");
        string recSep = Path.Combine(dir, $"selftest-recording{tag}.mp4");
        string recMix = Path.Combine(dir, $"selftest-recording{tag}-mixed.mp4");
        foreach (var p in new[] { clipSep, clipMix, recSep, recMix }) File.Delete(p);

        using var gpu = Gpu.Create();
        Console.WriteLine($"[{codec.Short()}] GPU: {gpu.AdapterName}");
        var tracks = new[] { AudioTrack.System, AudioTrack.Mic };
        var router = new MediaRouter();
        router.Replay.Duration = 3;
        router.Replay.SetAudioGroups(ClipWriter.AudioTracks(tracks, mixMic: false));
        router.BufferReplay = true;
        var replayMixed = new ReplayBuffer { Duration = 3 };
        replayMixed.SetAudioGroups(ClipWriter.AudioTracks(tracks, mixMic: true));
        var recorder = full ? new LiveRecorder(recSep, tracks, mixMic: false) : null;
        var recorderMixed = full ? new LiveRecorder(recMix, tracks, mixMic: true) : null;
        router.Recorder = recorder;

        using var converter = new FrameConverter(gpu, w, h, fps);
        VideoEncoder encoder;
        try
        {
            encoder = new VideoEncoder(gpu, w, h, fps, codec, 8_000_000, f => { router.Video(f); replayMixed.Append(f); recorderMixed?.AppendVideo(f); });
        }
        catch (Exception e) when (codec == VideoCodec.Hevc)
        {
            Console.WriteLine($"[HEVC] skipped: {e.Message}");
            return true;
        }
        Console.WriteLine($"[{codec.Short()}] encoder: {encoder.Name} ({(encoder.IsHardware ? "hardware" : "software")}, {(encoder.IsAsync ? "async" : "sync")})");
        if (recorder != null) recorder.RequestKeyframe = encoder.ForceKeyframe;
        if (recorderMixed != null) recorderMixed.RequestKeyframe = encoder.ForceKeyframe;

        using var bgra = gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        using var rtv = gpu.Device.CreateRenderTargetView(bgra);

        long start = Clock.Now, audioFrame = 0;
        int drops = 0;
        const int chunk = 480;
        for (int i = 0; i < fps * seconds; i++)
        {
            long pts = start + i * Clock.Second / fps;
            // Sync markers: a white frame at every whole second, a beep on the system track at the same instant.
            if (i % fps == 0) gpu.Context.ClearRenderTargetView(rtv, new Color4(1, 1, 1, 1));
            else
            {
                float lum = 0.15f + i * 7 % 30 / 100f;
                gpu.Context.ClearRenderTargetView(rtv, new Color4(lum, 0.4f, 0.6f - lum, 1));
            }
            var sample = converter.ConvertOwned(bgra);
            if (sample == null) drops++;
            else encoder.Encode(sample, pts);

            while (audioFrame / (double)Audio.Rate < (i + 1) / (double)fps)
            {
                long apts = start + audioFrame * Clock.Second / Audio.Rate;
                var sys = new AudioChunk(apts, Tone(audioFrame, chunk, 880, 0.3, beeps: true));
                var mic = new AudioChunk(apts, Tone(audioFrame, chunk, 660, 0.05, beeps: false));
                router.Audio(sys, AudioTrack.System);
                router.Audio(mic, AudioTrack.Mic);
                replayMixed.AppendAudio(sys, AudioTrack.System);
                replayMixed.AppendAudio(mic, AudioTrack.Mic);
                recorderMixed?.AppendAudio(sys, AudioTrack.System);
                recorderMixed?.AppendAudio(mic, AudioTrack.Mic);
                audioFrame += chunk;
            }
            long wait = start + (i + 1) * Clock.Second / fps - Clock.Now;
            if (wait > 0) Thread.Sleep(TimeSpan.FromTicks(wait));
        }
        encoder.Dispose();
        if (drops > 0) Console.WriteLine($"[{codec.Short()}] {drops} frames dropped (no free encoder surface)");
        if (encoder.Dropped > 0) Console.WriteLine($"[{codec.Short()}] {encoder.Dropped} frames dropped (encoder busy)");

        router.Recorder = null;
        try
        {
            recorder?.FinishAsync().GetAwaiter().GetResult();
            recorderMixed?.FinishAsync().GetAwaiter().GetResult();
        }
        catch (Exception e) { Console.WriteLine($"FAIL recording: {e}"); return false; }

        double buffered = router.Replay.BufferedSeconds;
        Console.WriteLine($"[{codec.Short()}] buffered {buffered:F2}s (replay length 3s)");
        bool ok = true;
        try
        {
            ClipWriter.WriteAsync(router.Replay.Snapshot(), clipSep).GetAwaiter().GetResult();
            ClipWriter.WriteAsync(replayMixed.Snapshot(), clipMix).GetAwaiter().GetResult();
        }
        catch (Exception e) { Console.WriteLine($"FAIL clip: {e}"); return false; }

        ok &= Inspect(clipSep, audioTracks: 2, minSeconds: 3);
        ok &= Inspect(clipMix, audioTracks: 1, minSeconds: 3);
        if (full)
        {
            ok &= Inspect(recSep, audioTracks: 2, minSeconds: seconds - 0.5);
            ok &= Inspect(recMix, audioTracks: 1, minSeconds: seconds - 0.5);
        }
        return ok;
    }

    static float[] Tone(long startFrame, int frames, double hz, double amp, bool beeps)
    {
        var s = new float[frames * 2];
        for (int n = 0; n < frames; n++)
        {
            long f = startFrame + n;
            if (beeps && f % Audio.Rate >= Audio.Rate / 20) continue;
            s[2 * n] = s[2 * n + 1] = (float)(amp * Math.Sin(2 * Math.PI * hz * f / Audio.Rate));
        }
        return s;
    }

    public static int InspectFile(string path)
    {
        MF.Startup();
        Environment.SetEnvironmentVariable("REWIND_SELFTEST_DEBUG", "1");
        Inspect(Path.GetFullPath(path), audioTracks: -1, minSeconds: 0);
        return 0;
    }

    static bool Inspect(string path, int audioTracks, double minSeconds)
    {
        try
        {
            var streams = new List<(bool video, int samples, double end, List<double> markers)>();
            for (int i = 0; ; i++)
            {
                using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null);
                IMFMediaType native;
                try { native = reader.GetNativeMediaType(i, 0); }
                catch { break; }
                bool video;
                using (native) video = native.GetGUID(MediaTypeAttributeKeys.MajorType) == MediaTypeGuids.Video;
                bool selectedByDefault = reader.GetStreamSelection(i);
                reader.SetStreamSelection(unchecked((int)0xFFFFFFFE), false);
                reader.SetStreamSelection(i, true);
                using (var t = MediaFactory.MFCreateMediaType())
                {
                    t.Set(MediaTypeAttributeKeys.MajorType, video ? MediaTypeGuids.Video : MediaTypeGuids.Audio);
                    t.Set(MediaTypeAttributeKeys.Subtype, video ? VideoFormatGuids.NV12 : AudioFormatGuids.Float);
                    reader.SetCurrentMediaType(i, t);
                }
                int count = 0; double end = 0;
                var markers = new List<double>();
                bool wasWhite = false; int quiet = Audio.Rate;
                while (true)
                {
                    using var s = reader.ReadSample(i, 0, out _, out var flags, out long ts);
                    if (s != null)
                    {
                        count++;
                        end = Math.Max(end, (ts + s.SampleDuration) / 1e7);
                        using var mb = s.ConvertToContiguousBuffer();
                        mb.Lock(out var p, out _, out int len);
                        unsafe
                        {
                            if (video)
                            {
                                byte* y = (byte*)p; int n = Math.Min(len, 256), sum = 0;
                                for (int k = 0; k < n; k++) sum += y[k];
                                bool white = n > 0 && sum / n > 200;
                                if (white && !wasWhite) markers.Add(ts / 1e7);
                                wasWhite = white;
                            }
                            else
                            {
                                float* f = (float*)p; int frames = len / 8;
                                for (int k = 0; k < frames; k++)
                                {
                                    if (Math.Abs(f[2 * k]) >= 0.12f)
                                    {
                                        if (quiet >= Audio.Rate / 100) markers.Add(ts / 1e7 + k / (double)Audio.Rate);
                                        quiet = 0;
                                    }
                                    else quiet++;
                                }
                            }
                        }
                        mb.Unlock();
                    }
                    if (((int)flags & 2) != 0) break;
                    if (((int)flags & 1) != 0) throw new RewindException("read error");
                }
                streams.Add((video, count, end, markers));
                if (Environment.GetEnvironmentVariable("REWIND_SELFTEST_DEBUG") == "1")
                    Console.WriteLine($"  stream {i}: {(video ? "video" : "audio")}{(selectedByDefault ? " (selected by default)" : "")}, {count} samples, end {end:F3}s, markers [{string.Join(", ", markers.Select(m => m.ToString("F3")))}]");
            }
            var v = streams.Where(s => s.video).ToList();
            var a = streams.Where(s => !s.video).ToList();
            double vEnd = v.FirstOrDefault().end, aEnd = a.FirstOrDefault().end;
            long size = new FileInfo(path).Length;

            // Each white frame should have a beep at the same moment on the system-sound track (the one
            // with beeps; Media Foundation doesn't list tracks in file order).
            var offsets = new List<double>();
            var beeps = a.OrderByDescending(x => x.markers.Count).FirstOrDefault().markers ?? new List<double>();
            if (v.Count == 1)
                foreach (var vm in v[0].markers)
                    if (beeps.Count > 0)
                    {
                        double nearest = beeps.MinBy(am => Math.Abs(am - vm));
                        if (Math.Abs(nearest - vm) < 0.5) offsets.Add(nearest - vm);
                    }
            string sync = offsets.Count == 0 ? "no sync markers" : $"A/V offset {offsets.Average() * 1000:+0;-0} ms (worst {offsets.Max(Math.Abs) * 1000:F0} ms, {offsets.Count} markers)";
            Console.WriteLine($"{Path.GetFileName(path)}: {v.Count} video track(s), {v.FirstOrDefault().samples} decoded frames, " +
                              $"{a.Count} audio track(s) (audio {aEnd:F2}s / video {vEnd:F2}s), {sync}, {size / 1024} KB");
            bool ok = v.Count == 1 && a.Count == audioTracks && v[0].samples > 0 && vEnd >= minSeconds &&
                      a.All(x => Math.Abs(x.end - vEnd) < 0.25) &&
                      offsets.Count >= 2 && offsets.Max(Math.Abs) <= 0.020;
            if (!ok) Console.WriteLine($"FAIL {Path.GetFileName(path)}: expected 1 video + {audioTracks} audio, ≥{minSeconds}s, audio within 0.25s of video, A/V sync within 20 ms");
            return ok;
        }
        catch (Exception e)
        {
            Console.WriteLine($"FAIL inspect {Path.GetFileName(path)}: {e.Message}");
            return false;
        }
    }
}
