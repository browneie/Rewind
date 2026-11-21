using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Rewind;

/// Re-encodes (part of) a clip into H.264, HEVC or AV1 at a chosen quality, decoding and encoding on the
/// graphics card. Audio is copied as-is. Trim's "Save as" row uses it; Original goes through ClipTrimmer.
static class ClipConverter
{
    /// The slider's words, from the lowest setting up.
    public static readonly (int At, string Word)[] QualityWords =
    {
        (0, "much smaller file"),
        (25, "smaller file"),
        (45, "balanced"),
        (65, "high quality"),
        (85, "very high quality"),
        (100, "original"),
    };

    public static string Word(int quality) => QualityWords.LastOrDefault(w => quality >= w.At).Word ?? QualityWords[0].Word;

    /// Original, H.264, HEVC and AV1 (which needs a graphics card that can encode it).
    public static IEnumerable<ExportFormat> Formats() => Enum.GetValues<ExportFormat>();

    public const string Av1Needs = "AV1 needs a graphics card that can encode it (NVIDIA RTX 40 series, AMD Radeon RX 7000, Intel Arc or newer).";

    /// Bits a codec needs for the same picture, relative to H.264.
    static double Efficiency(VideoCodec c) => c switch { VideoCodec.Hevc => 0.7, VideoCodec.Av1 => 0.6, _ => 1.0 };

    /// The new file's bitrate. Slider 0â€“99 scales the source's bitrate (adjusted for the codec) from 12% up to
    /// about 95%; 100 ("original") keeps the source's bitrate, so the file comes out about the same size.
    public static int TargetBitrate(double sourceBps, VideoCodec from, VideoCodec to, int quality)
    {
        if (sourceBps <= 0) sourceBps = 20_000_000;
        double bps = quality >= 100
            ? sourceBps
            : sourceBps * Efficiency(to) / Efficiency(from) * 0.12 * Math.Pow(1 / 0.12, Math.Clamp(quality, 0, 99) / 100.0);
        return (int)Math.Clamp(bps, 300_000, 200_000_000);
    }

    /// "Replay â€¦ (HEVC).mp4" for a whole clip, "(trimmed, HEVC)" for part of one.
    public static string OutputPath(string source, ExportFormat format, bool whole) =>
        ClipTrimmer.OutputPath(source, whole ? format.Label() : $"trimmed, {format.Label()}");

    public static Task<double> ConvertAsync(string source, double start, double end, ExportFormat format, int quality, string dest,
                                            Action<double> progress) =>
        Task.Run(() => Convert(source, start, end, format, quality, dest, progress));

