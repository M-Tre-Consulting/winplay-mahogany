using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPlaySender.Core.Audio;
using AirPlaySender.Core.Crypto;

namespace AirPlaySender.Core.Receiving;

/// <summary>
/// One complete H.264 access unit (a whole frame's worth of NAL units),
/// already assembled the way a decoder wants it:
/// <list type="bullet">
/// <item><see cref="AnnexB"/> is Annex-B byte-stream framing — every NAL
/// prefixed with a <c>00 00 00 01</c> start code, not the 4-byte length
/// prefix the wire format uses (UxPlay's <c>raop_rtp_mirror.c</c> does the
/// exact same swap: "we're replacing [the size] with the 4-byte start code
/// for the NAL Byte-Stream Format").</item>
/// <item>For a key frame, the most recent SPS/PPS is already prepended, so a
/// renderer/decoder can start (or recover) from any <see cref="IsKeyFrame"/>
/// unit on its own — it never has to have seen an earlier config packet.</item>
/// <item><see cref="TimestampNs"/> is the frame's own presentation time in
/// whole nanoseconds — the mirroring packet header carries it at offset 8 as
/// a little-endian NTP 64-bit fixed-point value, converted here via
/// <see cref="Ntp.ToNanoseconds"/> (matching UxPlay's
/// <c>raop_ntp_timestamp_to_nano_seconds</c>). It is on the iPhone's clock
/// (an arbitrary boot-relative epoch), so only differences between frames
/// are meaningful — but those differences are the encoder's real frame
/// intervals, jitter-free, which is exactly what a smooth playback timeline
/// needs.</item>
/// </list>
///
/// A plain class (not a record struct) on purpose: the App project's XAML
/// compiler is an old .NET Framework tool that scans referenced assemblies'
/// public types and has a documented habit of crashing silently on shapes it
/// doesn't expect — keep types on the surface it sees boringly conventional.
/// </summary>
public sealed class MirroringVideoFrame(byte[] annexB, bool isKeyFrame, ulong timestampNs)
{
    public byte[] AnnexB { get; } = annexB;
    public bool IsKeyFrame { get; } = isKeyFrame;
    public ulong TimestampNs { get; } = timestampNs;
}

