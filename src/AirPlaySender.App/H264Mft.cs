using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace AirPlaySender.App;

/// <summary>
/// Drives Windows' built-in H.264 decoder Media Foundation Transform
/// (<c>CLSID_CMSH264DecoderMFT</c>) directly — an explicit
/// <c>ProcessInput</c>/<c>ProcessOutput</c> loop with no
/// <see cref="Windows.Media.Playback.MediaPlayer"/> and no
/// <see cref="Windows.Media.Core.MediaStreamSource"/> around it.
///
/// Why: fed this exact live iPhone-mirror stream through
/// MediaStreamSource → MediaPlayer, the decoder stops producing output
/// frames after ~1 second (confirmed live twice: samples keep being pulled,
/// the playback clock keeps advancing, no error ever surfaces, but
/// <c>VideoFrameAvailable</c> fires exactly ~4 times and then never again —
/// even in frame-server mode, which rules out the compositor). Talking to
/// the MFT ourselves removes every hidden clock, buffering heuristic and
/// reorder assumption in that stack: one access unit in, decoded NV12 out,
/// converted to BGRA here and handed to the caller to blit.
///
/// Input is Annex-B (start codes), SPS/PPS in-band on every key frame (see
/// <see cref="AirPlaySender.Core.Receiving.MirroringDataReceiver"/>) — the
/// MFT parses parameter sets straight from the bitstream.
/// </summary>
internal sealed class H264Mft : IDisposable
{
    private static readonly Guid CLSID_CMSH264DecoderMFT = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
    private static readonly Guid IID_IMFTransform = new("BF94C121-5B05-4E6F-8000-BA598961414D");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    // Same GUID as CODECAPI_AVLowLatencyMode; the H.264 decoder honours it as an MFT
    // attribute. Without it the decoder buffers ~30 frames before its first output —
    // fatal for real-time mirroring (and a plausible part of the "few frames then
    // nothing" seen through MediaPlayer).
    private static readonly Guid MF_LOW_LATENCY = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");

    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    private const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
    private const int CLSCTX_INPROC_SERVER = 1;
    private const uint MFVideoInterlace_Progressive = 2;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    private readonly IMFTransform _mft;
    private int _codedW, _codedH, _stride;
    // The un-padded picture size the SPS says this stream has — passed to the ctor and
    // fixed for the life of this decoder. On an iPhone rotation the caller throws this
    // whole decoder away and builds a new one for the new size (see MirrorWindow's
    // decode loop), so it never has to change mid-stream. ReadOutputGeometry uses it as
    // the crop rectangle when it fits inside the decoder's coded size, else falls back
    // to the coded size.
    private readonly int _targetDisplayW;
    private readonly int _targetDisplayH;
    private bool _mfStarted;
    // Reused across frames — the FrameDecoded handler copies it out synchronously
    // (Win2D SetPixelBytes), so one buffer is enough and it keeps 40 * 3.8MB/s of
    // garbage off the heap.
    private byte[] _bgra = [];

    public int DisplayW { get; private set; }
    public int DisplayH { get; private set; }

    /// <summary>Raised once per decoded frame, on the calling (decode) thread: (bgra top-down, width, height, ptsHns).</summary>
    public event Action<byte[], int, int, long>? FrameDecoded;

