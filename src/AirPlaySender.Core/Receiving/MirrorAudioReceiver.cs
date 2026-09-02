using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace AirPlaySender.Core.Receiving;

/// <summary>
/// The UDP side of a mirroring session's audio: an RTP data socket the
/// iPhone streams AAC-ELD to, plus a control socket. Each RTP packet's
/// payload is AES-128-CBC encrypted (key = the same FairPlay session key
/// the video uses, IV = the session <c>eiv</c>, reset per packet, only the
/// whole 16-byte blocks — trailing bytes are plaintext); this class
/// decrypts, reorders by RTP sequence number (dropping the redundant
/// re-sends iOS sends with <c>redundantAudio</c>), and hands each raw
/// AAC-ELD frame to <see cref="AudioFrameReceived"/> for a decoder to turn
/// into PCM.
///
/// Wire format from UxPlay's <c>lib/raop_buffer.c</c>
/// (<c>raop_buffer_decrypt</c>, 12-byte RTP header, <c>seqnum</c> at bytes
/// 2-3 big-endian) and <c>renderers/audio_renderer.c</c> (AAC-ELD 44100/2,
/// spf 480, first byte of a real frame is <c>0x8c</c>/<c>0x8d</c>/<c>0x8e</c>)
/// — see the doc comment on <see cref="AirPlayMirroringAdvertiser"/> for
/// this project's attribution convention.
/// </summary>
public sealed class MirrorAudioReceiver : IAsyncDisposable
{
    private readonly UdpClient _data;
    private readonly UdpClient _control;
    private readonly Aes _aes;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public int DataPort { get; }
    public int ControlPort { get; }

    public event Action<string>? Diagnostics;

    /// <summary>Fires once per audio frame, in playback order — a raw AAC-ELD access unit (44100 Hz, stereo, 480 samples), no container, ready for the decoder.</summary>
    public event Action<byte[]>? AudioFrameReceived;

    /// <summary>Fires once when the audio receiver stops (peer gone, error, cancellation).</summary>
    public event Action? SessionEnded;

    /// <summary>Fires whenever the client changes the AirPlay volume — carries a linear playback gain in [0, 1] (0 = muted, 1 = full).</summary>
    public event Action<double>? VolumeGainChanged;

    /// <summary>Current linear gain from the last AirPlay volume the client sent — 1.0 until it says otherwise.</summary>
    public double VolumeGain { get; private set; } = 1.0;

    /// <summary>
    /// Applies an AirPlay volume value (dB-ish: 0.0 = loudest, about -30.0 = quietest,
    /// -144.0 = muted) as a linear gain, matching UxPlay's <c>raop_set_volume</c>
    /// (<c>10^(volume/20)</c>). Volume comes over RTSP as <c>SET_PARAMETER</c>
    /// <c>text/parameters</c> "volume: &lt;float&gt;" — see <see cref="AirPlayReceiverServer"/>.
    /// </summary>
    public void SetAirplayVolume(float airplayVolumeDb)
    {
        double gain = airplayVolumeDb <= -144f ? 0.0
                    : airplayVolumeDb >= 0f ? 1.0
                    : Math.Pow(10.0, airplayVolumeDb / 20.0);
        VolumeGain = gain;
        Trace($"volume: {airplayVolumeDb:F2} dB -> gain {gain:F4}");
        VolumeGainChanged?.Invoke(gain);
    }

    public MirrorAudioReceiver(byte[] key16, byte[] iv16)
    {
        _aes = Aes.Create();
        _aes.Key = key16;
        _aes.IV = iv16;                 // not mutated by CreateDecryptor() — each packet starts here
        _aes.Mode = CipherMode.CBC;
        _aes.Padding = PaddingMode.None;

        _data = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        _control = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        DataPort = ((IPEndPoint)_data.Client.LocalEndPoint!).Port;
        ControlPort = ((IPEndPoint)_control.Client.LocalEndPoint!).Port;
    }

    public void Start() => _loop = ReceiveLoopAsync(_cts.Token);

