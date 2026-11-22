using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace Rewind;

/// An app making sound, recorded on a track of its own. `Pid` is the top process of the app (its child
/// processes' sound is included).
sealed record AppSound(uint Pid, string Name)
{
    /// Louder than about -50 dB somewhere in the chunk: the app is really making sound, not just holding a stream open.
    public static bool IsLoud(AudioChunk c)
    {
        foreach (var v in c.Samples) if (v > 0.003f || v < -0.003f) return true;
        return false;
    }
}

/// What sound tracks are called in the files Rewind writes. The first track is the clip's own mix:
/// "All sound" (or the captured app's name), with " + Mic" when the microphone is mixed in.
static class SoundNames
{
    public const string All = "All sound", Mic = "Mic", WithMic = " + Mic";

    /// "All sound" while capturing a display, the app's name while capturing an app's sound only.
    public static volatile string System = All;

    public static string Of(AudioTrack[] group) =>
        group.Contains(AudioTrack.System) ? System + (group.Contains(AudioTrack.Mic) ? WithMic : "") : Mic;

    /// The game-sound-only copy kept beside a mix with the microphone in it: not needed once apps have tracks of their own.
    public static bool IsPlainCopy(AudioTrack[][] groups, int g) => g > 0 && groups[g] is [AudioTrack.System];
}

/// Watches which apps are making sound and records each one on its own (Windows' per-process loopback).
sealed class AppAudioWatcher : IDisposable
{
    public const int Most = 8;

    readonly Action<AudioChunk, AppSound> onChunk;
    readonly uint targetPid;
    readonly string? targetName;
    readonly Timer timer;
    readonly object gate = new();
    readonly Dictionary<uint, (AppSound App, AudioCapture Capture)> live = new();
    readonly HashSet<uint> failed = new();
    readonly uint self = (uint)Environment.ProcessId;
    bool disposed;

    /// `targetPid`/`targetName`: the app being captured, whose track gets the same name as the clip's sound.
    public AppAudioWatcher(uint targetPid, string? targetName, Action<AudioChunk, AppSound> onChunk)
    {
        this.targetPid = targetPid;
        this.targetName = targetName;
        this.onChunk = onChunk;
        timer = new Timer(_ => Poll(), null, 300, 1500);
    }

    void Poll()
    {
        if (!Monitor.TryEnter(gate)) return;
        try
        {
            if (disposed) return;
            var procs = Processes();
            foreach (var pid in live.Keys.ToList())
                if (!procs.ContainsKey(pid)) { live[pid].Capture.Dispose(); live.Remove(pid); }
            uint selfRoot = Root(self, procs);
            foreach (var pid in AudiblePids())
            {
                if (live.Count >= Most) break;
                uint root = Root(pid, procs);
                if (root == 0 || root == self || root == selfRoot || live.ContainsKey(root) || failed.Contains(root)) continue;
                var app = new AppSound(root, Unique(NameOf(root, procs)));
                try
                {
                    live[root] = (app, AudioCapture.Start(AudioCapture.Source.Process, root, c => onChunk(c, app)));
                    Log.Info($"app sound: {app.Name} (pid {root})");
                }
                catch (Exception e) { failed.Add(root); Log.Error($"app sound {app.Name}", e); }
            }
        }
        catch (Exception e) { Log.Error("app sounds", e); }
        finally { Monitor.Exit(gate); }
    }

    string Unique(string name)
    {
        if (name is SoundNames.Mic or SoundNames.All || name.EndsWith(SoundNames.WithMic)) name += " (app)";
        var taken = live.Values.Select(v => v.App.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = name;
        for (int i = 2; taken.Contains(n); i++) n = $"{name} {i}";
        return n;
    }

    string NameOf(uint pid, Dictionary<uint, (uint Parent, string Exe)> procs)
    {
        if (targetPid != 0 && targetName is { Length: > 0 } && Root(targetPid, procs) == pid) return targetName;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string? desc = null;
            try { desc = p.MainModule?.FileVersionInfo.FileDescription?.Trim(); } catch { }
            if (desc is { Length: > 0 and <= 28 } && !desc.Contains("(TM)") && !desc.Contains("binary", StringComparison.OrdinalIgnoreCase) &&
                !desc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return desc;
            if (p.MainWindowTitle is { Length: > 0 and <= 28 } title) return title;
        }
        catch { }
        var exe = Path.GetFileNameWithoutExtension(procs.TryGetValue(pid, out var e) ? e.Exe : "");
        return exe.Length > 0 ? char.ToUpperInvariant(exe[0]) + exe[1..] : "App";
    }

    /// The topmost process of the same program: a browser's or Discord's helper processes count as the app.
    static uint Root(uint pid, Dictionary<uint, (uint Parent, string Exe)> procs)
    {
        if (!procs.TryGetValue(pid, out var me)) return pid;
        for (int i = 0; i < 16; i++)
        {
            if (!procs.TryGetValue(me.Parent, out var up) || me.Parent == pid || !up.Exe.Equals(me.Exe, StringComparison.OrdinalIgnoreCase)) break;
            pid = me.Parent;
            me = up;
        }
        return pid;
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            timer.Dispose();
            foreach (var (_, c) in live.Values) c.Dispose();
            live.Clear();
        }
    }

