using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace AirPlaySender.Core.Receiving;

/// <summary>
/// The AirPlay 1 mirroring clock-sync exchange — and, contrary to what a
/// first guess would suggest, <b>we</b> (the receiver) are the active party:
/// this sends a 32-byte timing request to the client's own <c>timingPort</c>
/// (the value it puts in the SETUP request) roughly every 3 seconds, from
/// the UDP socket whose local port we already advertise back as our own
/// <c>timingPort</c> in the SETUP response, and reads whatever the client
/// sends back. Previously that socket was opened and left completely idle —
/// a real gap, not a stylistic choice: nothing was ever sent on it.
///
/// Wire format taken from UxPlay's <c>lib/raop_ntp.c</c>
/// (<c>raop_ntp_thread</c>) and <c>lib/byteutils.c</c>
/// (<c>byteutils_put_ntp_timestamp</c>/<c>byteutils_get_ntp_timestamp</c>) —
/// see the doc comment on <see cref="AirPlayMirroringAdvertiser"/> for this
/// project's attribution convention. A 32-byte packet, all big-endian:
/// <list type="bullet">
/// <item>bytes 0-3: fixed header <c>80 D2 00 07</c> (both directions)</item>
/// <item>bytes 4-7: reserved, zero</item>
/// <item>bytes 8-15: "origin" timestamp — on a request, the client's own
/// transmit timestamp from the previous response, copied back verbatim
/// (not reinterpreted); zero on the very first request</item>
/// <item>bytes 16-23: "receive" timestamp — our local receive time of the
/// previous response, as an NTP timestamp; zero on the very first request</item>
/// <item>bytes 24-31: "transmit" timestamp — our local send time right now,
/// as an NTP timestamp</item>
/// </list>
/// This doesn't yet compute/expose the resulting clock offset (nothing
/// downstream needs it — there's no real video renderer to sync against
/// yet): the goal right now is narrower, to test whether simply having a
/// live, correctly-shaped exchange in progress is what a modern iOS client
/// is waiting to see before it ever opens the mirroring data connection.
/// </summary>
public sealed class NtpTimingSession : IAsyncDisposable
{
    private const ulong SecondsFrom1900To1970 = 2208988800UL;
    private const ulong NanosecondsPerSecond = 1_000_000_000UL;

    private readonly UdpClient _socket;
    private readonly IPEndPoint _remote;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    // "client_ref_time" — the raw 8 bytes the client sent as ITS transmit
    // timestamp in the previous response, echoed back verbatim as the next
    // request's origin timestamp (exactly what raop_ntp_thread does: no
    // reinterpretation, just byteutils_get_long_be then byteutils_put_long_be).
    private ulong? _clientRefTimeRaw;
    private ulong? _lastLocalRecvNs;

    public event Action<string>? Diagnostics;

    /// <param name="socket">The already-bound UDP socket whose local port was reported as this session's <c>timingPort</c>.</param>
    /// <param name="remote">The client's own IP (from the TCP connection) and the <c>timingPort</c> value it sent in its SETUP request.</param>
    public NtpTimingSession(UdpClient socket, IPEndPoint remote)
    {
        _socket = socket;
        _remote = remote;
    }

    public void Start() => _loop = RunAsync(_cts.Token);

    private async Task RunAsync(CancellationToken ct)
    {
        Trace($"avvio scambio timing con {_remote}");
        while (!ct.IsCancellationRequested)
        {
            var request = new byte[32];
            request[0] = 0x80;
            request[1] = 0xd2;
            request[2] = 0x00;
            request[3] = 0x07;

            ulong sendTimeNs = UnixNowNanoseconds();
            if (_clientRefTimeRaw is { } refTime && _lastLocalRecvNs is { } recvNs)
            {
                BinaryPrimitives.WriteUInt64BigEndian(request.AsSpan(8, 8), refTime);
                WriteNtpTimestamp(request, 16, recvNs);
            }
            WriteNtpTimestamp(request, 24, sendTimeNs);

            try
            {
                await _socket.SendAsync(request, _remote, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace($"invio richiesta timing fallito: {ex.Message}");
            }

            await TryReceiveResponseAsync(ct).ConfigureAwait(false);

            try { await Task.Delay(3000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task TryReceiveResponseAsync(CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(300); // matches UxPlay's SO_RCVTIMEO of 300ms
        try
        {
            UdpReceiveResult result = await _socket.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            ulong recvNowNs = UnixNowNanoseconds();
            byte[] response = result.Buffer;
            if (response.Length < 32)
            {
                Trace($"risposta timing troppo corta ({response.Length} byte), ignorata");
                return;
            }
            _clientRefTimeRaw = BinaryPrimitives.ReadUInt64BigEndian(response.AsSpan(24, 8));
            _lastLocalRecvNs = recvNowNs;
            Trace($"risposta timing ricevuta da {result.RemoteEndPoint}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Trace("timeout risposta timing (300ms) — nessuna risposta dal client");
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            Trace($"errore ricezione timing: {ex.Message}");
        }
    }

    private static void WriteNtpTimestamp(byte[] buf, int offset, ulong nsSince1970)
    {
        ulong seconds = nsSince1970 / NanosecondsPerSecond;
        ulong nanoseconds = nsSince1970 % NanosecondsPerSecond;
        seconds += SecondsFrom1900To1970;
        ulong fraction = (nanoseconds << 32) / NanosecondsPerSecond;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset, 4), (uint)seconds);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(offset + 4, 4), (uint)fraction);
    }

    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static ulong UnixNowNanoseconds() => (ulong)((DateTime.UtcNow - UnixEpoch).Ticks * 100L); // 100ns ticks -> ns

    private void Trace(string message) => Diagnostics?.Invoke(message);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch { /* best-effort shutdown */ }
        }
        _cts.Dispose();
    }
}
