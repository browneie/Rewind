namespace Rewind;

sealed record AudioPackets(long Origin, List<(long Index, byte[] Data)> Packets);

sealed record ReplaySnapshot(List<EncodedFrame> Video, List<AudioPackets> Audio);

sealed class ReplayBuffer
{
    readonly object gate = new();
    readonly List<EncodedFrame> video = new();
    long videoBytes, audioBytes;
    double duration = 120;
    AudioTrack[][] groups = { new[] { AudioTrack.System }, new[] { AudioTrack.Mic } };
    AudioPipeline? pipeline;
    List<(long Index, byte[] Data)>[] packets = { new(), new() };
    static readonly long PumpInterval = Clock.Second / 4;
    long lastPump;

    public double Duration
    {
        get { lock (gate) return duration; }
        set { lock (gate) { duration = value; TrimLocked(); } }
    }

    public void SetAudioGroups(AudioTrack[][] value)
    {
        lock (gate)
        {
            if (Same(groups, value)) return;
            groups = value;
            ResetAudioLocked();
        }
    }

    static bool Same(AudioTrack[][] a, AudioTrack[][] b) =>
        a.Length == b.Length && a.Zip(b).All(p => p.First.SequenceEqual(p.Second));

    void ResetAudioLocked()
    {
        pipeline?.Dispose();
        pipeline = null;
        packets = groups.Select(_ => new List<(long, byte[])>()).ToArray();
        audioBytes = 0;
    }

    public void Append(EncodedFrame frame)
    {
        lock (gate)
        {
            if (video.Count == 0 && !frame.IsKey) return;
            // A new encoder session (capture restarted) can't be spliced onto the old one.
            if (video.Count > 0 && !ReferenceEquals(video[^1].Config, frame.Config))
            {
                if (!frame.IsKey) return;
                video.Clear(); videoBytes = 0;
            }
            video.Add(frame);
            videoBytes += frame.Size;
            if (pipeline != null)
            {
                pipeline.NoteMedia(frame.Pts);
                PumpLocked(force: false);
            }
            TrimLocked();
        }
    }

    public void AppendAudio(AudioChunk chunk, AudioTrack track)
    {
        lock (gate)
        {
            if (!groups.Any(g => g.Contains(track))) return;
            pipeline ??= new AudioPipeline(groups, chunk.Pts);
            pipeline.Add(chunk, track);
            PumpLocked(force: false);
        }
    }

    void PumpLocked(bool force)
    {
        long now = Clock.Now;
        if (pipeline == null || (!force && now - lastPump < PumpInterval)) return;
        lastPump = now;
        pipeline.Pump(EmitLocked, now);
    }

    void EmitLocked(int group, long index, byte[] data)
    {
        packets[group].Add((index, data));
        audioBytes += data.Length;
        long keep = (long)((duration + 2) * Audio.Rate / Audio.AacFrame);
        TrimPacketsLocked(group, index - keep);
    }

    void TrimLocked()
    {
        if (video.Count == 0) return;
        long cutoff = video[^1].Pts - (long)(duration * Clock.Second);
        // Keep from the last keyframe at or before the cutoff, so the clip is at least `duration` long.
        int cut = 0;
        for (int i = 0; i < video.Count && video[i].Pts <= cutoff; i++)
            if (video[i].IsKey) cut = i;
        if (cut > 0)
        {
            for (int i = 0; i < cut; i++) videoBytes -= video[i].Size;
            video.RemoveRange(0, cut);
        }
        if (pipeline != null)
        {
            // Audio from a little before the first frame is kept: a clip needs one packet of lead-in.
            long first = (long)Math.Floor((video[0].Pts - pipeline.Origin) * (double)Audio.Rate / Clock.Second / Audio.AacFrame) - 2;
            for (int g = 0; g < packets.Length; g++) TrimPacketsLocked(g, first);
        }
    }

    void TrimPacketsLocked(int group, long before)
    {
        var list = packets[group];
        int n = 0;
        while (n < list.Count && list[n].Index < before) { audioBytes -= list[n].Data.Length; n++; }
        if (n > 0) list.RemoveRange(0, n);
    }

    public ReplaySnapshot Snapshot()
    {
        lock (gate)
        {
            PumpLocked(force: true); // encode the latest audio so the clip doesn't end early
            long origin = pipeline?.Origin ?? 0;
            var audio = pipeline == null ? new List<AudioPackets>()
                : packets.Select(p => new AudioPackets(origin, new List<(long, byte[])>(p))).ToList();
            return new ReplaySnapshot(new List<EncodedFrame>(video), audio);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            video.Clear();
            videoBytes = 0;
            ResetAudioLocked();
        }
    }

    public double BufferedSeconds
    {
        get
        {
            lock (gate)
            {
                if (video.Count == 0) return 0;
                return Math.Min(Clock.Seconds(video[^1].Pts - video[0].Pts), duration);
            }
        }
    }

    public long Bytes { get { lock (gate) return videoBytes; } }

    public long AudioBytes { get { lock (gate) return audioBytes; } }
}

sealed class MediaRouter
{
    public ReplayBuffer Replay { get; } = new();
    readonly object gate = new();
    bool bufferReplay;
    LiveRecorder? recorder;

    public bool BufferReplay
    {
        get { lock (gate) return bufferReplay; }
        set { lock (gate) bufferReplay = value; }
    }

    public LiveRecorder? Recorder
    {
        get { lock (gate) return recorder; }
        set { lock (gate) recorder = value; }
    }

    public void Video(EncodedFrame frame)
    {
        bool rep; LiveRecorder? rec;
        lock (gate) { rep = bufferReplay; rec = recorder; }
        if (rep) Replay.Append(frame);
        rec?.AppendVideo(frame);
    }

    public void Audio(AudioChunk chunk, AudioTrack track)
    {
        bool rep; LiveRecorder? rec;
        lock (gate) { rep = bufferReplay; rec = recorder; }
        if (rep) Replay.AppendAudio(chunk, track);
        rec?.AppendAudio(chunk, track);
    }
}