    // MARK: Which apps are making sound

    static List<uint> AudiblePids()
    {
        var result = new List<uint>();
        var en = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!)!;
        try
        {
            if (en.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out var devices) != 0) return result;
            devices.GetCount(out uint n);
            for (uint d = 0; d < n; d++)
            {
                if (devices.Item(d, out var dev) != 0) continue;
                try
                {
                    var iid = typeof(IAudioSessionManager2).GUID;
                    if (dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var o) != 0) continue;
                    var manager = (IAudioSessionManager2)o;
                    if (manager.GetSessionEnumerator(out var sessions) != 0) continue;
                    sessions.GetCount(out int count);
                    for (int i = 0; i < count; i++)
                    {
                        if (sessions.GetSession(i, out var session) != 0) continue;
                        try
                        {
                            if (session.GetProcessId(out uint pid) != 0 || pid == 0) continue;
                            bool audible;
                            try { audible = ((IAudioMeterInformation)session).GetPeakValue(out float peak) == 0 && peak > 0.003f; }
                            catch { audible = session.GetState(out int state) == 0 && state == 1; }
                            if (audible && !result.Contains(pid)) result.Add(pid);
                        }
                        finally { Marshal.ReleaseComObject(session); }
                    }
                    Marshal.ReleaseComObject(sessions);
                    Marshal.ReleaseComObject(manager);
                }
                finally { Marshal.ReleaseComObject(dev); }
            }
            Marshal.ReleaseComObject(devices);
        }
        finally { Marshal.ReleaseComObject(en); }
        return result;
    }

    static Dictionary<uint, (uint Parent, string Exe)> Processes()
    {
        var map = new Dictionary<uint, (uint, string)>();
        var snap = CreateToolhelp32Snapshot(2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                map[e.th32ProcessID] = (e.th32ParentProcessID, e.szExeFile);
        }
        finally { CloseHandle(snap); }
        return map;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr context);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(IntPtr group, IntPtr context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}

/// One of a clip's sounds the trimmer offers to keep or leave out.
sealed record ClipSound(string Name, int Track, bool On, bool IsMic);

/// Which of a clip's sound tracks to keep when saving part of it, mixing several into one when needed.
static class ClipAudio
{
    /// The sounds a clip can be saved with, and which are in it as it is (the first track's mix).
    public static List<ClipSound> Sources(Mp4Reader r)
    {
        var names = r.AudioTracks.Select(t => t.Name).ToList();
        var list = new List<ClipSound>();
        if (names.Count == 0) return list;
        if (names.All(n => n.Length == 0))
        {
            // Clips from before tracks had names: the first is the sound, a second one the microphone.
            list.Add(new ClipSound("Sound", 0, true, false));
            for (int i = 1; i < names.Count; i++)
                list.Add(names.Count == 2 ? new ClipSound(SoundNames.Mic, i, false, true) : new ClipSound($"Track {i + 1}", i, false, false));
            return list;
        }
        int mic = names.FindIndex(1, n => n == SoundNames.Mic);
        bool mixed = mic > 0 && names[0].EndsWith(SoundNames.WithMic);
        string main = mixed ? names[0][..^SoundNames.WithMic.Length] : names[0];
        var others = Enumerable.Range(1, names.Count - 1).Where(i => i != mic).ToList();
        if (others.Count == 0)
        {
            if (!mixed) list.Add(new ClipSound(main, 0, true, false));
        }
        else
        {
            bool all = main == SoundNames.All || !others.Any(i => names[i] == main);
            foreach (var i in others) list.Add(new ClipSound(names[i], i, all || names[i] == main, false));
        }
        if (mic > 0) list.Add(new ClipSound(SoundNames.Mic, mic, mixed || list.Count == 0, true));
        return list;
    }

    /// The tracks to save: null keeps the clip's sound as it is (every track), empty saves no sound,
    /// several are mixed into one.
    public static int[]? Choose(List<ClipSound> sources, ICollection<int> on)
    {
        if (sources.All(s => s.On == on.Contains(s.Track))) return null;
        return sources.Where(s => on.Contains(s.Track)).Select(s => s.Track).ToArray();
    }

