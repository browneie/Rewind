using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace Rewind;

sealed record AudioChunk(long Pts, float[] Samples)
{
    public int Frames => Samples.Length / Audio.Channels;
}

static class Audio
{
    public const int Rate = 48_000;
    public const int Channels = 2;
    public const int Bitrate = 192_000;
    public const int AacFrame = 1024;

    public static readonly byte[] AacConfig = { 0x11, 0x90 };

    public static long FrameAt(long pts, long origin) => (long)Math.Round((pts - origin) * (double)Rate / Clock.Second);
}

/// Lays audio packets out on a continuous timeline that starts at `origin`, filling gaps with silence.
/// WASAPI loopback sends nothing while the system is silent, so the gaps are real and need filling to
/// keep sound in sync with the video. Packets that arrive a hair early or late are joined up seamlessly.
sealed class PcmTimeline(long origin)
{
    // Jitter in packet timestamps up to this much is ignored (joined up), beyond it the timestamp wins.
    const int Tolerance = Audio.Rate / 100;

    float[] buf = new float[Audio.Rate * Audio.Channels];
    int head, tail;
    long writeFrame;
    bool started;

    public long Origin { get; } = origin;
    public long WrittenFrames => writeFrame;
    public int Available => (tail - head) / Audio.Channels;

    public void Add(AudioChunk c)
    {
        long at = Audio.FrameAt(c.Pts, Origin);
        // The first packet goes exactly where its timestamp says (it may straddle the start); later ones
        // are joined to the previous one unless the gap is real.
        if (started && Math.Abs(at - writeFrame) <= Tolerance) at = writeFrame;
        if (at + c.Frames > 0) started = true;
        if (at > writeFrame) Silence(at - writeFrame);
        long skip = writeFrame - at;
        if (skip >= c.Frames) return;
        Append(c.Samples.AsSpan((int)Math.Max(skip, 0) * Audio.Channels));
    }

    public void PadTo(long frame)
    {
        if (frame > writeFrame) { Silence(frame - writeFrame); started = true; }
    }

    void Silence(long frames)
    {
        Ensure((int)frames * Audio.Channels);
        Array.Clear(buf, tail, (int)frames * Audio.Channels);
        tail += (int)frames * Audio.Channels;
        writeFrame += frames;
    }

    void Append(ReadOnlySpan<float> samples)
    {
        Ensure(samples.Length);
        samples.CopyTo(buf.AsSpan(tail));
        tail += samples.Length;
        writeFrame += samples.Length / Audio.Channels;
    }

    void Ensure(int extra)
    {
        if (tail + extra <= buf.Length) return;
        int used = tail - head;
        if (used + extra <= buf.Length / 2)
        {
            Array.Copy(buf, head, buf, 0, used);
        }
        else
        {
            var nb = new float[Math.Max(buf.Length * 2, used + extra)];
            Array.Copy(buf, head, nb, 0, used);
            buf = nb;
        }
        head = 0; tail = used;
    }

    public void Take(int frames, Span<float> dest, bool add)
    {
        int n = frames * Audio.Channels;
        var src = buf.AsSpan(head, n);
        if (add) for (int i = 0; i < n; i++) dest[i] += src[i];
        else src.CopyTo(dest);
        head += n;
    }
}

sealed class AudioPipeline : IDisposable
{
    // A source that has sent nothing for this long is treated as silent (loopback sends nothing then).
    // WASAPI delivers every 10 ms, so this is generous while keeping a saved clip's ending intact.
    static readonly long Patience = Clock.Second * 15 / 100;

    readonly Dictionary<AudioTrack, PcmTimeline> timelines = new();
    readonly AudioOutput[] outputs;
    readonly List<byte[]> scratch = new();
    readonly long[] next;
    long latestMedia;

    public long Origin { get; }
    public AudioTrack[][] Groups { get; }

    public AudioPipeline(AudioTrack[][] groups, long origin)
    {
        Origin = origin;
        Groups = groups;
        latestMedia = origin;
        outputs = groups.Select(g => new AudioOutput(g.Select(t => timelines[t] = new PcmTimeline(origin)).ToArray())).ToArray();
        next = new long[groups.Length];
    }

    public void Add(AudioChunk c, AudioTrack track)
    {
        if (!timelines.TryGetValue(track, out var tl)) return;
        tl.Add(c);
        latestMedia = Math.Max(latestMedia, c.Pts + c.Frames * Clock.Second / Audio.Rate);
    }

    public void NoteMedia(long pts) => latestMedia = Math.Max(latestMedia, pts);

    public void Pump(Action<int, long, byte[]> emit, long now) =>
        Run(Audio.FrameAt(Math.Min(now - Patience, latestMedia), Origin), final: false, emit);

    public void Finish(Action<int, long, byte[]> emit, long endPts)
    {
        long until = Audio.FrameAt(Math.Max(endPts, latestMedia), Origin);
        if (timelines.Count > 0) until = Math.Max(until, timelines.Values.Max(t => t.WrittenFrames));
        Run((until + Audio.AacFrame - 1) / Audio.AacFrame * Audio.AacFrame, final: true, emit);
    }

