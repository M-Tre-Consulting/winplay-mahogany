namespace AirPlaySender.Core.Receiving;

/// <summary>
/// The actual FairPlay content-key decryption used by the <c>SETUP</c> step
/// of AirPlay Mirroring — decrypts the 72-byte encrypted per-stream AES key
/// (<c>ekey</c>) a real iPhone sends, using the 164-byte "key message"
/// captured from the second round of <c>/fp-setup</c> (see
/// <see cref="FairPlaySetupSession"/>).
///
/// This is fundamentally different from the rest of this project's crypto,
/// and from <see cref="FairPlaySetupSession"/>'s simple byte replay: it is
/// Apple's actual FairPlay cipher, reimplemented from disassembly by
/// unrelated reverse engineers years ago (credited in UxPlay's source as
/// "OmgHax" / "hand_garble") — nobody, in any public project, has published
/// what this algorithm's *steps* actually mean; UxPlay's own source
/// describes it as recovered by black-box observation of inputs/outputs,
/// not by reading Apple's design. See the Phase 2 discussion in this
/// project's history for why this project proceeds with it anyway (private,
/// non-distributed build) despite that being a materially different kind of
/// reverse engineering than this project's own pairing/audio work.
///
/// Ported from UxPlay's <c>lib/playfair/{omg_hax.c,omg_hax.h,sap_hash.c,
/// hand_garble.c,modified_md5.c,playfair.c}</c> (GPLv3). Two porting
/// disciplines were used throughout, specifically because this code cannot
/// be understood or sanity-checked by reading it — only verified by
/// matching the reference byte-for-byte:
///  1. The ~480KB of opaque S-box/round-key tables were extracted
///     *mechanically* (scratchpad/convert_tables.py), not retyped, and
///     cross-checked with an independent SHA-256 of every table computed
///     both from the original C source and from the generated C#, in both
///     Python and C# — see FairPlayCipherTables.g.cs.
///  2. <see cref="Garble"/>'s ~200-line body (hand_garble.c) started from a
///     mechanical extraction (scratchpad/convert_garble.py) that copies
///     every expression character-for-character and only adds C#'s
///     required (byte) cast on a plain "=" assignment into a byte[]
///     element. On top of that base, C's implicit int/unsigned-int
///     promotion rules for small integer types don't exist in C# the same
///     way, so wherever the C# compiler rejected a line (mixing byte/uint/
///     int without an implicit conversion available), a cast was added by
///     hand around exactly the flagged sub-expression and nothing else —
///     each one checked against the original line to confirm it only
///     restates the C promotion that would have happened silently in C,
///     never changes operator precedence or which value flows where.
/// Everything else here (sap_hash's outer loop, the MD5 variant, the key
/// schedule / block cipher, the message decryption, and the entry point)
/// was ported by hand, keeping every expression textually identical to the
/// C source for the same reason — not because it was understood, but so it
/// could be diffed against the original if it's ever found not to match.
/// </summary>
// CS0675 ("bitwise-OR on a sign-extended operand") fires ~18 times below. It is
// EXPECTED here: this is a character-for-character port of C where small integer
// types sign/zero-extend to int per the C promotion rules, and the code is only
// valid if it stays textually identical to the reference (verified byte-for-byte
// against a real iPhone). Rewriting the flagged sub-expressions to silence the
// warning would be exactly the kind of "improvement" this port must not make.
#pragma warning disable CS0675
public static class FairPlayCipher
{
    /// <summary>
    /// <paramref name="keyMessage164"/>: the second-round <c>/fp-setup</c>
    /// request body, saved by <see cref="FairPlaySetupSession"/>.
    /// <paramref name="encryptedKey72"/>: the SETUP request's <c>ekey</c>.
    /// Returns the 16-byte raw per-stream AES key.
    /// </summary>
    public static byte[] Decrypt(byte[] keyMessage164, byte[] encryptedKey72)
    {
        byte[] chunk1 = encryptedKey72[16..32];
        byte[] chunk2 = encryptedKey72[56..72];

        var sapKey = new byte[16];
        GenerateSessionKey(FairPlayCipherTables.default_sap, keyMessage164, sapKey);

        var keySchedule = new uint[11][];
        GenerateKeySchedule(sapKey, keySchedule);

        var blockIn = new byte[16];
        ZXor(chunk2, blockIn, 1);
        Cycle(blockIn, keySchedule);

        var keyOut = new byte[16];
        for (int i = 0; i < 16; i++)
            keyOut[i] = (byte)(blockIn[i] ^ chunk1[i]);
        XXor(keyOut, keyOut, 1);
        ZXor(keyOut, keyOut, 1);
        return keyOut;
    }

    // ── sap_hash.c: generate_session_key's per-round "other hash" ──────

    private static void SapHash(byte[] blockIn, byte[] keyOut)
    {
        byte[] buffer0 = (byte[])FairPlayCipherTables.sap_hash_buffer0_init.Clone();
        var buffer1 = new byte[210];
        byte[] buffer2 = (byte[])FairPlayCipherTables.sap_hash_buffer2_init.Clone();
        var buffer3 = new byte[132];
        byte[] buffer4 = (byte[])FairPlayCipherTables.sap_hash_buffer4_init.Clone();
        int[] i0Index = [18, 22, 23, 0, 5, 19, 32, 31, 10, 21, 30];

        for (int i = 0; i < 210; i++)
        {
            int wordIdx = (i % 64) >> 2;
            int byteInWord = 3 - (i % 4);
            buffer1[i] = blockIn[wordIdx * 4 + byteInWord];
        }

        for (int i = 0; i < 840; i++)
        {
            byte x = buffer1[(uint)(i - 155) % 210];
            byte y = buffer1[(uint)(i - 57) % 210];
            byte z = buffer1[(uint)(i - 13) % 210];
            byte w = buffer1[(uint)i % 210];
            buffer1[i % 210] = (byte)((Rol8(y, 5) + (Rol8(z, 3) ^ w) - Rol8(x, 7)) & 0xff);
        }

        Garble(buffer0, buffer1, buffer2, buffer3, buffer4);

        for (int i = 0; i < 16; i++)
            keyOut[i] = 0xE1;

        for (int i = 0; i < 11; i++)
        {
            if (i == 3)
                keyOut[i] = 0x3d;
            else
                keyOut[i] = (byte)((keyOut[i] + buffer3[i0Index[i] * 4]) & 0xff);
        }

        for (int i = 0; i < 20; i++)
            keyOut[i % 16] ^= buffer0[i];
        for (int i = 0; i < 35; i++)
            keyOut[i % 16] ^= buffer2[i];
        for (int i = 0; i < 210; i++)
            keyOut[i % 16] ^= buffer1[i];

        for (int j = 0; j < 16; j++)
        {
            for (int i = 0; i < 16; i++)
            {
                byte x = keyOut[(uint)(i - 7) % 16];
                byte y = keyOut[i % 16];
                byte z = keyOut[(uint)(i - 37) % 16];
                byte w = keyOut[(uint)(i - 177) % 16];
                keyOut[i] = (byte)(Rol8(x, 1) ^ y ^ Rol8(z, 6) ^ Rol8(w, 5));
            }
        }
    }

