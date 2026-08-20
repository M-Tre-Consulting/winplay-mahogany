using System.Net.NetworkInformation;
using AirPlaySender.Core.Audio;
using AirPlaySender.Core.Discovery;
using AirPlaySender.Core.Pairing;
using AirPlaySender.Core.Plist;
using AirPlaySender.Core.Rtsp;

namespace AirPlaySender.Core;

public enum AirPlaySessionState { Idle, Connecting, Pairing, Handshake, Streaming }

/// <summary>
/// Orchestrates one AirPlay cast session end to end: connect, authenticate
/// (transient / on-screen-PIN / stored-credentials pair-verify, with the
/// same 403/470 fallbacks real receivers require), run the AirPlay-2
/// realtime SETUP sequence, then stream captured system audio until told
/// to stop. This is the async/await equivalent of the reference recipe's
/// callback state machine — the sequencing is identical, just expressed
/// linearly instead of through a manual stage enum.
/// </summary>
public sealed class AirPlaySession : IAsyncDisposable
{
    private const string ClientName = "AirPlayForWindows";

    private readonly CredentialStore _credentials;
    private readonly PairingIdentity _identity;

    private readonly Func<IAudioCaptureSource> _audioSourceFactory;

    private RtspConnection? _rtsp;
    private AirPlayEventChannel? _eventChannel;
    private RtpAudioTransport? _audio;
    private IAudioCaptureSource? _source;
    private CancellationTokenSource? _sessionCts;
    private Task? _feedbackLoop;
    private TaskCompletionSource<string>? _pinTcs;
    private uint _sessionId;
    private bool _airplay2;
    private double? _pendingVolumePercent;

    public AirPlaySessionState State { get; private set; } = AirPlaySessionState.Idle;
    public AirPlayDevice? Device { get; private set; }

    /// <summary>Fired when the device needs its on-screen PIN typed in; call <see cref="SubmitPin"/> with the code.</summary>
    public event Action<string>? PinRequired;
    public event Action? Disconnected;

    /// <param name="audioSourceFactory">Defaults to real WASAPI loopback capture; tests substitute a deterministic fake here.</param>
    public AirPlaySession(CredentialStore? credentialStore = null, PairingIdentity? identity = null, Func<IAudioCaptureSource>? audioSourceFactory = null)
    {
        _credentials = credentialStore ?? new CredentialStore();
        _identity = identity ?? LoadOrCreateIdentity(_credentials);
        _audioSourceFactory = audioSourceFactory ?? (() => new WasapiLoopbackSource());
    }

    private static PairingIdentity LoadOrCreateIdentity(CredentialStore store)
    {
        StoredCredentials? existing = store.Get("__sender_identity__");
        if (existing is not null) return PairingIdentity.FromStorage(Convert.ToHexString(existing.LtSeed), System.Text.Encoding.UTF8.GetString(existing.PairingId));

        PairingIdentity fresh = PairingIdentity.CreateNew();
        store.Set("__sender_identity__", new StoredCredentials
        {
            LtSeed = fresh.Seed32,
            PairingId = fresh.PairingId,
            AccessoryId = [],
            AccessoryLtpk = [],
        });
        return fresh;
    }

    public async Task ConnectAsync(AirPlayDevice device, CancellationToken ct = default)
    {
        if (State != AirPlaySessionState.Idle) throw new InvalidOperationException("Already connected — call DisposeAsync first");
        Device = device;
        _airplay2 = device.IsAirPlay2;
        State = AirPlaySessionState.Connecting;

        var rnd = Random.Shared;
        _sessionId = (uint)rnd.Next();
        string dacpId = ((ulong)rnd.NextInt64()).ToString("X");
        uint activeRemote = (uint)rnd.Next();

        _rtsp = await RtspConnection.ConnectAsync(device.Host, device.Port, dacpId, activeRemote, ct).ConfigureAwait(false);

        State = AirPlaySessionState.Pairing;
        PairingResult? pairing = _airplay2 ? await RunPairingWithFallbackAsync(device, ct).ConfigureAwait(false) : null;

        State = AirPlaySessionState.Handshake;
        _audio = new RtpAudioTransport(device.Host, _sessionId, pairing?.AudioKey);

        if (_airplay2)
            await RunAirPlay2HandshakeAsync(device, pairing!, ct).ConfigureAwait(false);
        else
            await RunAirPlay1HandshakeAsync(ct).ConfigureAwait(false);

        await StartStreamingAsync(ct).ConfigureAwait(false);
        State = AirPlaySessionState.Streaming;
    }

    // ── pairing, with the receiver-driven fallbacks real devices need ────