    /// The sound tracks a saved part gets: `choice` as from Choose, mixed if it names several tracks.
    /// `until`: seconds of the clip (as shown) that are needed.
    public static List<Mp4Reader.Track> Pick(string path, Mp4Reader r, int[]? choice, double until)
    {
        var tracks = r.AudioTracks.ToList();
        if (choice == null) return tracks;
        var chosen = choice.Where(i => i >= 0 && i < tracks.Count).Select(i => tracks[i]).ToList();
        return chosen.Count <= 1 ? chosen : new List<Mp4Reader.Track> { Mix(path, chosen, until) };
    }

    /// Decodes the tracks, adds them up and encodes the result: one track that starts at the clip's start.
    static Mp4Reader.Track Mix(string path, List<Mp4Reader.Track> parts, double until)
    {
        int frames = (int)Math.Ceiling((until + 0.25) * Audio.Rate);
        var mix = new float[frames * Audio.Channels];
        int shift = RoundTripDelay.Value;
        using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            foreach (var a in parts)
            {
                if (a.Count == 0 || a.AudioConfig == null) continue;
                using var dec = new AacDecoder(a.AudioConfig, a.SampleRate > 0 ? a.SampleRate : Audio.Rate, a.Channels > 0 ? a.Channels : Audio.Channels);
                long at = long.MinValue;
                void Add(long time, float[] pcm)
                {
                    if (at == long.MinValue) at = (long)Math.Round(time / 1e7 * Audio.Rate) - shift;
                    for (int i = 0; i < pcm.Length; i++)
                    {
                        long k = at * Audio.Channels + i;
                        if (k >= 0 && k < mix.Length) mix[k] += pcm[i];
                    }
                    at += pcm.Length / Audio.Channels;
                }
                for (int j = 0; j < a.Count && a.Presented(j) < until + 0.25; j++)
                {
                    var buf = new byte[a.Sizes[j]];
                    src.Position = a.Offsets[j];
                    src.ReadExactly(buf);
                    dec.Decode(buf, (long)Math.Round(a.Presented(j) * 1e7), Add);
                }
                dec.Finish(Add);
            }

        var packets = new List<byte[]>();
        using (var enc = new AacEncoder())
        {
            for (int off = 0; off < mix.Length; off += Audio.Rate * Audio.Channels)
                enc.Write(mix.AsSpan(off, Math.Min(Audio.Rate * Audio.Channels, mix.Length - off)), packets);
            enc.Finish(packets);
        }
        var t = new Mp4Reader.Track
        {
            Handler = "soun", Timescale = Audio.Rate, SampleRate = Audio.Rate, Channels = Audio.Channels, AudioConfig = Audio.AacConfig,
            Name = string.Join(" + ", parts.Select(p => p.Name.Length > 0 ? p.Name : "Sound")), Data = packets, LastDelta = Audio.AacFrame,
        };
        for (int k = 0; k < packets.Count; k++) { t.Dts.Add((long)k * Audio.AacFrame); t.Sizes.Add(packets[k].Length); t.Offsets.Add(0); }
        return t;
    }

    public static int Delay => RoundTripDelay.Value;

    /// Samples a sound moves later by being encoded and decoded once (Windows' AAC encoder and decoder):
    /// taken off when mixing, so a mixed track lines up like the tracks it came from.
    static readonly Lazy<int> RoundTripDelay = new(() =>
    {
        try
        {
            const int click = 20_000;
            var pcm = new float[Audio.Rate * Audio.Channels];
            pcm[click * 2] = pcm[click * 2 + 1] = 0.9f;
            var packets = new List<byte[]>();
            using (var enc = new AacEncoder()) { enc.Write(pcm, packets); enc.Finish(packets); }
            var outPcm = new List<float>();
            long first = long.MinValue;
            using (var dec = new AacDecoder(Audio.AacConfig, Audio.Rate, Audio.Channels))
            {
                void Add(long time, float[] s) { if (first == long.MinValue) first = (long)Math.Round(time / 1e7 * Audio.Rate); outPcm.AddRange(s); }
                for (int j = 0; j < packets.Count; j++) dec.Decode(packets[j], (long)Math.Round(j * Audio.AacFrame * 1e7 / Audio.Rate), Add);
                dec.Finish(Add);
            }
            int best = 0;
            for (int i = 0; i < outPcm.Count; i += Audio.Channels) if (Math.Abs(outPcm[i]) > Math.Abs(outPcm[best])) best = i;
            int delay = (int)(first + best / Audio.Channels - click);
            Log.Info($"AAC round trip delay: {delay} samples");
            return Math.Abs(delay) < Audio.Rate / 4 ? delay : 0;
        }
        catch (Exception e) { Log.Error("AAC round trip", e); return 0; }
    });
}

