using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AirPlaySender.Core.Crypto;
using AirPlaySender.Core.Plist;
using AirPlaySender.Core.Tlv;

namespace AirPlaySender.Core.Tests.TestSupport;

/// <summary>
/// A minimal, independent AirPlay-2 receiver: just enough of HAP transient
/// pair-setup (SRP-6a server role) and the realtime SETUP sequence to let
/// <see cref="AirPlaySession"/> run a COMPLETE, real handshake end to end
/// over a loopback TCP socket — no Apple hardware involved. This exercises
/// the client at a level no isolated unit test can: exact request/response
/// framing, HKDF key agreement between two independently-computed
/// derivations, and (via <see cref="LastAudioPacket"/>) that a real,
/// correctly-encrypted RTP audio packet comes out the other end.
///
/// Deliberately narrow: handles exactly the transient-pairing, no-event-
/// channel path <see cref="AirPlaySession"/> takes for a HomePod-class
/// device. It is NOT a general RAOP receiver.
/// </summary>
internal sealed class FakeAirPlay2Receiver : IAsyncDisposable
{
    private static readonly BigInteger N = BigInteger.Parse("00" +
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74" +
        "020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F1437" +
        "4FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF05" +
        "98DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB" +
        "9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF6955817183" +
        "995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33A" +
        "85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7A" +
        "BF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D" +
        "87602733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E20" +
        "8E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF",
        System.Globalization.NumberStyles.HexNumber);
    private const int NBytes = 384;
    private static readonly BigInteger G = 5;