    private async Task<PairingResult> RunPairingWithFallbackAsync(AirPlayDevice device, CancellationToken ct)
    {
        AirPlayAuthMethod method = device.DetermineAuthMethod();
        StoredCredentials? stored = method == AirPlayAuthMethod.HapPin ? _credentials.Get(device.DeviceId) : null;

        if (method == AirPlayAuthMethod.HapTransient)
        {
            try
            {
                return await PairSetupClient.RunTransientAsync(_rtsp!, ct).ConfigureAwait(false);
            }
            catch (PairingRejectedException ex) when (ex.HttpStatusCode == 470)
            {
                // Receiver refuses pin-less pairing outright — fall back to full on-screen-PIN pairing.
                return await RunFullPinPairingAsync(device, ct).ConfigureAwait(false);
            }
        }

        if (method == AirPlayAuthMethod.HapPin)
        {
            if (stored is not null)
            {
                try
                {
                    return await PairVerifyClient.RunAsync(_rtsp!, _identity, stored, ct).ConfigureAwait(false);
                }
                catch (PairingProtocolException)
                {
                    _credentials.Remove(device.DeviceId); // stale/rejected — forget and re-pair from scratch
                }
            }
            try
            {
                return await RunFullPinPairingAsync(device, ct).ConfigureAwait(false);
            }
            catch (PairingRejectedException ex) when (ex.HttpStatusCode == 403)
            {
                // A macOS-style receiver 403s /pair-pin-start (it never shows an on-screen code); try pin-less transient once.
                return await PairSetupClient.RunTransientAsync(_rtsp!, ct).ConfigureAwait(false);
            }
        }

        throw new NotSupportedException(
            $"'{device.Name}' uses an authentication method this app doesn't support yet ({method}). " +
            "AirPlay 2 speakers (HomePod, most modern speakers) and Apple TV both work; older " +
            "AirPort-Express-style (MFi-SAP) or password-protected devices are not implemented yet.");
    }

    private async Task<PairingResult> RunFullPinPairingAsync(AirPlayDevice device, CancellationToken ct)
    {
        StoredCredentials creds = await PairSetupClient.RunPinAsync(_rtsp!, _identity, () => RequestPinAsync(device.Name), ct).ConfigureAwait(false);
        _credentials.Set(device.DeviceId, creds);
        return await PairVerifyClient.RunAsync(_rtsp!, _identity, creds, ct).ConfigureAwait(false);
    }

    private Task<string> RequestPinAsync(string deviceName)
    {
        _pinTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        PinRequired?.Invoke(deviceName);
        return _pinTcs.Task;
    }

    /// <summary>Call once the UI has collected the 4-digit code shown on the receiver, in response to <see cref="PinRequired"/>.</summary>
    public void SubmitPin(string pin) => _pinTcs?.TrySetResult(pin);

    // ── AirPlay 2 realtime handshake ──────────────────────────────────

    private async Task RunAirPlay2HandshakeAsync(AirPlayDevice device, PairingResult pairing, CancellationToken ct)
    {
        _rtsp!.EnableEncryption(pairing.ControlWriteKey, pairing.ControlReadKey);

        await _rtsp.SendAp2RtspAsync("GET", "/info", ct: ct).ConfigureAwait(false); // capability plist — not needed for realtime audio

        string uri = _rtsp.BuildRtspUri(_sessionId);

        RtspResponse sessionResp = await _rtsp.SendAp2RtspAsync("SETUP", uri, "application/x-apple-binary-plist", BuildSessionSetupPlist(), ct: ct).ConfigureAwait(false);
        if (!sessionResp.IsSuccess) throw new IOException($"AirPlay 2 session SETUP failed (HTTP {sessionResp.StatusCode})");
        PlistValue? sessionReply = BinaryPlist.Decode(sessionResp.Body);
        int eventPort = (int)(sessionReply?.Find("eventPort")?.AsInt() ?? 0);

        // A modern receiver requires the event channel OPEN before it accepts RECORD.
        if (eventPort != 0)
            _eventChannel = await AirPlayEventChannel.ConnectAsync(device.Host, eventPort, pairing.EventReadKey, pairing.EventWriteKey, ct).ConfigureAwait(false);

        // RECORD between the two SETUPs (owntone/reference order). Some receivers reject this on the
        // encrypted realtime channel (they want PTP timing) — non-fatal, the FLUSH/sync timeline still drives playback.
        await _rtsp.SendAp2RtspAsync("RECORD", uri, ct: ct).ConfigureAwait(false);

        RtspResponse streamResp = await _rtsp.SendAp2RtspAsync("SETUP", uri, "application/x-apple-binary-plist", BuildStreamSetupPlist(pairing.AudioKey), isStreamSetup: true, ct: ct).ConfigureAwait(false);
        if (!streamResp.IsSuccess) throw new IOException($"AirPlay 2 stream SETUP failed (HTTP {streamResp.StatusCode})");
        PlistValue? streamReply = BinaryPlist.Decode(streamResp.Body);
        PlistValue? stream0 = streamReply?.Find("streams")?.ArrayValue.FirstOrDefault();
        int dataPort = (int)(stream0?.Find("dataPort")?.AsInt() ?? 0);
        int controlPort = (int)(stream0?.Find("controlPort")?.AsInt() ?? 0);
        if (dataPort == 0) throw new IOException("AirPlay 2 stream SETUP returned no data port");

        _audio!.SetRemotePorts(dataPort, controlPort == 0 ? dataPort : controlPort);
    }

