using System.Runtime.InteropServices;

namespace AirPlaySender.App;

/// <summary>
/// Wraps the Fraunhofer FDK AAC decoder (<c>libAACdec.dll</c>, from the
/// <c>fdk-aac</c> NuGet package) to turn the raw AAC-ELD frames of a
/// mirroring audio stream into interleaved 16-bit PCM.
///
/// Windows' own AAC decoder MFT does not do AAC-ELD (only LC / HE-AAC), so
/// — exactly like UxPlay, which hard-requires libfdk-aac for mirror-mode
/// audio — this is a P/Invoke to FDK. The AudioSpecificConfig
/// (<c>F8 E8 50 00</c>) is the ISO/IEC 14496-3 config for AAC-ELD
/// 44100 Hz / stereo / 480 samples per frame, taken verbatim from UxPlay's
/// <c>audio_renderer.c</c> (<c>aac_eld_caps ... codec_data=(buffer)f8e85000</c>).
/// </summary>
internal sealed class AacEldDecoder : IDisposable
{
    private const int TT_MP4_RAW = 0;
    private const int AAC_DEC_OK = 0;

    // AudioSpecificConfig for AAC-ELD 44100/2, spf 480 (frameLengthFlag = 1).
    private static readonly byte[] AscAacEld44100Stereo = [0xF8, 0xE8, 0x50, 0x00];

    private IntPtr _handle;
    private readonly short[] _pcm = new short[8 * 1024]; // 480*2 = 960 needed; generous

    public int SampleRate { get; private set; } = 44100;
    public int Channels { get; private set; } = 2;

    public AacEldDecoder()
    {
        _handle = aacDecoder_Open(TT_MP4_RAW, 1);
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("aacDecoder_Open ha restituito NULL");

        GCHandle asc = GCHandle.Alloc(AscAacEld44100Stereo, GCHandleType.Pinned);
        try
        {
            IntPtr[] confPtrs = [asc.AddrOfPinnedObject()];
            uint[] confLen = [(uint)AscAacEld44100Stereo.Length];
            int err = aacDecoder_ConfigRaw(_handle, confPtrs, confLen);
            if (err != AAC_DEC_OK) throw new InvalidOperationException($"aacDecoder_ConfigRaw errore 0x{err:X4}");
        }
        finally { asc.Free(); }
    }

    /// <summary>Decodes one AAC-ELD frame to interleaved int16 PCM, or null on error / no output.</summary>
    public short[]? Decode(byte[] aacFrame)
    {
        if (_handle == IntPtr.Zero || aacFrame.Length == 0) return null;

        GCHandle frame = GCHandle.Alloc(aacFrame, GCHandleType.Pinned);
        try
        {
            IntPtr[] bufPtrs = [frame.AddrOfPinnedObject()];
            uint[] bufSize = [(uint)aacFrame.Length];
            uint valid = (uint)aacFrame.Length;
            int fillErr = aacDecoder_Fill(_handle, bufPtrs, bufSize, ref valid);
            if (fillErr != AAC_DEC_OK) return null;

            int decErr = aacDecoder_DecodeFrame(_handle, _pcm, _pcm.Length, 0);
            if (decErr != AAC_DEC_OK) return null;

            IntPtr info = aacDecoder_GetStreamInfo(_handle);
            if (info == IntPtr.Zero) return null;
            SampleRate = Marshal.ReadInt32(info, 0);
            int frameSize = Marshal.ReadInt32(info, 4);
            Channels = Marshal.ReadInt32(info, 8);

            int n = frameSize * Channels;
            if (n <= 0 || n > _pcm.Length) return null;
            var outp = new short[n];
            Array.Copy(_pcm, outp, n);
            return outp;
        }
        finally { frame.Free(); }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) { aacDecoder_Close(_handle); _handle = IntPtr.Zero; }
    }

    // --- FDK AAC decoder API (libAACdec.dll), __cdecl -------------------------------
    private const string Lib = "libAACdec";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr aacDecoder_Open(int transportFmt, uint nrOfLayers);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void aacDecoder_Close(IntPtr self);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int aacDecoder_ConfigRaw(IntPtr self, IntPtr[] conf, uint[] length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int aacDecoder_Fill(IntPtr self, IntPtr[] pBuffer, uint[] bufferSize, ref uint bytesValid);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int aacDecoder_DecodeFrame(IntPtr self, short[] pTimeData, int timeDataSize, uint flags);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr aacDecoder_GetStreamInfo(IntPtr self);
}