    // --- RTP reorder / redundancy dedup -------------------------------------------
    private readonly SortedDictionary<ushort, byte[]> _pending = new();
    private bool _haveSeq;
    private ushort _nextSeq;

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        Trace($"in ascolto audio: dataPort={DataPort} controlPort={ControlPort}");
        int count = 0, big = 0;
        int minLen = int.MaxValue, maxLen = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r = await _data.ReceiveAsync(ct).ConfigureAwait(false);
                byte[] pkt = r.Buffer;
                if (pkt.Length < 12 || (pkt[0] & 0xC0) != 0x80) continue; // not RTP v2

                byte pt = (byte)(pkt[1] & 0x7F);
                ushort seq = (ushort)((pkt[2] << 8) | pkt[3]); // bytes 2-3, big-endian
                int payloadLen = pkt.Length - 12;
                if (payloadLen <= 0) continue;

                byte[] frame = Decrypt(pkt, payloadLen);
                count++;
                minLen = Math.Min(minLen, payloadLen);
                maxLen = Math.Max(maxLen, payloadLen);
                if (count <= 5)
                    Trace($"pacchetto #{count}: pt={pt} seq={seq}, {payloadLen}B, primi byte decifrati {Hex(frame, 8)} (grezzi {Hex(pkt.AsSpan(12), 8)})");
                else if (payloadLen > 16 && ++big <= 5)
                    Trace($"pacchetto 'grande' #{big}: pt={pt} seq={seq}, {payloadLen}B, primi byte decifrati {Hex(frame, 12)} (grezzi {Hex(pkt.AsSpan(12), 12)})");
                else if (count % 500 == 0)
                    Trace($"audio: {count} pacchetti, dimensione payload {minLen}..{maxLen}B");

                Reorder(seq, frame);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Trace($"errore canale audio: {ex.Message}"); }
        finally
        {
            Trace("canale audio terminato");
            SessionEnded?.Invoke();
        }
    }

    private byte[] Decrypt(byte[] pkt, int payloadLen)
    {
        int encLen = payloadLen & ~0xF; // whole 16-byte blocks only
        var outp = new byte[payloadLen];
        if (encLen > 0)
        {
            using ICryptoTransform dec = _aes.CreateDecryptor(); // starts from _aes.IV every time
            dec.TransformBlock(pkt, 12, encLen, outp, 0);
        }
        Array.Copy(pkt, 12 + encLen, outp, encLen, payloadLen - encLen); // trailing bytes are plaintext
        return outp;
    }

    private void Reorder(ushort seq, byte[] frame)
    {
        if (!_haveSeq)
        {
            _haveSeq = true;
            _nextSeq = seq;
        }

        short ahead = (short)(seq - _nextSeq); // signed → handles 16-bit wraparound
        if (ahead < 0) return;                 // already played, or a redundant re-send — drop

        if (ahead == 0)
        {
            Emit(frame);
            _nextSeq++;
            DrainPending();
            return;
        }

        _pending[seq] = frame;
        if (_pending.Count > 32)
        {
            // A packet is genuinely lost — don't stall audio waiting for it. Jump to the
            // oldest thing we have and play on from there.
            ushort oldest = First(_pending);
            Trace($"audio: buco nella sequenza, salto da {_nextSeq} a {oldest}");
            _nextSeq = oldest;
            DrainPending();
        }
    }

    private void DrainPending()
    {
        while (_pending.TryGetValue(_nextSeq, out byte[]? f))
        {
            _pending.Remove(_nextSeq);
            Emit(f);
            _nextSeq++;
        }
        // Drop anything now behind the cursor.
        while (_pending.Count > 0 && (short)(First(_pending) - _nextSeq) < 0)
            _pending.Remove(First(_pending));
    }

    private static ushort First(SortedDictionary<ushort, byte[]> d)
    {
        foreach (ushort k in d.Keys) return k;
        return 0;
    }

    private void Emit(byte[] frame)
    {
        try { AudioFrameReceived?.Invoke(frame); }
        catch (Exception ex) { Trace($"handler audio ha lanciato: {ex.Message}"); }
    }

    private static string Hex(ReadOnlySpan<byte> b, int n) => Convert.ToHexString(b[..Math.Min(n, b.Length)]);

    private void Trace(string m) => Diagnostics?.Invoke(m);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _data.Dispose();
        _control.Dispose();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch { /* best-effort */ }
        }
        _aes.Dispose();
        _cts.Dispose();
    }
}
