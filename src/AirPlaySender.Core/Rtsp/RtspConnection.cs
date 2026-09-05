using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPlaySender.Core.Crypto;

namespace AirPlaySender.Core.Rtsp;

/// <summary>
/// One TCP connection carrying BOTH the pairing HTTP POSTs (/pair-setup,
/// /pair-verify, /auth-setup, /pair-pin-start) and the RTSP audio handshake
/// (OPTIONS/ANNOUNCE/SETUP/RECORD for AirPlay 1, or the binary-plist
/// SETUP/RECORD for AirPlay 2) — real receivers parse both an RTSP and an
/// HTTP request line on the same socket, exactly as a real AirPlay sender
/// does. Requests are answered in order, one at a time (a
/// <see cref="SemaphoreSlim"/> serializes callers), so there's no need for
/// the method-FIFO the reference C++ implementation uses to route replies —
/// async/await already gives每 caller its own matching response.
///
/// After HAP pair-verify completes, the channel becomes ChaCha20-Poly1305
/// encrypted: every write is framed as
/// <c>[2-byte LE length][ciphertext][16-byte tag]</c>, chunked at 1024
/// bytes, AAD = the 2 length bytes, nonce = a per-direction 8-byte
/// little-endian frame counter. <see cref="EnableEncryption"/> flips this on.
/// </summary>
public sealed class RtspConnection : IAsyncDisposable
{
    private const int MaxBufferedBytes = 4 * 1024 * 1024; // guard against a hostile/buggy receiver dribbling an unbounded response

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<byte> _rxEncrypted = [];
    private readonly List<byte> _rxPlain = [];
    private byte[]? _writeKey;
    private byte[]? _readKey;
    private ulong _sendCounter;
    private ulong _recvCounter;
    private int _cseq;

    public string DacpId { get; }
    public uint ActiveRemote { get; }
    public string LocalAddress { get; }
    public string RemoteAddress { get; }
    public bool IsEncrypted => _writeKey is not null;

