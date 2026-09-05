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