    /// <summary>
    /// Ported mechanically (not retyped by hand) from UxPlay's
    /// lib/playfair/hand_garble.c "garble" function — see the class-level
    /// doc comment for what this is and why it looks like this. Expressions
    /// are copied character-for-character from the C source; the only
    /// changes are C#'s required (byte) casts on plain "=" assignments into
    /// a byte[] element (compound assignments like ^=/+= don't need one —
    /// C# applies the narrowing conversion automatically for those) and
    /// declaring the C source's "unsigned int" locals as uint, which keeps
    /// the same mod-2^32 wraparound arithmetic.
    /// </summary>
    private static void Garble(byte[] buffer0, byte[] buffer1, byte[] buffer2, byte[] buffer3, byte[] buffer4)
    {
        uint tmp, tmp2, tmp3;
        uint A, B, C, D, E, M, J, G, F, H, K, R, S, T, U, V, W, X, Y, Z;
        buffer2[12] = (byte)(0x14 + (((buffer1[64] & 92) | ((buffer1[99] / 3) & 35)) & buffer4[Rol8x(buffer4[(buffer1[206] % 21)], 4) % 21]));
        buffer1[4] = (byte)((buffer1[99] / 5) * (buffer1[99] / 5) * 2);
        buffer2[34] = (byte)(0xb8);
        buffer1[153] ^= (byte)(buffer2[buffer1[203] % 35] * buffer2[buffer1[203] % 35] * buffer1[190]);
        buffer0[3] -= (byte)(((buffer4[buffer1[205] % 21] >> 1) & 80) | 0xe6440);
        buffer0[16] = (byte)(0x93);
        buffer0[13] = (byte)(0x62);
        buffer1[33] -= (byte)(buffer4[buffer1[36] % 21] & 0xf6);
        tmp2 = buffer2[buffer1[67] % 35];
        buffer2[12] = (byte)(0x07);
        tmp = buffer0[buffer1[181] % 20];
        buffer1[2] -= unchecked((byte)3136); // 3136 mod 256 == 64; C truncates the same way on assignment, this is exact
        buffer0[19] = (byte)(buffer4[buffer1[58] % 21]);
        buffer3[0] = (byte)(92 - buffer2[buffer1[32] % 35]);
        buffer3[4] = (byte)(buffer2[buffer1[15] % 35] + 0x9e);
        buffer1[34] += (byte)(buffer4[((buffer2[buffer1[15] % 35] + 0x9e) & 0xff) % 21] / 5);
        buffer0[19] += (byte)(0xfffffee6 - ((buffer0[buffer3[4] % 20] >> 1) & 102));
        buffer1[15] = (byte)((3 * (((buffer1[72] >> (buffer4[buffer1[190] % 21] & 7)) ^ (buffer1[72] << ((7 - (buffer4[buffer1[190] % 21] - 1) & 7)))) - (3 * buffer4[buffer1[126] % 21]))) ^ buffer1[15]);
        buffer0[15] ^= (byte)(buffer2[buffer1[181] % 35] * buffer2[buffer1[181] % 35] * buffer2[buffer1[181] % 35]);
        buffer2[4] ^= (byte)(buffer1[202] / 3);
        A = (uint)(92 - buffer0[buffer3[0] % 20]);
        E = (A & 0xc6) | ((uint)(~buffer1[105]) & 0xc6) | (A & (uint)(~buffer1[105]));
        buffer2[1] += (byte)(E * E * E);
        buffer0[19] ^= (byte)(((224 | (buffer4[buffer1[92] % 21] & 27)) * buffer2[buffer1[41] % 35]) / 3);
        buffer1[140] += (byte)WeirdRor8(92, buffer1[5] & 7);
        buffer2[12] += (byte)(((((uint)(~buffer1[4]) ^ buffer2[buffer1[12] % 35]) | buffer1[182]) & 192) | (((uint)(~buffer1[4]) ^ buffer2[buffer1[12] % 35]) & buffer1[182]));
        buffer1[36] += 125;
        buffer1[124] = (byte)Rol8x((byte)((((74 & buffer1[138]) | ((74 | buffer1[138]) & buffer0[15])) & buffer0[buffer1[43] % 20]) | (((74 & buffer1[138]) | ((74 | buffer1[138]) & buffer0[15]) | buffer0[buffer1[43] % 20]) & 95)), 4);
        buffer3[8] = (byte)(((((buffer0[buffer3[4] % 20] & 95)) & ((buffer4[buffer1[68] % 21] & 46) << 1)) | 16) ^ 92);
        A = (uint)(buffer1[177] + buffer4[buffer1[79] % 21]);
        D = (((A >> 1) | ((3 * (uint)buffer1[148]) / 5)) & buffer2[1]) | ((A >> 1) & ((3 * (uint)buffer1[148]) / 5));
        buffer3[12] = (byte)(-34 - D);
        A = (uint)(8 - ((buffer2[22] & 7)));
        B = (uint)(buffer1[33] >> (int)(A & 7));
        C = (uint)(buffer1[33] << (buffer2[22] & 7));
        buffer2[16] += (byte)(((buffer2[buffer3[0] % 35] & 159) | buffer0[buffer3[4] % 20] | 8) - ((B ^ C) | 128));
        buffer0[14] ^= buffer2[buffer3[12] % 35];
        A = WeirdRol8(buffer4[buffer0[buffer1[201] % 20] % 21], ((buffer2[buffer1[112] % 35] << 1) & 7));
        D = (uint)((buffer0[buffer1[208] % 20] & 131) | (buffer0[buffer1[164] % 20] & 124));
        buffer1[19] += (byte)((A & (D / 5)) | ((A | (D / 5)) & 37));
        buffer2[8] = (byte)WeirdRor8(140, (int)(((uint)(buffer4[buffer1[45] % 21] + 92) * (uint)(buffer4[buffer1[45] % 21] + 92)) & 7));
        buffer1[190] = (byte)(56);
        buffer2[8] ^= buffer3[0];
        buffer1[53] = (byte)(~((buffer0[buffer1[83] % 20] | 204) / 5));
        buffer0[13] += buffer0[buffer1[41] % 20];
        buffer0[10] = (byte)(((buffer2[buffer3[0] % 35] & buffer1[2]) | ((buffer2[buffer3[0] % 35] | buffer1[2]) & buffer3[12])) / 15);
        A = (uint)((((56 | (buffer4[buffer1[2] % 21] & 68)) | buffer2[buffer3[8] % 35]) & 42) | (((buffer4[buffer1[2] % 21] & 68) | 56) & buffer2[buffer3[8] % 35]));
        buffer3[16] = (byte)((A * A) + 110);
        buffer3[20] = (byte)(202 - buffer3[16]);
        buffer3[24] = (byte)(buffer1[151]);
        buffer2[13] ^= buffer4[buffer3[0] % 21];
        B = (uint)(((buffer2[buffer1[179] % 35] - 38) & 177) | (buffer3[12] & 177));
        C = (uint)(((buffer2[buffer1[179] % 35] - 38)) & buffer3[12]);
        buffer3[28] = (byte)(30 + ((B | C) * (B | C)));
        buffer3[32] = (byte)(buffer3[28] + 62);
        A = (uint)(((buffer3[20] + (buffer3[0] & 74)) | (uint)(~buffer4[buffer3[0] % 21])) & 121);
        B = (uint)((buffer3[20] + (buffer3[0] & 74)) & (uint)(~buffer4[buffer3[0] % 21]));
        tmp3 = (A | B);
        C = (((A | B) ^ 0xffffffa6) | buffer3[0]) & 4 | (((A | B) ^ 0xffffffa6) & buffer3[0]);
        buffer1[47] = (byte)((buffer2[buffer1[89] % 35] + C) ^ buffer1[47]);
        buffer3[36] = (byte)(((Rol8((byte)((tmp & 179) + 68), 2) & buffer0[3]) | (tmp2 & (uint)(~buffer0[3]))) - 15);
        buffer1[123] ^= 221;
        A = (uint)(((buffer4[buffer3[0] % 21]) / 3) - buffer2[buffer3[4] % 35]);
        C = (uint)((((buffer3[0] & 163) + 92) & 246) | (buffer3[0] & 92));
        E = ((C | buffer3[24]) & 54) | (C & buffer3[24]);
        buffer3[40] = (byte)(A - E);
        buffer3[44] = (byte)(tmp3 ^ 81 ^ (((buffer3[0] >> 1) & 101) + 26));
        buffer3[48] = (byte)(buffer2[buffer3[4] % 35] & 27);
        buffer3[52] = (byte)(27);
        buffer3[56] = (byte)(199);
        buffer3[64] = (byte)(buffer3[4] + (((((((buffer3[40] | buffer3[24]) & 177) | (buffer3[40] & buffer3[24])) & ((((buffer4[buffer3[0] % 20] & 177) | 176)) | ((uint)(buffer4[buffer3[0] % 21]) & ~3u))) | ((((buffer3[40] & buffer3[24]) | ((buffer3[40] | buffer3[24]) & 177)) & 199) | ((((buffer4[buffer3[0] % 21] & 1) + 176) | (buffer4[buffer3[0] % 21] & ~3u)) & buffer3[56]))) & (uint)(~buffer3[52])) | buffer3[48]));
        buffer2[33] ^= buffer1[26];
        buffer1[106] ^= (byte)(buffer3[20] ^ 133);
        buffer2[30] = (byte)(((buffer3[64] / 3) - (275 | (buffer3[0] & 247))) ^ buffer0[buffer1[122] % 20]);
        buffer1[22] = (byte)((buffer2[buffer1[90] % 35] & 95) | 68);
        A = (uint)((buffer4[buffer3[36] % 21] & 184) | (buffer2[buffer3[44] % 35] & ~184));
        buffer2[18] += (byte)((A * A * A) >> 1);
        buffer2[5] -= buffer4[buffer1[92] % 21];
        A = (uint)((((buffer1[41] & ~24) | (buffer2[buffer1[183] % 35] & 24)) & (buffer3[16] + 53)) | (buffer3[20] & buffer2[buffer3[20] % 35]));
        B = (uint)((buffer1[17] & (uint)(~buffer3[44])) | (buffer0[buffer1[59] % 20] & buffer3[44]));
        buffer2[18] ^= (byte)(A * B);
        A = (uint)(WeirdRor8(buffer1[11], buffer2[buffer1[28] % 35] & 7) & 7);
        B = (uint)((((buffer0[buffer1[93] % 20] & (uint)(~buffer0[14])) | (buffer0[14] & 150)) & ~28u) | (buffer1[7] & 28));
        buffer2[22] = (byte)((((((B | WeirdRol8(buffer2[buffer3[0] % 35], (int)A)) & buffer2[33]) | (B & WeirdRol8(buffer2[buffer3[0] % 35], (int)A))) + 74) & 0xff));
        A = buffer4[(buffer0[buffer1[39] % 20] ^ 217) % 21];
        buffer0[15] -= (byte)((((((buffer3[20] | buffer3[0]) & 214) | (buffer3[20] & buffer3[0])) & A) | ((((buffer3[20] | buffer3[0]) & 214) | (buffer3[20] & buffer3[0]) | A) & buffer3[32])));
        B = (uint)((((buffer2[buffer1[57] % 35] & buffer0[buffer3[64] % 20]) | ((buffer0[buffer3[64] % 20] | buffer2[buffer1[57] % 35]) & 95) | (buffer3[64] & 45) | 82) & 32));
        C = (uint)(((buffer2[buffer1[57] % 35] & buffer0[buffer3[64] % 20]) | ((buffer2[buffer1[57] % 35] | buffer0[buffer3[64] % 20]) & 95)) & ((buffer3[64] & 45) | 82));
        D = (uint)((((buffer3[0] / 3) - (buffer3[64] | buffer1[22]))) ^ (buffer3[28] + 62) ^ ((B | C)));
        T = buffer0[(D & 0xff) % 20];
        buffer3[68] = (byte)((buffer0[buffer1[99] % 20] * buffer0[buffer1[99] % 20] * buffer0[buffer1[99] % 20] * buffer0[buffer1[99] % 20]) | buffer2[buffer3[64] % 35]);
        U = buffer0[buffer1[50] % 20];
        W = buffer2[buffer1[138] % 35];
        X = buffer4[buffer1[39] % 21];
        Y = buffer0[buffer1[4] % 20];
        Z = buffer4[buffer1[202] % 21];
        V = buffer0[buffer1[151] % 20];
        S = buffer2[buffer1[14] % 35];
        R = buffer0[buffer1[145] % 20];
        A = (uint)((buffer2[buffer3[68] % 35] & buffer0[buffer1[209] % 20]) | ((buffer2[buffer3[68] % 35] | buffer0[buffer1[209] % 20]) & 24));
        B = WeirdRol8(buffer4[buffer1[127] % 21], (int)(buffer2[buffer3[68] % 35] & 7));
        C = (A & buffer0[10]) | (B & (uint)(~buffer0[10]));
        D = (uint)(7 ^ (buffer4[buffer2[buffer3[36] % 35] % 21] << 1));
        buffer3[72] = (byte)((C & 71) | (D & ~71u));
        buffer2[2] += (byte)((((buffer0[buffer3[20] % 20] << 1) & 159) | (buffer4[buffer1[190] % 21] & ~159)) & ((((buffer4[buffer3[64] % 21] & 110) | (buffer0[buffer1[25] % 20] & ~110)) & ~150) | (buffer1[25] & 150)));
        buffer2[14] -= (byte)(((buffer2[buffer3[20] % 35] & (buffer3[72] ^ buffer2[buffer1[100] % 35])) & ~34) | (buffer1[97] & 34));
        buffer0[17] = (byte)(115);
        buffer1[23] ^= (byte)((((((buffer4[buffer1[17] % 21] | buffer0[buffer3[20] % 20]) & buffer3[72]) | (buffer4[buffer1[17] % 21] & buffer0[buffer3[20] % 20])) & (buffer1[50] / 3)) |
                        ((((buffer4[buffer1[17] % 21] | buffer0[buffer3[20] % 20]) & buffer3[72]) | (buffer4[buffer1[17] % 21] & buffer0[buffer3[20] % 20]) | (buffer1[50] / 3)) & 246)) << 1);
        buffer0[13] = (byte)((((((buffer0[buffer3[40] % 20] | buffer1[10]) & 82) | (buffer0[buffer3[40] % 20] & buffer1[10])) & 209) |
                       ((buffer0[buffer1[39] % 20] << 1) & 46)) >> 1);
        buffer2[33] -= (byte)(buffer1[113] & 9);
        buffer2[28] -= (byte)(((((2 | (buffer1[110] & 222)) >> 1) & ~223) | (buffer3[20] & 223)));
        J = WeirdRol8((byte)(V | Z), (int)(U & 7));
        A = (uint)((buffer2[16] & T) | (W & (~buffer2[16])));
        B = (uint)((buffer1[33] & 17) | (X & ~17));
        E = (((Y | ((A + B) / 5)) & 147) |
            (Y & ((A + B) / 5)));
        M = (uint)((buffer3[40] & buffer4[((buffer3[8] + J + E) & 0xff) % 21]) |
            ((buffer3[40] | buffer4[((buffer3[8] + J + E) & 0xff) % 21]) & buffer2[23]));
        buffer0[15] = (byte)((((buffer4[buffer3[20] % 21] - 48) & (uint)(~buffer1[184])) | ((buffer4[buffer3[20] % 21] - 48) & 189) | (189 & (uint)(~buffer1[184]))) & (M * M * M));
        buffer2[22] += buffer1[183];
        buffer3[76] = (byte)((3 * buffer4[buffer1[1] % 21]) ^ buffer3[0]);
        A = buffer2[((buffer3[8] + (J + E)) & 0xff) % 35];
        F = (uint)((((buffer4[buffer1[178] % 21] & A) | ((buffer4[buffer1[178] % 21] | A) & 209)) * buffer0[buffer1[13] % 20]) * (uint)(buffer4[buffer1[26] % 21] >> 1));
        G = (F + 0x733ffff9) * 198 - (((F + 0x733ffff9) * 396 + 212) & 212) + 85;
        buffer3[80] = (byte)(buffer3[36] + (G ^ 148) + ((G ^ 107) << 1) - 127);
        buffer3[84] = (byte)(((buffer2[buffer3[64] % 35]) & 245) | (buffer2[buffer3[20] % 35] & 10));
        A = (uint)(buffer0[buffer3[68] % 20] | 81);
        buffer2[18] -= (byte)(((A * A * A) & (uint)(~buffer0[15])) | ((buffer3[80] / 15) & buffer0[15]));
        buffer3[88] = (byte)(buffer3[8] + J + E - buffer0[buffer1[160] % 20] + (buffer4[buffer0[((buffer3[8] + J + E) & 255) % 20] % 21] / 3));
        B = (uint)(((R ^ buffer3[72]) & ~198) | ((S * S) & 198));
        F = (uint)((buffer4[buffer1[69] % 21] & buffer1[172]) | ((buffer4[buffer1[69] % 21] | buffer1[172]) & ((buffer3[12] - B) + 77)));
        buffer0[16] = (byte)(147 - ((buffer3[72] & ((F & 251) | 1)) | (((F & 250) | buffer3[72]) & 198)));
        C = (uint)((buffer4[buffer1[168] % 21] & buffer0[buffer1[29] % 20] & 7) | ((buffer4[buffer1[168] % 21] | buffer0[buffer1[29] % 20]) & 6));
        F = (uint)((buffer4[buffer1[155] % 21] & buffer1[105]) | ((buffer4[buffer1[155] % 21] | buffer1[105]) & 141));
        buffer0[3] -= buffer4[WeirdRol32((byte)F, (int)C) % 21];
        buffer1[5] = (byte)(WeirdRor8(buffer0[12], (int)(((uint)buffer0[buffer1[61] % 20] / 5) & 7)) ^ (((uint)(~buffer2[buffer3[84] % 35])) / 5));
        buffer1[198] += buffer1[3];
        A = (uint)(162 | buffer2[buffer3[64] % 35]);
        buffer1[164] += (byte)((A * A) / 5);
        G = WeirdRor8(139, (buffer3[80] & 7));
        C = (uint)(((buffer4[buffer3[64] % 21] * buffer4[buffer3[64] % 21] * buffer4[buffer3[64] % 21]) & 95) | (buffer0[buffer3[40] % 20] & ~95));
        buffer3[92] = (byte)((G & 12) | (buffer0[buffer3[20] % 20] & 12) | (G & buffer0[buffer3[20] % 20]) | C);
        buffer2[12] += (byte)(((buffer1[103] & 32) | (buffer3[92] & ((buffer1[103] | 60))) | 16) / 3);
        buffer3[96] = (byte)(buffer1[143]);
        buffer3[100] = (byte)(27);
        buffer3[104] = (byte)((((buffer3[40] & ~buffer2[8]) | (buffer1[35] & buffer2[8])) & buffer3[64]) ^ 119);
        buffer3[108] = (byte)(238 & ((((buffer3[40] & ~buffer2[8]) | (buffer1[35] & buffer2[8])) & buffer3[64]) << 1));
        buffer3[112] = (byte)(((uint)(~buffer3[64]) & (buffer3[84] / 3)) ^ 49);
        buffer3[116] = (byte)(98 & (((uint)(~buffer3[64]) & (buffer3[84] / 3)) << 1));
        A = (uint)((buffer1[35] & buffer2[8]) | (buffer3[40] & ~buffer2[8]));
        B = (uint)((A & buffer3[64]) | (((buffer3[84] / 3) & (uint)(~buffer3[64]))));
        buffer1[143] = (byte)(buffer3[96] - ((B & (86 + ((buffer1[172] & 64) >> 1))) | (((((buffer1[172] & 65) >> 1) ^ 86) | (((uint)(~buffer3[64]) & (buffer3[84] / 3)) | (((buffer3[40] & ~buffer2[8]) | (buffer1[35] & buffer2[8])) & buffer3[64]))) & buffer3[100])));
        buffer2[29] = (byte)(162);
        A = (uint)((((buffer4[buffer3[88] % 21]) & 160) | (buffer0[buffer1[125] % 20] & 95)) >> 1);
        B = (uint)(buffer2[buffer1[149] % 35] ^ (buffer1[43] * buffer1[43]));
        buffer0[15] += (byte)((B & A) | ((A | B) & 115));
        buffer3[120] = (byte)(buffer3[64] - buffer0[buffer3[40] % 20]);
        buffer1[95] = (byte)(buffer4[buffer3[20] % 21]);
        A = WeirdRor8(buffer2[buffer3[80] % 35], (int)((buffer2[buffer1[17] % 35] * buffer2[buffer1[17] % 35] * buffer2[buffer1[17] % 35]) & 7));
        buffer0[7] -= (byte)(A * A);
        buffer2[8] = (byte)(buffer2[8] - buffer1[184] + (buffer4[buffer1[202] % 21] * buffer4[buffer1[202] % 21] * buffer4[buffer1[202] % 21]));
        buffer0[16] = (byte)((buffer2[buffer1[102] % 35] << 1) & 132);
        buffer3[124] = (byte)((buffer4[buffer3[40] % 21] >> 1) ^ buffer3[68]);
        buffer0[7] -= (byte)((buffer0[buffer1[191] % 20] - (((buffer4[buffer1[80] % 21] << 1) & ~177) | (buffer4[buffer4[buffer3[88] % 21] % 21] & 177))));
        buffer0[6] = (byte)(buffer0[buffer1[119] % 20]);
        A = (uint)((buffer4[buffer1[190] % 21] & ~209) | (buffer1[118] & 209));
        B = (uint)(buffer0[buffer3[120] % 20] * buffer0[buffer3[120] % 20]);
        buffer0[12] = (byte)((buffer0[buffer3[84] % 20] ^ (buffer2[buffer1[71] % 35] + buffer2[buffer1[15] % 35])) & ((A & B) | ((A | B) & 27)));
        B = (uint)((buffer1[32] & buffer2[buffer3[88] % 35]) | ((buffer1[32] | buffer2[buffer3[88] % 35]) & 23));
        D = (uint)(((buffer4[buffer1[57] % 21] * 231) & 169) | (B & 86));
        F = (uint)((((buffer0[buffer1[82] % 20] & ~29) | (buffer4[buffer3[124] % 21] & 29)) & 190) | (buffer4[(D / 5) % 21] & ~190));
        H = (uint)(buffer0[buffer3[40] % 20] * buffer0[buffer3[40] % 20] * buffer0[buffer3[40] % 20]);
        K = (uint)((H & buffer1[82]) | (H & 92) | (buffer1[82] & 92));
        buffer3[128] = (byte)(((F & K) | ((F | K) & 192)) ^ (D / 5));
        buffer2[25] ^= (byte)(((buffer0[buffer3[120] % 20] << 1) * buffer1[5]) - (WeirdRol8(buffer3[76], (int)(buffer4[buffer3[124] % 21] & 7)) & (buffer3[20] + 110)));
    }

