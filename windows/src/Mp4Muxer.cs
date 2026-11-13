using System.Buffers.Binary;
using System.Text;

namespace Rewind;

sealed class Mp4Muxer : IDisposable
{
    public const int VideoTimescale = 90_000;

    sealed class Track
    {
        public required int Id;
        public required bool IsVideo;
        public required int Timescale;
        public CodecConfig? Video;
        public int SampleRate, Channels, Bitrate;
        public byte[]? AudioConfig;
        public long EditMediaTime, DelaySamples;
        public readonly List<int> Sizes = new();
        public readonly List<long> Offsets = new();
        public readonly List<long> Dts = new();
        public readonly List<int> Sync = new();
        public long LastDuration;
        public long Duration => Dts.Count == 0 ? 0 : Dts[^1] - Dts[0] + LastDuration;
        public long Presentation => Math.Max(Duration - EditMediaTime, 0) + DelaySamples;
    }

    const int MdatHeader = 16;
    readonly string path;
    readonly bool fastStart;
    readonly FileStream? fs;
    readonly List<byte[]> planned = new();
    readonly List<Track> tracks = new();
    readonly long mdatStart;
    readonly byte[] ftyp;
    long position;
    bool finished;

    public Mp4Muxer(string path, bool fastStart)
    {
        this.path = path;
        this.fastStart = fastStart;
        ftyp = BuildFtyp();
        mdatStart = ftyp.Length;
        position = mdatStart + MdatHeader;
        if (fastStart) return;
        fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
        fs.Write(ftyp);
        fs.Write(MdatHeaderBytes(0));
    }