    private readonly TcpListener _listener;
    private readonly string _pin;
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);
    private readonly BigInteger _b = new(RandomNumberGenerator.GetBytes(32), isUnsigned: true, isBigEndian: true);
    private readonly UdpClient _audioSock = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly UdpClient _controlSock = new(new IPEndPoint(IPAddress.Loopback, 0));

    private TcpClient? _client;
    private NetworkStream? _stream;
    private byte[]? _decryptKey; // = client's Control-Write key
    private byte[]? _encryptKey; // = client's Control-Read key
    private ulong _sendCtr, _recvCtr;
    private readonly List<byte> _rxEncrypted = [];
    private readonly List<byte> _rxPlain = [];
    private bool _sessionSetupSeen;
    private Task? _serveTask;
    private readonly TaskCompletionSource<byte[]> _audioPacketTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Port { get; }

    /// <summary>Completes with the first raw RTP audio packet (header + encrypted payload + trailing nonce) the client sends.</summary>
    public Task<byte[]> AudioPacketReceived => _audioPacketTcs.Task;

    /// <summary>The AirPlay-2 audio key (bplist "shk") the client negotiated in the stream SETUP request, once seen.</summary>
    public byte[]? AudioKey { get; private set; }

    public FakeAirPlay2Receiver(string pin = "3939")
    {
        _pin = pin;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Start()
    {
        _serveTask = Task.Run(ServeAsync);
        _ = Task.Run(ListenForAudioAsync);
    }

    private async Task ListenForAudioAsync()
    {
        try
        {
            UdpReceiveResult r = await _audioSock.ReceiveAsync();
            _audioPacketTcs.TrySetResult(r.Buffer);
        }
        catch { /* socket disposed at teardown */ }
    }

    private async Task ServeAsync()
    {
        _client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
        _client.NoDelay = true;
        _stream = _client.GetStream();
        try
        {
            while (true)
            {
                (string method, string uri, IReadOnlyDictionary<string, string> headers, byte[] body)? req = await ReadRequestAsync().ConfigureAwait(false);
                if (req is null) return;
                await HandleAsync(req.Value.method, req.Value.uri, req.Value.body).ConfigureAwait(false);
            }
        }
        catch (IOException) { /* client disconnected */ }
    }

    private async Task HandleAsync(string method, string uri, byte[] body)
    {
        if (uri == "/pair-setup")
        {
            Tlv8.Map req = Tlv8.Decode(body);
            byte[]? state = req.Get(Tlv8Type.State);
            if (state is [0x01]) { await RespondPairSetupM2Async().ConfigureAwait(false); return; }
            if (state is [0x03]) { await RespondPairSetupM4Async(req).ConfigureAwait(false); return; }
            throw new InvalidOperationException($"unexpected pair-setup state {state?[0]}");
        }
        if (method == "GET" && uri == "/info") { await WriteResponseAsync(200, null, []).ConfigureAwait(false); return; }
        if (method == "SETUP" && !_sessionSetupSeen)
        {
            _sessionSetupSeen = true;
            byte[] resp = BinaryPlist.Encode(new PlistDictBuilder().Add("eventPort", 0L).Build());
            await WriteResponseAsync(200, "application/x-apple-binary-plist", resp).ConfigureAwait(false);
            return;
        }
        if (method == "RECORD") { await WriteResponseAsync(200, null, []).ConfigureAwait(false); return; }
        if (method == "SETUP" && _sessionSetupSeen)
        {
            PlistValue? root = BinaryPlist.Decode(body);
            PlistValue? stream0 = root?.Find("streams")?.ArrayValue.FirstOrDefault();
            AudioKey = stream0?.Find("shk")?.DataValue;
            PlistValue streamResp = new PlistDictBuilder()
                .Add("dataPort", (long)((IPEndPoint)_audioSock.Client.LocalEndPoint!).Port)
                .Add("controlPort", (long)((IPEndPoint)_controlSock.Client.LocalEndPoint!).Port)
                .Build();
            byte[] resp = BinaryPlist.Encode(new PlistDictBuilder().Add("streams", PlistValue.Array([streamResp])).Build());
            await WriteResponseAsync(200, "application/x-apple-binary-plist", resp).ConfigureAwait(false);
            return;
        }
        // /feedback and anything else the test doesn't need to react to.
        await WriteResponseAsync(200, null, []).ConfigureAwait(false);
    }

    // ── HAP pair-setup, SRP-6a SERVER role ──────────────────────────────

    private async Task RespondPairSetupM2Async()
    {
        BigInteger x = ComputeX(_salt, _pin);
        BigInteger v = BigInteger.ModPow(G, x, N);
        BigInteger k = HashPaddedPair(N, G);
        BigInteger bPublic = (k * v + BigInteger.ModPow(G, _b, N)) % N;
        _serverB = bPublic;
        _verifier = v;

        var m = new Tlv8.Map();
        m.Add(Tlv8Type.State, 0x02);
        m.Add(Tlv8Type.Salt, _salt);
        m.Add(Tlv8Type.PublicKey, bPublic.ToByteArray(isUnsigned: true, isBigEndian: true));
        await WriteResponseAsync(200, "application/octet-stream", Tlv8.Encode(m)).ConfigureAwait(false);
    }

    private BigInteger _serverB, _verifier;

    private async Task RespondPairSetupM4Async(Tlv8.Map req)
    {
        byte[] aBytes = req.Get(Tlv8Type.PublicKey) ?? throw new InvalidOperationException("M3 missing PublicKey");
        byte[] clientProof = req.Get(Tlv8Type.Proof) ?? throw new InvalidOperationException("M3 missing Proof");
        BigInteger clientA = new(aBytes, isUnsigned: true, isBigEndian: true);

        BigInteger u = HashPaddedPair(clientA, _serverB);
        BigInteger s = BigInteger.ModPow(clientA * BigInteger.ModPow(_verifier, u, N) % N, _b, N);
        byte[] sessionKey = Sha.Sha512(s.ToByteArray(isUnsigned: true, isBigEndian: true));

        byte[] hN = Sha.Sha512(N.ToByteArray(isUnsigned: true, isBigEndian: true));
        byte[] hg = Sha.Sha512(G.ToByteArray(isUnsigned: true, isBigEndian: true));
        var hXor = new byte[64];
        for (int i = 0; i < 64; i++) hXor[i] = (byte)(hN[i] ^ hg[i]);
        byte[] hI = Sha.Sha512("Pair-Setup"u8.ToArray());
        byte[] expectedM1 = Sha.Sha512(Concat(hXor, hI, _salt, aBytes, _serverB.ToByteArray(isUnsigned: true, isBigEndian: true), sessionKey));

        if (!CryptographicOperations.FixedTimeEquals(expectedM1, clientProof))
            throw new InvalidOperationException("client SRP proof (M1) did not verify against the server's independent computation");

        byte[] serverM2 = Sha.Sha512(Concat(aBytes, clientProof, sessionKey));
        var m = new Tlv8.Map();
        m.Add(Tlv8Type.State, 0x04);
        m.Add(Tlv8Type.Proof, serverM2);
        await WriteResponseAsync(200, "application/octet-stream", Tlv8.Encode(m)).ConfigureAwait(false);

        // Same HKDF derivation as the client, keys used with the roles SWAPPED
        // (mirrors real HAP semantics: the receiver decrypts with the client's "write" key).
        _decryptKey = Hkdf.DeriveSha512("Control-Salt", "Control-Write-Encryption-Key", sessionKey, 32);
        _encryptKey = Hkdf.DeriveSha512("Control-Salt", "Control-Read-Encryption-Key", sessionKey, 32);
    }

    private static BigInteger ComputeX(byte[] salt, string pin)
    {
        byte[] ucpHash = Sha.Sha512(Encoding.ASCII.GetBytes("Pair-Setup:" + pin));
        return new BigInteger(Sha.Sha512(Concat(salt, ucpHash)), isUnsigned: true, isBigEndian: true);
    }

    private static BigInteger HashPaddedPair(BigInteger a, BigInteger b) =>
        new(Sha.Sha512(Concat(PadTo(a, NBytes), PadTo(b, NBytes))), isUnsigned: true, isBigEndian: true);

    private static byte[] PadTo(BigInteger v, int length)
    {
        byte[] natural = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (natural.Length >= length) return natural;
        var padded = new byte[length];
        natural.CopyTo(padded, length - natural.Length);
        return padded;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    // ── wire framing (mirrors RtspConnection, server-side) ──────────────

    private async Task<(string method, string uri, IReadOnlyDictionary<string, string> headers, byte[] body)?> ReadRequestAsync()
    {
        var buf = new byte[8192];
        while (true)
        {
            if (TryParseRequest(out var parsed)) return parsed;
            int n;
            try { n = await _stream!.ReadAsync(buf).ConfigureAwait(false); }
            catch (IOException) { return null; }
            if (n == 0) return null;

            if (_decryptKey is null)
            {
                _rxPlain.AddRange(buf.AsSpan(0, n).ToArray());
            }
            else
            {
                _rxEncrypted.AddRange(buf.AsSpan(0, n).ToArray());
                while (true)
                {
                    byte[]? plaintext = HapFrameCodec.TryDecryptNextFrame(_decryptKey, ref _recvCtr, _rxEncrypted);
                    if (plaintext is null) break;
                    _rxPlain.AddRange(plaintext);
                }
            }
        }
    }

    private bool TryParseRequest(out (string method, string uri, IReadOnlyDictionary<string, string> headers, byte[] body)? result)
    {
        result = null;
        byte[] snapshot = _rxPlain.ToArray();
        string text = Encoding.ASCII.GetString(snapshot);
        int headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headEnd < 0) return false;

        string[] lines = text[..headEnd].Split('\n');
        string[] reqLineParts = lines[0].Trim().Split(' ');
        string method = reqLineParts[0];
        string uri = reqLineParts.Length > 1 ? reqLineParts[1] : "";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        int contentLength = headers.TryGetValue("Content-Length", out string? cl) && int.TryParse(cl, out int n) ? n : 0;
        int bodyStart = headEnd + 4;
        if (snapshot.Length < bodyStart + contentLength) return false;

        byte[] body = snapshot.AsSpan(bodyStart, contentLength).ToArray();
        _rxPlain.RemoveRange(0, bodyStart + contentLength);
        result = (method, uri, headers, body);
        return true;
    }

    private async Task WriteResponseAsync(int statusCode, string? contentType, byte[] body)
    {
        var sb = new StringBuilder();
        sb.Append("RTSP/1.0 ").Append(statusCode).Append(" OK\r\n");
        if (!string.IsNullOrEmpty(contentType)) sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n\r\n");
        byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
        byte[] full = [.. head, .. body];

        if (_encryptKey is null)
        {
            await _stream!.WriteAsync(full).ConfigureAwait(false);
        }
        else
        {
            byte[] framed = HapFrameCodec.EncryptChunked(_encryptKey, ref _sendCtr, full);
            await _stream!.WriteAsync(framed).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _client?.Dispose();
        _audioSock.Dispose();
        _controlSock.Dispose();
        if (_serveTask is not null) { try { await _serveTask.ConfigureAwait(false); } catch { } }
    }
}