    /// `start` and `end` are in seconds of the clip as it's shown (edit lists applied). Returns the new length.
    /// At 100 ("original") a clip already in `format` is cut without re-encoding, exactly like Trim.
    /// With `bitrate` (bits per second), `quality` is ignored and the video is encoded at that bitrate
    /// (for fitting a size limit, see ClipSharer).
    public static double Convert(string source, double start, double end, ExportFormat format, int quality, string dest,
                                 Action<double> progress, int? bitrate = null)
    {
        var codec = format.Codec() ?? throw new RewindException("Nothing to convert to.");
        var r = Mp4Reader.Open(source);
        var v = r.Video ?? throw new RewindException("This clip has no video.");
        if (v.Count == 0) throw new RewindException("This clip's video can't be read.");
        end = Math.Min(end, v.PresentedDuration);
        if (end <= start) throw new RewindException("Choose a part to keep first.");
        if (quality >= 100 && bitrate == null && v.Codec == codec)
        {
            double len = ClipTrimmer.Trim(source, start, end, dest);
            progress(1);
            return len;
        }
        if (codec == VideoCodec.Av1 && !VideoEncoder.HasHardwareEncoder(VideoCodec.Av1))
            throw new RewindException("This PC's graphics card can't encode AV1. " + Av1Needs);

        int fps = (int)Math.Clamp(Math.Round((v.Count - 1) / Math.Max(v.MediaDuration, 0.001)), 1, 360);
        double sourceBps = v.Sizes.Sum(x => (long)x) * 8 / Math.Max(v.MediaDuration, 0.001);
        int rate = bitrate is { } fixedRate ? Math.Max(fixedRate, 100_000) : TargetBitrate(sourceBps, v.Codec, codec, quality);

        // f: the frame on screen at `start`; e: one past the last frame kept.
        int f = 0;
        while (f + 1 < v.Count && v.Presented(f + 1) <= start + 1e-4) f++;
        int e = f + 1;
        while (e < v.Count && v.Presented(e) < end - 1e-4) e++;
        int k = 0; // the keyframe decoding has to start from
        for (int i = 0; i <= f; i++) if (v.IsSync(i)) k = i;
        double shownFrom = v.Presented(f), length = Math.Min(end, v.Presented(e - 1) + 1.0 / fps) - shownFrom;
        double Raw(int i) => (v.Dts[i] - v.Dts[0]) / (double)v.Timescale;

        var dir = Path.GetDirectoryName(dest)!;
        var tmp = Path.Combine(dir, $".converting-{Guid.NewGuid():N}.mp4");
        var spill = Path.Combine(Path.GetTempPath(), $"rewind-convert-{Guid.NewGuid():N}.bin");
        var frames = new List<(long Pts, bool Key, long Offset, int Size)>();
        try
        {
            using (var spillFs = new FileStream(spill, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20, FileOptions.DeleteOnClose))
            {
                CodecConfig config;
                using (var gpu = Gpu.Create())
                {
                    var tuning = new EncoderTuning(RateControl: 1, PeakFactor: 1.5, BitrateFactor: 1.0, QualityVsSpeed: 100, KeyframeSeconds: 2);
                    Exception? encoderError = null;
                    var enc = new VideoEncoder(gpu, v.Width, v.Height, fps, codec, rate, frame =>
                    {
                        lock (frames)
                        {
                            try
                            {
                                long offset = spillFs.Position;
                                spillFs.Write(frame.Data);
                                frames.Add((frame.Pts, frame.IsKey, offset, frame.Size));
                            }
                            catch (Exception ex) { encoderError ??= ex; }
                        }
                    }, tuning, realtime: false);
                    try
                    {
                        Decode(gpu, source, v, Raw, k, f, e, shownFrom, length, enc, progress);
                    }
                    finally { enc.Dispose(); }
                    config = enc.Config;
                    if (encoderError != null) throw encoderError;
                }
                if (frames.Count == 0 || !config.Ready) throw new RewindException("The encoder didn't produce any video.");
                frames.Sort((a, b) => a.Pts.CompareTo(b.Pts));
                if (!frames[0].Key) throw new RewindException("The new file doesn't start on a keyframe.");

                using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                byte[] ReadAt(Stream s, long off, int size)
                {
                    var buf = new byte[size];
                    lock (s) { s.Position = off; s.ReadExactly(buf); }
                    return buf;
                }

                using var mp4 = new Mp4Muxer(tmp, fastStart: true);
                int vt = mp4.AddVideo(config);
                var samples = new List<(double Time, int Track, int Size, long Dts, bool Sync, Func<byte[]> Load)>();
                foreach (var fr in frames)
                {
                    var (off, size) = (fr.Offset, fr.Size);
                    samples.Add((Clock.Seconds(fr.Pts), vt, size, Ticks.ToVideo(fr.Pts), fr.Key, () => ReadAt(spillFs, off, size)));
                }
                foreach (var a in r.AudioTracks)
                {
                    if (a.Count == 0 || a.AudioConfig == null) continue;
                    // Starts at the packet nearest the first frame; an edit list lines it up exactly.
                    int j0 = 0;
                    for (int j = 1; j < a.Count; j++)
                        if (Math.Abs(a.Presented(j) - shownFrom) < Math.Abs(a.Presented(j0) - shownFrom)) j0 = j;
                    long lead = (long)Math.Round((shownFrom - a.Presented(j0)) * a.Timescale);
                    int at = mp4.AddAudio(a.SampleRate > 0 ? a.SampleRate : Audio.Rate, a.Channels > 0 ? a.Channels : Audio.Channels, Audio.Bitrate,
                                          a.AudioConfig, editMediaTime: Math.Max(lead, 0), delaySamples: Math.Max(-lead, 0));
                    double frame = Audio.AacFrame / (double)a.Timescale;
                    for (int j = j0; j < a.Count && a.Presented(j) < shownFrom + length + frame; j++)
                    {
                        var (off, size) = (a.Offsets[j], a.Sizes[j]);
                        samples.Add((a.Presented(j) - shownFrom, at, size, a.Dts[j] - a.Dts[j0], true, () => ReadAt(src, off, size)));
                    }
                }
                foreach (var s in samples.OrderBy(s => s.Time))
                    mp4.Write(s.Track, s.Size, s.Dts, s.Sync, s.Load);
                mp4.Finish();
            }
            File.Move(tmp, dest);
            progress(1);
            return length;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            try { if (File.Exists(spill)) File.Delete(spill); } catch { }
        }
    }

