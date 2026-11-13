using System.Buffers.Binary;
using System.Text;

namespace Rewind;

/// Reads the structure of an MP4 that Rewind wrote: tracks, sample tables, edit lists and codec setup.
sealed class Mp4Reader
{
    public sealed class Track
    {
        public string Handler = "";
        public bool IsVideo => Handler == "vide";
        public bool IsAudio => Handler == "soun";
        public int Timescale;
        public int Width, Height;
        public long EditMediaTime;
        public double DelaySeconds;
        public VideoCodec Codec;
        public byte[]? Vps, Sps, Pps, AudioConfig;
        public int SampleRate, Channels;
        public readonly List<long> Dts = new(), Offsets = new();
        public readonly List<int> Sizes = new();
        public HashSet<int>? Sync; // null: every sample is a sync sample
        public long LastDelta;

        public int Count => Dts.Count;
        public bool IsSync(int i) => Sync == null || Sync.Contains(i);
        /// When sample `i` is shown, in seconds from the start of the movie (edit lists applied).
        public double Presented(int i) => (Dts[i] - EditMediaTime) / (double)Timescale + DelaySeconds;
        public double MediaDuration => Count == 0 ? 0 : (Dts[^1] - Dts[0] + LastDelta) / (double)Timescale;
        public double PresentedDuration => Math.Max(MediaDuration - EditMediaTime / (double)Timescale, 0) + DelaySeconds;
    }

    public List<Track> Tracks { get; } = new();
    public int MovieTimescale { get; private set; } = 1000;
    public Track? Video => Tracks.FirstOrDefault(t => t.IsVideo);
    public IEnumerable<Track> AudioTracks => Tracks.Where(t => t.IsAudio);

    public static Mp4Reader Open(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var hdr = new byte[16];
        long pos = 0;
        while (pos + 8 <= fs.Length)
        {
            fs.Position = pos;
            fs.ReadExactly(hdr, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            string type = Encoding.ASCII.GetString(hdr, 4, 4);
            int header = 8;
            if (size == 1) { fs.ReadExactly(hdr, 8, 8); size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(8)); header = 16; }
            else if (size == 0) size = fs.Length - pos;
            if (size < header) break;
            if (type == "moov")
            {
                var moov = new byte[size - header];
                fs.ReadExactly(moov);
                var r = new Mp4Reader();
                r.ParseMoov(moov);
                return r;
            }
            pos += size;
        }
        throw new RewindException("This file has no movie header.");
    }

