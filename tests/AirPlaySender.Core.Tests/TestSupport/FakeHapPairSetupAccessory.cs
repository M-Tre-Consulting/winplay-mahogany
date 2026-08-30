using System.Net;
using System.Net.Sockets;
using System.Text;
using AirPlaySender.Core.Receiving;

namespace AirPlaySender.Core.Tests.TestSupport;

/// <summary>
/// Same "run the real controller-role code against a minimal fake" trick as
/// <see cref="FakeHapAccessory"/>, for <see cref="HapPairSetupAccessorySession"/>
/// (transient pair-setup) instead of pair-verify. Handles exactly one
/// connection, exactly two <c>POST /pair-setup</c> round-trips (M1→M2, M3→M4).
/// </summary>
internal sealed class FakeHapPairSetupAccessory : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly HapPairSetupAccessorySession _session;
    private TcpClient? _client;
    private Task? _serveTask;

    public int Port { get; }

    public FakeHapPairSetupAccessory(HapPairSetupAccessorySession session)
    {
        _session = session;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void Start() => _serveTask = ServeAsync();

    private async Task ServeAsync()
    {
        _client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
        _client.NoDelay = true;
        NetworkStream stream = _client.GetStream();
        var rx = new List<byte>();
        var buf = new byte[8192];
        try
        {
            for (int i = 0; i < 2; i++) // exactly M1 and M3
            {
                byte[]? body = null;
                while (body is null)
                {
                    if (TryParseBody(rx, out byte[]? parsed) && parsed is not null) { body = parsed; break; }
                    int n = await stream.ReadAsync(buf).ConfigureAwait(false);
                    if (n == 0) return;
                    rx.AddRange(buf.AsSpan(0, n).ToArray());
                }
                byte[]? respBody = _session.Handle(body);
                if (respBody is null) throw new InvalidOperationException("HapPairSetupAccessorySession rejected the request");

                var sb = new StringBuilder();
                sb.Append("RTSP/1.0 200 OK\r\n");
                sb.Append("Content-Type: application/octet-stream\r\n");
                sb.Append("Content-Length: ").Append(respBody.Length).Append("\r\n\r\n");
                byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
                await stream.WriteAsync((byte[])[.. head, .. respBody]).ConfigureAwait(false);
            }
        }
        catch (IOException) { /* client disconnected */ }
    }

    private static bool TryParseBody(List<byte> rx, out byte[]? body)
    {
        body = null;
        byte[] snapshot = [.. rx];
        string text = Encoding.ASCII.GetString(snapshot);
        int headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headEnd < 0) return false;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] lines = text[..headEnd].Split('\n');
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

        body = snapshot.AsSpan(bodyStart, contentLength).ToArray();
        rx.RemoveRange(0, bodyStart + contentLength);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _client?.Dispose();
        if (_serveTask is not null) { try { await _serveTask.ConfigureAwait(false); } catch { } }
    }
}
