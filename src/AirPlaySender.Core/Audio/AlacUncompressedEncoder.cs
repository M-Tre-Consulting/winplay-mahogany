namespace AirPlaySender.Core.Audio;

/// <summary>
/// Packs interleaved 16-bit stereo PCM into an "uncompressed ALAC" frame —
/// the escape mode of Apple's ALAC bitstream where samples are stored raw
/// instead of prediction/Rice-coded. A modern AirPlay-2 receiver's realtime
/// path (RTP payload type 0x60) is HARDCODED to expect ALAC framing
/// (shairport-sync's rtsp.c: type 96 → ALAC regardless of the negotiated
/// codec), so every realtime audio packet must be wrapped this way even
/// though no actual compression happens — this is what makes a from-scratch
/// ALAC encoder unnecessary for Phase 1.
///
/// Per-frame element layout (MSB-first, byte-aligned only at the end):
/// 3-bit channel tag=1 (stereo CPE) · 4-bit unused=0 · 12-bit unknown=0 ·
/// 1-bit hasSize=0 (frame length comes from the cookie's default, 352) ·
/// 2-bit wastedBytes=0 · 1-bit isNotCompressed=1 · then frameCount×{L,R}
/// 16-bit samples · 3-bit END tag=7 · zero-pad to a byte boundary.
/// </summary>
public static class AlacUncompressedEncoder
{
    /// <param name="interleavedStereoSamples">L,R,L,R,… — at least <paramref name="frameCount"/>*2 samples.</param>
    public static byte[] EncodeFrame(ReadOnlySpan<short> interleavedStereoSamples, int frameCount)
    {
        var w = new MsbBitWriter();
        w.Write(1, 3);  // stereo channel-pair element
        w.Write(0, 4);  // unused
        w.Write(0, 12); // unknown
        w.Write(0, 1);  // hasSize = 0
        w.Write(0, 2);  // wastedBytes = 0
        w.Write(1, 1);  // isNotCompressed = 1 (uncompressed escape)
        int sampleCount = frameCount * 2;
        for (int i = 0; i < sampleCount; i++)
            w.Write((ushort)interleavedStereoSamples[i], 16);
        w.Write(7, 3); // END element tag
        return w.ToArray();
    }
}