    private byte[] BuildSessionSetupPlist()
    {
        string mac = LocalMacAddressOrPlaceholder();
        PlistValue root = new PlistDictBuilder()
            .Add("deviceID", mac)
            .Add("sessionUUID", Guid.NewGuid().ToString("D").ToUpperInvariant())
            .Add("timingPort", _audio!.LocalTimingPort)
            .Add("timingProtocol", "NTP")
            .Add("isMultiSelectAirPlay", true)
            .Add("groupContainsGroupLeader", false)
            .Add("macAddress", mac)
            // The model/OS strings below identify us to the receiver as an
            // Apple client. This isn't spoofing for deception — every
            // third-party AirPlay sender (pyatv included) does the same,
            // because some receivers gate AirPlay-2 features on a
            // recognized Apple model string. X-Apple-Client-Name (a
            // separate header, see RtspConnection) is where this app
            // actually identifies itself.
            .Add("model", "iPhone14,3")
            .Add("name", ClientName)
            .Add("osBuildVersion", "20F66")
            .Add("osName", "iPhone OS")
            .Add("osVersion", "16.5")
            .Add("senderSupportsRelay", false)
            .Add("sourceVersion", "690.7.1")
            .Add("statsCollectionEnabled", false)
            .Build();
        return BinaryPlist.Encode(root);
    }

    private byte[] BuildStreamSetupPlist(byte[] audioKey)
    {
        PlistValue stream = new PlistDictBuilder()
            .Add("audioFormat", 0x40000L) // ALAC/44100/16/2
            .Add("audioMode", "default")
            .Add("controlPort", _audio!.LocalControlPort)
            .Add("ct", 2L) // ALAC
            .Add("isMedia", true)
            .Add("latencyMax", 88200L)
            .Add("latencyMin", 11025L)
            .Add("shk", audioKey)
            .Add("spf", (long)RtpAudioTransport.FramesPerPacket)
            .Add("sr", (long)RtpAudioTransport.SampleRate)
            .Add("type", 0x60L) // realtime
            .Add("supportsDynamicStreamID", false)
            .Add("streamConnectionID", (long)_sessionId)
            .Build();
        PlistValue root = new PlistDictBuilder().Add("streams", PlistValue.Array([stream])).Build();
        return BinaryPlist.Encode(root);
    }

