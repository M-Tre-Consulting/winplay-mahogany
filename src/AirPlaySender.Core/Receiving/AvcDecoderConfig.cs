namespace AirPlaySender.Core.Receiving;

/// <summary>
/// Parses the unencrypted "SPS+PPS" mirroring packet (payload type
/// <c>0x01</c>) — confirmed from a real capture to be a genuine
/// AVCDecoderConfigurationRecord (ISO/IEC 14496-15 §5.2.4.1: version,
/// profile/level, length-size, then length-prefixed SPS(s) and PPS(s)),
/// not a bare Annex-B SPS+PPS pair. Only the first SPS and first PPS are
/// extracted — real devices only ever send one of each in this packet.
/// </summary>
public static class AvcDecoderConfig
{
    /// <returns>The raw SPS and PPS NAL units, each INCLUDING its 1-byte NAL header, or null if the record doesn't look valid.</returns>
    public static (byte[] Sps, byte[] Pps)? TryParse(ReadOnlySpan<byte> record)
    {
        if (record.Length < 7 || record[0] != 1) return null; // configurationVersion must be 1

        int pos = 5;
        int numSps = record[pos] & 0x1F;
        pos++;
        byte[]? sps = null;
        for (int i = 0; i < numSps; i++)
        {
            if (pos + 2 > record.Length) return null;
            int len = (record[pos] << 8) | record[pos + 1];
            pos += 2;
            if (pos + len > record.Length) return null;
            if (i == 0) sps = record.Slice(pos, len).ToArray();
            pos += len;
        }
        if (sps is null) return null;

        if (pos >= record.Length) return null;
        int numPps = record[pos];
        pos++;
        byte[]? pps = null;
        for (int i = 0; i < numPps; i++)
        {
            if (pos + 2 > record.Length) return null;
            int len = (record[pos] << 8) | record[pos + 1];
            pos += 2;
            if (pos + len > record.Length) return null;
            if (i == 0) pps = record.Slice(pos, len).ToArray();
            pos += len;
        }
        if (pps is null) return null;

        return (sps, pps);
    }

    /// <summary>
    /// Rebuilds a minimal, valid AVCDecoderConfigurationRecord from a parsed SPS/PPS pair —
    /// the exact inverse of <see cref="TryParse"/>. For handing to a consumer that wants the
    /// record as codec private/"format user" data (e.g. WinRT's
    /// <c>VideoEncodingProperties.SetFormatUserData</c>) instead of the Annex-B in-band-
    /// parameter-set convention.
    /// </summary>
    public static byte[] BuildRecord(ReadOnlySpan<byte> sps, ReadOnlySpan<byte> pps)
    {
        // sps[1..3] are the SPS's own profile_idc / constraint flags / level_idc bytes —
        // the AVCDecoderConfigurationRecord's AVCProfileIndication/profile_compatibility/
        // AVCLevelIndication fields are literally copies of these (ISO/IEC 14496-15).
        var record = new List<byte>(11 + sps.Length + pps.Length)
        {
            1, // configurationVersion
            sps.Length > 1 ? sps[1] : (byte)0, // AVCProfileIndication
            sps.Length > 2 ? sps[2] : (byte)0, // profile_compatibility
            sps.Length > 3 ? sps[3] : (byte)0, // AVCLevelIndication
            0xFF, // 6 reserved bits (111111) + lengthSizeMinusOne=3 -> 4-byte length prefixes
            0xE1, // 3 reserved bits (111) + numOfSequenceParameterSets=1
        };
        record.Add((byte)(sps.Length >> 8));
        record.Add((byte)sps.Length);
        record.AddRange(sps.ToArray());
        record.Add(1); // numOfPictureParameterSets
        record.Add((byte)(pps.Length >> 8));
        record.Add((byte)pps.Length);
        record.AddRange(pps.ToArray());
        return [.. record];
    }

    /// <summary>
    /// Splits a decrypted mirroring VCL payload into its individual AVCC
    /// (4-byte big-endian length prefix, per-NAL) units — usually just one
    /// per packet in practice, but this loops in case a real device ever
    /// packs more than one NAL into a single mirroring data packet.
    /// </summary>
    public static List<byte[]> SplitAvccNalUnits(ReadOnlySpan<byte> data)
    {
        var result = new List<byte[]>();
        int pos = 0;
        while (pos + 4 <= data.Length)
        {
            int len = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            pos += 4;
            if (len < 0 || len > data.Length - pos) break; // malformed tail — keep whatever parsed cleanly so far
            result.Add(data.Slice(pos, len).ToArray());
            pos += len;
        }
        return result;
    }

