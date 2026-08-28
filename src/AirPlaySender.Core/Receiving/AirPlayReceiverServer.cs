using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPlaySender.Core.Crypto;
using AirPlaySender.Core.Plist;

namespace AirPlaySender.Core.Receiving;

/// <summary>
/// The RTSP/HTTP server an AirPlay controller (an iPhone tapping this PC in
/// Control Center) actually talks to: <c>OPTIONS</c>, <c>GET /info</c>, and
/// pairing (<c>POST /pair-setup</c>, <c>/pair-verify</c> — see
/// <see cref="PairingAccessorySession"/> for that handshake's shape).
/// Next milestone: the actual mirroring SETUP/video path, once pairing is
/// confirmed end-to-end against real hardware.
///
/// Method/path routing and the <c>GET /info</c> "txtAirPlay qualifier"
/// shape are taken from UxPlay's <c>lib/raop.c</c> (dispatch table) and
/// <c>lib/raop_handlers.h</c> (<c>raop_handler_info</c>) — see the doc
/// comment on <see cref="AirPlayMirroringAdvertiser"/> for the attribution
/// this project already gives that reference throughout.
/// </summary>
public sealed class AirPlayReceiverServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly ReceiverIdentity _identity;
    private readonly string _deviceId;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public event Action<string>? Diagnostics;

    public AirPlayReceiverServer(int port, ReceiverIdentity identity, string deviceId)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _identity = identity;
        _deviceId = deviceId;
    }

    public void Start()
    {
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }

            _ = HandleConnectionAsync(client, ct); // fire-and-forget: each connection is independent
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        string peer = client.Client.RemoteEndPoint?.ToString() ?? "?";
        Trace($"Connessione accettata da {peer}");
        var pairing = new PairingAccessorySession(_identity);
        var fairplay = new FairPlaySetupSession();
        var mirror = new MirrorSetupState();
        try
        {
            client.NoDelay = true;
            using NetworkStream stream = client.GetStream();
            var rx = new List<byte>();
            var buf = new byte[8192];

            while (!ct.IsCancellationRequested)
            {
                RtspRequest? request = TryParseRequest(rx);
                if (request is null)
                {
                    int n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
                    if (n == 0) break; // peer closed
                    rx.AddRange(buf.AsSpan(0, n).ToArray());
                    continue;
                }

                Trace($"{request.Method} {request.Url} (CSeq {request.CSeq})");
                (byte[] responseBytes, bool closeAfter) = BuildResponse(request, pairing, fairplay, mirror);
                await stream.WriteAsync(responseBytes, ct).ConfigureAwait(false);
                if (closeAfter)
                {
                    Trace("  chiudo la connessione (pairing rifiutato)");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Trace($"Connessione con {peer} interrotta: {ex.Message}");
        }
        finally
        {
            if (mirror.DataReceiver is not null) await mirror.DataReceiver.DisposeAsync().ConfigureAwait(false);
            mirror.TimingSocket?.Dispose();
            mirror.EventListener?.Stop();
            client.Dispose();
            Trace($"Connessione con {peer} chiusa");
        }
    }

    /// <summary>Per-connection state that only SETUP fills in: the FairPlay-decrypted session AES key and the mirror data listener.</summary>
    private sealed class MirrorSetupState
    {
        public byte[]? SessionAesKey { get; set; }
        public MirroringDataReceiver? DataReceiver { get; set; }
        public UdpClient? TimingSocket { get; set; }
        public TcpListener? EventListener { get; set; }
    }

    /// <summary>
    /// The eventPort connection, treated as HAP-encrypted (see
    /// <see cref="PairingAccessorySession.EventChannelKeys"/>) — decrypts
    /// whatever the client pushes and, if it looks like an RTSP request
    /// (has a CSeq), answers an encrypted bare 200 OK, mirroring exactly
    /// what this project's own <see cref="Rtsp.AirPlayEventChannel"/> does
    /// from the controller side (confirmed against a real HomePod in
    /// Phase 1) — just with the reversed key pair now that we're the
    /// accessory. Unverified whether this specific pairing flow (the
    /// mirroring one) uses the same key-derivation convention; this is the
    /// experiment that tests it.
    /// </summary>
    private void StartEventListener(TcpListener listener, byte[] writeKey, byte[] readKey)
    {
        CancellationToken ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            ulong sendCounter = 0, recvCounter = 0;
            var rxEncrypted = new List<byte>();
            var rxPlain = new List<byte>();
            try
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                Trace($"  [canale eventi] connessione accettata da {client.Client.RemoteEndPoint}");
                var buf = new byte[8192];
                NetworkStream stream = client.GetStream();
                while (!ct.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
                    if (n == 0) { Trace("  [canale eventi] chiuso dal client"); break; }
                    rxEncrypted.AddRange(buf.AsSpan(0, n).ToArray());

                    while (true)
                    {
                        byte[]? plaintext;
                        try { plaintext = HapFrameCodec.TryDecryptNextFrame(readKey, ref recvCounter, rxEncrypted); }
                        catch (IOException ex) { Trace($"  [canale eventi] decrittazione fallita: {ex.Message}"); return; }
                        if (plaintext is null) break;
                        rxPlain.AddRange(plaintext);
                        Trace($"  [canale eventi] decifrato: {Encoding.ASCII.GetString(plaintext).Replace("\r\n", " | ")}");
                    }

                    string text = Encoding.ASCII.GetString([.. rxPlain]);
                    int headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headEnd >= 0)
                    {
                        string? cseq = text[..headEnd].Split('\n')
                            .Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase))
                            ?.Split(':', 2)[1].Trim();
                        rxPlain.RemoveRange(0, headEnd + 4);
                        string resp = "RTSP/1.0 200 OK\r\nServer: AirPlay/220.68\r\n" + (cseq is not null ? $"CSeq: {cseq}\r\n" : "") + "\r\n";
                        byte[] frame = HapFrameCodec.EncryptFrame(writeKey, ref sendCounter, Encoding.ASCII.GetBytes(resp));
                        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
                        Trace("  [canale eventi] risposto 200 OK cifrato");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Trace($"  [canale eventi] errore: {ex.Message}"); }
        }, ct);
    }

    private (byte[] Bytes, bool CloseAfter) BuildResponse(RtspRequest request, PairingAccessorySession pairing, FairPlaySetupSession fairplay, MirrorSetupState mirror)
    {
        return (request.Method, request.Url) switch
        {
            ("OPTIONS", _) => (BuildOptionsResponse(request), false),
            ("GET", var url) when url.Contains("/info") => (BuildInfoResponse(request), false),
            ("POST", "/pair-setup") => (BuildOctetStreamResponse(request, pairing.HandlePairSetup(request.Body)), false),
            ("POST", "/pair-verify") => BuildPairVerifyResponse(request, pairing),
            ("POST", "/fp-setup") => BuildFpSetupResponse(request, fairplay),
            ("SETUP", _) => BuildSetupResponse(request, pairing, fairplay, mirror),
            ("GET_PARAMETER", _) => (BuildGetParameterResponse(request), false),
            ("RECORD", _) => (BuildRecordResponse(request), false),
            ("SET_PARAMETER" or "FLUSH", _) => (BuildStatusResponse(request, 200, "OK"), false),
            _ => (BuildStatusResponse(request, 501, "Not Implemented"), false),
        };
    }

    /// <summary>
    /// The mirroring SETUP: a session-level call (has <c>ekey</c>/<c>eiv</c> —
    /// decrypt the real per-session AES key via <see cref="FairPlayCipher"/>
    /// and re-hash it with the pair-verify ECDH secret) and/or a stream-level
    /// call (has a <c>streams</c> array — for <c>type: 110</c>/Mirroring,
    /// derive the per-connection video AES-CTR key and open the TCP data
    /// listener a real device connects to next). Shape confirmed against
    /// UxPlay's <c>raop_handler_setup</c> — see the doc comment on
    /// <see cref="AirPlayMirroringAdvertiser"/> for this project's
    /// attribution convention.
    /// </summary>
    private (byte[], bool) BuildSetupResponse(RtspRequest request, PairingAccessorySession pairing, FairPlaySetupSession fairplay, MirrorSetupState mirror)
    {
        PlistValue? req = request.Body.Length > 0 ? BinaryPlist.Decode(request.Body) : null;
        if (req is null) return (BuildStatusResponse(request, 400, "Bad Request"), true);

        if (req.Type == PlistValue.Kind.Dict)
            foreach ((string key, PlistValue val) in req.DictValue)
            {
                string shown = val.Type switch
                {
                    PlistValue.Kind.Str => val.StrValue,
                    PlistValue.Kind.Int => val.IntValue.ToString(),
                    PlistValue.Kind.Bool => val.BoolValue.ToString(),
                    PlistValue.Kind.Data => $"<{val.DataValue.Length} byte>",
                    PlistValue.Kind.Real => val.RealValue.ToString(),
                    _ => $"<{val.Type}>",
                };
                Trace($"  SETUP campo: {key} = {shown}");
            }

        var res = new PlistDictBuilder();

        PlistValue? ekeyNode = req.Find("ekey");
        PlistValue? eivNode = req.Find("eiv");
        if (ekeyNode is { Type: PlistValue.Kind.Data } && eivNode is { Type: PlistValue.Kind.Data })
        {
            Trace("  SETUP sessione (ekey/eiv presenti)");
            if (fairplay.KeyMessage is null)
            {
                Trace("  manca il messaggio chiave di /fp-setup — non posso decifrare ekey");
                return (BuildStatusResponse(request, 400, "Bad Request"), true);
            }

            byte[] rawKey = FairPlayCipher.Decrypt(fairplay.KeyMessage, ekeyNode.DataValue);
            byte[]? ecdh = pairing.EcdhSecret;
            byte[] sessionKey = ecdh is null
                ? rawKey
                : Sha.Sha512([.. rawKey, .. ecdh])[..16];
            mirror.SessionAesKey = sessionKey;
            Trace($"  chiave di sessione decifrata: {Convert.ToHexString(sessionKey)}");

            // A real receiver always returns a genuine bound port here — no
            // NTP responder behind it yet (that's still to build), but an
            // open, real port costs nothing and tests the hypothesis that a
            // client reading timingPort=0 gives up before ever asking for
            // the actual mirroring stream.
            mirror.TimingSocket = new UdpClient(0);
            int timingPort = ((IPEndPoint)mirror.TimingSocket.Client.LocalEndPoint!).Port;
            res.Add("timingPort", (long)timingPort);

            // Experiment: UxPlay's own comment says eventPort is unused for
            // mirroring, and every prior variant tried here (real
            // timingPort, proactive streams) made zero observable
            // difference to this client's behavior — it always goes
            // RECORD -> TEARDOWN without ever touching the offered data
            // port. Testing whether a real, connectable eventPort changes
            // that, since it's the one thing not yet tried.
            mirror.EventListener = new TcpListener(IPAddress.Any, 0);
            mirror.EventListener.Start();
            int eventPort = ((IPEndPoint)mirror.EventListener.LocalEndpoint).Port;
            (byte[] WriteKey, byte[] ReadKey)? eventKeys = pairing.EventChannelKeys;
            if (eventKeys is { } k) StartEventListener(mirror.EventListener, k.WriteKey, k.ReadKey);
            else Trace("  niente segreto ECDH disponibile — canale eventi aperto ma non cifrato");
            res.Add("eventPort", (long)eventPort);

            // This device's SETUP never carries a "streams" array or a
            // streamConnectionID at all (confirmed — logged every key that
            // arrived) — a shape UxPlay's own source doesn't handle either.
            // Speculative experiment: some newer client might expect the
            // receiver to just proactively offer the mirroring data port in
            // this same reply instead of waiting to be asked. No real
            // streamConnectionID exists to derive the video key from, so
            // this uses 0 — only meaningful as a test of whether the client
            // proceeds any further at all, not expected to make the actual
            // video decrypt correctly (see the real per-stream derivation
            // path below, still used when a client *does* send streams).
            bool isMirroring = req.Find("isScreenMirroringSession")?.BoolValue ?? false;
            if (isMirroring)
            {
                Trace("  nessun array streams nella richiesta — provo a offrire la porta dati proattivamente (streamConnectionID=0)");
                (byte[] videoKey, byte[] videoIv) = MirroringDataReceiver.DeriveVideoKeyIv(sessionKey, 0);
                var receiver = new MirroringDataReceiver();
                receiver.SetVideoKeyIv(videoKey, videoIv);
                receiver.Diagnostics += msg => Trace($"  [dati mirroring] {msg}");
                receiver.Start();
                mirror.DataReceiver = receiver;
                Trace($"  in ascolto proattivamente su porta dati {receiver.LocalPort}");
                res.Add("streams", PlistValue.Array([
                    new PlistDictBuilder().Add("dataPort", (long)receiver.LocalPort).Add("type", 110L).Build(),
                ]));
            }
        }

        PlistValue? streamsNode = req.Find("streams");
        if (streamsNode is { Type: PlistValue.Kind.Array })
        {
            var resStreams = new List<PlistValue>();
            foreach (PlistValue s in streamsNode.ArrayValue)
            {
                long type = s.Find("type")?.AsInt() ?? -1;
                if (type == 110) // Mirroring
                {
                    long streamConnectionId = s.Find("streamConnectionID")?.AsInt() ?? 0;
                    if (mirror.SessionAesKey is null)
                    {
                        Trace("  SETUP stream di mirroring senza una chiave di sessione valida — rifiuto");
                        return (BuildStatusResponse(request, 400, "Bad Request"), true);
                    }

                    (byte[] videoKey, byte[] videoIv) = MirroringDataReceiver.DeriveVideoKeyIv(mirror.SessionAesKey, streamConnectionId);
                    var receiver = new MirroringDataReceiver();
                    receiver.SetVideoKeyIv(videoKey, videoIv);
                    receiver.Diagnostics += msg => Trace($"  [dati mirroring] {msg}");
                    receiver.Start();
                    mirror.DataReceiver = receiver;
                    Trace($"  stream mirroring: streamConnectionID={streamConnectionId}, in ascolto su porta dati {receiver.LocalPort}");

                    resStreams.Add(new PlistDictBuilder()
                        .Add("dataPort", (long)receiver.LocalPort)
                        .Add("type", 110L)
                        .Build());
                }
                else
                {
                    Trace($"  SETUP stream di tipo {type} non supportato in questa fase");
                }
            }
            res.Add("streams", PlistValue.Array(resStreams));
        }

        byte[] body = BinaryPlist.Encode(res.Build());
        var sb = new StringBuilder();
        AppendStatusLine(sb, request, 200, "OK");
        sb.Append("Content-Type: application/x-apple-binary-plist\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n\r\n");
        return ([.. Encoding.ASCII.GetBytes(sb.ToString()), .. body], false);
    }

    private static (byte[], bool) BuildFpSetupResponse(RtspRequest request, FairPlaySetupSession fairplay)
    {
        byte[]? body = fairplay.Handle(request.Body);
        if (body is null) return (BuildStatusResponse(request, 400, "Bad Request"), true);
        return (BuildOctetStreamResponse(request, body), false);
    }

    /// <summary>pair-setup and pair-verify step 1 both reply with a raw <c>application/octet-stream</c> body — no plist, no TLV8 wrapper.</summary>
    private static byte[] BuildOctetStreamResponse(RtspRequest request, byte[] body)
    {
        var sb = new StringBuilder();
        AppendStatusLine(sb, request, 200, "OK");
        sb.Append("Content-Type: application/octet-stream\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n\r\n");
        return [.. Encoding.ASCII.GetBytes(sb.ToString()), .. body];
    }

    private static (byte[], bool) BuildPairVerifyResponse(RtspRequest request, PairingAccessorySession pairing)
    {
        byte[]? body = pairing.HandlePairVerify(request.Body);
        if (body is null) return (BuildStatusResponse(request, 400, "Bad Request"), true);
        return (BuildOctetStreamResponse(request, body), false);
    }

    /// <summary>The one query a mirroring client is known to poll here: a "volume\r\n" body under Content-Type text/parameters. Shape from UxPlay's raop_handler_get_parameter.</summary>
    private static byte[] BuildGetParameterResponse(RtspRequest request)
    {
        if (request.Header("Content-Type") == "text/parameters" && Encoding.ASCII.GetString(request.Body).Contains("volume"))
        {
            byte[] body = Encoding.ASCII.GetBytes("volume: 0.000000\r\n");
            var sb = new StringBuilder();
            AppendStatusLine(sb, request, 200, "OK");
            sb.Append("Content-Type: text/parameters\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n\r\n");
            return [.. Encoding.ASCII.GetBytes(sb.ToString()), .. body];
        }
        return BuildStatusResponse(request, 200, "OK");
    }

    /// <summary>Matches UxPlay's raop_handler_record exactly (audio-oriented headers, but present regardless of stream type in the reference).</summary>
    private static byte[] BuildRecordResponse(RtspRequest request)
    {
        var sb = new StringBuilder();
        AppendStatusLine(sb, request, 200, "OK");
        sb.Append("Audio-Latency: 0\r\n");
        sb.Append("Audio-Jack-Status: connected; type=analog\r\n");
        sb.Append("Content-Length: 0\r\n\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildOptionsResponse(RtspRequest request)
    {
        var sb = new StringBuilder();
        AppendStatusLine(sb, request, 200, "OK");
        sb.Append("Public: OPTIONS, GET, POST, SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, GET_PARAMETER, SET_PARAMETER\r\n");
        sb.Append("Content-Length: 0\r\n\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// The very first request a real iPhone sends after picking us in
    /// Control Center: a bplist body <c>{"qualifier": ["txtAirPlay"]}</c>,
    /// asking for a copy of exactly the TXT record we already advertise
    /// over mDNS. Reusing <see cref="AirPlayTxtRecord"/> means the two can
    /// never describe a different accessory by accident.
    /// </summary>
    private byte[] BuildInfoResponse(RtspRequest request)
    {
        bool wantsAirPlayTxt = true; // default even without a recognizable qualifier body — harmless to include
        bool wantsRaopTxt = false;
        PlistValue? reqPlist = request.Body.Length > 0 ? BinaryPlist.Decode(request.Body) : null;
        PlistValue? qualifier = reqPlist?.Find("qualifier");
        if (qualifier is { Type: PlistValue.Kind.Array })
        {
            string? q = qualifier.ArrayValue.FirstOrDefault()?.AsStr();
            if (q is not null)
            {
                wantsAirPlayTxt = q == "txtAirPlay";
                wantsRaopTxt = q == "txtRAOP";
            }
        }

        var builder = new PlistDictBuilder();
        if (wantsAirPlayTxt)
            builder.Add("txtAirPlay", AirPlayTxtRecord.EncodeWire(AirPlayTxtRecord.BuildEntries(_identity, _deviceId, AirPlayMirroringAdvertiser.Model)));
        if (wantsRaopTxt)
            builder.Add("txtRAOP", []); // no RAOP (audio-only) service on this receiver yet — empty, not omitted, matching UxPlay's shape when a device offers no RAOP TXT

        byte[] body = BinaryPlist.Encode(builder.Build());

        var sb = new StringBuilder();
        AppendStatusLine(sb, request, 200, "OK");
        sb.Append("Content-Type: application/x-apple-binary-plist\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n\r\n");
        return [.. Encoding.ASCII.GetBytes(sb.ToString()), .. body];
    }

    private static byte[] BuildStatusResponse(RtspRequest request, int code, string reason)
    {
        var sb = new StringBuilder();
        AppendStatusLine(sb, request, code, reason);
        sb.Append("Content-Length: 0\r\n\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static void AppendStatusLine(StringBuilder sb, RtspRequest request, int code, string reason)
    {
        sb.Append(request.Protocol.Length > 0 ? request.Protocol : "RTSP/1.0").Append(' ').Append(code).Append(' ').Append(reason).Append("\r\n");
        if (request.CSeq is { Length: > 0 } cseq) sb.Append("CSeq: ").Append(cseq).Append("\r\n");
        sb.Append("Server: AirPlay/220.68\r\n");
    }

    /// <summary>Incremental request parser — same head-then-Content-Length shape as <see cref="Rtsp.RtspConnection"/>'s response parser, mirrored for the server side.</summary>
    private static RtspRequest? TryParseRequest(List<byte> rx)
    {
        if (rx.Count == 0) return null;

        byte[] snapshot = [.. rx];
        string headText = Encoding.ASCII.GetString(snapshot);
        int headEnd = headText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headEnd < 0) return null;

        string[] lines = headText[..headEnd].Split('\n');
        string[] requestLine = lines[0].Trim().Split(' ', 3);
        if (requestLine.Length < 3) return null;

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
        if (snapshot.Length < bodyStart + contentLength) return null; // body still arriving

        byte[] body = snapshot.AsSpan(bodyStart, contentLength).ToArray();
        rx.RemoveRange(0, bodyStart + contentLength);

        return new RtspRequest { Method = requestLine[0], Url = requestLine[1], Protocol = requestLine[2], Headers = headers, Body = body };
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