    private static string LocalMacAddressOrPlaceholder()
    {
        try
        {
            NetworkInterface? nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
                .OrderByDescending(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                .FirstOrDefault();
            byte[]? bytes = nic?.GetPhysicalAddress().GetAddressBytes();
            if (bytes is { Length: 6 })
                return string.Join(':', bytes.Select(b => b.ToString("X2")));
        }
        catch { /* best-effort — fall through to the placeholder */ }
        return "AA:BB:CC:DD:EE:FF";
    }

    // ── AirPlay 1 (legacy, unencrypted RAOP) handshake ────────────────

    private async Task RunAirPlay1HandshakeAsync(CancellationToken ct)
    {
        await _rtsp!.SendRtspAsync("OPTIONS", "*", ct: ct).ConfigureAwait(false);

        string uri = _rtsp.BuildRtspUri(_sessionId);
        string sdp =
            $"v=0\r\no=iTunes {_sessionId} 0 IN IP4 {_rtsp.LocalAddress}\r\ns=iTunes\r\n" +
            $"c=IN IP4 {_rtsp.RemoteAddress}\r\nt=0 0\r\nm=audio 0 RTP/AVP 96\r\n" +
            "a=rtpmap:96 L16/44100/2\r\na=fmtp:96 352 0 16 40 10 14 2 255 0 0 44100\r\n";
        RtspResponse announceResp = await _rtsp.SendRtspAsync("ANNOUNCE", uri, "application/sdp", System.Text.Encoding.ASCII.GetBytes(sdp), ct: ct).ConfigureAwait(false);
        if (!announceResp.IsSuccess) throw new IOException($"ANNOUNCE was rejected (HTTP {announceResp.StatusCode})");

        string transport = $"RTP/AVP/UDP;unicast;interleaved=0-1;mode=record;control_port={_audio!.LocalControlPort};timing_port={_audio.LocalTimingPort}";
        RtspResponse setupResp = await _rtsp.SendRtspAsync("SETUP", uri, extraHeaders: [("Transport", transport)], ct: ct).ConfigureAwait(false);
        if (!setupResp.IsSuccess) throw new IOException($"SETUP was rejected (HTTP {setupResp.StatusCode})");

        int serverPort = 0, controlPort = 0;
        foreach (string part in (setupResp.Header("Transport") ?? "").Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = part[..eq].Trim();
            if (int.TryParse(part[(eq + 1)..].Trim(), out int val))
            {
                if (key == "server_port") serverPort = val;
                else if (key == "control_port") controlPort = val;
            }
        }
        if (serverPort == 0) throw new IOException("SETUP reply carried no server_port");
        _audio.SetRemotePorts(serverPort, controlPort == 0 ? serverPort : controlPort);

        string session = setupResp.Header("Session") ?? "1";
        string rtpInfo = $"seq=0;rtptime={_audio.CurrentRtpTimestamp}";
        RtspResponse recordResp = await _rtsp.SendRtspAsync("RECORD", uri,
            extraHeaders: [("Range", "npt=0-"), ("RTP-Info", rtpInfo), ("Session", session)], ct: ct).ConfigureAwait(false);
        if (!recordResp.IsSuccess) throw new IOException($"RECORD was rejected (HTTP {recordResp.StatusCode})");
    }

    // ── streaming ──────────────────────────────────────────────────────

    private async Task StartStreamingAsync(CancellationToken ct)
    {
        _source = _audioSourceFactory();
        _source.Start();

        _audio!.StartAncillaryLoops();
        _audio.StartPacing(_source);

        _sessionCts = new CancellationTokenSource();
        _feedbackLoop = Task.Run(() => FeedbackLoopAsync(_sessionCts.Token), CancellationToken.None);

        // AirPlay-2 receivers can otherwise sit at their own (possibly
        // muted) default volume — audible-by-default matches what a user
        // expects from "connect and it plays".
        if (_airplay2 && _pendingVolumePercent is null) await SetVolumeAsync(100, ct).ConfigureAwait(false);
        else if (_pendingVolumePercent is { } pct) await SetVolumeAsync(pct, ct).ConfigureAwait(false);
    }

    private async Task FeedbackLoopAsync(CancellationToken ct)
    {
        TimeSpan interval = _airplay2 ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(25);
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { await _rtsp!.SendFeedbackAsync(ct).ConfigureAwait(false); }
                catch when (!ct.IsCancellationRequested) { /* a missed keep-alive isn't fatal; the next tick tries again */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>0-100%; AirPlay maps 0% to its dedicated mute sentinel (-144dBFS), not to -30dBFS.</summary>
    public async Task SetVolumeAsync(double percent, CancellationToken ct = default)
    {
        percent = Math.Clamp(percent, 0, 100);
        _pendingVolumePercent = percent;
        if (_rtsp is null || State != AirPlaySessionState.Streaming) return;
        double dbfs = percent < 0.01 ? -144.0 : -30.0 + 0.3 * percent;
        string body = $"volume: {dbfs.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}";
        await _rtsp.SendRtspAsync("SET_PARAMETER", _rtsp.BuildRtspUri(_sessionId), "text/parameters", System.Text.Encoding.ASCII.GetBytes(body), ct: ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (State == AirPlaySessionState.Idle) return;
        State = AirPlaySessionState.Idle;

        if (_sessionCts is not null) await _sessionCts.CancelAsync().ConfigureAwait(false);
        if (_feedbackLoop is not null) { try { await _feedbackLoop.ConfigureAwait(false); } catch { } }

        try
        {
            if (_rtsp is not null)
                await _rtsp.SendRtspAsync("TEARDOWN", _rtsp.BuildRtspUri(_sessionId)).ConfigureAwait(false);
        }
        catch { /* best-effort — we're tearing down regardless */ }

        if (_source is not null) { _source.Stop(); await _source.DisposeAsync().ConfigureAwait(false); }
        if (_audio is not null) await _audio.DisposeAsync().ConfigureAwait(false);
        if (_eventChannel is not null) await _eventChannel.DisposeAsync().ConfigureAwait(false);
        if (_rtsp is not null) await _rtsp.DisposeAsync().ConfigureAwait(false);
        _sessionCts?.Dispose();

        Disconnected?.Invoke();
    }
}