    void Run(long until, bool final, Action<int, long, byte[]> emit)
    {
        foreach (var tl in timelines.Values) tl.PadTo(until);
        for (int i = 0; i < outputs.Length; i++)
        {
            outputs[i].Pump(scratch, final);
            foreach (var p in scratch) emit(i, next[i]++, p);
            scratch.Clear();
        }
    }

    public void Dispose()
    {
        foreach (var o in outputs) o.Dispose();
    }
}

sealed class AudioOutput : IDisposable
{
    readonly PcmTimeline[] sources;
    readonly AacEncoder encoder = new();
    readonly float[] scratch = new float[Audio.Rate * Audio.Channels];

    public AudioOutput(params PcmTimeline[] sources) { this.sources = sources; }

    public void Pump(List<byte[]> packets, bool final = false)
    {
        while (true)
        {
            int avail = sources.Min(s => s.Available);
            int frames = final ? avail : avail / Audio.AacFrame * Audio.AacFrame;
            frames = Math.Min(frames, scratch.Length / Audio.Channels);
            if (frames <= 0) break;
            var span = scratch.AsSpan(0, frames * Audio.Channels);
            for (int i = 0; i < sources.Length; i++) sources[i].Take(frames, span, add: i > 0);
            encoder.Write(span, packets);
        }
        if (final) encoder.Finish(packets);
    }

    public void Dispose() => encoder.Dispose();
}

sealed class AacEncoder : IDisposable
{
    static readonly Lazy<byte[]> silence = new(() =>
    {
        using var enc = new AacEncoder();
        var packets = new List<byte[]>();
        enc.Write(new float[Audio.AacFrame * 4 * Audio.Channels], packets);
        enc.Finish(packets);
        return packets[Math.Min(2, packets.Count - 1)];
    });

    public static byte[] SilentFrame => silence.Value;

    readonly IMFTransform mft;
    short[] pcm = new short[8192];
    long inputFrames;

    public AacEncoder()
    {
        MF.Startup();
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Pcm };
        var output = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Aac };
        using var list = MediaFactory.MFTEnumEx(TransformCategoryGuids.AudioEncoder,
            (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter), input, output);
        var act = list.FirstOrDefault() ?? throw new RewindException(
            "Windows' AAC encoder isn't available. On Windows N editions, install the Media Feature Pack.");
        mft = act.ActivateObject<IMFTransform>();

        using var inType = MediaFactory.MFCreateMediaType();
        inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        inType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
        inType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        inType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)Audio.Rate);
        inType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)Audio.Channels);
        inType.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(Audio.Channels * 2));
        inType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(Audio.Rate * Audio.Channels * 2));

        using var outType = MediaFactory.MFCreateMediaType();
        outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        outType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
        outType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        outType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)Audio.Rate);
        outType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)Audio.Channels);
        outType.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(Audio.Bitrate / 8));
        outType.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
        outType.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29u);

        mft.SetInputType(0, inType, 0);
        mft.SetOutputType(0, outType, 0);
        mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    public void Write(ReadOnlySpan<float> interleaved, List<byte[]> packets)
    {
        if (pcm.Length < interleaved.Length) pcm = new short[interleaved.Length];
        for (int i = 0; i < interleaved.Length; i++)
        {
            float v = interleaved[i];
            pcm[i] = (short)Math.Clamp((int)MathF.Round(v * 32767f), short.MinValue, short.MaxValue);
        }
        int bytes = interleaved.Length * 2;
        using var mb = MediaFactory.MFCreateMemoryBuffer(bytes);
        mb.Lock(out var p, out _, out _);
        Marshal.Copy(pcm, 0, p, interleaved.Length);
        mb.Unlock();
        mb.CurrentLength = bytes;
        using var s = MediaFactory.MFCreateSample();
        s.AddBuffer(mb);
        int frames = interleaved.Length / Audio.Channels;
        s.SampleTime = inputFrames * Clock.Second / Audio.Rate;
        s.SampleDuration = (long)frames * Clock.Second / Audio.Rate;
        inputFrames += frames;
        mft.ProcessInput(0, s, 0);
        Drain(packets);
    }

    public void Finish(List<byte[]> packets)
    {
        mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        mft.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        Drain(packets);
    }

    void Drain(List<byte[]> packets)
    {
        var info = mft.GetOutputStreamInfo(0);
        while (true)
        {
            var buf = new OutputDataBuffer { StreamID = 0 };
            var sample = MediaFactory.MFCreateSample();
            var mb = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, 8192));
            sample.AddBuffer(mb);
            mb.Dispose();
            buf.Sample = sample;
            var hr = mft.ProcessOutput(ProcessOutputFlags.None, 1, ref buf, out _);
            try
            {
                if (hr.Code == MF.E_TRANSFORM_NEED_MORE_INPUT || hr.Failure) return;
                using var outBuf = buf.Sample!.ConvertToContiguousBuffer();
                outBuf.Lock(out var p, out _, out int len);
                if (len > 0)
                {
                    var a = new byte[len];
                    Marshal.Copy(p, a, 0, len);
                    packets.Add(a);
                }
                outBuf.Unlock();
            }
            finally
            {
                buf.Sample?.Dispose();
                buf.Events?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        try { mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        mft.Dispose();
    }
}
