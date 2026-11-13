using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Rewind;

sealed class CodecConfig
{
    public required VideoCodec Codec { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Fps { get; init; }
    public byte[]? Vps { get; set; }
    public byte[]? Sps { get; set; }
    public byte[]? Pps { get; set; }
    public bool Ready => Sps != null && Pps != null && (Codec == VideoCodec.H264 || Vps != null);
}

sealed record EncoderTuning(int RateControl = 1, double PeakFactor = 1.5, double BitrateFactor = 1.0,
                            uint? Quality = null, uint? QualityVsSpeed = null, uint? MaxQp = null, double KeyframeSeconds = 1)
{
    public static readonly EncoderTuning Original = new();

    // Chosen with --quality-test: smaller files with PSNR, worst-frame PSNR and SSIM all at least as good as
    // Original on both game and desktop footage.
    public static EncoderTuning For(VideoCodec codec) => codec == VideoCodec.Hevc
        ? new(QualityVsSpeed: 100, KeyframeSeconds: 2, BitrateFactor: 0.90)
        : new(QualityVsSpeed: 100, KeyframeSeconds: 2, BitrateFactor: 0.95);
}

sealed class EncodedFrame(long pts, bool isKey, byte[] data, CodecConfig config)
{
    public long Pts { get; } = pts;
    public bool IsKey { get; } = isKey;
    public byte[] Data { get; } = data;
    public CodecConfig Config { get; } = config;
    public int Size => Data.Length;
}

sealed class VideoEncoder : IDisposable
{
    readonly Gpu gpu;
    readonly IMFTransform mft;
    readonly ICodecAPI? codecApi;
    readonly IMFMediaEventGenerator? events;
    readonly Action<EncodedFrame> onFrame;
    readonly EncoderTuning tuning;
    readonly CodecConfig config;
    readonly long frameDuration;
    readonly object sync = new();
    readonly Queue<(IMFSample sample, bool key)> pending = new();
    readonly Thread? eventThread;
    readonly ManualResetEventSlim drained = new();
    int needInput;
    bool forceKey, disposed;
    long lastKeyPts = long.MinValue, lastForcedPts = long.MinValue;

    public bool IsHardware { get; }
    public string Name { get; }
    public bool IsAsync => events != null;
    public int Dropped { get; private set; }

    public string TuningReport { get; private set; } = "";
    public int Bitrate { get; }
    public int Pending { get { lock (sync) return pending.Count; } }

    public VideoEncoder(Gpu gpu, int width, int height, int fps, VideoCodec codec, int bitrate, Action<EncodedFrame> onFrame,
                        EncoderTuning? tuning = null)
    {
        this.gpu = gpu;
        this.onFrame = onFrame;
        this.tuning = tuning ?? EncoderTuning.For(codec);
        bitrate = (int)Math.Max(bitrate * this.tuning.BitrateFactor, 500_000);
        Bitrate = bitrate;
        frameDuration = Clock.Second / fps;
        config = new CodecConfig { Codec = codec, Width = width, Height = height, Fps = fps };

        Exception? lastError = null;
        foreach (var (candidate, name, hardware) in Candidates(codec))
        {
            try
            {
                Configure(candidate, hardware, width, height, fps, codec, bitrate, out var api, out var gen);
                mft = candidate; codecApi = api; events = gen; Name = name; IsHardware = hardware;
                break;
            }
            catch (Exception e)
            {
                lastError = e;
                Log.Info($"encoder {name} rejected {width}x{height}@{fps}: {e.Message}");
                try { MediaFactory.MFShutdownObject(candidate); } catch { }
                candidate.Dispose();
            }
        }
        if (mft == null)
            throw new RewindException($"Couldn't start a {codec.Short()} encoder at {width}×{height}" +
                                      (lastError != null ? $" ({lastError.Message})" : "") + ".");
        Name ??= "";
        Log.Info($"encoder: {Name} ({(IsHardware ? "hardware" : "software")}) {width}x{height}@{fps} {codec.Short()} {bitrate / 1_000_000} Mbps");

        mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        if (events != null)
        {
            eventThread = new Thread(EventLoop) { IsBackground = true, Name = "Rewind encoder events", Priority = ThreadPriority.AboveNormal };
            eventThread.Start();
        }
    }

