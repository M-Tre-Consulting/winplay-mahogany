using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPlaySender.Core.Crypto;
using AirPlaySender.Core.Net;
using AirPlaySender.Core.Plist;
using AirPlaySender.Core.Tlv;

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
        var hapPairSetup = new HapPairSetupAccessorySession();
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
                var remoteAddr = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                (byte[] responseBytes, bool closeAfter) = BuildResponse(request, pairing, hapPairSetup, fairplay, mirror, remoteAddr);
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
            if (mirror.Timing is not null) await mirror.Timing.DisposeAsync().ConfigureAwait(false);
            mirror.TimingSocket?.Dispose();
            client.Dispose();
            Trace($"Connessione con {peer} chiusa");
        }
    }

    /// <summary>Per-connection state that only SETUP fills in: the FairPlay-decrypted session AES key, the mirror data listener, and the timing exchange.</summary>
    private sealed class MirrorSetupState
    {
        public byte[]? SessionAesKey { get; set; }
        public MirroringDataReceiver? DataReceiver { get; set; }
        public UdpClient? TimingSocket { get; set; }
        public NtpTimingSession? Timing { get; set; }
    }

    private (byte[] Bytes, bool CloseAfter) BuildResponse(RtspRequest request, PairingAccessorySession pairing, HapPairSetupAccessorySession hapPairSetup, FairPlaySetupSession fairplay, MirrorSetupState mirror, IPAddress remoteAddr)
    {
        return (request.Method, request.Url) switch
        {
            ("OPTIONS", _) => (BuildOptionsResponse(request), false),
            ("GET", var url) when url.Contains("/info") => (BuildInfoResponse(request), false),
            ("POST", "/pair-setup") => BuildPairSetupResponse(request, pairing, hapPairSetup),
            ("POST", "/pair-verify") => BuildPairVerifyResponse(request, pairing),
            ("POST", "/fp-setup") => BuildFpSetupResponse(request, fairplay),
            ("SETUP", _) => BuildSetupResponse(request, pairing, fairplay, mirror, remoteAddr),
            ("GET_PARAMETER", _) => (BuildGetParameterResponse(request), false),
            ("RECORD", _) => (BuildRecordResponse(request), false),
            // OPTIONS advertises both of these in its Public header (matching
            // UxPlay), but nothing here answered them — found by code review,
            // they were falling through to 501 instead of 200.
            ("SET_PARAMETER" or "FLUSH" or "TEARDOWN" or "PAUSE", _) => (BuildStatusResponse(request, 200, "OK"), false),
            _ => (BuildStatusResponse(request, 501, "Not Implemented"), false),
        };
    }

    /// <summary>
    /// The mirroring SETUP: a session-level call (has <c>ekey</c>/<c>eiv</c> —
    /// decrypt the real per-session AES key via <see cref="FairPlayCipher"/>
    /// and re-hash it with the pair-verify ECDH secret) and/or a stream-level
    /// call (has a <c>streams</c> array — for <c>type: 110</c>/Mirroring,
    /// derive the per-connection video AES-CTR key and open the TCP data
    /// listener a real device connects to next). Shape read line-by-line from
    /// UxPlay's actual <c>raop_handler_setup</c> (not just its docs) — see
    /// the doc comment on <see cref="AirPlayMirroringAdvertiser"/> for this
    /// project's attribution convention. Two things earlier experiments got
    /// wrong, corrected once the real reference source was read closely:
    /// <c>eventPort</c> is UxPlay's own literal <c>0</c> ("the event port is
    /// not used in mirror mode or audio mode" — no real listener), and the
    /// response never proactively adds a <c>streams</c> entry the request
    /// didn't ask for. What real UxPlay hardware DOES actively do that this
    /// project never had until now: run <see cref="NtpTimingSession"/> —
    /// send it periodic clock-sync requests on the very socket whose port we
    /// hand back as <c>timingPort</c>, instead of leaving that socket idle.
    /// </summary>
    private (byte[], bool) BuildSetupResponse(RtspRequest request, PairingAccessorySession pairing, FairPlaySetupSession fairplay, MirrorSetupState mirror, IPAddress remoteAddr)
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
            if (ekeyNode.DataValue.Length != 72)
            {
                // FairPlayCipher.Decrypt slices this assuming exactly 72 bytes
                // (chunk1/chunk2) — found by code review: an unexpected length
                // used to throw an unhandled exception instead of a clean 400.
                Trace($"  ekey ha lunghezza inattesa ({ekeyNode.DataValue.Length}, attesi 72) — rifiuto");
                return (BuildStatusResponse(request, 400, "Bad Request"), true);
            }

            byte[] rawKey = FairPlayCipher.Decrypt(fairplay.KeyMessage, ekeyNode.DataValue);
            byte[]? ecdh = pairing.EcdhSecret;
            byte[] sessionKey = ecdh is null
                ? rawKey
                : Sha.Sha512([.. rawKey, .. ecdh])[..16];
            mirror.SessionAesKey = sessionKey;
            Trace($"  chiave di sessione decifrata: {Convert.ToHexString(sessionKey)}");

            // A second ekey/eiv-bearing SETUP on the same connection (retry/
            // renegotiation) would otherwise leak the previous UdpClient and
            // leave its NtpTimingSession's send loop running forever — found
            // by code review. BuildSetupResponse is synchronous, so this is a
            // best-effort fire-and-forget cleanup rather than an awaited one.
            if (mirror.Timing is not null || mirror.TimingSocket is not null)
            {
                NtpTimingSession? oldTiming = mirror.Timing;
                UdpClient? oldSocket = mirror.TimingSocket;
                _ = Task.Run(async () =>
                {
                    if (oldTiming is not null) { try { await oldTiming.DisposeAsync().ConfigureAwait(false); } catch { } }
                    oldSocket?.Dispose();
                });
            }

            mirror.TimingSocket = new UdpClient(0);
            int timingPort = ((IPEndPoint)mirror.TimingSocket.Client.LocalEndPoint!).Port;
            res.Add("timingPort", (long)timingPort);

            // eventPort: UxPlay's own source returns the literal constant 0
            // here ("the event port is not used in mirror mode or audio
            // mode") — no real listener. A real, HAP-encrypted eventPort was
            // tried in an earlier session: the client did connect to it, but
            // still went RECORD -> TEARDOWN, so it wasn't the fix — reverted
            // to match the verified reference exactly.
            res.Add("eventPort", 0L);

            // The client's own timingPort (where IT expects OUR clock-sync
            // requests, not the other way around — see NtpTimingSession's
            // doc comment) only appears on this same ekey/eiv-bearing SETUP.
            long? clientTimingPort = req.Find("timingPort")?.AsInt();
            if (clientTimingPort is { } ctp and > 0)
            {
                var remote = new IPEndPoint(remoteAddr, (int)ctp);
                var timing = new NtpTimingSession(mirror.TimingSocket, remote);
                timing.Diagnostics += msg => Trace($"  [timing] {msg}");
                timing.Start();
                mirror.Timing = timing;
            }
            else
            {
                Trace("  il client non ha inviato un proprio timingPort — nessuno scambio timing da avviare");
            }

            // Confermato con una cattura di rete reale (pktmon, non a
            // intuito): questo client manda UNA SOLA SETUP — quella con
            // ekey/eiv — e non ne manda mai una seconda con un array
            // "streams". Offrire comunque la porta dati qui è la cosa
            // corretta da fare (senza, non avevamo mai detto al client di
            // sapere fare mirroring) — ma anche con questo, la stessa
            // cattura ripetuta subito dopo mostra lo stesso RECORD->TEARDOWN
            // di sempre, e la porta offerta non viene mai contattata. Non è
            // quindi (da sola) la causa del blocco, resta comunque il
            // comportamento corretto da tenere. streamConnectionID=0 replica
            // ciò che fa UxPlay quando il campo non arriva affatto (resta al
            // suo valore di default).
            bool isMirroring = req.Find("isScreenMirroringSession")?.BoolValue ?? false;
            if (isMirroring && req.Find("streams") is not { Type: PlistValue.Kind.Array })
            {
                Trace("  isScreenMirroringSession=true, nessun array streams in questa richiesta — offro la porta dati qui stesso (streamConnectionID=0)");
                (byte[] videoKey, byte[] videoIv) = MirroringDataReceiver.DeriveVideoKeyIv(sessionKey, 0);
                var receiver = new MirroringDataReceiver();
                receiver.SetVideoKeyIv(videoKey, videoIv);
                receiver.Diagnostics += msg => Trace($"  [dati mirroring] {msg}");
                receiver.Start();
                mirror.DataReceiver = receiver;
                Trace($"  in ascolto su porta dati {receiver.LocalPort}");
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

                    // Same leaked-listener risk as the timing socket above if
                    // a mirroring stream gets SETUP more than once.
                    if (mirror.DataReceiver is not null)
                    {
                        MirroringDataReceiver oldReceiver = mirror.DataReceiver;
                        _ = Task.Run(async () => { try { await oldReceiver.DisposeAsync().ConfigureAwait(false); } catch { } });
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

    /// <summary>
    /// Dispatches on <c>X-Apple-HKP</c>: <c>6</c> is the value confirmed
    /// tonight (real capture, a genuine mirroring session) on
    /// <c>/pair-verify</c> for the modern HAP TLV8 scheme — nothing has
    /// confirmed it's the same value on <c>/pair-setup</c> yet, this is the
    /// live test of that. Any other value (or the header's absence, as on
    /// every request seen from real hardware against this receiver so far)
    /// keeps using the legacy byte-offset scheme this project has already
    /// verified end-to-end — this dispatch changes nothing for that path.
    /// </summary>
    private (byte[], bool) BuildPairSetupResponse(RtspRequest request, PairingAccessorySession pairing, HapPairSetupAccessorySession hapPairSetup)
    {
        if (request.Header("X-Apple-HKP") != "6")
            return (BuildOctetStreamResponse(request, pairing.HandlePairSetup(request.Body)), false);

        Trace("  pair-setup HAP (X-Apple-HKP: 6) — schema mai confermato per questo verso, vedi HapPairSetupAccessorySession");
        if (request.Body.Length > 0)
        {
            Tlv8.Map req = Tlv8.Decode(request.Body);
            foreach ((byte tag, byte[] value) in req)
                Trace($"    TLV8 tag 0x{tag:X2} = {Convert.ToHexString(value)}");
        }

        byte[]? body = hapPairSetup.Handle(request.Body);
        if (body is null)
        {
            Trace("  pair-setup HAP rifiutato (forma inattesa — vedi il dump TLV8 sopra per la forma reale)");
            return (BuildStatusResponse(request, 400, "Bad Request"), true);
        }
        return (BuildOctetStreamResponse(request, body), false);
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
            // A client can ask for both blobs in one qualifier array (e.g.
            // ["txtAirPlay", "txtRAOP"]) — found by code review: this used
            // to only look at the first entry and silently drop the rest.
            var wanted = qualifier.ArrayValue.Select(v => v.AsStr()).Where(q => q is not null).ToHashSet();
            if (wanted.Count > 0)
            {
                wantsAirPlayTxt = wanted.Contains("txtAirPlay");
                wantsRaopTxt = wanted.Contains("txtRAOP");
            }
        }

        var builder = new PlistDictBuilder();
        if (wantsAirPlayTxt)
            builder.Add("txtAirPlay", AirPlayTxtRecord.EncodeWire(AirPlayTxtRecord.BuildEntries(_identity, _deviceId, AirPlayMirroringAdvertiser.Model)));
        if (wantsRaopTxt)
            builder.Add("txtRAOP", []); // no RAOP (audio-only) service on this receiver yet — empty, not omitted, matching UxPlay's shape when a device offers no RAOP TXT

        // A real receiver's GET /info carries a MUCH richer top-level dict
        // than just txtAirPlay — confirmed tonight from a real capture (a
        // Hisense TV's response was 2538 bytes, ours was ~100). Added here,
        // unconditional on the qualifier (the real device's response wasn't
        // gated on it either): deviceID/model/name/pi (already advertised
        // elsewhere, just not here) plus displays — this PC's actual screen
        // resolution, not a placeholder, since a client plausibly uses this
        // to decide whether mirroring here is even worth attempting.
        // Deliberately NOT touched: "features" — the real device's bitmask
        // differs from ours, but guessing at which bits matter risks
        // breaking the legacy path that already works end-to-end, for a
        // change nobody can verify tonight.
        (int widthPx, int heightPx) = ScreenResolution.GetPrimary();
        builder.Add("deviceID", _deviceId);
        builder.Add("model", AirPlayMirroringAdvertiser.Model);
        builder.Add("name", _identity.Pi.ToString("N")[..8]); // short, stable, not the machine's real name
        builder.Add("pi", _identity.Pi.ToString());
        builder.Add("displays", PlistValue.Array([
            new PlistDictBuilder()
                .Add("widthPixels", (long)widthPx)
                .Add("heightPixels", (long)heightPx)
                .Add("maxFPS", 60L)
                .Add("uuid", _identity.Pi.ToString())
                .Build(),
        ]));

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
