namespace AirPlaySender.Core.Rtsp;

public sealed class RtspResponse
{
    // Not `required` — see AirPlayDevice's doc comment (XamlCompiler.exe + `required`).
    public int StatusCode { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public byte[] Body { get; init; } = [];

    public bool IsSuccess => StatusCode is >= 200 and < 300;
    public string? Header(string name) => Headers.GetValueOrDefault(name);
}