    /// <summary>
    /// Rewrites one encoder-produced Annex-B access unit (arbitrary mix of
    /// (4-byte big-endian length prefix per NAL) — the exact inverse of
    /// <see cref="RewriteAvccToAnnexBInPlace"/>, needed on the sending side
    /// not Annex-B (confirmed on the *receiving* side against real hardware —
    /// see that method's own doc comment). Unlike the AVCC→Annex-B rewrite,
    /// this can't be done in place: a start code and a length prefix are the
    /// same width, but SPS/PPS NALs commonly carry <c>emulation_prevention</c>
    /// bytes a length-prefixed framing doesn't need to reinterpret — copying
    /// into a fresh buffer keeps this simple and correct rather than clever.
    /// </summary>
    public static byte[] AnnexBToAvcc(ReadOnlySpan<byte> annexB)
    {
        var nals = new List<(int Start, int Length)>();
        int pos = 0;
        while (pos < annexB.Length)
        {
            int scLen = StartCodeLengthAt(annexB, pos);
            if (scLen == 0) { pos++; continue; } // not a start code here — keep scanning
            int nalStart = pos + scLen;
            int next = nalStart;
            while (next < annexB.Length && StartCodeLengthAt(annexB, next) == 0) next++;
            if (next > nalStart) nals.Add((nalStart, next - nalStart));
            pos = next;
        }

        int total = nals.Sum(n => 4 + n.Length);
        var outp = new byte[total];
        int o = 0;
        foreach ((int start, int length) in nals)
        {
            outp[o] = (byte)(length >> 24);
            outp[o + 1] = (byte)(length >> 16);
            outp[o + 2] = (byte)(length >> 8);
            outp[o + 3] = (byte)length;
            annexB.Slice(start, length).CopyTo(outp.AsSpan(o + 4));
            o += 4 + length;
        }
        return outp;
    }

    /// <summary>0 if no start code begins at <paramref name="pos"/>, else 3 or 4 (its length).</summary>
    private static int StartCodeLengthAt(ReadOnlySpan<byte> data, int pos)
    {
        if (pos + 3 <= data.Length && data[pos] == 0 && data[pos + 1] == 0 && data[pos + 2] == 1) return 3;
        if (pos + 4 <= data.Length && data[pos] == 0 && data[pos + 1] == 0 && data[pos + 2] == 0 && data[pos + 3] == 1) return 4;
        return 0;
    }

    /// <summary>
    /// Rewrites a decrypted mirroring VCL payload from AVCC (4-byte big-endian length
    /// prefix per NAL) to Annex-B (<c>00 00 00 01</c> start code) <b>in place</b> — the
    /// two framings are the same 4 bytes wide, so the whole payload becomes a valid
    /// Annex-B access unit with no copy at all (the hot path is ~60 P-frames/second).
    /// Reports what's inside so the caller can decide whether to prepend SPS/PPS or drop.
    /// Returns false only if nothing parsed as a NAL.
    /// </summary>
    public static bool RewriteAvccToAnnexBInPlace(Span<byte> data, out bool hasKeyFrame, out bool hasVcl, out bool startsWithSps)
    {
        hasKeyFrame = false;
        hasVcl = false;
        startsWithSps = false;
        int pos = 0;
        bool first = true, any = false;
        while (pos + 4 <= data.Length)
        {
            int len = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            if (len <= 0 || len > data.Length - pos - 4) break; // trailing garbage — keep what parsed cleanly
            data[pos] = 0; data[pos + 1] = 0; data[pos + 2] = 0; data[pos + 3] = 1;

            int nalType = data[pos + 4] & 0x1F;
            if (first) { startsWithSps = nalType == 7; first = false; }
            if (nalType == 5) hasKeyFrame = true;
            if (nalType is >= 1 and <= 5) hasVcl = true;
            any = true;
            pos += 4 + len;
        }
        return any;
    }
}
