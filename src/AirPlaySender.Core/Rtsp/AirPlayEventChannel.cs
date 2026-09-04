using System.Net.Sockets;
using System.Text;
using AirPlaySender.Core.Crypto;

namespace AirPlaySender.Core.Rtsp;

/// <summary>
/// AirPlay 2 event channel: a second TCP connection to the eventPort the
/// session SETUP reply carries. A modern receiver requires this connection
/// OPEN before it will accept RECORD. Once open, the receiver pushes
/// encrypted RTSP requests on it (remote-control / now-playing events we
/// don't need to act on); we must decrypt each and answer an encrypted
/// "200 OK" within roughly 25 seconds or the receiver tears the whole
/// session down. Same HomeKit frame format as the control channel, but the
/// read/write keys are SWAPPED — it's a reverse connection: we decrypt the
/// receiver's pushes with "Events-Write" and encrypt our replies with
/// "Events-Read".
/// </summary>
public sealed class AirPlayEventChannel : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly byte[] _readKey;
    private readonly byte[] _writeKey;
    private ulong _recvCounter;
    private ulong _sendCounter;
    private readonly List<byte> _rxEncrypted = [];
    private readonly List<byte> _rxPlain = [];
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>
    /// Added 2026-09-04 during live debugging: this class had NO logging at
    /// all — if a real receiver's push ever failed to decrypt (wrong keys,
    /// a framing bug, anything), <see cref="RunAsync"/>'s loop would just
    /// silently stop (only <see cref="OperationCanceledException"/> and
    /// <see cref="IOException"/> were ever caught; anything else propagated
    /// out of the unobserved background <see cref="Task.Run"/> and vanished
    /// until <see cref="DisposeAsync"/> eventually swallowed it). This
    /// channel's own doc comment says a receiver tears the session down if
    /// we don't answer a push within ~25s — suspiciously close to the ~30s
    /// after this channel opens. Fase 1 (audio) uses the identical key
    /// wiring and has run for months against a real HomePod, but may simply
    /// never have received an actual push on it — an audio-only session may
    /// never trigger one, so the DECRYPT direction specifically (as opposed
    /// to just opening the channel) may never have been genuinely exercised.
    /// </summary>
    public event Action<string>? Diagnostics;
    private void Trace(string message) => Diagnostics?.Invoke(message);

    private AirPlayEventChannel(TcpClient tcp, byte[] readKey, byte[] writeKey)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _readKey = readKey;
        _writeKey = writeKey;
    }

    public static async Task<AirPlayEventChannel> ConnectAsync(string host, int port, byte[] readKey, byte[] writeKey, CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
        var channel = new AirPlayEventChannel(tcp, readKey, writeKey);
        channel._loop = Task.Run(() => channel.RunAsync(channel._cts.Token), CancellationToken.None);
        return channel;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        Trace("in ascolto");
        var buf = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buf, ct).ConfigureAwait(false);
                if (n == 0) { Trace("connessione chiusa dal ricevitore (n=0)"); return; }
                Trace($"ricevuti {n} byte cifrati");
                _rxEncrypted.AddRange(buf.AsSpan(0, n).ToArray());
                while (true)
                {
                    byte[]? plaintext;
                    try { plaintext = HapFrameCodec.TryDecryptNextFrame(_readKey, ref _recvCounter, _rxEncrypted); }
                    catch (Exception ex) { Trace($"decrypt frame FALLITO: {ex}"); throw; }
                    if (plaintext is null) break;
                    Trace($"frame decifrato: {plaintext.Length} byte");
                    _rxPlain.AddRange(plaintext);
                }
                await RespondToCompletedRequestsAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown via DisposeAsync
        }
        catch (IOException ex)
        {
            // socket torn down under us during session teardown, not actionable
            Trace($"IOException (probabile teardown): {ex.Message}");
        }
        catch (Exception ex)
        {
            // Previously fell through uncaught and silently killed this loop —
            // see this class's own doc comment on why that mattered.
            Trace($"!!! ciclo eventi terminato per eccezione non gestita: {ex}");
        }
    }

    private async Task RespondToCompletedRequestsAsync(CancellationToken ct)
    {
        while (true)
        {
            byte[] snapshot = _rxPlain.ToArray();
            string text = Encoding.ASCII.GetString(snapshot);
            int headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headEnd < 0) return;

            int contentLength = 0;
            string? cseq = null;
            foreach (string rawLine in text[..headEnd].Split('\n'))
            {
                string line = rawLine.Trim();
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string key = line[..colon].Trim();
                string value = line[(colon + 1)..].Trim();
                if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out contentLength);
                else if (key.Equals("CSeq", StringComparison.OrdinalIgnoreCase)) cseq = value;
            }
            int total = headEnd + 4 + Math.Max(0, contentLength);
            if (snapshot.Length < total) return; // body still arriving
            Trace($"richiesta push ricevuta: {text[..Math.Min(headEnd, 200)].Replace("\r\n", " | ")} (CSeq={cseq}, Content-Length={contentLength})");
            _rxPlain.RemoveRange(0, total);

            // A bare 200 OK — no Content-Length/Audio-Latency, which can corrupt the receiver's realtime timeline.
            string resp = "RTSP/1.0 200 OK\r\nServer: AirTunes/550.10\r\n" + (cseq is not null ? $"CSeq: {cseq}\r\n" : "") + "\r\n";
            byte[] frame = HapFrameCodec.EncryptFrame(_writeKey, ref _sendCounter, Encoding.ASCII.GetBytes(resp));
            Trace($"rispondo 200 OK (CSeq={cseq})");
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch { /* best-effort shutdown */ }
        }
        _tcp.Dispose();
        _cts.Dispose();
    }
}
