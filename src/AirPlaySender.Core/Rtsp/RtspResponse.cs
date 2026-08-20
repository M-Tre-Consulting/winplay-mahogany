namespace AirPlaySender.Core.Rtsp;

public sealed class RtspResponse
{
    public required int StatusCode { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required byte[] Body { get; init; }

    public bool IsSuccess => StatusCode is >= 200 and < 300;
    public string? Header(string name) => Headers.GetValueOrDefault(name);
}