/// Windows' AAC decoder, giving 32-bit float samples.
sealed class AacDecoder : IDisposable
{
    readonly IMFTransform mft;
    readonly bool pcm16;
    readonly int rate;

    public AacDecoder(byte[] audioSpecificConfig, int sampleRate, int channels)
    {
        rate = sampleRate;
        MF.Startup();
        var input = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Aac };
        using var list = MediaFactory.MFTEnumEx(TransformCategoryGuids.AudioDecoder,
            (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagSortandfilter), input, null);
        var act = list.FirstOrDefault() ?? throw new RewindException(
            "Windows' AAC decoder isn't available. On Windows N editions, install the Media Feature Pack.");
        mft = act.ActivateObject<IMFTransform>();

        using var inType = MediaFactory.MFCreateMediaType();
        inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
        inType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
        inType.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)sampleRate);
        inType.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels);
        inType.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
        inType.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
        // HEAACWAVEINFO's fields after WAVEFORMATEX (raw payload, profile unknown), then the AudioSpecificConfig.
        var user = new byte[12 + audioSpecificConfig.Length];
        user[2] = 0xFE;
        audioSpecificConfig.CopyTo(user, 12);
        inType.SetBlob(MediaTypeAttributeKeys.UserData, user);
        mft.SetInputType(0, inType, 0);

        IMFMediaType? chosen = null;
        for (int i = 0; i < 32; i++)
        {
            IMFMediaType t;
            try { t = mft.GetOutputAvailableType(0, i); } catch { break; }
            var sub = t.GetGUID(MediaTypeAttributeKeys.Subtype);
            if (sub == AudioFormatGuids.Float) { chosen?.Dispose(); chosen = t; break; }
            if (sub == AudioFormatGuids.Pcm && chosen == null) chosen = t; else t.Dispose();
        }
        if (chosen == null) throw new RewindException("Windows' AAC decoder can't give plain samples.");
        pcm16 = chosen.GetGUID(MediaTypeAttributeKeys.Subtype) == AudioFormatGuids.Pcm;
        mft.SetOutputType(0, chosen, 0);
        chosen.Dispose();
        mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    /// `time` in 100 ns units. `output` gets each run of decoded samples with its time.
    public void Decode(byte[] packet, long time, Action<long, float[]> output)
    {
        using var mb = MediaFactory.MFCreateMemoryBuffer(packet.Length);
        mb.Lock(out var p, out _, out _);
        Marshal.Copy(packet, 0, p, packet.Length);
        mb.Unlock();
        mb.CurrentLength = packet.Length;
        using var s = MediaFactory.MFCreateSample();
        s.AddBuffer(mb);
        s.SampleTime = time;
        s.SampleDuration = Audio.AacFrame * 10_000_000L / rate;
        mft.ProcessInput(0, s, 0);
        Drain(output, time);
    }

    public void Finish(Action<long, float[]> output)
    {
        mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        mft.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        Drain(output, 0);
    }

    void Drain(Action<long, float[]> output, long fallbackTime)
    {
        var info = mft.GetOutputStreamInfo(0);
        while (true)
        {
            var buf = new OutputDataBuffer { StreamID = 0 };
            var sample = MediaFactory.MFCreateSample();
            var mb = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, 65536));
            sample.AddBuffer(mb);
            mb.Dispose();
            buf.Sample = sample;
            var hr = mft.ProcessOutput(ProcessOutputFlags.None, 1, ref buf, out _);
            try
            {
                if (hr.Code == MF.E_TRANSFORM_NEED_MORE_INPUT || hr.Failure) return;
                long time = fallbackTime;
                try { time = buf.Sample!.SampleTime; } catch { }
                using var outBuf = buf.Sample!.ConvertToContiguousBuffer();
                outBuf.Lock(out var p, out _, out int len);
                float[] f;
                if (pcm16)
                {
                    var sh = new short[len / 2];
                    Marshal.Copy(p, sh, 0, sh.Length);
                    f = new float[sh.Length];
                    for (int i = 0; i < sh.Length; i++) f[i] = sh[i] / 32768f;
                }
                else
                {
                    f = new float[len / 4];
                    Marshal.Copy(p, f, 0, f.Length);
                }
                outBuf.Unlock();
                if (f.Length > 0) output(time, f);
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