    public H264Mft(int displayW, int displayH)
    {
        DisplayW = displayW > 0 ? displayW : 1920;
        DisplayH = displayH > 0 ? displayH : 1080;
        _targetDisplayW = displayW > 0 ? displayW : 0;
        _targetDisplayH = displayH > 0 ? displayH : 0;

        MediaFactory.MFStartup(false).CheckError();
        _mfStarted = true;

        int hr = CoCreateInstance(in CLSID_CMSH264DecoderMFT, IntPtr.Zero, CLSCTX_INPROC_SERVER, in IID_IMFTransform, out IntPtr pMft);
        if (hr < 0 || pMft == IntPtr.Zero) throw new InvalidOperationException($"CoCreateInstance(CMSH264DecoderMFT) 0x{hr:X8}");
        _mft = new IMFTransform(pMft);

        try { _mft.Attributes.Set(MF_LOW_LATENCY, true); } catch (SharpGenException) { /* not all builds expose it */ }

        ConfigureInput();
        NegotiateOutput();

        _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    private void ConfigureInput()
    {
        IMFMediaType t = MediaFactory.MFCreateMediaType();
        t.Set(MediaTypeAttributeKeys.MajorType, MFMediaType_Video);
        t.Set(MediaTypeAttributeKeys.Subtype, MFVideoFormat_H264);
        t.Set(MediaTypeAttributeKeys.InterlaceMode, MFVideoInterlace_Progressive);
        t.Set(MediaTypeAttributeKeys.FrameSize, Pack(DisplayW, DisplayH));
        t.Set(MediaTypeAttributeKeys.FrameRate, Pack(60, 1));
        t.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        _mft.SetInputType(0, t, 0);
        t.Dispose();
    }

    private void NegotiateOutput()
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType t;
            try { t = _mft.GetOutputAvailableType(0, i); }
            catch (SharpGenException) { throw new InvalidOperationException("Il decoder H.264 non offre un tipo di output NV12"); }

            bool isNv12 = t.GetGUID(MediaTypeAttributeKeys.Subtype) == MFVideoFormat_NV12;
            if (isNv12)
            {
                _mft.SetOutputType(0, t, 0);
                t.Dispose();
                ReadOutputGeometry();
                return;
            }
            t.Dispose();
        }
    }

    private void ReadOutputGeometry()
    {
        IMFMediaType t = _mft.GetOutputCurrentType(0);
        ulong fs = t.GetUInt64(MediaTypeAttributeKeys.FrameSize);
        _codedW = (int)(fs >> 32);
        _codedH = (int)(fs & 0xFFFFFFFF);
        try { _stride = unchecked((int)t.GetUInt32(MediaTypeAttributeKeys.DefaultStride)); } catch (SharpGenException) { _stride = _codedW; }
        if (_stride <= 0) _stride = _codedW;
        t.Dispose();

        // Recompute the display (crop) rectangle from scratch on every renegotiation —
        // never carry a previous DisplayW/DisplayH forward. Prefer the SPS-derived size
        // when it fits inside the decoder's coded rectangle, else fall back to the coded
        // size (right aspect ratio, at most a ~15px macroblock-padding strip).
        int tw = _targetDisplayW, th = _targetDisplayH;
        if (tw > 0 && th > 0 && tw <= _codedW && th <= _codedH)
        {
            DisplayW = tw;
            DisplayH = th;
        }
        else
        {
            DisplayW = _codedW;
            DisplayH = _codedH;
        }
    }

    // Input sample/buffer, allocated once and grown only if a bigger access
    // unit needs it (an IDR runs several times the size of an ordinary
    // P-frame) — found by code review: unlike the OUTPUT side just below
    // (EnsureOutputBuffer, already reused for exactly this reason), Decode
    // was calling MFCreateSample/MFCreateMemoryBuffer fresh on every single
    // call, 60 times a second — the identical COM-allocation-churn cost
    // already identified and fixed on the output side, just left standing
    // on the input side. Safe for the same reason the output buffer already
    // is: this MFT runs synchronously (ProcessInput has fully consumed the
    // sample by the time it returns — the DrainOutputs() call right after
    // is what pulls the frame(s) that produced), so nothing keeps a
    // reference to it once Decode returns.
    private IMFSample? _inSample;
    private IMFMediaBuffer? _inBuffer;
    private int _inBufferCapacity;

    private void EnsureInputBuffer(int need)
    {
        if (_inSample is not null && _inBufferCapacity >= need) return;
        _inBuffer?.Dispose();
        _inSample?.Dispose();
        _inBuffer = MediaFactory.MFCreateMemoryBuffer(need);
        _inSample = MediaFactory.MFCreateSample();
        _inSample.AddBuffer(_inBuffer);
        _inBufferCapacity = need;
    }

    /// <summary>Feed one Annex-B access unit and blit out every frame it completes.</summary>
    public void Decode(byte[] annexB, long ptsHns)
    {
        EnsureInputBuffer(annexB.Length);
        IMFSample sample = _inSample!;
        IMFMediaBuffer buffer = _inBuffer!;
        buffer.Lock(out IntPtr p, out _, out _);
        Marshal.Copy(annexB, 0, p, annexB.Length);
        buffer.Unlock();
        buffer.CurrentLength = annexB.Length;
        sample.SampleTime = ptsHns;
        sample.SampleDuration = 166_667;

        try
        {
            _mft.ProcessInput(0, sample, 0);
        }
        catch (SharpGenException ex) when (ex.ResultCode.Code == MF_E_NOTACCEPTING)
        {
            DrainOutputs();
            _mft.ProcessInput(0, sample, 0);
        }
        DrainOutputs();
    }

    // Output sample/buffer for the software decoder, allocated once and reused every
    // frame — MFCreateMemoryBuffer of a ~1.5 MB NV12 frame 60 times a second was pure
    // COM allocation churn. Grown (not just reallocated) only if a STREAM_CHANGE asks
    // for more. The MFT's output stream does not provide its own samples (stock
    // CMSH264DecoderMFT), so we always supply this one.
    private IMFSample? _outSample;
    private IMFMediaBuffer? _outBuffer;
    private int _outBufferSize;

    private void EnsureOutputBuffer()
    {
        OutputStreamInfo info = _mft.GetOutputStreamInfo(0);
        int need = Math.Max(info.Size, _stride * _codedH * 3 / 2 + 64);
        if (_outSample is not null && _outBufferSize >= need) return;

        _outBuffer?.Dispose();
        _outSample?.Dispose();
        _outBuffer = MediaFactory.MFCreateMemoryBuffer(need);
        _outSample = MediaFactory.MFCreateSample();
        _outSample.AddBuffer(_outBuffer);
        _outBufferSize = need;
    }

    private void DrainOutputs()
    {
        EnsureOutputBuffer();
        while (true)
        {
            _outBuffer!.CurrentLength = 0; // reset for reuse — the MFT writes the real length

            var db = new OutputDataBuffer { StreamID = 0, Sample = _outSample! };
            Result hr = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref db, out _);

            // db.Sample after the call is a fresh managed wrapper around the SAME native
            // pointer as _outSample, created WITHOUT an AddRef (SharpGen's struct
            // __MarshalFrom). It must NEVER be Disposed — doing so double-releases
            // _outSample and corrupts the heap a few dozen frames later ("crashed a caso",
            // window gone, no log). We only ever read the frame back through _outSample.
            if (hr.Code == MF_E_TRANSFORM_NEED_MORE_INPUT) return;
            if (hr.Code == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                NegotiateOutput();   // decoder learned the real coded size
                EnsureOutputBuffer(); // ...which may need a bigger buffer
                continue;
            }
            hr.CheckError();

            EmitFrame(_outSample!);
        }
    }

    private void EmitFrame(IMFSample s)
    {
        long pts = s.SampleTime;
        IMFMediaBuffer cb = s.ConvertToContiguousBuffer();
        cb.Lock(out IntPtr p, out _, out int _);
        try
        {
            byte[] bgra = Nv12ToBgra(p);
            FrameDecoded?.Invoke(bgra, DisplayW, DisplayH, pts);
        }
        finally
        {
            cb.Unlock();
            cb.Dispose();
        }
    }

    /// <summary>
    /// NV12 (BT.709 limited range) → BGRA32 top-down, cropped to the display rectangle.
    /// Fills and returns the reused <see cref="_bgra"/> buffer.
    ///
    /// Rows are fully independent (each reads its own Y row + the shared 4:2:0 UV row,
    /// writes its own output row) so they're converted in parallel across CPU cores —
    /// at high mirror resolutions (e.g. a Mac sending 2560x1440) the sequential scalar
    /// loop this replaced took ~30ms/frame on its own, already over the 16.7ms/frame
    /// budget for 60fps before decode or blit even ran, which is what built up the
    /// multi-second backlog seen against a MacBook Air (see README, "Limitazioni note").
    /// Per-pixel math is untouched from the sequential version — verified byte-for-byte
    /// identical output across both real resolutions plus padded-stride/odd-size cases,
    /// and confirmed live: the perceptible delay against that MacBook Air dropped to zero.
    /// </summary>
    private byte[] Nv12ToBgra(IntPtr nv12)
    {
        int w = DisplayW, h = DisplayH, stride = _stride;
        int uvOffset = stride * _codedH;
        int need = w * h * 4;
        if (_bgra.Length != need) _bgra = new byte[need];
        byte[] outp = _bgra;

        unsafe
        {
            nint srcBase = nv12;
            fixed (byte* dstFixed = outp)
            {
                nint dstBase = (nint)dstFixed;
                Parallel.For(0, h, y =>
                {
                    byte* src = (byte*)srcBase;
                    byte* dst = (byte*)dstBase;
                    byte* yRow = src + y * stride;
                    byte* uvRow = src + uvOffset + (y >> 1) * stride;
                    byte* d = dst + y * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        int c = yRow[x] - 16;
                        int uvx = (x >> 1) << 1;
                        int dd = uvRow[uvx] - 128;
                        int e = uvRow[uvx + 1] - 128;

                        int r = (298 * c + 459 * e + 128) >> 8;
                        int g = (298 * c - 55 * dd - 136 * e + 128) >> 8;
                        int b = (298 * c + 541 * dd + 128) >> 8;

                        d[0] = (byte)(b < 0 ? 0 : b > 255 ? 255 : b);
                        d[1] = (byte)(g < 0 ? 0 : g > 255 ? 255 : g);
                        d[2] = (byte)(r < 0 ? 0 : r > 255 ? 255 : r);
                        d[3] = 255;
                        d += 4;
                    }
                });
            }
        }
        return outp;
    }

    private static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    public void Dispose()
    {
        try { _mft?.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
        try { _mft?.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        try { _outBuffer?.Dispose(); } catch { }
        try { _outSample?.Dispose(); } catch { }
        try { _inBuffer?.Dispose(); } catch { }
        try { _inSample?.Dispose(); } catch { }
        try { _mft?.Dispose(); } catch { }
        if (_mfStarted) { try { MediaFactory.MFShutdown(); } catch { } _mfStarted = false; }
    }
}
