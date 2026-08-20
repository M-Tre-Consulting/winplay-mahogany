using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AirPlaySender.Core.Crypto;

namespace AirPlaySender.Core.Audio;

/// <summary>
/// The RAOP/AirPlay realtime audio transport: three UDP sockets (audio,
/// control, timing), a token-bucket pacer that keeps frames-on-the-wire
/// tracking wall clock at 44100/s, 1Hz NTP sync packets, a timing-request
/// responder, and a retransmit backlog the receiver can pull lost packets
/// from. Works for both AirPlay 1 (unencrypted big-endian L16) and
/// AirPlay 2 (ChaCha20-Poly1305-encrypted uncompressed-ALAC) — pass an
/// audio key to get the latter, omit it for the former.
/// </summary>
public sealed class RtpAudioTransport : IAsyncDisposable
{
    public const int FramesPerPacket = 352; // RAOP fixed
    public const uint SampleRate = 44100;    // RAOP fixed
    private const int Channels = 2;
    private const int BacklogSize = 1024;    // power of two
    private const int MaxPacketsPerTick = 16; // catch-up cap after a stall
    private static readonly TimeSpan PacerInterval = TimeSpan.FromMilliseconds(8);
    private const uint Latency = 22050 + 44100; // fixed RAOP latency (matches pyatv/reference)

    private readonly UdpClient _audioSock = new(0);
    private readonly UdpClient _controlSock = new(0);
    private readonly UdpClient _timingSock = new(0);
    private readonly IPAddress _remoteHost;
    private readonly uint _ssrc;
    private readonly byte[]? _audioKey; // null => AirPlay 1, unencrypted

    private int _serverPort;
    private int _controlPort;
    private ushort _seq;
    private ulong _framesSent;
    private ulong _startTs;
    private bool _firstAudio = true;
    private ulong _audioNonce;

    private readonly byte[]?[] _backlog = new byte[BacklogSize][];
    private readonly int[] _backlogSeq = new int[BacklogSize];

    private readonly System.Diagnostics.Stopwatch _clock = new();
    private CancellationTokenSource? _cts;
    private Task? _controlLoop, _timingLoop, _pacerLoop;
    private IPcmFrameSource? _source;

    public int LocalControlPort => ((IPEndPoint)_controlSock.Client.LocalEndPoint!).Port;
    public int LocalTimingPort => ((IPEndPoint)_timingSock.Client.LocalEndPoint!).Port;

    public RtpAudioTransport(string remoteHost, uint ssrc, byte[]? audioKey)
    {
        _remoteHost = IPAddress.Parse(remoteHost);
        _ssrc = ssrc;
        _audioKey = audioKey;
        _seq = (ushort)Random.Shared.Next(0, 65536);
        Array.Fill(_backlogSeq, -1);
    }

    /// <summary>The ports the receiver returned from SETUP (AirPlay 1's Transport header, or AirPlay 2's stream-SETUP plist).</summary>
    public void SetRemotePorts(int serverPort, int controlPort) => (_serverPort, _controlPort) = (serverPort, controlPort == 0 ? serverPort : controlPort);

    public uint CurrentRtpTimestamp => (uint)(Latency + _framesSent);

    /// <summary>Starts the sync (1Hz), timing-responder, and retransmit-responder background loops, and sends the first (marker-bit) sync packet.</summary>
    /// <summary>
    /// Starts the control (retransmit) and timing (NTP) UDP responder loops
    /// only — no clock anchor, no sync packet yet. Call this BEFORE
    /// announcing "timingPort"/"timingProtocol: NTP" in the AirPlay-2
    /// session SETUP request, not after: confirmed against a real HomePod
    /// that if nothing is listening on the timing port yet when the
    /// receiver tries to time-sync as part of accepting that SETUP, the
    /// receiver just never replies — SETUP itself hangs, not merely
    /// timing sync. <see cref="AnchorStreamClock"/> is the separate,
    /// later call that actually starts the streaming timeline.
    /// </summary>
    public void StartResponders()
    {
        _cts ??= new CancellationTokenSource();
        _controlLoop = Task.Run(() => ControlLoopAsync(_cts.Token));
        _timingLoop = Task.Run(() => TimingLoopAsync(_cts.Token));
    }

