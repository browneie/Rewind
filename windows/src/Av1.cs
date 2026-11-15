namespace Rewind;

/// AV1 as MP4 wants it (the AV1-ISOBMFF binding): each sample is one temporal unit of OBUs, every OBU carries its
/// size, temporal delimiters and padding are left out, and the sequence header also goes in the av1C box.
static class Av1
{
    const int SequenceHeader = 1, TemporalDelimiter = 2, FrameHeader = 3, Frame = 6, Padding = 15;

    /// One encoder output sample to an MP4 sample; remembers the sequence header in `cfg`.
    public static (byte[] data, bool key) ToMp4(byte[] raw, CodecConfig cfg)
    {
        var ms = new MemoryStream(raw.Length + 8);
        bool key = false, sawFrame = false;
        int p = 0;
        // An encoder may hand over an av1C record (MF_MT_MPEG_SEQUENCE_HEADER); its OBUs start after 4 bytes.
        if (raw.Length > 4 && raw[0] == 0x81) p = 4;
        while (p < raw.Length)
        {
            byte h = raw[p];
            int type = (h >> 3) & 0xF;
            bool ext = (h & 4) != 0, sized = (h & 2) != 0;
            int headerLen = ext ? 2 : 1;
            if (p + headerLen > raw.Length) break;
            int q = p + headerLen;
            long size = raw.Length - q;
            if (sized && !Leb128(raw, ref q, out size)) break;
            if (size < 0 || q + size > raw.Length) break;
            var payload = raw.AsSpan(q, (int)size);
            int next = q + (int)size;

            if (type is TemporalDelimiter or Padding) { p = next; continue; }

            var obu = new MemoryStream(headerLen + 8 + payload.Length);
            obu.WriteByte((byte)(h | 2));
            if (ext) obu.WriteByte(raw[p + 1]);
            WriteLeb128(obu, payload.Length);
            obu.Write(payload);
            var bytes = obu.ToArray();

            if (type == SequenceHeader) cfg.SequenceHeader ??= bytes;
            if ((type is FrameHeader or Frame) && !sawFrame && payload.Length > 0)
            {
                sawFrame = true;
                // show_existing_frame = 0, then frame_type = KEY_FRAME (0).
                key = (payload[0] & 0x80) == 0 && (payload[0] & 0x60) == 0;
            }
            ms.Write(bytes);
            p = next;
        }
        return (ms.ToArray(), key);
    }

    /// The AV1CodecConfigurationRecord (av1C box payload) for a sequence header OBU.
    public static byte[] ConfigRecord(byte[] sequenceHeaderObu)
    {
        int q = (sequenceHeaderObu[0] & 4) != 0 ? 2 : 1;
        if ((sequenceHeaderObu[0] & 2) != 0) Leb128(sequenceHeaderObu, ref q, out _);
        var s = Parse(sequenceHeaderObu.AsSpan(q).ToArray());
        var record = new byte[4 + sequenceHeaderObu.Length];
        record[0] = 0x81; // marker, version 1
        record[1] = (byte)((s.Profile << 5) | s.Level);
        record[2] = (byte)((s.Tier << 7) | (s.HighBitDepth << 6) | (s.TwelveBit << 5) | (s.Mono << 4) |
                           (s.SubX << 3) | (s.SubY << 2) | s.ChromaPosition);
        record[3] = 0; // no initial_presentation_delay
        sequenceHeaderObu.CopyTo(record, 4);
        return record;
    }

    /// The configOBUs of an av1C box payload (what ConfigRecord was given).
    public static byte[]? FromConfigRecord(ReadOnlySpan<byte> av1C) =>
        av1C.Length > 4 && av1C[0] == 0x81 ? av1C[4..].ToArray() : null;

    record struct Header(int Profile, int Level, int Tier, int HighBitDepth, int TwelveBit, int Mono, int SubX, int SubY, int ChromaPosition);

