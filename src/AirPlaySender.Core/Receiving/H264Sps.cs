namespace AirPlaySender.Core.Receiving;

/// <summary>
/// Just enough of an H.264 SPS (Sequence Parameter Set) parser to recover
/// the real picture width/height — needed so the mirroring render window
/// can open at the phone's actual video resolution instead of a guess.
/// Handles the common case a phone's real-time hardware H.264 encoder
/// actually produces (confirmed from a real capture tonight: profile_idc
/// 0x64 = 100 = High Profile, no custom scaling matrices) — the full H.264
/// spec's more exotic corners (custom scaling lists, separate colour
/// planes, interlaced/field coding) are deliberately out of scope; if one
/// of those turns up, <see cref="TryParseDimensions"/> returns false
/// rather than silently computing a wrong size.
/// </summary>
public static class H264Sps
{
    /// <param name="sps">The raw SPS NAL, INCLUDING its 1-byte NAL header (type 7).</param>
    public static bool TryParseDimensions(ReadOnlySpan<byte> sps, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (sps.Length < 2 || (sps[0] & 0x1F) != 7) return false; // not a SPS NAL

        byte[] rbsp = RemoveEmulationPrevention(sps[1..]); // drop the NAL header byte, unescape the rest
        var r = new BitReader(rbsp);

        try
        {
            int profileIdc = r.U(8);
            r.U(8); // constraint_set flags + reserved
            r.U(8); // level_idc
            r.Ue(); // seq_parameter_set_id

            int chromaFormatIdc = 1; // default 4:2:0 when the extended fields below aren't present
            if (profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                chromaFormatIdc = r.Ue();
                if (chromaFormatIdc == 3) r.U(1); // separate_colour_plane_flag
                r.Ue(); // bit_depth_luma_minus8
                r.Ue(); // bit_depth_chroma_minus8
                r.U(1); // qpprime_y_zero_transform_bypass_flag
                if (r.U(1) == 1) return false; // seq_scaling_matrix_present_flag — out of scope, see class doc comment
            }

            r.Ue(); // log2_max_frame_num_minus4
            int picOrderCntType = r.Ue();
            if (picOrderCntType == 0)
            {
                r.Ue(); // log2_max_pic_order_cnt_lsb_minus4
            }
            else if (picOrderCntType == 1)
            {
                r.U(1); // delta_pic_order_always_zero_flag
                r.Se(); // offset_for_non_ref_pic
                r.Se(); // offset_for_top_to_bottom_field
                int numRefFramesInCycle = r.Ue();
                for (int i = 0; i < numRefFramesInCycle; i++) r.Se(); // offset_for_ref_frame[i]
            }

            r.Ue(); // max_num_ref_frames
            r.U(1); // gaps_in_frame_num_value_allowed_flag
            int picWidthInMbsMinus1 = r.Ue();
            int picHeightInMapUnitsMinus1 = r.Ue();
            int frameMbsOnlyFlag = r.U(1);
            if (frameMbsOnlyFlag == 0) r.U(1); // mb_adaptive_frame_field_flag
            r.U(1); // direct_8x8_inference_flag

            int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;
            if (r.U(1) == 1) // frame_cropping_flag
            {
                cropLeft = r.Ue();
                cropRight = r.Ue();
                cropTop = r.Ue();
                cropBottom = r.Ue();
            }

            int subWidthC = chromaFormatIdc is 1 or 2 ? 2 : 1; // 4:2:0 or 4:2:2
            int subHeightC = chromaFormatIdc == 1 ? 2 : 1;     // 4:2:0 only halves vertically

            width = (picWidthInMbsMinus1 + 1) * 16 - (cropLeft + cropRight) * subWidthC;
            int frameHeightInMbs = (2 - frameMbsOnlyFlag) * (picHeightInMapUnitsMinus1 + 1);
            height = frameHeightInMbs * 16 - (cropTop + cropBottom) * subHeightC * (2 - frameMbsOnlyFlag);

            return width is > 0 and < 16384 && height is > 0 and < 16384;
        }
        catch (IndexOutOfRangeException)
        {
            return false; // ran off the end of the RBSP — malformed or a field shape we didn't expect
        }
    }

    /// <summary>Strips H.264's "emulation prevention" byte: any 0x03 immediately after a 00 00 sequence (within the NAL payload) is a stuffing byte, not real data.</summary>
    private static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> nalPayload)
    {
        var outp = new List<byte>(nalPayload.Length);
        int zeroRun = 0;
        foreach (byte b in nalPayload)
        {
            if (zeroRun >= 2 && b == 0x03)
            {
                zeroRun = 0;
                continue; // drop the emulation-prevention byte itself
            }
            outp.Add(b);
            zeroRun = b == 0x00 ? zeroRun + 1 : 0;
        }
        return [.. outp];
    }

    /// <summary>MSB-first bit reader plus H.264's Exp-Golomb codes (ue(v)/se(v)).</summary>
    private sealed class BitReader(byte[] data)
    {
        private int _bitPos;

        public int U(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++)
            {
                int byteIdx = _bitPos >> 3;
                int bitIdx = 7 - (_bitPos & 7);
                int bit = (data[byteIdx] >> bitIdx) & 1;
                v = (v << 1) | bit;
                _bitPos++;
            }
            return v;
        }

        /// <summary>Unsigned Exp-Golomb: count leading zero bits, then read that many + 1 bits, minus 1.</summary>
        public int Ue()
        {
            int leadingZeros = 0;
            while (U(1) == 0) leadingZeros++;
            if (leadingZeros == 0) return 0;
            int suffix = U(leadingZeros);
            return (1 << leadingZeros) - 1 + suffix;
        }

        /// <summary>Signed Exp-Golomb: map the unsigned code back to signed per the spec's zigzag.</summary>
        public int Se()
        {
            int code = Ue();
            int magnitude = (code + 1) / 2;
            return (code % 2 == 1) ? magnitude : -magnitude;
        }
    }
}
