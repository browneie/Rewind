using System.Collections.Concurrent;

namespace Rewind;

static class Ticks
{
    public static long ToVideo(long ticks) => (long)Math.Round(ticks * (double)Mp4Muxer.VideoTimescale / Clock.Second);
}

static class ClipWriter
{
    public static Task<double> WriteAsync(ReplaySnapshot snap, string path) => Task.Run(() => Write(snap, path));

    static double Write(ReplaySnapshot snap, string path)
    {
        int k = snap.Video.FindIndex(f => f.IsKey);
        if (k < 0) throw new RewindException("Nothing buffered yet.");
        var frames = snap.Video.GetRange(k, snap.Video.Count - k);
        var cfg = frames[0].Config;
        frames = frames.Where(f => ReferenceEquals(f.Config, cfg)).ToList();
        long start = frames[0].Pts, last = frames[^1].Pts;
        long end = last + Clock.Second / cfg.Fps;

        using var mp4 = new Mp4Muxer(path, fastStart: true);
        int vt = mp4.AddVideo(cfg);

        // Audio: AAC packets only start every 1024 samples (21 ms), so each track starts at the packet nearest the
        // first video frame (within ±10.7 ms, which is all Windows' own players get, as they ignore edit lists)
        // and an edit list makes it exact in players that honour one (ffmpeg, browsers, QuickTime).
        var tracks = new List<(int track, double lead, byte[][] packets)>();
        foreach (var a in snap.Audio)
        {
            double startSample = (start - a.Origin) * (double)Audio.Rate / Clock.Second;
            double endSample = (end - a.Origin) * (double)Audio.Rate / Clock.Second;
            long first = (long)Math.Round(startSample / Audio.AacFrame);
            long stop = (long)Math.Ceiling(endSample / Audio.AacFrame);
            double lead = startSample - first * (double)Audio.AacFrame;
            var byIndex = a.Packets.ToDictionary(p => p.Index, p => p.Data);
            var data = new byte[Math.Max(stop - first, 0)][];
            for (long i = first; i < stop; i++)
                data[i - first] = byIndex.TryGetValue(i, out var d) ? d : AacEncoder.SilentFrame;
            if (a.Name.Length > 0 && tracks.Count > 0 && !a.Packets.Any(p => p.Index >= first && p.Index < stop)) continue; // an app not heard in this part
            int track = mp4.AddAudio(Audio.Rate, Audio.Channels, Audio.Bitrate, Audio.AacConfig,
                editMediaTime: (long)Math.Round(Math.Max(lead, 0)), delaySamples: (long)Math.Round(Math.Max(-lead, 0)), name: a.Name);
            tracks.Add((track, lead, data));
        }

        int vi = 0;
        var ai = new int[tracks.Count];
        while (true)
        {
            double vTime = vi < frames.Count ? Clock.Seconds(frames[vi].Pts - start) : double.MaxValue;
            int best = -1; double bestTime = vTime;
            for (int j = 0; j < tracks.Count; j++)
            {
                if (ai[j] >= tracks[j].packets.Length) continue;
                double t = (ai[j] * (double)Audio.AacFrame - tracks[j].lead) / Audio.Rate;
                if (t < bestTime) { bestTime = t; best = j; }
            }
            if (best < 0 && vi >= frames.Count) break;
            if (best < 0)
            {
                var f = frames[vi++];
                mp4.Write(vt, f.Data, Ticks.ToVideo(f.Pts - start), f.IsKey);
            }
            else
            {
                var (track, _, packets) = tracks[best];
                mp4.Write(track, packets[ai[best]], (long)ai[best] * Audio.AacFrame, true);
                ai[best]++;
            }
        }
        mp4.Finish();
        return Clock.Seconds(last - start);
    }

    /// Which sources go into which output track. System sound always comes first, so players that only
    /// play the first track still play the game.
    /// With the microphone mixed in, the microphone and the game sound are also kept apart, so the trimmer
    /// can leave either out.
    public static AudioTrack[][] AudioTracks(IEnumerable<AudioTrack> present, bool mixMic)
    {
        var list = present.Distinct().OrderBy(t => (int)t).ToArray();
        if (mixMic && list.Length > 1) return new[] { list, new[] { AudioTrack.Mic }, new[] { AudioTrack.System } };
        return list.Select(t => new[] { t }).ToArray();
    }
}

sealed class LiveRecorder
{
    public string Path { get; }
    public Action? RequestKeyframe { get; set; }

    readonly AudioTrack[][] groups;
    readonly BlockingCollection<object> queue = new(new ConcurrentQueue<object>());
    readonly Thread thread;
    readonly TaskCompletionSource<double> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly List<(AudioChunk chunk, AudioTrack track)> early = new();
    readonly Dictionary<uint, (int Track, AudioPipeline Pipeline)> apps = new();

    Mp4Muxer? mp4;
    CodecConfig? config;
    AudioPipeline? audio;
    int videoTrack;
    int[] audioTracks = Array.Empty<int>();
    long start, lastPts;
    Exception? failure;
    int waitingForKey;

    public LiveRecorder(string path, IReadOnlyCollection<AudioTrack> tracks, bool mixMic)
    {
        Path = path;
        groups = ClipWriter.AudioTracks(tracks, mixMic);
        thread = new Thread(Run) { IsBackground = true, Name = "Rewind recorder" };
        thread.Start();
    }