    static IEnumerable<(IMFTransform, string, bool)> Candidates(VideoCodec codec)
    {
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = codec == VideoCodec.H264 ? VideoFormatGuids.H264 : VideoFormatGuids.Hevc };
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
        foreach (var hardware in new[] { true, false })
        {
            if (!hardware && codec != VideoCodec.H264) yield break;
            var flags = (uint)((hardware ? EnumFlag.EnumFlagHardware : EnumFlag.EnumFlagSyncmft) | EnumFlag.EnumFlagSortandfilter);
            IMFActivateCollection? list = null;
            try { list = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, input, output); }
            catch (Exception e) { Log.Error("MFTEnumEx", e); }
            if (list == null) continue;
            try
            {
                foreach (var act in list.ToList())
                {
                    string name = "encoder";
                    try { name = act.GetString(TransformAttributeKeys.MftFriendlyNameAttribute); } catch { }
                    IMFTransform? t = null;
                    try { t = act.ActivateObject<IMFTransform>(); }
                    catch (Exception e) { Log.Info($"couldn't activate {name}: {e.Message}"); }
                    if (t != null) yield return (t, name, hardware);
                }
            }
            finally { list.Dispose(); }
        }
    }

    void Configure(IMFTransform t, bool hardware, int w, int h, int fps, VideoCodec codec, int bitrate,
                   out ICodecAPI? api, out IMFMediaEventGenerator? gen)
    {
        api = null; gen = null;
        using var attrs = t.Attributes;
        bool isAsync = attrs.GetUInt32(TransformAttributeKeys.TransformAsync, out uint a).Success && a != 0;
        if (isAsync) attrs.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
        attrs.Set(CodecApi.LowLatencyMode, 1u);

        if (hardware)
        {
            bool d3d11 = attrs.GetUInt32(TransformAttributeKeys.D3D11Aware, out uint aware).Success && aware != 0;
            if (!d3d11) throw new RewindException("encoder isn't Direct3D 11 aware");
            t.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)gpu.Manager.NativePointer);
        }

        var codecApi = (ICodecAPI)Marshal.GetObjectForIUnknown(t.NativePointer);
        api = codecApi;
        // Rate control and GOP have to be set before the media types on some drivers.
        var report = new List<string>();
        void Try(string name, Guid key, object value) => report.Add($"{name}={value}{(CodecApi.TrySet(codecApi, key, value) ? "" : " (refused)")}");
        Try("rc", CodecApi.RateControlMode, (uint)tuning.RateControl);
        Try("mean", CodecApi.MeanBitRate, (uint)bitrate);
        Try("max", CodecApi.MaxBitRate, (uint)Math.Min(bitrate * tuning.PeakFactor, uint.MaxValue));
        if (tuning.Quality is uint q) Try("quality", CodecApi.Quality, q);
        if (tuning.QualityVsSpeed is uint qs) Try("qualityVsSpeed", CodecApi.QualityVsSpeed, qs);
        if (tuning.MaxQp is uint maxQp) Try("maxQP", CodecApi.MaxQp, maxQp);
        TuningReport = string.Join(", ", report);
        // A keyframe every second, so a replay can start within 1 s of the requested length.
        CodecApi.TrySet(api, CodecApi.GopSize, (uint)Math.Max(1, Math.Round(fps * tuning.KeyframeSeconds)));
        // No B-frames: keeps PTS == DTS so clips can be cut and muxed without re-encoding.
        CodecApi.TrySet(api, CodecApi.BPictureCount, 0u);
        CodecApi.TrySet(api, CodecApi.LowLatencyMode, true);

        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outType.Set(MediaTypeAttributeKeys.Subtype, codec == VideoCodec.H264 ? VideoFormatGuids.H264 : VideoFormatGuids.Hevc);
            outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
            outType.Set(MediaTypeAttributeKeys.FrameSize, MF.Pack(w, h));
            outType.Set(MediaTypeAttributeKeys.FrameRate, MF.Pack(fps, 1));
            outType.Set(MediaTypeAttributeKeys.PixelAspectRatio, MF.Pack(1, 1));
            outType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, codec == VideoCodec.H264 ? 100u /* High */ : 1u /* Main */);
            SetBt709(outType);
            t.SetOutputType(0, outType, 0);
        }
        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            inType.Set(MediaTypeAttributeKeys.FrameSize, MF.Pack(w, h));
            inType.Set(MediaTypeAttributeKeys.FrameRate, MF.Pack(fps, 1));
            inType.Set(MediaTypeAttributeKeys.PixelAspectRatio, MF.Pack(1, 1));
            inType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            SetBt709(inType);
            t.SetInputType(0, inType, 0);
        }
        CodecApi.TrySet(api, CodecApi.MeanBitRate, (uint)bitrate);

        if (isAsync) gen = t.QueryInterface<IMFMediaEventGenerator>();
    }

    static void SetBt709(IMFMediaType type)
    {
        type.Set(MediaTypeAttributeKeys.YuvMatrix, 1u);
        type.Set(MediaTypeAttributeKeys.VideoPrimaries, 2u);
        type.Set(MediaTypeAttributeKeys.TransferFunction, 5u);
        type.Set(MediaTypeAttributeKeys.VideoNominalRange, 2u);
    }

    public void ForceKeyframe()
    {
        lock (sync) forceKey = true;
    }

    public void Encode(IMFSample sample, long pts)
    {
        sample.SampleTime = pts;
        sample.SampleDuration = frameDuration;
        lock (sync)
        {
            if (disposed) { sample.Dispose(); return; }
            // Frames only arrive when the screen changes, so also force a keyframe at least once a second.
            long gop = (long)(Clock.Second * tuning.KeyframeSeconds);
            bool key = forceKey || (pts - Volatile.Read(ref lastKeyPts) >= gop && pts - lastForcedPts >= gop / 2);
            if (key) { forceKey = false; lastForcedPts = pts; }
            if (events != null)
            {
                pending.Enqueue((sample, key));
                // The encoder is falling behind (e.g. a game is hogging the GPU): drop the oldest waiting frame.
                while (pending.Count > 6) { pending.Dequeue().sample.Dispose(); Dropped++; }
                FeedLocked();
            }
            else
            {
                EncodeSync(sample, key);
            }
        }
    }

    void FeedLocked()
    {
        while (needInput > 0 && pending.Count > 0)
        {
            var (s, key) = pending.Dequeue();
            try
            {
                if (key && codecApi != null) CodecApi.TrySet(codecApi, CodecApi.ForceKeyFrame, 1u);
                mft.ProcessInput(0, s, 0);
                needInput--;
            }
            catch (Exception e) { Log.Error("encoder input", e); }
            finally { s.Dispose(); }
        }
    }

    void EventLoop()
    {
        while (true)
        {
            IMFMediaEvent ev;
            try { ev = events!.GetEvent(0); }
            catch { return; }
            using (ev)
            {
                var type = ev.EventType;
                if (type == MediaEventTypes.TransformNeedInput)
                {
                    lock (sync) { needInput++; if (!disposed) FeedLocked(); }
                }
                else if (type == MediaEventTypes.TransformHaveOutput)
                {
                    lock (sync) { DrainOutput(); }
                }
                else if (type == MediaEventTypes.TransformDrainComplete)
                {
                    drained.Set();
                }
            }
        }
    }

    void EncodeSync(IMFSample nv12, bool key)
    {
        // Microsoft's software encoder takes system-memory frames.
        using var cpu = CopyToSystemMemory(nv12);
        nv12.Dispose();
        if (cpu == null) return;
        if (key && codecApi != null) CodecApi.TrySet(codecApi, CodecApi.ForceKeyFrame, 1u);
        try { mft.ProcessInput(0, cpu, 0); }
        catch (SharpGen.Runtime.SharpGenException e) when (e.HResult == MF.E_NOTACCEPTING)
        {
            DrainOutput();
            mft.ProcessInput(0, cpu, 0);
        }
        DrainOutput();
    }

    ID3D11Texture2D? staging;

    IMFSample? CopyToSystemMemory(IMFSample nv12)
    {
        using var buffer = nv12.GetBufferByIndex(0);
        using var dxgi = buffer.QueryInterface<IMFDXGIBuffer>();
        using var tex = new ID3D11Texture2D(dxgi.GetResource(typeof(ID3D11Texture2D).GUID));
        int w = config.Width, h = config.Height;
        staging ??= gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Vortice.DXGI.Format.NV12,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        gpu.Context.CopySubresourceRegion(staging, 0, 0, 0, 0, tex, dxgi.SubresourceIndex, null);
        var map = gpu.Context.Map(staging, 0, MapMode.Read, MapFlags.None);
        try
        {
            int size = w * h * 3 / 2;
            var mb = MediaFactory.MFCreateMemoryBuffer(size);
            mb.Lock(out var dst, out _, out _);
            unsafe
            {
                byte* src = (byte*)map.DataPointer, d = (byte*)dst;
                for (int y = 0; y < h; y++) Buffer.MemoryCopy(src + y * map.RowPitch, d + y * w, w, w);
                byte* uvSrc = src + map.RowPitch * h, uvDst = d + w * h;
                for (int y = 0; y < h / 2; y++) Buffer.MemoryCopy(uvSrc + y * map.RowPitch, uvDst + y * w, w, w);
            }
            mb.Unlock();
            mb.CurrentLength = size;
            var s = MediaFactory.MFCreateSample();
            s.AddBuffer(mb);
            mb.Dispose();
            s.SampleTime = nv12.SampleTime;
            s.SampleDuration = nv12.SampleDuration;
            return s;
        }
        finally { gpu.Context.Unmap(staging, 0); }
    }

    void DrainOutput()
    {
        while (true)
        {
            var info = mft.GetOutputStreamInfo(0);
            bool mftAllocates = (info.Flags & 0x300) != 0;
            var buf = new OutputDataBuffer { StreamID = 0 };
            if (!mftAllocates)
            {
                var s = MediaFactory.MFCreateSample();
                using var mb = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, config.Width * config.Height * 3 / 2));
                s.AddBuffer(mb);
                buf.Sample = s;
            }
            var hr = mft.ProcessOutput(ProcessOutputFlags.None, 1, ref buf, out _);
            try
            {
                if (hr.Code == MF.E_TRANSFORM_NEED_MORE_INPUT) return;
                if (hr.Code == MF.E_TRANSFORM_STREAM_CHANGE)
                {
                    using var t = mft.GetOutputAvailableType(0, 0);
                    mft.SetOutputType(0, t, 0);
                    continue;
                }
                if (hr.Failure) { Log.Info($"encoder output failed: 0x{hr.Code:X8}"); return; }
                if (buf.Sample != null) Deliver(buf.Sample);
            }
            finally
            {
                buf.Sample?.Dispose();
                buf.Events?.Dispose();
            }
            if (events != null) return; // async MFTs post one HaveOutput event per frame
        }
    }

    void Deliver(IMFSample sample)
    {
        byte[] raw;
        using (var mb = sample.ConvertToContiguousBuffer())
        {
            mb.Lock(out var p, out _, out int len);
            raw = new byte[len];
            Marshal.Copy(p, raw, 0, len);
            mb.Unlock();
        }
        long pts = sample.SampleTime;
        var (data, isKey) = Nal.ToMp4(raw, config);
        if (!config.Ready) LoadSequenceHeader();
        if (!config.Ready || data.Length == 0) return;
        if (isKey) Volatile.Write(ref lastKeyPts, pts);
        onFrame(new EncodedFrame(pts, isKey, data, config));
    }

    void LoadSequenceHeader()
    {
        try
        {
            using var t = mft.GetOutputCurrentType(0);
            var blob = t.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
            if (blob is { Length: > 0 }) Nal.ToMp4(blob, config);
        }
        catch { }
    }

    public CodecConfig Config => config;

    public void Dispose()
    {
        // Let the encoder take the frames it was already handed before draining it.
        for (int i = 0; events != null && i < 30; i++)
        {
            lock (sync) if (pending.Count == 0) break;
            Thread.Sleep(10);
        }
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            while (pending.Count > 0) pending.Dequeue().sample.Dispose();
        }
        try
        {
            mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            mft.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
            if (events != null) drained.Wait(1500);
            else lock (sync) DrainOutput();
            mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        }
        catch (Exception e) { Log.Error("encoder drain", e); }
        if (Dropped > 0) Log.Info($"encoder dropped {Dropped} frames it couldn't keep up with");
        try { MediaFactory.MFShutdownObject(mft); } catch { }
        eventThread?.Join(1000);
        events?.Dispose();
        if (codecApi != null) Marshal.ReleaseComObject(codecApi);
        staging?.Dispose();
        mft.Dispose();
    }
}