    static byte[] MdatHeaderBytes(long size)
    {
        var hdr = new byte[MdatHeader];
        BinaryPrimitives.WriteUInt32BigEndian(hdr, 1);
        Encoding.ASCII.GetBytes("mdat", hdr.AsSpan(4));
        BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(8), (ulong)size);
        return hdr;
    }

    public int AddVideo(CodecConfig cfg)
    {
        var t = new Track { Id = tracks.Count + 1, IsVideo = true, Timescale = VideoTimescale, Video = cfg, LastDuration = VideoTimescale / Math.Max(cfg.Fps, 1) };
        tracks.Add(t);
        return tracks.Count - 1;
    }

    public int AddAudio(int sampleRate, int channels, int bitrate, byte[] audioSpecificConfig, long editMediaTime = 0, long delaySamples = 0)
    {
        var t = new Track
        {
            Id = tracks.Count + 1, IsVideo = false, Timescale = sampleRate, SampleRate = sampleRate, Channels = channels,
            Bitrate = bitrate, AudioConfig = audioSpecificConfig, LastDuration = 1024,
            EditMediaTime = editMediaTime, DelaySamples = delaySamples,
        };
        tracks.Add(t);
        return tracks.Count - 1;
    }

    public void Write(int track, byte[] data, long dts, bool sync)
    {
        var t = tracks[track];
        if (t.Dts.Count > 0 && dts <= t.Dts[^1]) dts = t.Dts[^1] + 1;
        t.Offsets.Add(position);
        t.Sizes.Add(data.Length);
        t.Dts.Add(dts);
        if (sync) t.Sync.Add(t.Dts.Count);
        if (fastStart) planned.Add(data);
        else fs!.Write(data);
        position += data.Length;
    }

    public int SampleCount(int track) => tracks[track].Dts.Count;

    public double VideoSeconds => tracks.FirstOrDefault(t => t.IsVideo) is { } v ? v.Duration / (double)v.Timescale : 0;

    public void Finish()
    {
        if (finished) return;
        finished = true;
        long mdatSize = position - mdatStart;

        if (!fastStart)
        {
            fs!.Position = mdatStart;
            fs.Write(MdatHeaderBytes(mdatSize));
            fs.Position = position;
            fs.Write(BuildMoov(0));
            fs.Flush(true);
            fs.Dispose();
            return;
        }

        // ftyp, moov, mdat: every sample offset moves forward by the size of moov.
        var moov = BuildMoov(0);
        moov = BuildMoov(moov.Length);
        moov = BuildMoov(moov.Length); // in case co64 changed the size
        var part = path + ".part";
        try
        {
            using (var outFs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                outFs.SetLength(ftyp.Length + moov.Length + mdatSize);
                outFs.Write(ftyp);
                outFs.Write(moov);
                outFs.Write(MdatHeaderBytes(mdatSize));
                foreach (var d in planned) outFs.Write(d);
            }
            File.Move(part, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
        finally { planned.Clear(); }
    }

    public void Abort()
    {
        finished = true;
        planned.Clear();
        if (fs == null) return;
        try { fs.Dispose(); } catch { }
        try { File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        if (!finished) Abort();
    }


    static byte[] BuildFtyp()
    {
        var b = new Box();
        b.Begin("ftyp").Fourcc("isom").U32(0x200).Fourcc("isom").Fourcc("iso2").Fourcc("avc1").Fourcc("mp41").End();
        return b.ToArray();
    }

    static uint MacTime() => (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2082844800L);

    byte[] BuildMoov(long shift)
    {
        uint now = MacTime();
        var b = new Box();
        long movieDuration = tracks.Count == 0 ? 0 : tracks.Max(t => t.Presentation * 1000 / t.Timescale);
        b.Begin("moov");
        b.Full("mvhd", 0, 0).U32(now).U32(now).U32(1000).U32((uint)movieDuration)
            .U32(0x00010000).U16(0x0100).Zeros(10).Matrix().Zeros(24).U32((uint)(tracks.Count + 1)).End();
        foreach (var t in tracks) BuildTrak(b, t, now, shift);
        b.End();
        return b.ToArray();
    }

    void BuildTrak(Box b, Track t, uint now, long shift)
    {
        long dur = t.Duration;
        long movieDur = t.Presentation * 1000 / t.Timescale;
        int w = t.Video?.Width ?? 0, h = t.Video?.Height ?? 0;
        b.Begin("trak");
        b.Full("tkhd", 0, 3).U32(now).U32(now).U32((uint)t.Id).U32(0).U32((uint)movieDur).Zeros(8)
            .U16(0).U16(0).U16(t.IsVideo ? (ushort)0 : (ushort)0x0100).U16(0).Matrix()
            .U32((uint)w << 16).U32((uint)h << 16).End();
        if (t.EditMediaTime > 0 || t.DelaySamples > 0)
        {
            long delayMovie = t.DelaySamples * 1000 / t.Timescale;
            b.Begin("edts").Full("elst", 0, 0).U32(t.DelaySamples > 0 ? 2u : 1u);
            if (t.DelaySamples > 0) b.U32((uint)delayMovie).U32(0xFFFFFFFF).U16(1).U16(0); // empty edit: nothing yet
            b.U32((uint)Math.Max(movieDur - delayMovie, 0)).U32((uint)t.EditMediaTime).U16(1).U16(0);
            b.End().End();
        }
        b.Begin("mdia");
        if (dur > uint.MaxValue)
            b.Full("mdhd", 1, 0).U64(now).U64(now).U32((uint)t.Timescale).U64((ulong)dur).U16(0x55C4).U16(0).End();
        else
            b.Full("mdhd", 0, 0).U32(now).U32(now).U32((uint)t.Timescale).U32((uint)dur).U16(0x55C4).U16(0).End();
        b.Full("hdlr", 0, 0).U32(0).Fourcc(t.IsVideo ? "vide" : "soun").Zeros(12)
            .Bytes(Encoding.ASCII.GetBytes(t.IsVideo ? "VideoHandler\0" : "SoundHandler\0")).End();
        b.Begin("minf");
        if (t.IsVideo) b.Full("vmhd", 0, 1).U16(0).U16(0).U16(0).U16(0).End();
        else b.Full("smhd", 0, 0).U16(0).U16(0).End();
        b.Begin("dinf").Full("dref", 0, 0).U32(1).Full("url ", 0, 1).End().End().End();
        b.Begin("stbl");
        b.Full("stsd", 0, 0).U32(1);
        if (t.IsVideo) VideoEntry(b, t.Video!); else AudioEntry(b, t);
        b.End();

        var runs = new List<(uint count, uint delta)>();
        for (int i = 0; i < t.Dts.Count; i++)
        {
            long d = i + 1 < t.Dts.Count ? t.Dts[i + 1] - t.Dts[i] : t.LastDuration;
            uint delta = (uint)Math.Clamp(d, 1, uint.MaxValue);
            if (runs.Count > 0 && runs[^1].delta == delta) runs[^1] = (runs[^1].count + 1, delta);
            else runs.Add((1, delta));
        }
        b.Full("stts", 0, 0).U32((uint)runs.Count);
        foreach (var (count, delta) in runs) b.U32(count).U32(delta);
        b.End();

        if (t.IsVideo && t.Sync.Count < t.Dts.Count)
        {
            b.Full("stss", 0, 0).U32((uint)t.Sync.Count);
            foreach (var s in t.Sync) b.U32((uint)s);
            b.End();
        }

        b.Full("stsc", 0, 0).U32(1).U32(1).U32(1).U32(1).End();
        b.Full("stsz", 0, 0).U32(0).U32((uint)t.Sizes.Count);
        foreach (var s in t.Sizes) b.U32((uint)s);
        b.End();

        bool large = t.Offsets.Count > 0 && t.Offsets[^1] + shift > uint.MaxValue;
        b.Full(large ? "co64" : "stco", 0, 0).U32((uint)t.Offsets.Count);
        foreach (var o in t.Offsets) { if (large) b.U64((ulong)(o + shift)); else b.U32((uint)(o + shift)); }
        b.End();

        b.End();
        b.End();
        b.End();
        b.End();
    }

    static void VideoEntry(Box b, CodecConfig c)
    {
        bool hevc = c.Codec == VideoCodec.Hevc;
        b.Begin(hevc ? "hvc1" : "avc1").Zeros(6).U16(1)
            .U16(0).U16(0).Zeros(12)
            .U16((ushort)c.Width).U16((ushort)c.Height)
            .U32(0x00480000).U32(0x00480000).U32(0).U16(1);
        var name = new byte[32];
        var label = Encoding.ASCII.GetBytes(hevc ? "HEVC Coding" : "AVC Coding");
        name[0] = (byte)label.Length;
        label.CopyTo(name, 1);
        b.Bytes(name).U16(0x0018).U16(0xFFFF);
        if (hevc) Hvcc(b, c); else Avcc(b, c);
        b.Begin("colr").Fourcc("nclx").U16(1).U16(1).U16(1).U8(0).End();
        b.Begin("pasp").U32(1).U32(1).End();
        b.End();
    }

    static void Avcc(Box b, CodecConfig c)
    {
        var sps = c.Sps!; var pps = c.Pps!;
        b.Begin("avcC").U8(1).U8(sps[1]).U8(sps[2]).U8(sps[3]).U8(0xFF)
            .U8(0xE1).U16((ushort)sps.Length).Bytes(sps)
            .U8(1).U16((ushort)pps.Length).Bytes(pps);
        if (sps[1] is 100 or 110 or 122 or 144)
            b.U8(0xFC | 1).U8(0xF8).U8(0xF8).U8(0);
        b.End();
    }

    static void Hvcc(Box b, CodecConfig c)
    {
        var r = Nal.Unescape(c.Sps!);
        int maxSubLayers = ((r[2] >> 1) & 7) + 1;
        int nested = r[2] & 1;
        b.Begin("hvcC").U8(1)
            .U8(r[3])                         // general_profile_space / tier / profile_idc
            .Bytes(r.AsSpan(4, 4))            // general_profile_compatibility_flags
            .Bytes(r.AsSpan(8, 6))            // general_constraint_indicator_flags
            .U8(r[14])                        // general_level_idc
            .U16(0xF000).U8(0xFC).U8(0xFC | 1).U8(0xF8).U8(0xF8)
            .U16(0)
            .U8((byte)((maxSubLayers << 3) | (nested << 2) | 3))
            .U8(3);
        foreach (var (type, nal) in new[] { (32, c.Vps!), (33, c.Sps!), (34, c.Pps!) })
            b.U8((byte)(0x80 | type)).U16(1).U16((ushort)nal.Length).Bytes(nal);
        b.End();
    }

    static void AudioEntry(Box b, Track t)
    {
        var asc = t.AudioConfig!;
        b.Begin("mp4a").Zeros(6).U16(1).Zeros(8)
            .U16((ushort)t.Channels).U16(16).U16(0).U16(0).U32((uint)t.SampleRate << 16);
        int decSpecific = 2 + asc.Length;
        int decConfig = 13 + decSpecific;
        int esLen = 3 + 2 + decConfig + 3;
        b.Full("esds", 0, 0)
            .U8(0x03).U8((byte)esLen).U16((ushort)t.Id).U8(0)
            .U8(0x04).U8((byte)decConfig).U8(0x40).U8(0x15).U24(0).U32((uint)t.Bitrate).U32((uint)t.Bitrate)
            .U8(0x05).U8((byte)asc.Length).Bytes(asc)
            .U8(0x06).U8(1).U8(0x02)
            .End();
        b.End();
    }

    sealed class Box
    {
        readonly MemoryStream ms = new();
        readonly Stack<long> starts = new();

        public Box Begin(string type) { starts.Push(ms.Position); U32(0); return Fourcc(type); }
        public Box Full(string type, byte version, uint flags) { Begin(type); return U32(((uint)version << 24) | flags); }

        public Box End()
        {
            long s = starts.Pop(), e = ms.Position;
            ms.Position = s;
            U32((uint)(e - s));
            ms.Position = e;
            return this;
        }

        public Box U8(byte v) { ms.WriteByte(v); return this; }
        public Box U16(ushort v) { Span<byte> x = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(x, v); ms.Write(x); return this; }
        public Box U24(uint v) { U8((byte)(v >> 16)); U8((byte)(v >> 8)); return U8((byte)v); }
        public Box U32(uint v) { Span<byte> x = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(x, v); ms.Write(x); return this; }
        public Box U64(ulong v) { Span<byte> x = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(x, v); ms.Write(x); return this; }
        public Box Bytes(ReadOnlySpan<byte> v) { ms.Write(v); return this; }
        public Box Zeros(int n) { for (int i = 0; i < n; i++) ms.WriteByte(0); return this; }
        public Box Fourcc(string s) { ms.Write(Encoding.ASCII.GetBytes(s)); return this; }
        public Box Matrix() => U32(0x00010000).U32(0).U32(0).U32(0).U32(0x00010000).U32(0).U32(0).U32(0).U32(0x40000000);
        public byte[] ToArray() => ms.ToArray();
    }
}