/// <summary>
/// The TCP connection a real device opens after a successful mirroring
/// <c>SETUP</c> — this is where the actual H.264 video arrives. Decrypts
/// real frames (verified against real hardware: ~2200 consecutive packets,
/// one real mirroring session, all correct — see README), assembles each
/// into a whole <see cref="MirroringVideoFrame"/> and hands it to
/// <see cref="FrameReceived"/> for a renderer to consume; decoding pixels
/// and drawing them is that renderer's job, not this class's.
///
/// Packet framing (128-byte header + payload) and the AES-CTR video key/iv
/// derivation are taken from UxPlay's <c>lib/raop_rtp_mirror.c</c>
/// (<c>raop_rtp_mirror_thread</c>, <c>mirror_buffer_init_aes</c>) — see the
/// doc comment on <see cref="AirPlayMirroringAdvertiser"/> for this
/// project's attribution convention.
/// </summary>
public sealed class MirroringDataReceiver : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public event Action<string>? Diagnostics;

    /// <summary>Fires once, when the unencrypted AVCDecoderConfigurationRecord packet arrives — the SPS/PPS a renderer needs to size its window (the decoder itself gets them prepended to every key frame, see <see cref="MirroringVideoFrame"/>). Args: (sps, pps), each a raw NAL including its 1-byte header.</summary>
    public event Action<byte[], byte[]>? ConfigReceived;

    /// <summary>Fires once per decrypted H.264 access unit, in wire order — see <see cref="MirroringVideoFrame"/> for the exact shape (Annex-B framed, SPS/PPS already prepended on key frames, real per-frame timestamp).</summary>
    public event Action<MirroringVideoFrame>? FrameReceived;

    /// <summary>Fires once when this session's data connection ends, for any reason (peer closed, error, cancellation) — the signal a renderer uses to close its own window instead of sitting there showing a frozen last frame forever.</summary>
    public event Action? SessionEnded;

    /// <summary>
    /// Raised by <see cref="RequestSessionClose"/> — a renderer (MirrorWindow) calls that
    /// when the USER closes its window, so <see cref="AirPlayReceiverServer"/> can close the
    /// owning RTSP control connection too. Without this, closing our render window left the
    /// phone still thinking it was mirroring (its data just went nowhere) instead of it
    /// noticing the receiver went away and stopping, matching normal AirPlay behavior.
    /// </summary>
    public event Action? CloseSessionRequested;

    /// <summary>Call when the user closes the window showing this session — see <see cref="CloseSessionRequested"/>.</summary>
    public void RequestSessionClose() => CloseSessionRequested?.Invoke();

    // --- late-subscriber replay -------------------------------------------------------
    // The renderer window is created on the UI thread, one dispatcher hop after the data
    // receiver is already reading packets — and the first Win2D device init can make that
    // hop take a few hundred ms. Without this, the config packet and the session's one
    // IDR fire into the void before anyone is listening, and the window never starts
    // (confirmed live: window never even appeared). Everything the receiver emits goes
    // through EmitConfig/EmitFrame/EmitSessionEnded under _emitGate; a late renderer
    // subscribes AND gets the backlog replayed in one atomic step (AttachRenderer), so a
    // frame arriving mid-attach is delivered live right after with no gap and no dup.
    private readonly object _emitGate = new();
    private (byte[] Sps, byte[] Pps)? _lastConfig;
    private readonly List<MirroringVideoFrame> _replaySinceKeyFrame = [];
    private bool _sessionEnded;
    private const int MaxReplayFrames = 600; // ~10s at 60fps — past this a late joiner can't get a clean start anyway

    /// <summary>
    /// Subscribes a renderer to <see cref="ConfigReceived"/>/<see cref="FrameReceived"/>/
    /// <see cref="SessionEnded"/> and immediately replays whatever already happened (the
    /// last config, then every frame since the last key frame; or just <paramref name="onEnded"/>
    /// if the session is already over) — atomically, so nothing is missed or doubled.
    /// </summary>
    public void AttachRenderer(Action<byte[], byte[]> onConfig, Action<MirroringVideoFrame> onFrame, Action onEnded)
    {
        lock (_emitGate)
        {
            if (_lastConfig is { } cfg) { try { onConfig(cfg.Sps, cfg.Pps); } catch { } }
            foreach (MirroringVideoFrame f in _replaySinceKeyFrame) { try { onFrame(f); } catch { } }
            if (_sessionEnded) { try { onEnded(); } catch { } return; }
            ConfigReceived += onConfig;
            FrameReceived += onFrame;
            SessionEnded += onEnded;
        }
    }

    /// <summary>Undoes <see cref="AttachRenderer"/>.</summary>
    public void DetachRenderer(Action<byte[], byte[]> onConfig, Action<MirroringVideoFrame> onFrame, Action onEnded)
    {
        lock (_emitGate)
        {
            ConfigReceived -= onConfig;
            FrameReceived -= onFrame;
            SessionEnded -= onEnded;
        }
    }

    private void EmitConfig(byte[] sps, byte[] pps)
    {
        lock (_emitGate)
        {
            _lastConfig = (sps, pps);
            ConfigReceived?.Invoke(sps, pps);
        }
    }

    private void EmitFrame(MirroringVideoFrame frame)
    {
        lock (_emitGate)
        {
            if (frame.IsKeyFrame) _replaySinceKeyFrame.Clear();
            if (_replaySinceKeyFrame.Count < MaxReplayFrames) _replaySinceKeyFrame.Add(frame);
            FrameReceived?.Invoke(frame);
        }
    }

    private void EmitSessionEnded()
    {
        lock (_emitGate)
        {
            _sessionEnded = true;
            SessionEnded?.Invoke();
        }
    }

    public int LocalPort { get; }

    public MirroringDataReceiver()
    {
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Start() => _acceptLoop = AcceptLoopAsync(_cts.Token);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            Trace($"Connessione dati mirroring accettata da {client.Client.RemoteEndPoint}");
            await ReadPacketsAsync(client, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Trace($"Errore sul canale dati mirroring: {ex.Message}"); }
        finally
        {
            Trace($"AcceptLoopAsync termina (iscritti a SessionEnded: {SessionEnded?.GetInvocationList().Length ?? 0})");
            EmitSessionEnded();
        }
    }

    /// <summary>
    /// Derives the per-stream video AES-CTR key/iv: SHA-512("AirPlayStreamKey{id}" or
    /// "...IV{id}" || sessionAesKey)[0..16]. Confirmed against UxPlay's real
    /// <c>mirror_buffer_init_aes</c> (lib/mirror_buffer.c): it formats the id with
    /// <c>PRIu64</c> — unsigned. The plist's <c>streamConnectionID</c> field decodes as a
    /// signed 64-bit <see cref="long"/> here (bplist's 8-byte integer encoding), so an id
    /// whose top bit is set comes out negative in C# but must still be formatted as its
    /// unsigned 64-bit decimal string to match the real device's hash input — reinterpret,
    /// don't just print the signed value. Never exercised against a real negative id until
    /// a live session got one and decrypted every video packet to garbage while the
    /// unencrypted SPS/PPS packet (this derivation doesn't touch it) decoded fine, which is
    /// what gave it away.
    /// </summary>
    public static (byte[] Key, byte[] Iv) DeriveVideoKeyIv(byte[] sessionAesKey, long streamConnectionId)
    {
        ulong unsignedId = unchecked((ulong)streamConnectionId);
        byte[] key = Sha.Sha512([.. Encoding.ASCII.GetBytes($"AirPlayStreamKey{unsignedId}"), .. sessionAesKey])[..16];
        byte[] iv = Sha.Sha512([.. Encoding.ASCII.GetBytes($"AirPlayStreamIV{unsignedId}"), .. sessionAesKey])[..16];
        return (key, iv);
    }

    private static readonly byte[] AnnexBStartCode = [0x00, 0x00, 0x00, 0x01];

    // The most recent SPS/PPS, already Annex-B framed (00000001 SPS 00000001 PPS),
    // ready to prepend to a key frame. UxPlay does exactly this in its mirror thread
    // ("if (prepend_sps_pps) ... memcpy(payload_out, sps_pps, sps_pps_len)"): a
    // real-time H.264 mirror stream only carries a parameter-set packet right before
    // an IDR, so a decoder that faults mid-stream (a dropped packet, a corrupt
    // reference frame) can otherwise never recover — the next IDR alone isn't enough,
    // it needs the SPS/PPS in-band with it. Prepending on EVERY key frame (not just
    // the first) is what makes the stream self-healing.
    private byte[]? _spsPpsAnnexB;

    // True once we've emitted at least one key frame. A decoder cannot start on a
    // P-frame — if our data connection comes up a beat after the stream already
    // started (or after any resync), the frames before the first IDR are undecodable
    // and must be dropped rather than fed in and left to rot in the decoder.
    private bool _emittedKeyFrame;

    // Leading non-VCL NAL units (SEI, AUD, a bare SPS/PPS) that arrived in a packet
    // of their own, with no slice — held here and prepended to the next frame that
    // does carry a slice, so one MirroringVideoFrame is always one real access unit.
    private readonly List<byte> _pendingPrefix = [];

    private int _droppedBeforeFirstKeyFrame;

    private async Task ReadPacketsAsync(TcpClient client, CancellationToken ct)
    {
        NetworkStream stream = client.GetStream();
        var header = new byte[128];
        int packetCount = 0;

        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExactAsync(stream, header, 128, ct).ConfigureAwait(false))
            {
                Trace("Canale dati mirroring chiuso dal peer");
                return;
            }

            // Confirmed against UxPlay's real raop_rtp_mirror.c: payload_size =
            // byteutils_get_int(packet, 0) — byteutils_get_int is little-endian
            // (byteutils_get_int_be is the separate big-endian variant UxPlay
            // uses elsewhere, e.g. for the NAL length prefix INSIDE a decrypted
            // payload).
            int payloadSize = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
            byte payloadTypeHigh = header[4], payloadTypeLow = header[5];
            if (payloadSize is < 0 or > 8 * 1024 * 1024)
            {
                Trace($"Header mirroring con payloadSize implausibile ({payloadSize}), mi fermo");
                return;
            }

            var payload = new byte[payloadSize];
            if (!await ReadExactAsync(stream, payload, payloadSize, ct).ConfigureAwait(false))
            {
                Trace("Canale dati mirroring chiuso a metà payload");
                return;
            }

            packetCount++;
            string kind = (payloadTypeHigh, payloadTypeLow) switch
            {
                (0x00, 0x00) => "video (non-IDR, cifrato)",
                (0x00, 0x10) => "video (IDR, cifrato)",
                (0x01, 0x00) => "SPS+PPS (non cifrato)",
                (0x02, 0x00) => "keepalive",
                (0x05, 0x00) => "streaming report",
                _ => $"tipo sconosciuto 0x{payloadTypeHigh:X2}{payloadTypeLow:X2}",
            };
            if (packetCount <= 8 || packetCount % 300 == 0)
                Trace($"Pacchetto #{packetCount}: {kind}, {payloadSize} byte");

            if (payloadTypeHigh == 0x01)
            {
                // Unencrypted "SPS+PPS" packet — a real AVCDecoderConfigurationRecord (see AvcDecoderConfig), not a bare NAL pair.
                (byte[] Sps, byte[] Pps)? config = AvcDecoderConfig.TryParse(payload);
                if (config is { } c)
                {
                    _spsPpsAnnexB = [.. AnnexBStartCode, .. c.Sps, .. AnnexBStartCode, .. c.Pps];
                    Trace($"  SPS/PPS: config valido (SPS {c.Sps.Length} byte, PPS {c.Pps.Length} byte)");
                    EmitConfig(c.Sps, c.Pps);
                }
                else
                {
                    Trace($"  SPS/PPS: AVCDecoderConfigurationRecord non valido ({Convert.ToHexString(payload.AsSpan(0, Math.Min(8, payload.Length)))}...)");
                }
            }
            else if (payloadTypeHigh == 0x00 && payloadSize > 0)
            {
                // Header offset 8 is an NTP 64-bit fixed-point timestamp (seconds<<32 |
                // 2^32-scaled fraction), NOT a raw nanosecond count — convert it the way
                // UxPlay's raop_rtp_mirror.c does (raop_ntp_timestamp_to_nano_seconds).
                ulong tsNs = Ntp.ToNanoseconds(BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8, 8)));
                HandleVideoPayload(payload, tsNs);
            }
        }
    }

    private void HandleVideoPayload(byte[] encryptedPayload, ulong timestampNs)
    {
        byte[] decrypted = _videoCipher!.Transform(encryptedPayload);

        // The wire payload is one or more NALs, each with a 4-byte big-endian length
        // prefix (AVCC). Split them out; a byte-perfect decrypt makes this exact, and
        // a malformed split (which would mean the keystream drifted) yields nothing
        // and the frame is skipped rather than fed in as garbage.
        List<byte[]> nals = AvcDecoderConfig.SplitAvccNalUnits(decrypted);
        nals.RemoveAll(n => n.Length == 0);
        if (nals.Count == 0)
        {
            Trace("  frame video senza NAL validi dopo la decifratura — scartato");
            return;
        }

        bool hasKeyFrame = nals.Exists(n => (n[0] & 0x1F) == 5);   // NAL type 5 = IDR slice
        bool hasSlice = nals.Exists(n => (n[0] & 0x1F) is >= 1 and <= 5);
        bool startsWithSps = (nals[0][0] & 0x1F) == 7;

        if (!hasSlice)
        {
            // Non-VCL only (SEI/AUD/parameter sets on their own) — stash and glue onto
            // the next real frame instead of shipping a "frame" with no picture in it.
            foreach (byte[] n in nals) { _pendingPrefix.AddRange(AnnexBStartCode); _pendingPrefix.AddRange(n); }
            return;
        }

        if (!_emittedKeyFrame && !hasKeyFrame)
        {
            _pendingPrefix.Clear();
            if (++_droppedBeforeFirstKeyFrame <= 10)
                Trace($"  frame non-IDR prima del primo IDR (#{_droppedBeforeFirstKeyFrame}) — scartato, un decoder non può partire da qui");
            return;
        }

        var annexB = new List<byte>(decrypted.Length + 64);
        if (hasKeyFrame && _spsPpsAnnexB is not null && !startsWithSps)
            annexB.AddRange(_spsPpsAnnexB);
        if (_pendingPrefix.Count > 0) { annexB.AddRange(_pendingPrefix); _pendingPrefix.Clear(); }
        foreach (byte[] n in nals) { annexB.AddRange(AnnexBStartCode); annexB.AddRange(n); }

        if (hasKeyFrame) _emittedKeyFrame = true;
        EmitFrame(new MirroringVideoFrame([.. annexB], hasKeyFrame, timestampNs));
    }

    // Set by the caller right after construction, once the stream-level SETUP
    // (which carries streamConnectionID) has told us what key to derive — see
    // AirPlayReceiverServer. One AesCtrKeystreamCipher per connection: the
    // video data channel is a single continuous AES-CTR byte stream split
    // across packets of arbitrary (non-16-byte-aligned) length, so the
    // keystream position has to carry across ReadPacketsAsync's packets
    // rather than restarting at the IV for each one.
    private AesCtrKeystreamCipher? _videoCipher;
    public void SetVideoKeyIv(byte[] key, byte[] iv) => _videoCipher = new AesCtrKeystreamCipher(key, iv);

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private void Trace(string message) => Diagnostics?.Invoke(message);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch { /* best-effort shutdown */ }
        }
        _cts.Dispose();
    }
}