    /// sequence_header_obu() up to color_config(), AV1 spec 5.5.
    static Header Parse(byte[] d)
    {
        var r = new Bits(d);
        int profile = r.F(3);
        r.F(1); // still_picture
        bool reduced = r.F(1) == 1;
        int level, tier = 0;
        if (reduced) level = r.F(5);
        else
        {
            bool decoderModel = false;
            int bufferDelayLength = 0;
            if (r.F(1) == 1) // timing_info_present_flag
            {
                r.F(32); r.F(32);
                if (r.F(1) == 1) r.Uvlc(); // equal_picture_interval → num_ticks_per_picture_minus_1
                decoderModel = r.F(1) == 1;
                if (decoderModel)
                {
                    bufferDelayLength = r.F(5) + 1;
                    r.F(32); r.F(5); r.F(5);
                }
            }
            bool initialDisplayDelay = r.F(1) == 1;
            int points = r.F(5) + 1;
            level = 0;
            for (int i = 0; i < points; i++)
            {
                r.F(12); // operating_point_idc
                int lvl = r.F(5);
                int t = lvl > 7 ? r.F(1) : 0;
                if (i == 0) { level = lvl; tier = t; }
                if (decoderModel && r.F(1) == 1) { r.F(bufferDelayLength); r.F(bufferDelayLength); r.F(1); }
                if (initialDisplayDelay && r.F(1) == 1) r.F(4);
            }
        }
        int widthBits = r.F(4) + 1, heightBits = r.F(4) + 1;
        r.F(widthBits); r.F(heightBits);
        if (!reduced && r.F(1) == 1) { r.F(4); r.F(3); } // frame_id_numbers_present_flag
        r.F(1); r.F(1); r.F(1);                         // 128x128 superblocks, filter intra, intra edge filter
        if (!reduced)
        {
            r.F(1); r.F(1); r.F(1); r.F(1);             // interintra, masked compound, warped motion, dual filter
            bool orderHint = r.F(1) == 1;
            if (orderHint) { r.F(1); r.F(1); }          // jnt_comp, ref_frame_mvs
            int forceScreenContent = r.F(1) == 1 ? 2 : r.F(1);
            if (forceScreenContent > 0 && r.F(1) == 0) r.F(1); // seq_choose_integer_mv, else seq_force_integer_mv
            if (orderHint) r.F(3);
        }
        r.F(1); r.F(1); r.F(1);                         // superres, cdef, restoration

        // color_config()
        int high = r.F(1), twelve = 0;
        if (profile == 2 && high == 1) twelve = r.F(1);
        int mono = profile == 1 ? 0 : r.F(1);
        int cp = 2, tc = 2, mc = 2;
        if (r.F(1) == 1) { cp = r.F(8); tc = r.F(8); mc = r.F(8); }
        int subX, subY, position = 0;
        if (mono == 1) { subX = subY = 1; }
        else if (cp == 1 && tc == 13 && mc == 0) { subX = subY = 0; }
        else
        {
            r.F(1); // color_range
            if (profile == 0) { subX = subY = 1; }
            else if (profile == 1) { subX = subY = 0; }
            else if (twelve == 1) { subX = r.F(1); subY = subX == 1 ? r.F(1) : 0; }
            else { subX = 1; subY = 0; }
            if (subX == 1 && subY == 1) position = r.F(2);
        }
        return new Header(profile, level, tier, high, twelve, mono, subX, subY, position);
    }

    static bool Leb128(byte[] d, ref int p, out long value)
    {
        value = 0;
        for (int i = 0; i < 8; i++)
        {
            if (p >= d.Length) return false;
            byte b = d[p++];
            value |= (long)(b & 0x7F) << (i * 7);
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }

    static void WriteLeb128(Stream s, int value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            s.WriteByte(value != 0 ? (byte)(b | 0x80) : b);
        } while (value != 0);
    }

    sealed class Bits(byte[] d)
    {
        int pos;

        public int F(int n)
        {
            long v = 0;
            for (int i = 0; i < n; i++)
            {
                int bit = pos / 8 < d.Length ? (d[pos / 8] >> (7 - pos % 8)) & 1 : 0;
                v = (v << 1) | (uint)bit;
                pos++;
            }
            return (int)v;
        }

        public void Uvlc()
        {
            int zeros = 0;
            while (zeros < 32 && F(1) == 0) zeros++;
            if (zeros < 32) F(zeros);
        }
    }
}