    /// Decodes frames f..e-1 on the graphics card and hands them to the encoder, timed from `shownFrom`.
    static void Decode(Gpu gpu, string source, Mp4Reader.Track v, Func<int, double> raw, int k, int f, int e,
                       double shownFrom, double length, VideoEncoder enc, Action<double> progress)
    {
        using var attrs = MediaFactory.MFCreateAttributes(3);
        attrs.Set(SourceReaderAttributeKeys.D3DManager, gpu.Manager);
        attrs.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(source, attrs);
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        using (var t = MediaFactory.MFCreateMediaType())
        {
            t.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            t.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, t);
        }

        using var copier = new Nv12Copier(gpu, v.Width, v.Height, Math.Max(1, (int)Math.Round((v.Count - 1) / Math.Max(v.MediaDuration, 0.001))));
        // Media Foundation's sample times may or may not include the edit list: the first frame tells which.
        double origin = double.NaN;
        bool seeked = k == 0;
        double lastReport = -1;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double readMs = 0, copyMs = 0, waitMs = 0, encodeMs = 0; int n = 0;
        while (true)
        {
            double t0 = watch.Elapsed.TotalMilliseconds;
            using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out long ts);
            if ((flags & SourceReaderFlag.Error) != 0) throw new RewindException("Couldn't decode the clip.");
            if (sample == null)
            {
                if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
                continue;
            }
            readMs += watch.Elapsed.TotalMilliseconds - t0;
            double t = ts / 1e7;
            if (double.IsNaN(origin))
            {
                origin = t - raw(0);
                if (!seeked)
                {
                    seeked = true;
                    reader.SetCurrentPosition((long)Math.Round((raw(k) + origin) * 1e7));
                    continue;
                }
            }
            int i = Nearest(raw, v.Count, t - origin);
            if (i < f) continue;
            if (i >= e) break;
            double shown = v.Presented(i) - shownFrom;
            double t1 = watch.Elapsed.TotalMilliseconds;
            var nv12 = copier.Copy(sample) ?? throw new RewindException("Couldn't copy a decoded frame.");
            double t2 = watch.Elapsed.TotalMilliseconds;
            WaitFor(() => enc.Pending <= 3);
            double t3 = watch.Elapsed.TotalMilliseconds;
            enc.Encode(nv12, (long)Math.Round(shown * Clock.Second));
            copyMs += t2 - t1; waitMs += t3 - t2; encodeMs += watch.Elapsed.TotalMilliseconds - t3; n++;
            double p = Math.Clamp(shown / Math.Max(length, 0.001), 0, 0.99);
            if (p - lastReport >= 0.01) { lastReport = p; progress(p); }
            if (i == e - 1) break;
        }
        WaitFor(() => enc.Pending == 0, timeoutMs: 5000);
        if (Environment.GetEnvironmentVariable("REWIND_CONVERT_DEBUG") == "1")
            Console.WriteLine($"{n} frames: read {readMs:F0} ms, copy {copyMs:F0} ms, wait for encoder {waitMs:F0} ms, encode call {encodeMs:F0} ms, total {watch.ElapsedMilliseconds} ms");
        if (enc.Dropped > 0) throw new RewindException($"The encoder dropped {enc.Dropped} frames.");
    }

    /// Waits without Thread.Sleep(1), which sleeps ~15 ms on Windows and would cap conversion near 60 fps.
    static void WaitFor(Func<bool> done, int timeoutMs = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var spin = new SpinWait();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs) spin.SpinOnce(sleep1Threshold: -1);
    }

    static int Nearest(Func<int, double> raw, int count, double t)
    {
        int lo = 0, hi = count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (raw(mid) < t) lo = mid + 1; else hi = mid;
        }
        if (lo > 0 && Math.Abs(raw(lo - 1) - t) < Math.Abs(raw(lo) - t)) lo--;
        return lo;
    }

    /// Copies decoded NV12 frames (GPU or system memory) into textures the encoder can take.
    sealed class Nv12Copier : IDisposable
    {
        readonly Gpu gpu;
        readonly IMFVideoSampleAllocatorEx allocator;
        readonly int w, h;
        ID3D11Texture2D? staging;

        public Nv12Copier(Gpu gpu, int width, int height, int fps)
        {
            this.gpu = gpu; w = width; h = height;
            allocator = new IMFVideoSampleAllocatorEx(MediaFactory.MFCreateVideoSampleAllocatorEx(typeof(IMFVideoSampleAllocatorEx).GUID));
            allocator.SetDirectXManager(gpu.Manager);
            using var attrs = MediaFactory.MFCreateAttributes(2);
            attrs.Set(TransformAttributeKeys.D3D11Bindflags, (uint)(BindFlags.RenderTarget | BindFlags.VideoEncoder));
            using var type = MediaFactory.MFCreateMediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            type.Set(MediaTypeAttributeKeys.FrameSize, MF.Pack(width, height));
            type.Set(MediaTypeAttributeKeys.FrameRate, MF.Pack(fps, 1));
            type.Set(MediaTypeAttributeKeys.PixelAspectRatio, MF.Pack(1, 1));
            type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            allocator.InitializeSampleAllocatorEx(6, 24, attrs, type);
        }

        public IMFSample? Copy(IMFSample decoded)
        {
            IMFSample? outSample = null;
            // The encoder hands surfaces back as it finishes them; wait without Thread.Sleep's 15 ms steps.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var spin = new SpinWait();
            while (outSample == null && sw.ElapsedMilliseconds < 5000)
            {
                try { outSample = allocator.AllocateSample(); }
                catch (SharpGen.Runtime.SharpGenException) { spin.SpinOnce(sleep1Threshold: -1); }
            }
            if (outSample == null) return null;
            try
            {
                using var outBuf = outSample.GetBufferByIndex(0);
                using var outDxgi = outBuf.QueryInterface<IMFDXGIBuffer>();
                using var dst = new ID3D11Texture2D(outDxgi.GetResource(typeof(ID3D11Texture2D).GUID));
                uint dstSub = outDxgi.SubresourceIndex;

                using var inBuf = decoded.GetBufferByIndex(0);
                var inDxgi = inBuf.QueryInterfaceOrNull<IMFDXGIBuffer>();
                if (inDxgi != null)
                {
                    using (inDxgi)
                    using (var src = new ID3D11Texture2D(inDxgi.GetResource(typeof(ID3D11Texture2D).GUID)))
                        gpu.Context.CopySubresourceRegion(dst, dstSub, 0, 0, 0, src, inDxgi.SubresourceIndex, new Vortice.Mathematics.Box(0, 0, 0, w, h, 1));
                }
                else
                {
                    CopyFromMemory(decoded, dst, dstSub);
                }
                return outSample;
            }
            catch
            {
                outSample.Dispose();
                throw;
            }
        }

        /// Software decoders hand back system memory: through a staging texture onto the GPU.
        void CopyFromMemory(IMFSample decoded, ID3D11Texture2D dst, uint dstSub)
        {
            staging ??= gpu.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.NV12,
                SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Write,
            });
            using var mb = decoded.ConvertToContiguousBuffer();
            mb.Lock(out var p, out _, out int len);
            try
            {
                // Rows are `w` bytes; the luma plane may be padded to a taller height.
                int paddedH = Math.Max(h, len * 2 / 3 / Math.Max(w, 1));
                var map = gpu.Context.Map(staging, 0, MapMode.Write, MapFlags.None);
                try
                {
                    unsafe
                    {
                        byte* s = (byte*)p, d = (byte*)map.DataPointer;
                        for (int y = 0; y < h; y++) Buffer.MemoryCopy(s + (long)y * w, d + (long)y * map.RowPitch, w, w);
                        byte* uvS = s + (long)w * paddedH, uvD = d + (long)map.RowPitch * h;
                        for (int y = 0; y < h / 2; y++) Buffer.MemoryCopy(uvS + (long)y * w, uvD + (long)y * map.RowPitch, w, w);
                    }
                }
                finally { gpu.Context.Unmap(staging, 0); }
            }
            finally { mb.Unlock(); }
            gpu.Context.CopySubresourceRegion(dst, dstSub, 0, 0, 0, staging, 0, null);
        }

        public void Dispose()
        {
            staging?.Dispose();
            allocator.UninitializeSampleAllocator();
            allocator.Dispose();
        }
    }
}