    public void AppendVideo(EncodedFrame frame)
    {
        if (!queue.IsAddingCompleted) try { queue.Add(frame); } catch (InvalidOperationException) { }
    }

    public void AppendAudio(AudioChunk chunk, AudioTrack track)
    {
        if (!queue.IsAddingCompleted) try { queue.Add((chunk, track)); } catch (InvalidOperationException) { }
    }

    public void AppendAppAudio(AudioChunk chunk, AppSound app)
    {
        if (!queue.IsAddingCompleted) try { queue.Add((chunk, app)); } catch (InvalidOperationException) { }
    }

    public Task<double> FinishAsync()
    {
        queue.CompleteAdding();
        return result.Task;
    }

    void Run()
    {
        try
        {
            long lastPump = 0;
            while (!queue.IsCompleted)
            {
                if (queue.TryTake(out var item, 250) && failure == null) Handle(item);
                long now = Clock.Now;
                if (failure == null && audio != null && now - lastPump >= Clock.Second / 4)
                {
                    lastPump = now;
                    audio.Pump(WriteAudio, now);
                    foreach (var a in apps.Values) a.Pipeline.Pump((_, index, packet) => mp4!.Write(a.Track, packet, index * Audio.AacFrame, true), now);
                }
            }
            if (failure != null) throw failure;
            if (mp4 == null) throw new RewindException("No video was captured.");
            long endPts = lastPts + Clock.Second / Math.Max(config!.Fps, 1);
            audio!.Finish(WriteAudio, endPts);
            foreach (var a in apps.Values) a.Pipeline.Finish((_, index, packet) => mp4.Write(a.Track, packet, index * Audio.AacFrame, true), endPts);
            if (apps.Count > 0)
                for (int g = 0; g < groups.Length; g++)
                    if (SoundNames.IsPlainCopy(groups, g)) mp4.Omit(audioTracks[g]);
            mp4.Finish();
            result.SetResult(Clock.Seconds(lastPts - start));
        }
        catch (Exception e)
        {
            mp4?.Abort();
            result.TrySetException(e);
        }
        finally
        {
            audio?.Dispose();
            foreach (var a in apps.Values) a.Pipeline.Dispose();
        }
    }

    void WriteAudio(int group, long index, byte[] packet) => mp4!.Write(audioTracks[group], packet, index * Audio.AacFrame, true);

    void Handle(object item)
    {
        try
        {
            if (item is EncodedFrame f) HandleVideo(f);
            else if (item is ValueTuple<AudioChunk, AudioTrack> a) HandleAudio(a.Item1, a.Item2);
            else if (item is ValueTuple<AudioChunk, AppSound> app) HandleApp(app.Item1, app.Item2);
        }
        catch (Exception e)
        {
            Log.Error("recording", e);
            failure = e;
        }
    }

    void HandleVideo(EncodedFrame f)
    {
        if (mp4 == null)
        {
            if (!f.IsKey)
            {
                if (waitingForKey++ % 30 == 0) RequestKeyframe?.Invoke();
                return;
            }
            config = f.Config;
            start = f.Pts;
            mp4 = new Mp4Muxer(Path, fastStart: false);
            videoTrack = mp4.AddVideo(config);
            audioTracks = groups.Select(g => mp4.AddAudio(Audio.Rate, Audio.Channels, Audio.Bitrate, Audio.AacConfig, name: SoundNames.Of(g))).ToArray();
            audio = new AudioPipeline(groups, start);
            foreach (var (c, t) in early) audio.Add(c, t);
            early.Clear();
        }
        if (!ReferenceEquals(f.Config, config)) return; // capture restarted mid-recording; can't splice
        mp4.Write(videoTrack, f.Data, Ticks.ToVideo(f.Pts - start), f.IsKey);
        lastPts = f.Pts;
        long shown = f.Pts + Clock.Second / Math.Max(config.Fps, 1);
        audio!.NoteMedia(shown);
        foreach (var a in apps.Values) a.Pipeline.NoteMedia(shown);
    }

    /// An app gets its own track from when it's first heard; the track starts that far into the recording.
    void HandleApp(AudioChunk c, AppSound app)
    {
        if (mp4 == null) return;
        if (!apps.TryGetValue(app.Pid, out var t))
        {
            if (!AppSound.IsLoud(c) || apps.Count >= AppAudioWatcher.Most) return;
            long origin = Math.Max(c.Pts, start);
            int track = mp4.AddAudio(Audio.Rate, Audio.Channels, Audio.Bitrate, Audio.AacConfig,
                                     delaySamples: Audio.FrameAt(origin, start), name: app.Name);
            apps[app.Pid] = t = (track, new AudioPipeline(new[] { new[] { AudioTrack.System } }, origin));
        }
        t.Pipeline.Add(c, AudioTrack.System);
    }

    void HandleAudio(AudioChunk c, AudioTrack track)
    {
        if (audio == null)
        {
            // Keep a couple of seconds so audio from just before the first keyframe isn't lost.
            early.Add((c, track));
            if (early[0].chunk.Pts < c.Pts - 2 * Clock.Second) early.RemoveAt(0);
            return;
        }
        audio.Add(c, track);
    }
}