    // ── modified_md5.c ──────────────────────────────────────────────────

    private static readonly int[] Md5Shift =
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    ];

    private static uint MdF(uint b, uint c, uint d) => (b & c) | (~b & d);
    private static uint MdG(uint b, uint c, uint d) => (b & d) | (c & ~d);
    private static uint MdH(uint b, uint c, uint d) => b ^ c ^ d;
    private static uint MdI(uint b, uint c, uint d) => c ^ (b | ~d);
    private static uint MdRol(uint input, int count) => (input << count) | (input >> (32 - count));

    private static void ModifiedMd5(byte[] originalBlockIn, byte[] keyIn, byte[] keyOut)
    {
        byte[] blockIn = (byte[])originalBlockIn.Clone(); // mutated in place at round 31 — must be our own copy

        uint A = ReadU32Le(keyIn, 0), B = ReadU32Le(keyIn, 4), C = ReadU32Le(keyIn, 8), D = ReadU32Le(keyIn, 12);

        for (int i = 0; i < 64; i++)
        {
            int j = i switch
            {
                < 16 => i,
                < 32 => (5 * i + 1) % 16,
                < 48 => (3 * i + 5) % 16,
                _ => 7 * i % 16,
            };

            uint input = (uint)((blockIn[4 * j] << 24) | (blockIn[4 * j + 1] << 16) | (blockIn[4 * j + 2] << 8) | blockIn[4 * j + 3]);
            uint sineConst = (uint)((1UL << 32) * Math.Abs(Math.Sin(i + 1)));
            uint z = A + input + sineConst;
            z = i switch
            {
                < 16 => MdRol(z + MdF(B, C, D), Md5Shift[i]),
                < 32 => MdRol(z + MdG(B, C, D), Md5Shift[i]),
                < 48 => MdRol(z + MdH(B, C, D), Md5Shift[i]),
                _ => MdRol(z + MdI(B, C, D), Md5Shift[i]),
            };
            z += B;
            uint tmp = D;
            D = C;
            C = B;
            B = z;
            A = tmp;

            if (i == 31)
            {
                SwapWord(blockIn, (int)(A & 15), (int)(B & 15));
                SwapWord(blockIn, (int)(C & 15), (int)(D & 15));
                SwapWord(blockIn, (int)((A & (15 << 4)) >> 4), (int)((B & (15 << 4)) >> 4));
                SwapWord(blockIn, (int)((A & (15 << 8)) >> 8), (int)((B & (15 << 8)) >> 8));
                SwapWord(blockIn, (int)((A & (15 << 12)) >> 12), (int)((B & (15 << 12)) >> 12));
            }
        }

        WriteU32Le(keyOut, 0, ReadU32Le(keyIn, 0) + A);
        WriteU32Le(keyOut, 4, ReadU32Le(keyIn, 4) + B);
        WriteU32Le(keyOut, 8, ReadU32Le(keyIn, 8) + C);
        WriteU32Le(keyOut, 12, ReadU32Le(keyIn, 12) + D);
    }

    private static void SwapWord(byte[] block16, int wordIndexA, int wordIndexB)
    {
        uint a = ReadU32Le(block16, wordIndexA * 4);
        uint b = ReadU32Le(block16, wordIndexB * 4);
        WriteU32Le(block16, wordIndexA * 4, b);
        WriteU32Le(block16, wordIndexB * 4, a);
    }

    // ── omg_hax.c: generate_session_key / decryptMessage ────────────────

    private static void GenerateSessionKey(byte[] oldSap, byte[] messageIn, byte[] sessionKey)
    {
        var decryptedMessage = new byte[128];
        var newSap = new byte[320];

        DecryptMessage(messageIn, decryptedMessage);
        Array.Copy(FairPlayCipherTables.static_source_1, 0, newSap, 0x000, 0x11);
        Array.Copy(decryptedMessage, 0, newSap, 0x011, 0x80);
        Array.Copy(oldSap, 0x80, newSap, 0x091, 0x80);
        Array.Copy(FairPlayCipherTables.static_source_2, 0, newSap, 0x111, 0x2f);
        Array.Copy(FairPlayCipherTables.initial_session_key, sessionKey, 16);

        var md5 = new byte[16];
        var baseBlock = new byte[64];
        for (int round = 0; round < 5; round++)
        {
            Array.Copy(newSap, round * 64, baseBlock, 0, 64);

            ModifiedMd5(baseBlock, sessionKey, md5);
            SapHash(baseBlock, sessionKey); // overwrites sessionKey entirely, independent of its previous value

            for (int i = 0; i < 4; i++)
                WriteU32Le(sessionKey, i * 4, ReadU32Le(sessionKey, i * 4) + ReadU32Le(md5, i * 4));
        }

        for (int i = 0; i < 16; i += 4)
        {
            (sessionKey[i], sessionKey[i + 3]) = (sessionKey[i + 3], sessionKey[i]);
            (sessionKey[i + 1], sessionKey[i + 2]) = (sessionKey[i + 2], sessionKey[i + 1]);
        }
        for (int i = 0; i < 16; i++)
            sessionKey[i] ^= 121;
    }

    private static void DecryptMessage(byte[] messageIn, byte[] decryptedMessage)
    {
        var buffer = new byte[16];
        int mode = messageIn[12];
        var keySchedule = new uint[11][];
        GenerateKeySchedule(FairPlayCipherTables.initial_session_key, keySchedule);

        byte[] messageKey = FairPlayCipherTables.message_key[mode];
        byte[] messageIv = FairPlayCipherTables.message_iv[mode];
        byte[] s2 = FairPlayCipherTables.table_s2;
        byte[] s10 = FairPlayCipherTables.table_s10;
        uint[] s9 = FairPlayCipherTables.table_s9;

        for (int i = 0; i < 8; i++)
        {
            for (int j = 0; j < 16; j++)
            {
                buffer[j] = mode == 3
                    ? messageIn[(0x80 - 0x10 * i) + j]
                    : messageIn[(0x10 * (i + 1)) + j];
            }

            for (int j = 0; j < 9; j++)
            {
                int b = 0x80 - 0x10 * j;

                buffer[0x0] = (byte)(s2[MessageTableIndexOffset(b + 0x0) + buffer[0x0]] ^ messageKey[b + 0x0]);
                buffer[0x4] = (byte)(s2[MessageTableIndexOffset(b + 0x4) + buffer[0x4]] ^ messageKey[b + 0x4]);
                buffer[0x8] = (byte)(s2[MessageTableIndexOffset(b + 0x8) + buffer[0x8]] ^ messageKey[b + 0x8]);
                buffer[0xc] = (byte)(s2[MessageTableIndexOffset(b + 0xc) + buffer[0xc]] ^ messageKey[b + 0xc]);

                byte tmp = buffer[0x0d];
                buffer[0xd] = (byte)(s2[MessageTableIndexOffset(b + 0xd) + buffer[0x9]] ^ messageKey[b + 0xd]);
                buffer[0x9] = (byte)(s2[MessageTableIndexOffset(b + 0x9) + buffer[0x5]] ^ messageKey[b + 0x9]);
                buffer[0x5] = (byte)(s2[MessageTableIndexOffset(b + 0x5) + buffer[0x1]] ^ messageKey[b + 0x5]);
                buffer[0x1] = (byte)(s2[MessageTableIndexOffset(b + 0x1) + tmp] ^ messageKey[b + 0x1]);

                tmp = buffer[0x02];
                buffer[0x2] = (byte)(s2[MessageTableIndexOffset(b + 0x2) + buffer[0xa]] ^ messageKey[b + 0x2]);
                buffer[0xa] = (byte)(s2[MessageTableIndexOffset(b + 0xa) + tmp] ^ messageKey[b + 0xa]);
                tmp = buffer[0x06];
                buffer[0x6] = (byte)(s2[MessageTableIndexOffset(b + 0x6) + buffer[0xe]] ^ messageKey[b + 0x6]);
                buffer[0xe] = (byte)(s2[MessageTableIndexOffset(b + 0xe) + tmp] ^ messageKey[b + 0xe]);

                tmp = buffer[0x3];
                buffer[0x3] = (byte)(s2[MessageTableIndexOffset(b + 0x3) + buffer[0x7]] ^ messageKey[b + 0x3]);
                buffer[0x7] = (byte)(s2[MessageTableIndexOffset(b + 0x7) + buffer[0xb]] ^ messageKey[b + 0x7]);
                buffer[0xb] = (byte)(s2[MessageTableIndexOffset(b + 0xb) + buffer[0xf]] ^ messageKey[b + 0xb]);
                buffer[0xf] = (byte)(s2[MessageTableIndexOffset(b + 0xf) + tmp] ^ messageKey[b + 0xf]);

                uint w0 = s9[0x000 + buffer[0x0]] ^ s9[0x100 + buffer[0x1]] ^ s9[0x200 + buffer[0x2]] ^ s9[0x300 + buffer[0x3]];
                uint w1 = s9[0x000 + buffer[0x4]] ^ s9[0x100 + buffer[0x5]] ^ s9[0x200 + buffer[0x6]] ^ s9[0x300 + buffer[0x7]];
                uint w2 = s9[0x000 + buffer[0x8]] ^ s9[0x100 + buffer[0x9]] ^ s9[0x200 + buffer[0xa]] ^ s9[0x300 + buffer[0xb]];
                uint w3 = s9[0x000 + buffer[0xc]] ^ s9[0x100 + buffer[0xd]] ^ s9[0x200 + buffer[0xe]] ^ s9[0x300 + buffer[0xf]];
                WriteU32Le(buffer, 0, w0);
                WriteU32Le(buffer, 4, w1);
                WriteU32Le(buffer, 8, w2);
                WriteU32Le(buffer, 12, w3);
            }

            buffer[0x0] = s10[(0x0 << 8) + buffer[0x0]];
            buffer[0x4] = s10[(0x4 << 8) + buffer[0x4]];
            buffer[0x8] = s10[(0x8 << 8) + buffer[0x8]];
            buffer[0xc] = s10[(0xc << 8) + buffer[0xc]];

            byte t = buffer[0x0d];
            buffer[0xd] = s10[(0xd << 8) + buffer[0x9]];
            buffer[0x9] = s10[(0x9 << 8) + buffer[0x5]];
            buffer[0x5] = s10[(0x5 << 8) + buffer[0x1]];
            buffer[0x1] = s10[(0x1 << 8) + t];

            t = buffer[0x02];
            buffer[0x2] = s10[(0x2 << 8) + buffer[0xa]];
            buffer[0xa] = s10[(0xa << 8) + t];
            t = buffer[0x06];
            buffer[0x6] = s10[(0x6 << 8) + buffer[0xe]];
            buffer[0xe] = s10[(0xe << 8) + t];

            t = buffer[0x3];
            buffer[0x3] = s10[(0x3 << 8) + buffer[0x7]];
            buffer[0x7] = s10[(0x7 << 8) + buffer[0xb]];
            buffer[0xb] = s10[(0xb << 8) + buffer[0xf]];
            buffer[0xf] = s10[(0xf << 8) + t];

            if (mode is 2 or 1 or 0)
            {
                int off = 0x10 * i;
                for (int k = 0; k < 16; k++)
                    decryptedMessage[off + k] = (byte)(buffer[k] ^ (i > 0 ? messageIn[off + k] : messageIv[k]));
            }
            else
            {
                int off = 0x70 - 0x10 * i;
                for (int k = 0; k < 16; k++)
                    decryptedMessage[off + k] = (byte)(buffer[k] ^ (i < 7 ? messageIn[off + k] : messageIv[k]));
            }
        }
    }

    // ── omg_hax.c: key schedule + block cipher ("cycle") ────────────────

    private static void GenerateKeySchedule(byte[] keyMaterial, uint[][] keySchedule)
    {
        for (int i = 0; i < 11; i++)
            keySchedule[i] = [0xdeadbeef, 0xdeadbeef, 0xdeadbeef, 0xdeadbeef];

        var buffer = new byte[16]; // alias of the C source's "key_data[4]", accessed only byte-wise here
        TXor(keyMaterial, buffer);

        byte[] s1 = FairPlayCipherTables.table_s1;
        byte[] indexMangle = FairPlayCipherTables.index_mangle;
        int ti = 0;
        for (int round = 0; round < 11; round++)
        {
            keySchedule[round][0] = ReadU32Le(buffer, 0);

            int t1 = TableIndexOffset(ti);
            int t2 = TableIndexOffset(ti + 1);
            int t3 = TableIndexOffset(ti + 2);
            int t4 = TableIndexOffset(ti + 3);
            ti += 4;

            buffer[0] ^= (byte)(s1[t1 + buffer[0x0d]] ^ indexMangle[round]);
            buffer[1] ^= s1[t2 + buffer[0x0e]];
            buffer[2] ^= s1[t3 + buffer[0x0f]];
            buffer[3] ^= s1[t4 + buffer[0x0c]];

            keySchedule[round][1] = ReadU32Le(buffer, 4);
            WriteU32Le(buffer, 4, ReadU32Le(buffer, 4) ^ ReadU32Le(buffer, 0));

            keySchedule[round][2] = ReadU32Le(buffer, 8);
            WriteU32Le(buffer, 8, ReadU32Le(buffer, 8) ^ ReadU32Le(buffer, 4));

            keySchedule[round][3] = ReadU32Le(buffer, 12);
            WriteU32Le(buffer, 12, ReadU32Le(buffer, 12) ^ ReadU32Le(buffer, 8));
        }
    }

    private static void Cycle(byte[] block, uint[][] keySchedule)
    {
        WriteU32Le(block, 0, ReadU32Le(block, 0) ^ keySchedule[10][0]);
        WriteU32Le(block, 4, ReadU32Le(block, 4) ^ keySchedule[10][1]);
        WriteU32Le(block, 8, ReadU32Le(block, 8) ^ keySchedule[10][2]);
        WriteU32Le(block, 12, ReadU32Le(block, 12) ^ keySchedule[10][3]);
        PermuteBlock1(block);

        uint[] s5w = FairPlayCipherTables.table_s5;
        uint[] s6w = FairPlayCipherTables.table_s6;
        uint[] s7w = FairPlayCipherTables.table_s7;
        uint[] s8w = FairPlayCipherTables.table_s8;

        for (int round = 0; round < 9; round++)
        {
            uint k0 = keySchedule[9 - round][0];
            uint ab = s5w[block[3] ^ ByteOf(k0, 3)] ^ s6w[block[2] ^ ByteOf(k0, 2)] ^ s8w[block[0] ^ ByteOf(k0, 0)] ^ s7w[block[1] ^ ByteOf(k0, 1)];
            WriteU32Le(block, 0, ab);

            uint k1 = keySchedule[9 - round][1];
            ab = s6w[block[6] ^ ByteOf(k1, 2)] ^ s5w[block[7] ^ ByteOf(k1, 3)] ^ s7w[block[5] ^ ByteOf(k1, 1)] ^ s8w[block[4] ^ ByteOf(k1, 0)];
            WriteU32Le(block, 4, ab);

            uint k2 = keySchedule[9 - round][2];
            uint k3 = keySchedule[9 - round][3];
            WriteU32Le(block, 8,
                s5w[block[11] ^ ByteOf(k2, 3)] ^ s6w[block[10] ^ ByteOf(k2, 2)] ^ s7w[block[9] ^ ByteOf(k2, 1)] ^ s8w[block[8] ^ ByteOf(k2, 0)]);
            WriteU32Le(block, 12,
                s5w[block[15] ^ ByteOf(k3, 3)] ^ s6w[block[14] ^ ByteOf(k3, 2)] ^ s7w[block[13] ^ ByteOf(k3, 1)] ^ s8w[block[12] ^ ByteOf(k3, 0)]);

            PermuteBlock2(block, 8 - round);
        }

        WriteU32Le(block, 0, ReadU32Le(block, 0) ^ keySchedule[0][0]);
        WriteU32Le(block, 4, ReadU32Le(block, 4) ^ keySchedule[0][1]);
        WriteU32Le(block, 8, ReadU32Le(block, 8) ^ keySchedule[0][2]);
        WriteU32Le(block, 12, ReadU32Le(block, 12) ^ keySchedule[0][3]);
    }

    private static byte ByteOf(uint word, int index) => (byte)(word >> (8 * index));

    private static void PermuteBlock1(byte[] block)
    {
        byte[] t = FairPlayCipherTables.table_s3;
        block[0] = t[block[0]];
        block[4] = t[0x400 + block[4]];
        block[8] = t[0x800 + block[8]];
        block[12] = t[0xc00 + block[12]];

        byte tmp = block[13];
        block[13] = t[0x100 + block[9]];
        block[9] = t[0xd00 + block[5]];
        block[5] = t[0x900 + block[1]];
        block[1] = t[0x500 + tmp];

        tmp = block[2];
        block[2] = t[0xa00 + block[10]];
        block[10] = t[0x200 + tmp];
        tmp = block[6];
        block[6] = t[0xe00 + block[14]];
        block[14] = t[0x600 + tmp];

        tmp = block[3];
        block[3] = t[0xf00 + block[7]];
        block[7] = t[0x300 + block[11]];
        block[11] = t[0x700 + block[15]];
        block[15] = t[0xb00 + tmp];
    }

    private static int PermuteTable2Offset(int i) => ((71 * i) % 144) << 8;

    private static void PermuteBlock2(byte[] block, int round)
    {
        byte[] t = FairPlayCipherTables.table_s4;
        block[0] = t[PermuteTable2Offset(round * 16 + 0) + block[0]];
        block[4] = t[PermuteTable2Offset(round * 16 + 4) + block[4]];
        block[8] = t[PermuteTable2Offset(round * 16 + 8) + block[8]];
        block[12] = t[PermuteTable2Offset(round * 16 + 12) + block[12]];

        byte tmp = block[13];
        block[13] = t[PermuteTable2Offset(round * 16 + 13) + block[9]];
        block[9] = t[PermuteTable2Offset(round * 16 + 9) + block[5]];
        block[5] = t[PermuteTable2Offset(round * 16 + 5) + block[1]];
        block[1] = t[PermuteTable2Offset(round * 16 + 1) + tmp];

        tmp = block[2];
        block[2] = t[PermuteTable2Offset(round * 16 + 2) + block[10]];
        block[10] = t[PermuteTable2Offset(round * 16 + 10) + tmp];
        tmp = block[6];
        block[6] = t[PermuteTable2Offset(round * 16 + 6) + block[14]];
        block[14] = t[PermuteTable2Offset(round * 16 + 14) + tmp];

        tmp = block[3];
        block[3] = t[PermuteTable2Offset(round * 16 + 3) + block[7]];
        block[7] = t[PermuteTable2Offset(round * 16 + 7) + block[11]];
        block[11] = t[PermuteTable2Offset(round * 16 + 11) + block[15]];
        block[15] = t[PermuteTable2Offset(round * 16 + 15) + tmp];
    }

    private static int TableIndexOffset(int i) => ((31 * i) % 0x28) << 8;
    private static int MessageTableIndexOffset(int i) => (97 * i % 144) << 8;

    // ── z_key/x_key/t_key XORs ───────────────────────────────────────────

    private static void ZXor(byte[] input, byte[] output, int blocks)
    {
        for (int j = 0; j < blocks; j++)
            for (int i = 0; i < 16; i++)
                output[j * 16 + i] = (byte)(input[j * 16 + i] ^ FairPlayCipherTables.z_key[i]);
    }

    private static void XXor(byte[] input, byte[] output, int blocks)
    {
        for (int j = 0; j < blocks; j++)
            for (int i = 0; i < 16; i++)
                output[j * 16 + i] = (byte)(input[j * 16 + i] ^ FairPlayCipherTables.x_key[i]);
    }

    private static void TXor(byte[] input, byte[] output)
    {
        for (int i = 0; i < 16; i++)
            output[i] = (byte)(input[i] ^ FairPlayCipherTables.t_key[i]);
    }

    // ── hand_garble.c small helpers ──────────────────────────────────────

    private static byte Rol8(byte input, int count) =>
        (byte)(((input << count) & 0xff) | ((input & 0xff) >> (8 - count)));

    private static uint Rol8x(byte input, int count) =>
        (uint)((input << count) | (input >> (8 - count)));

    private static uint WeirdRor8(byte input, int count)
    {
        if (count == 0) return 0;
        return (uint)(((input >> count) & 0xff) | ((input & 0xff) << (8 - count)));
    }

    private static uint WeirdRol8(byte input, int count)
    {
        if (count == 0) return 0;
        return (uint)(((input << count) & 0xff) | ((input & 0xff) >> (8 - count)));
    }

    private static uint WeirdRol32(byte input, int count)
    {
        if (count == 0) return 0;
        return (uint)((input << count) ^ (input >> (8 - count)));
    }

    // ── little-endian 4-byte word helpers (mirrors the C source's
    //    uint32_t* reinterpret-casts of byte buffers on a little-endian
    //    machine) ───────────────────────────────────────────────────────

    private static uint ReadU32Le(byte[] buf, int offset) =>
        (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));

    private static void WriteU32Le(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }

}
#pragma warning restore CS0675