[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ICodecAPI
{
    [PreserveSig] int IsSupported(in Guid api);
    [PreserveSig] int IsModifiable(in Guid api);
    [PreserveSig] int GetParameterRange(in Guid api, out object min, out object max, out object step);
    [PreserveSig] int GetParameterValues(in Guid api, out IntPtr values, out uint count);
    [PreserveSig] int GetDefaultValue(in Guid api, out object value);
    [PreserveSig] int GetValue(in Guid api, out object value);
    [PreserveSig] int SetValue(in Guid api, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
}

static class CodecApi
{
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid MaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    public static readonly Guid Quality = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
    public static readonly Guid QualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    public static readonly Guid MaxQp = new("3daf6f66-a6a7-45e0-a8e5-f2743f46a3a2");

    public static bool TrySet(ICodecAPI api, Guid key, object value)
    {
        try { return api.SetValue(key, ref value) >= 0; }
        catch { return false; }
    }
}


static class Nal
{
    public static List<(int Start, int Length)> Split(ReadOnlySpan<byte> d)
    {
        var units = new List<(int, int)>();
        bool annexB = d.Length >= 3 && d[0] == 0 && d[1] == 0 && (d[2] == 1 || (d.Length >= 4 && d[2] == 0 && d[3] == 1));
        if (!annexB)
        {
            int p = 0;
            while (p + 4 <= d.Length)
            {
                int len = (int)BinaryPrimitives.ReadUInt32BigEndian(d[p..]);
                if (len <= 0 || p + 4 + len > d.Length) break;
                units.Add((p + 4, len));
                p += 4 + len;
            }
            return units;
        }
        int i = 0, start = -1;
        while (i + 2 < d.Length)
        {
            if (d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1)
            {
                if (start >= 0)
                {
                    int end = i;
                    while (end > start && d[end - 1] == 0) end--;
                    units.Add((start, end - start));
                }
                i += 3;
                start = i;
            }
            else i++;
        }
        if (start >= 0 && start < d.Length) units.Add((start, d.Length - start));
        return units;
    }

    public static (byte[] data, bool key) ToMp4(byte[] raw, CodecConfig cfg)
    {
        var units = Split(raw);
        var ms = new MemoryStream(raw.Length + units.Count * 4);
        Span<byte> len = stackalloc byte[4];
        bool key = false;
        foreach (var (s, n) in units)
        {
            if (n <= 0) continue;
            var nal = raw.AsSpan(s, n);
            if (cfg.Codec == VideoCodec.H264)
            {
                int t = nal[0] & 0x1F;
                if (t == 7) { cfg.Sps ??= nal.ToArray(); continue; }
                if (t == 8) { cfg.Pps ??= nal.ToArray(); continue; }
                if (t == 9) continue;
                if (t == 5) key = true;
            }
            else
            {
                int t = (nal[0] >> 1) & 0x3F;
                if (t == 32) { cfg.Vps ??= nal.ToArray(); continue; }
                if (t == 33) { cfg.Sps ??= nal.ToArray(); continue; }
                if (t == 34) { cfg.Pps ??= nal.ToArray(); continue; }
                if (t == 35) continue;
                if (t is >= 16 and <= 21) key = true;
            }
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)n);
            ms.Write(len);
            ms.Write(nal);
        }
        return (ms.ToArray(), key);
    }

    public static byte[] Unescape(ReadOnlySpan<byte> nal)
    {
        var o = new List<byte>(nal.Length);
        int zeros = 0;
        foreach (var b in nal)
        {
            if (zeros >= 2 && b == 3) { zeros = 0; continue; }
            o.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return o.ToArray();
    }
}