    /// <summary>Anchors the RTP timeline to wall-clock NTP and sends the first (marker-bit) sync packet. Call once, right before <see cref="StartPacing"/>.</summary>
    public void AnchorStreamClock()
    {
        _startTs = Ntp.ToRtpTimestamp(Ntp.Now(), SampleRate);
        _framesSent = 0;
        _firstAudio = true;
        _clock.Restart();
        SendSyncPacket(first: true);
    }

    /// <summary>Starts the ~8ms pacer that pulls frames from <paramref name="source"/> and puts them on the wire in real time.</summary>
    public void StartPacing(IPcmFrameSource source)
    {
        _source = source;
        _cts ??= new CancellationTokenSource();
        _pacerLoop = Task.Run(() => PacerLoopAsync(_cts.Token));
    }

    private async Task PacerLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PacerInterval);
        // Reused every tick — a heap array, not stackalloc: its lifetime must
        // span the `await` in the outer loop, which a stack-allocated Span cannot do.
        var frameBuf = new short[FramesPerPacket * Channels];
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                ulong targetFrames = (ulong)(_clock.Elapsed.TotalSeconds * SampleRate);
                int sentThisTick = 0;
                while (_framesSent + FramesPerPacket <= targetFrames && sentThisTick < MaxPacketsPerTick)
                {
                    Array.Clear(frameBuf);
                    _source!.FillFrames(frameBuf);
                    SendAudioPacket(frameBuf);
                    sentThisTick++;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void SendSyncPacket(bool first)
    {
        if (_serverPort == 0) return;
        ulong curNtp = Ntp.FromRtpTimestamp(_startTs + _framesSent, SampleRate);
        uint now = CurrentRtpTimestamp;

        Span<byte> pkt = stackalloc byte[20];
        pkt[0] = first ? (byte)0x90 : (byte)0x80;
        pkt[1] = 0xD4; // type 0x54 | 0x80
        BinaryPrimitives.WriteUInt16BigEndian(pkt[2..], 0x0007);
        BinaryPrimitives.WriteUInt32BigEndian(pkt[4..], now - Latency);
        BinaryPrimitives.WriteUInt32BigEndian(pkt[8..], (uint)(curNtp >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(pkt[12..], (uint)(curNtp & 0xFFFFFFFFUL));
        BinaryPrimitives.WriteUInt32BigEndian(pkt[16..], now);
        _controlSock.Send(pkt, new IPEndPoint(_remoteHost, _controlPort));
    }

    public void SendAudioPacket(ReadOnlySpan<short> interleavedSamples)
    {
        if (_serverPort == 0) return;

        Span<byte> header = stackalloc byte[12];
        header[0] = 0x80;
        header[1] = _firstAudio ? (byte)0xE0 : (byte)0x60; // marker bit on the first packet
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], _seq);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], CurrentRtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], _ssrc);

        // Raw big-endian L16 PCM either way (RFC 3551 payload type 96) — encrypted
        // when paired (AirPlay 2), plaintext when not (AirPlay 1). Verified against
        // a real HomePod (gen 2): asking for ALAC (ct=2/audioFormat=0x40000 in the
        // stream SETUP plist — the older akustikrausch/airplay2-sender-cpp recipe's
        // "type 0x60 realtime is hardcoded ALAC" claim, itself sourced from
        // shairport-sync) got the stream SETUP itself rejected outright (HTTP 400)
        // on this device. Raw PCM with ct=1/audioFormat=0x800 (see
        // BuildStreamSetupPlist) is what pyatv sends successfully to the same
        // HomePod, byte-for-byte confirmed on the wire — so that's what this
        // project matches now rather than the ALAC path.
        byte[] payload = EncodeRawL16(interleavedSamples);
        byte[] wirePayload = _audioKey is not null ? EncryptPayload(header, payload) : payload;

        var pkt = new byte[12 + wirePayload.Length];
        header.CopyTo(pkt);
        wirePayload.CopyTo(pkt, 12);
        _audioSock.Send(pkt, new IPEndPoint(_remoteHost, _serverPort));

        int slot = _seq & (BacklogSize - 1);
        _backlog[slot] = pkt;
        _backlogSeq[slot] = _seq;

        _firstAudio = false;
        _seq++;
        _framesSent += FramesPerPacket;
    }

    private static byte[] EncodeRawL16(ReadOnlySpan<short> interleavedSamples)
    {
        var payload = new byte[FramesPerPacket * Channels * 2];
        for (int i = 0; i < FramesPerPacket * Channels; i++)
            BinaryPrimitives.WriteInt16BigEndian(payload.AsSpan(i * 2), interleavedSamples[i]);
        return payload;
    }

    /// <summary>AAD = RTP header bytes [4..12) (timestamp+SSRC); nonce = the 8-byte little-endian audio-packet counter, appended AFTER the ciphertext+tag on the wire.</summary>
    private byte[] EncryptPayload(ReadOnlySpan<byte> rtpHeader, byte[] payload)
    {
        byte[] nonce = ChaCha20Poly1305Cipher.CounterNonce8(_audioNonce++);
        byte[] aad = rtpHeader[4..12].ToArray();
        byte[] ciphertext = ChaCha20Poly1305Cipher.Encrypt(_audioKey!, nonce, payload, aad);
        var wire = new byte[ciphertext.Length + 8];
        ciphertext.CopyTo(wire, 0);
        nonce.CopyTo(wire, ciphertext.Length);
        return wire;
    }

    private async Task ControlLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result = await _controlSock.ReceiveAsync(ct).ConfigureAwait(false);
                byte[] dg = result.Buffer;
                if (dg.Length < 8) continue;
                byte type = (byte)(dg[1] & 0x7F);
                if (type != 0x55) continue; // only retransmit requests expected here

                ushort lostSeq = BinaryPrimitives.ReadUInt16BigEndian(dg.AsSpan(4));
                ushort lostCount = BinaryPrimitives.ReadUInt16BigEndian(dg.AsSpan(6));
                for (int i = 0; i < lostCount; i++)
                {
                    ushort s = (ushort)(lostSeq + i);
                    int slot = s & (BacklogSize - 1);
                    if (_backlogSeq[slot] != s || _backlog[slot] is null) continue; // aged out of the backlog

                    byte[] original = _backlog[slot]!;
                    var resp = new byte[4 + original.Length];
                    resp[0] = 0x80;
                    resp[1] = 0xD6;
                    BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2), s);
                    original.CopyTo(resp, 4);
                    await _controlSock.SendAsync(resp, result.RemoteEndPoint, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task TimingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result = await _timingSock.ReceiveAsync(ct).ConfigureAwait(false);
                byte[] dg = result.Buffer;
                if (dg.Length < 32) continue; // TimingPacket is 32 bytes

                ulong now = Ntp.Now();
                var resp = new byte[32];
                resp[0] = dg[0]; // proto byte echoed
                resp[1] = 0xD3;  // type 0x53 | 0x80
                BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2), 0x0007);
                // bytes [4..8) padding stay zero
                dg.AsSpan(24, 8).CopyTo(resp.AsSpan(8)); // reftime = request sendtime
                BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(16), (uint)(now >> 32));   // recvtime
                BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(20), (uint)(now & 0xFFFFFFFFUL));
                BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(24), (uint)(now >> 32));   // sendtime
                BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(28), (uint)(now & 0xFFFFFFFFUL));
                await _timingSock.SendAsync(resp, result.RemoteEndPoint, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        foreach (Task? t in new[] { _controlLoop, _timingLoop, _pacerLoop })
            if (t is not null) { try { await t.ConfigureAwait(false); } catch { } }
        _audioSock.Dispose();
        _controlSock.Dispose();
        _timingSock.Dispose();
        _cts?.Dispose();
    }
}
