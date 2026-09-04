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
    private const string ClientName = "WinPlay Mahogany"; // the "name" the receiver shows for this sender

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
    private readonly LocalPlaybackMuter _muter = new();

    public AirPlaySessionState State { get; private set; } = AirPlaySessionState.Idle;
    public AirPlayDevice? Device { get; private set; }

    /// <summary>Fired when the device needs its on-screen PIN typed in; call <see cref="SubmitPin"/> with the code.</summary>
    public event Action<string>? PinRequired;
    public event Action? Disconnected;
    /// <summary>Fires one short line per handshake step (e.g. "SETUP session -> HTTP 200 (140 ms)"). Not localized; for diagnostics/log windows, not end-user UI copy.</summary>
    public event Action<string>? Diagnostics;

    private void Trace(string message) => Diagnostics?.Invoke(message);

    /// <param name="audioSourceFactory">Defaults to real WASAPI loopback capture; tests substitute a deterministic fake here.</param>
    public AirPlaySession(CredentialStore? credentialStore = null, PairingIdentity? identity = null, Func<IAudioCaptureSource>? audioSourceFactory = null)
    {
        _credentials = credentialStore ?? new CredentialStore();
        _identity = identity ?? PairingIdentity.LoadOrCreate(_credentials);
        _audioSourceFactory = audioSourceFactory ?? (() => new WasapiLoopbackSource());
    }

    public async Task ConnectAsync(AirPlayDevice device, CancellationToken ct = default)
    {
        if (State != AirPlaySessionState.Idle) throw new InvalidOperationException("Already connected — call DisposeAsync first");
        Device = device;
        _airplay2 = device.IsAirPlay2;
        State = AirPlaySessionState.Connecting;

        try
        {
            var rnd = Random.Shared;
            _sessionId = (uint)rnd.Next();
            string dacpId = ((ulong)rnd.NextInt64()).ToString("X");
            uint activeRemote = (uint)rnd.Next();

            _rtsp = await RtspConnection.ConnectAsync(device.Host, device.Port, dacpId, activeRemote, ct).ConfigureAwait(false);
            Trace($"TCP connesso a {device.Host}:{device.Port}, sessionId={_sessionId:X8} auth={(_airplay2 ? device.DetermineAuthMethod().ToString() : "nessuno (AirPlay 1)")}");

            State = AirPlaySessionState.Pairing;
            PairingResult? pairing = _airplay2 ? await RunPairingWithFallbackAsync(device, ct).ConfigureAwait(false) : null;
            if (pairing is not null) Trace($"Pairing completato, sharedSecret={pairing.SharedSecret.Length} byte");

            State = AirPlaySessionState.Handshake;
            _audio = new RtpAudioTransport(device.Host, _sessionId, pairing?.AudioKey);
            // Must be listening before the session SETUP announces our timingPort — see StartResponders' doc comment.
            _audio.StartResponders();

            if (_airplay2)
                await RunAirPlay2HandshakeAsync(device, pairing!, ct).ConfigureAwait(false);
            else
                await RunAirPlay1HandshakeAsync(ct).ConfigureAwait(false);

            await StartStreamingAsync(ct).ConfigureAwait(false);
            State = AirPlaySessionState.Streaming;
            Trace("Streaming avviato");
        }
        catch
        {
            // Found by code review: a failed connect attempt (rejected
            // pairing, a receiver that 400s the handshake, a network hiccup —
            // all routine, not rare) used to leave State stuck at whatever
            // intermediate stage it failed on, never back at Idle — and
            // MainWindow's own catch block just drops the AirPlaySession
            // reference on a failed ConnectAsync without ever calling
            // DisposeAsync. Together that leaked every socket/background loop
            // this got through starting (the RTSP TCP connection,
            // RtpAudioTransport's three UDP sockets plus its responder
            // loops) forever, one more leak per failed attempt. DisposeAsync
            // already tears all of that down defensively (every field it
            // touches is null-checked) and resets State to Idle — just route
            // through it here too, so no caller has to remember to.
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
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
        Trace("Canale di controllo cifrato armato");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        RtspResponse infoResp = await _rtsp.SendAp2RtspAsync("GET", "/info", ct: ct).ConfigureAwait(false); // capability plist — not needed for realtime audio
        Trace($"GET /info -> HTTP {infoResp.StatusCode} ({sw.ElapsedMilliseconds} ms, {infoResp.Body.Length} byte)");

        string uri = _rtsp.BuildRtspUri(_sessionId);
        Trace($"URI di sessione: {uri}");

        sw.Restart();
        RtspResponse sessionResp = await _rtsp.SendAp2RtspAsync("SETUP", uri, "application/x-apple-binary-plist", BuildSessionSetupPlist(), ct: ct).ConfigureAwait(false);
        Trace($"SETUP sessione -> HTTP {sessionResp.StatusCode} ({sw.ElapsedMilliseconds} ms, {sessionResp.Body.Length} byte)");
        if (!sessionResp.IsSuccess) throw new IOException($"AirPlay 2 session SETUP failed (HTTP {sessionResp.StatusCode}): {DescribeBody(sessionResp.Body)}");
        PlistValue? sessionReply = BinaryPlist.Decode(sessionResp.Body);
        int eventPort = (int)(sessionReply?.Find("eventPort")?.AsInt() ?? 0);
        Trace($"eventPort={eventPort}");

        // A modern receiver requires the event channel OPEN before it accepts RECORD.
        if (eventPort != 0)
        {
            _eventChannel = await AirPlayEventChannel.ConnectAsync(device.Host, eventPort, pairing.EventReadKey, pairing.EventWriteKey, ct).ConfigureAwait(false);
            Trace("Canale eventi aperto");
        }

        // RECORD between the two SETUPs (owntone/reference order). Some receivers reject this on the
        // encrypted realtime channel (they want PTP timing) — non-fatal, the FLUSH/sync timeline still drives playback.
        sw.Restart();
        RtspResponse recordResp = await _rtsp.SendAp2RtspAsync("RECORD", uri, ct: ct).ConfigureAwait(false);
        Trace($"RECORD -> HTTP {recordResp.StatusCode} ({sw.ElapsedMilliseconds} ms)");

        sw.Restart();
        RtspResponse streamResp = await _rtsp.SendAp2RtspAsync("SETUP", uri, "application/x-apple-binary-plist", BuildStreamSetupPlist(pairing.AudioKey), isStreamSetup: true, ct: ct).ConfigureAwait(false);
        Trace($"SETUP stream -> HTTP {streamResp.StatusCode} ({sw.ElapsedMilliseconds} ms, {streamResp.Body.Length} byte)");
        if (!streamResp.IsSuccess) throw new IOException($"AirPlay 2 stream SETUP failed (HTTP {streamResp.StatusCode}): {DescribeBody(streamResp.Body)}");
        PlistValue? streamReply = BinaryPlist.Decode(streamResp.Body);
        PlistValue? stream0 = streamReply?.Find("streams")?.ArrayValue.FirstOrDefault();
        int dataPort = (int)(stream0?.Find("dataPort")?.AsInt() ?? 0);
        int controlPort = (int)(stream0?.Find("controlPort")?.AsInt() ?? 0);
        if (dataPort == 0) throw new IOException("AirPlay 2 stream SETUP returned no data port");
        Trace($"dataPort={dataPort} controlPort={controlPort}");

        _audio!.SetRemotePorts(dataPort, controlPort == 0 ? dataPort : controlPort);
    }

    /// <summary>Best-effort human-readable dump of an error response body — a plist if it decodes as one, else UTF-8 text, else hex — so a rejection is diagnosable without a debugger.</summary>
    private static string DescribeBody(byte[] body)
    {
        if (body.Length == 0) return "(corpo vuoto)";
        PlistValue? plist = BinaryPlist.Decode(body);
        if (plist is not null) return DescribePlist(plist);
        try
        {
            string text = System.Text.Encoding.UTF8.GetString(body);
            if (text.All(c => !char.IsControl(c) || c is '\r' or '\n' or '\t')) return text;
        }
        catch { /* not valid UTF-8 — fall through to hex */ }
        return "hex: " + Convert.ToHexString(body[..Math.Min(body.Length, 256)]);
    }

    private static string DescribePlist(PlistValue value) => value.Type switch
    {
        PlistValue.Kind.Dict => "{" + string.Join(", ", value.DictValue.Select(kv => $"{kv.Key}={DescribePlist(kv.Value)}")) + "}",
        PlistValue.Kind.Array => "[" + string.Join(", ", value.ArrayValue.Select(DescribePlist)) + "]",
        PlistValue.Kind.Str => $"\"{value.StrValue}\"",
        PlistValue.Kind.Int => value.IntValue.ToString(),
        PlistValue.Kind.Bool => value.BoolValue.ToString(),
        PlistValue.Kind.Data => $"<{value.DataValue.Length} bytes>",
        _ => value.Type.ToString(),
    };

    /// <summary>
    /// "isRemoteControlOnly": true is for a REMOTE-CONTROL session
    /// (control/command messages only — documented in pyatv's own
    /// protocols.md under "AirPlay 2 / Remote Control"), not for audio.
    /// This dict — no "isRemoteControlOnly", a real "timingPort", and
    /// "timingProtocol": "NTP" — is pyatv's actual audio-streaming session
    /// SETUP, confirmed byte-for-byte off the wire from a real pyatv run
    /// against this project's test HomePod.
    ///
    /// Getting this exact dict accepted took a while to track down: the
    /// receiver doesn't just record "timingPort" — the moment this SETUP
    /// names an NTP timing port, it tries to time-sync against that port
    /// as part of deciding how to answer, and if nothing is listening yet
    /// it just never replies (SETUP itself hangs, no error). The actual
    /// fix was ordering — <see cref="RtpAudioTransport.StartResponders"/>
    /// must run, and be listening on that port, BEFORE this request goes
    /// out. It is NOT any of: X-Apple-Client-Name (an extra header this
    /// project used to send — removing it made no difference),
    /// isMultiSelectAirPlay's value, or a firewall rule (all real things
    /// tried and ruled out on real hardware first).
    /// </summary>
    private byte[] BuildSessionSetupPlist()
    {
        string mac = LocalMacAddressOrPlaceholder();
        PlistValue root = new PlistDictBuilder()
            .Add("deviceID", mac)
            .Add("groupContainsGroupLeader", false)
            .Add("isMultiSelectAirPlay", true)
            .Add("macAddress", mac)
            // The model/OS strings below identify us to the receiver as an
            // Apple client. This isn't spoofing for deception — every
            // third-party AirPlay sender (pyatv included) does the same,
            // because some receivers gate AirPlay-2 features on a
            // recognized Apple model string.
            .Add("model", "iPhone14,3")
            .Add("name", ClientName)
            .Add("osBuildVersion", "20F66")
            .Add("osName", "iPhone OS")
            .Add("osVersion", "16.5")
            .Add("senderSupportsRelay", false)
            .Add("sessionUUID", Guid.NewGuid().ToString("D").ToUpperInvariant())
            .Add("sourceVersion", "690.7.1")
            .Add("statsCollectionEnabled", false)
            .Add("timingPort", _audio!.LocalTimingPort)
            .Add("timingProtocol", "NTP")
            .Build();
        return BinaryPlist.Encode(root);
    }

    /// <summary>audioFormat/ct ask for raw Linear PCM, not ALAC — see the comment on <see cref="RtpAudioTransport.SendAudioPacket"/> for why (confirmed on the wire against a real HomePod).</summary>
    private byte[] BuildStreamSetupPlist(byte[] audioKey)
    {
        PlistValue stream = new PlistDictBuilder()
            .Add("audioFormat", 0x800L) // Linear PCM/44100/16/2
            .Add("audioMode", "default")
            .Add("controlPort", _audio!.LocalControlPort)
            .Add("ct", 1L) // Raw PCM
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

        _audio!.AnchorStreamClock();
        _audio.StartPacing(_source);

        _sessionCts = new CancellationTokenSource();
        _feedbackLoop = Task.Run(() => FeedbackLoopAsync(_sessionCts.Token), CancellationToken.None);

        if (MuteLocalPlayback) _muter.Mute();

        // A receiver can otherwise sit at its own (possibly muted) default
        // volume — audible-by-default matches what a user expects from
        // "connect and it plays". Found by code review: this used to only
        // apply `if (_airplay2)`, leaving AirPlay 1 receivers (older
        // AirPort-Express-style devices) with exactly the silent-by-default
        // problem this was written to avoid in the first place — SET_PARAMETER
        // volume is equally valid RAOP/AirPlay 1, no reason to special-case it.
        if (_pendingVolumePercent is null) await SetVolumeAsync(100, ct).ConfigureAwait(false);
        else await SetVolumeAsync(_pendingVolumePercent.Value, ct).ConfigureAwait(false);
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

    /// <summary>
    /// True once the local PC output has been muted so only the AirPlay
    /// target plays this session's audio ("solo sul dispositivo" in the UI)
    /// — false (the default) leaves it duplicated on both, matching the
    /// behavior every version of this app had before this toggle existed.
    /// </summary>
    public bool MuteLocalPlayback { get; private set; }

    /// <summary>
    /// Call any time — before <see cref="ConnectAsync"/> (applied once
    /// streaming starts) or live, mid-session (applied immediately). See
    /// <see cref="LocalPlaybackMuter"/> for why this doesn't also silence
    /// what reaches the AirPlay target.
    /// </summary>
    public void SetMuteLocalPlayback(bool mute)
    {
        MuteLocalPlayback = mute;
        if (State != AirPlaySessionState.Streaming) return; // StartStreamingAsync applies it once streaming begins
        if (mute) _muter.Mute(); else _muter.Restore();
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

        _muter.Dispose(); // Dispose() calls Restore() internally, then releases the COM device handle
        if (_source is not null) { _source.Stop(); await _source.DisposeAsync().ConfigureAwait(false); }
        if (_audio is not null) await _audio.DisposeAsync().ConfigureAwait(false);
        if (_eventChannel is not null) await _eventChannel.DisposeAsync().ConfigureAwait(false);
        if (_rtsp is not null) await _rtsp.DisposeAsync().ConfigureAwait(false);
        _sessionCts?.Dispose();

        Disconnected?.Invoke();
    }
}