    static IEnumerable<(string type, int start, int end)> Boxes(byte[] b, int start, int end)
    {
        int p = start;
        while (p + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
            string type = Encoding.ASCII.GetString(b, p + 4, 4);
            int header = 8;
            if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(p + 8)); header = 16; }
            else if (size == 0) size = end - p;
            if (size < header || p + size > end) yield break;
            yield return (type, p + header, (int)(p + size));
            p += (int)size;
        }
    }

    static uint U32(byte[] b, int p) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
    static ushort U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p));
    static ulong U64(byte[] b, int p) => BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(p));

    void ParseMoov(byte[] b)
    {
        foreach (var (type, s, e) in Boxes(b, 0, b.Length))
        {
            if (type == "mvhd") MovieTimescale = (int)U32(b, s + (b[s] == 1 ? 20 : 12));
            else if (type == "trak") Tracks.Add(ParseTrak(b, s, e));
        }
        foreach (var t in Tracks.Where(t => t.DelaySeconds > 0)) t.DelaySeconds /= MovieTimescale;
    }

    Track ParseTrak(byte[] b, int start, int end)
    {
        var t = new Track();
        List<(long dur, int count)> stts = new();
        List<(int first, int perChunk)> stsc = new();
        List<long> chunks = new();
        foreach (var (type, s, e) in Boxes(b, start, end))
        {
            if (type == "tkhd") { t.Width = (int)(U32(b, e - 8) >> 16); t.Height = (int)(U32(b, e - 4) >> 16); }
            else if (type == "edts") ParseElst(b, s, e, t);
            else if (type == "mdia")
                foreach (var (mt, ms, me) in Boxes(b, s, e))
                {
                    if (mt == "mdhd") t.Timescale = (int)U32(b, ms + (b[ms] == 1 ? 20 : 12));
                    else if (mt == "hdlr") t.Handler = Encoding.ASCII.GetString(b, ms + 8, 4);
                    else if (mt == "minf")
                        foreach (var (nt, ns, ne) in Boxes(b, ms, me))
                            if (nt == "stbl")
                                foreach (var (st, ss, se) in Boxes(b, ns, ne))
                                    switch (st)
                                    {
                                        case "stsd": ParseStsd(b, ss + 8, se, t); break;
                                        case "stts":
                                            for (int i = 0, n = (int)U32(b, ss + 4); i < n; i++)
                                                stts.Add((U32(b, ss + 12 + i * 8), (int)U32(b, ss + 8 + i * 8)));
                                            break;
                                        case "stss":
                                            t.Sync = new HashSet<int>();
                                            for (int i = 0, n = (int)U32(b, ss + 4); i < n; i++) t.Sync.Add((int)U32(b, ss + 8 + i * 4) - 1);
                                            break;
                                        case "stsz":
                                        {
                                            uint fixedSize = U32(b, ss + 4); int n = (int)U32(b, ss + 8);
                                            for (int i = 0; i < n; i++) t.Sizes.Add(fixedSize != 0 ? (int)fixedSize : (int)U32(b, ss + 12 + i * 4));
                                            break;
                                        }
                                        case "stsc":
                                            for (int i = 0, n = (int)U32(b, ss + 4); i < n; i++)
                                                stsc.Add(((int)U32(b, ss + 8 + i * 12), (int)U32(b, ss + 12 + i * 12)));
                                            break;
                                        case "stco":
                                            for (int i = 0, n = (int)U32(b, ss + 4); i < n; i++) chunks.Add(U32(b, ss + 8 + i * 4));
                                            break;
                                        case "co64":
                                            for (int i = 0, n = (int)U32(b, ss + 4); i < n; i++) chunks.Add((long)U64(b, ss + 8 + i * 8));
                                            break;
                                    }
                }
        }
        long dts = 0;
        foreach (var (dur, count) in stts)
            for (int i = 0; i < count; i++) { t.Dts.Add(dts); dts += dur; t.LastDelta = dur; }
        // Sample offsets: chunks hold runs of samples laid end to end (stsc says how many per chunk).
        int sample = 0;
        for (int c = 0; c < chunks.Count && sample < t.Sizes.Count; c++)
        {
            int perChunk = stsc.LastOrDefault(x => x.first <= c + 1).perChunk;
            long off = chunks[c];
            for (int k = 0; k < perChunk && sample < t.Sizes.Count; k++, sample++) { t.Offsets.Add(off); off += t.Sizes[sample]; }
        }
        return t;
    }

    static void ParseElst(byte[] b, int start, int end, Track t)
    {
        foreach (var (type, s, _) in Boxes(b, start, end))
        {
            if (type != "elst") continue;
            bool v1 = b[s] == 1;
            int n = (int)U32(b, s + 4), p = s + 8;
            for (int i = 0; i < n; i++)
            {
                long dur = v1 ? (long)U64(b, p) : U32(b, p);
                long media = v1 ? (long)U64(b, p + 8) : (int)U32(b, p + 4);
                p += v1 ? 20 : 12;
                if (media == -1) t.DelaySeconds += dur; // empty edit; converted to seconds once mvhd is known
                else { t.EditMediaTime = media; break; }
            }
        }
    }

    static void ParseStsd(byte[] b, int start, int end, Track t)
    {
        foreach (var (type, s, e) in Boxes(b, start, end))
        {
            if (type is "avc1" or "avc3" or "hvc1" or "hev1")
            {
                t.Codec = type.StartsWith("avc") ? VideoCodec.H264 : VideoCodec.Hevc;
                foreach (var (ct, cs, ce) in Boxes(b, s + 78, e))
                {
                    if (ct == "avcC")
                    {
                        int p = cs + 5, nSps = b[p++] & 0x1F;
                        for (int i = 0; i < nSps; i++) { int len = U16(b, p); t.Sps ??= b[(p + 2)..(p + 2 + len)]; p += 2 + len; }
                        int nPps = b[p++];
                        for (int i = 0; i < nPps; i++) { int len = U16(b, p); t.Pps ??= b[(p + 2)..(p + 2 + len)]; p += 2 + len; }
                    }
                    else if (ct == "hvcC")
                    {
                        int p = cs + 22, arrays = b[p++];
                        for (int a = 0; a < arrays; a++)
                        {
                            int nalType = b[p] & 0x3F, n = U16(b, p + 1);
                            p += 3;
                            for (int i = 0; i < n; i++)
                            {
                                int len = U16(b, p);
                                var nal = b[(p + 2)..(p + 2 + len)];
                                if (nalType == 32) t.Vps ??= nal; else if (nalType == 33) t.Sps ??= nal; else if (nalType == 34) t.Pps ??= nal;
                                p += 2 + len;
                            }
                        }
                    }
                }
            }
            else if (type == "mp4a")
            {
                t.Channels = U16(b, s + 16);
                t.SampleRate = (int)(U32(b, s + 24) >> 16);
                foreach (var (ct, cs, ce) in Boxes(b, s + 28, e))
                    if (ct == "esds") t.AudioConfig = DecoderSpecificInfo(b, cs + 4, ce);
            }
        }
    }

    /// The AudioSpecificConfig inside an esds box (descriptor tag 5).
    static byte[]? DecoderSpecificInfo(byte[] b, int p, int end)
    {
        while (p < end)
        {
            int tag = b[p++], len = 0;
            for (int i = 0; i < 4 && p < end; i++) { int x = b[p++]; len = (len << 7) | (x & 0x7F); if ((x & 0x80) == 0) break; }
            if (tag == 5) return b[p..Math.Min(p + len, end)];
            if (tag == 3) { p += 3; continue; }   // ES_Descriptor: ES_ID and flags, then nested descriptors
            if (tag == 4) { p += 13; continue; }  // DecoderConfigDescriptor header, then DecoderSpecificInfo
            p += len;
        }
        return null;
    }
}