    private RtspConnection(TcpClient tcp, string dacpId, uint activeRemote)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        DacpId = dacpId;
        ActiveRemote = activeRemote;
        LocalAddress = FormatHost(((IPEndPoint)tcp.Client.LocalEndPoint!).Address);
        RemoteAddress = FormatHost(((IPEndPoint)tcp.Client.RemoteEndPoint!).Address);
    }

    /// <summary>
    /// A dual-stack socket connecting to an IPv4 peer reports its local/remote
    /// endpoint as an IPv4-mapped IPv6 address ("::ffff:192.168.1.88"), which
    /// <see cref="BuildRtspUri"/> would otherwise drop straight into a URI
    /// unbracketed — syntactically invalid (the colons collide with the
    /// URI's own host:port separator), and confirmed against a real HomePod
    /// to make it either reject the request (HTTP 400) or never reply at
    /// all. Map back to plain IPv4 first; bracket anything that's still
    /// genuinely IPv6.
    /// </summary>
    private static string FormatHost(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
    }

    public static async Task<RtspConnection> ConnectAsync(string host, int port, string dacpId, uint activeRemote, CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
        return new RtspConnection(tcp, dacpId, activeRemote);
    }

    /// <summary>Switches the channel to ChaCha20-Poly1305 framing — called once, right after HAP pair-verify M3. AirPlay 1 (no pairing) never calls this.</summary>
    public void EnableEncryption(byte[] writeKey, byte[] readKey)
    {
        _writeKey = writeKey;
        _readKey = readKey;
        _sendCounter = 0;
        _recvCounter = 0;
        _rxEncrypted.Clear(); // any bytes buffered before encryption armed are from before the receiver flipped too — discard, not decrypt
    }

    /// <summary>The <c>rtsp://&lt;our-ip-as-the-receiver-sees-it&gt;/&lt;sessionId&gt;</c> URI every RTSP method in the handshake addresses.</summary>
    public string BuildRtspUri(uint sessionId) => $"rtsp://{LocalAddress}/{sessionId}";

    // ── the three request shapes the AirPlay wire protocol actually uses ──

    /// <summary>Classic AirPlay-1 RTSP handshake request (OPTIONS/ANNOUNCE/SETUP/RECORD/SET_PARAMETER/TEARDOWN).</summary>
    public Task<RtspResponse> SendRtspAsync(string method, string uri, string? contentType = null, byte[]? body = null,
        IReadOnlyList<(string Name, string Value)>? extraHeaders = null, string? authorization = null, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.Append(method).Append(' ').Append(uri).Append(" RTSP/1.0\r\n");
        sb.Append("CSeq: ").Append(_cseq++).Append("\r\n");
        sb.Append("User-Agent: AirPlay/550.10\r\n");
        if (authorization is not null) sb.Append("Authorization: ").Append(authorization).Append("\r\n");
        sb.Append("DACP-ID: ").Append(DacpId).Append("\r\n");
        sb.Append("Active-Remote: ").Append(ActiveRemote).Append("\r\n");
        sb.Append("Client-Instance: ").Append(DacpId).Append("\r\n");
        if (extraHeaders is not null)
            foreach ((string name, string value) in extraHeaders) sb.Append(name).Append(": ").Append(value).Append("\r\n");
        sb.Append("X-Apple-Client-Name: WinPlay Mahogany\r\n");
        AppendContentHeadersAndTerminator(sb, contentType, body);
        return ExchangeAsync(sb.ToString(), body, ct);
    }

    /// <summary>HAP pairing POST (/pair-setup, /pair-verify, /auth-setup, /pair-pin-start). <paramref name="hkpMode"/>: 3 = normal HAP PIN, 4 = transient.</summary>
    public Task<RtspResponse> SendPairingPostAsync(string uri, byte[] body, int hkpMode, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.Append("POST ").Append(uri).Append(" HTTP/1.1\r\n");
        sb.Append("CSeq: ").Append(_cseq++).Append("\r\n");
        sb.Append("User-Agent: AirPlay/550.10\r\n");
        sb.Append("Connection: keep-alive\r\n");
        sb.Append("X-Apple-HKP: ").Append(hkpMode).Append("\r\n");
        sb.Append("DACP-ID: ").Append(DacpId).Append("\r\n");
        sb.Append("Active-Remote: ").Append(ActiveRemote).Append("\r\n");
        sb.Append("Client-Instance: ").Append(DacpId).Append("\r\n");
        sb.Append("X-Apple-Client-Name: WinPlay Mahogany\r\n");
        sb.Append("Content-Type: application/octet-stream\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n\r\n");
        return ExchangeAsync(sb.ToString(), body, ct);
    }

    /// <summary>
    /// <c>POST /fp-setup</c> — the FairPlay round-trip. First attempt against a real
    /// device (a Hisense AirPlay-2 TV) used <see cref="SendPairingPostAsync"/>'s
    /// HTTP/1.1 shape and got a flat HTTP 404 — the request never even routed.
    /// This is the "realtime handshake" family instead (<see
    /// cref="SendAp2RtspAsync"/>'s RTSP/1.0 shape, same as <c>GET /info</c> and
    /// <c>SETUP</c>, which this project's own real capture log shows fp-setup
    /// sitting right alongside), not the separate HAP pairing POST family
    /// pair-setup/pair-verify belong to.
    /// </summary>
    /// <summary>
    /// <c>X-Apple-ET: 32</c> — found live (2026-09-04) on a REAL fp-setup
    /// request, captured by mirroring a real Mac (macOS 26) and a real
    /// iPhone to this project's OWN receiver (which decrypts everything, so
    /// the request is visible in full): every genuine sender sends this
    /// header on <c>/fp-setup</c>, and the mirroring SETUP plist's own
    /// <c>et</c> field is <c>32</c> too (not the audio-context 0/1/3/4/5 this
    /// project assumed earlier tonight, going by the unofficial spec's *audio*
    /// <c>et</c> table — mirroring apparently uses a different value
    /// entirely). Every earlier live attempt against a real receiver omitted
    /// this header — a very plausible reason for the flat HTTP 403 it always got.
    /// </summary>
    private static readonly (string Name, string Value)[] FpSetupHeaders = [("X-Apple-ET", "32")];

    public Task<RtspResponse> SendFpSetupAsync(byte[] body, CancellationToken ct = default) =>
        SendAp2RtspAsync("POST", "/fp-setup", "application/octet-stream", body, extraHeaders: FpSetupHeaders, ct: ct);

    /// <summary>
    /// <c>POST /auth-setup</c> — MFi-SAP authentication (encryption type 4 —
    /// "MFiSAP, 3rd-party devices" per the unofficial AirPlay spec's <c>et</c>
    /// table), the alternative to <c>/fp-setup</c> a device that doesn't
    /// advertise the FairPlay feature bit (14) but does advertise
    /// SupportsUnifiedPairSetupAndMFi (51) is expected to use instead — same
    /// RTSP/1.0 request family as fp-setup/GET-info/SETUP.
    /// </summary>
    public Task<RtspResponse> SendAuthSetupAsync(byte[] body, CancellationToken ct = default) =>
        SendAp2RtspAsync("POST", "/auth-setup", "application/octet-stream", body, ct: ct);

    /// <summary>AirPlay-2 realtime handshake: GET /info and the binary-plist SETUP/RECORD — RTSP *methods* on the <c>rtsp://</c> URI, not an HTTP path (which 404s), but the reply carries a plist body.</summary>
    public Task<RtspResponse> SendAp2RtspAsync(string method, string uri, string? contentType = null, byte[]? body = null, bool isStreamSetup = false,
        IReadOnlyList<(string Name, string Value)>? extraHeaders = null, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.Append(method).Append(' ').Append(uri).Append(" RTSP/1.0\r\n");
        sb.Append("CSeq: ").Append(_cseq++).Append("\r\n");
        sb.Append("User-Agent: AirPlay/550.10\r\n");
        sb.Append("DACP-ID: ").Append(DacpId).Append("\r\n");
        sb.Append("Active-Remote: ").Append(ActiveRemote).Append("\r\n");
        sb.Append("Client-Instance: ").Append(DacpId).Append("\r\n");
        if (extraHeaders is not null)
            foreach ((string name, string value) in extraHeaders) sb.Append(name).Append(": ").Append(value).Append("\r\n");
        // Deliberately no X-Apple-Client-Name / X-Apple-StreamID here (an
        // earlier version sent both, using `isStreamSetup` for the latter):
        // confirmed against a real HomePod that a "full" audio-streaming
        // session SETUP (isMultiSelectAirPlay + a real timingPort) gets
        // silently dropped — no reply, ever — the moment either custom header
        // is present, while the identical body without them gets a clean 200.
        // pyatv, which streams to the same device successfully, sends neither.
        _ = isStreamSetup; // kept as a parameter for call-site clarity even though it's currently unused
        AppendContentHeadersAndTerminator(sb, contentType, body);
        return ExchangeAsync(sb.ToString(), body, ct);
    }

    /// <summary>Bare POST used for the AP2 ~2s keep-alive (encrypted control channel, no body).</summary>
    public Task<RtspResponse> SendFeedbackAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.Append("POST /feedback RTSP/1.0\r\n");
        sb.Append("CSeq: ").Append(_cseq++).Append("\r\n");
        sb.Append("User-Agent: AirPlay/550.10\r\n");
        sb.Append("DACP-ID: ").Append(DacpId).Append("\r\n");
        sb.Append("Active-Remote: ").Append(ActiveRemote).Append("\r\n");
        sb.Append("Client-Instance: ").Append(DacpId).Append("\r\n");
        sb.Append("Content-Length: 0\r\n\r\n");
        return ExchangeAsync(sb.ToString(), null, ct);
    }

    private static void AppendContentHeadersAndTerminator(StringBuilder sb, string? contentType, byte[]? body)
    {
        if (!string.IsNullOrEmpty(contentType)) sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        if (body is { Length: > 0 }) sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        sb.Append("\r\n");
    }

    private async Task<RtspResponse> ExchangeAsync(string headerText, byte[]? body, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[] head = Encoding.ASCII.GetBytes(headerText);
            byte[] full = body is { Length: > 0 } ? [.. head, .. body] : head;
            await WriteAsync(full, ct).ConfigureAwait(false);
            return await ReadResponseAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        if (_writeKey is null)
        {
            await _stream.WriteAsync(data, ct).ConfigureAwait(false);
            return;
        }
        byte[] framed = HapFrameCodec.EncryptChunked(_writeKey, ref _sendCounter, data);
        await _stream.WriteAsync(framed, ct).ConfigureAwait(false);
    }

    private async Task<RtspResponse> ReadResponseAsync(CancellationToken ct)
    {
        var buf = new byte[8192];
        while (true)
        {
            if (TryParseResponse(out RtspResponse? response)) return response!;
            if (_rxPlain.Count > MaxBufferedBytes) throw new IOException("Oversized RTSP response from the device");

            int n = await _stream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("The AirPlay device closed the connection");

            if (_readKey is null)
            {
                _rxPlain.AddRange(buf.AsSpan(0, n).ToArray());
            }
            else
            {
                _rxEncrypted.AddRange(buf.AsSpan(0, n).ToArray());
                DecryptAvailableFrames();
            }
        }
    }

    private void DecryptAvailableFrames()
    {
        while (true)
        {
            byte[]? plaintext = HapFrameCodec.TryDecryptNextFrame(_readKey!, ref _recvCounter, _rxEncrypted);
            if (plaintext is null) break;
            _rxPlain.AddRange(plaintext);
        }
    }

    private bool TryParseResponse(out RtspResponse? response)
    {
        response = null;
        if (_rxPlain.Count == 0) return false;

        byte[] snapshot = _rxPlain.ToArray();
        string headText = Encoding.ASCII.GetString(snapshot);
        int headEnd = headText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headEnd < 0) return false;

        string[] lines = headText[..headEnd].Split('\n');
        string statusLine = lines[0].Trim();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        int contentLength = 0;
        if (headers.TryGetValue("Content-Length", out string? clStr) && int.TryParse(clStr, out int cl) && cl > 0)
            contentLength = cl;

        int bodyStart = headEnd + 4;
        if (snapshot.Length < bodyStart + contentLength) return false; // body still arriving

        byte[] body = snapshot.AsSpan(bodyStart, contentLength).ToArray();
        _rxPlain.RemoveRange(0, bodyStart + contentLength);

        string[] statusParts = statusLine.Split(' ', 3);
        int code = statusParts.Length > 1 && int.TryParse(statusParts[1], out int c) ? c : 0;
        response = new RtspResponse { StatusCode = code, Headers = headers, Body = body };
        return true;
    }

    public ValueTask DisposeAsync()
    {
        _tcp.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
