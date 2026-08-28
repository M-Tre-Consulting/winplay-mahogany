namespace AirPlaySender.Core.Receiving;

/// <summary>An incoming request on the receiver side — the mirror image of <see cref="Rtsp.RtspResponse"/>.</summary>
public sealed class RtspRequest
{
    public string Method { get; init; } = "";
    public string Url { get; init; } = "";
    public string Protocol { get; init; } = "";
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public byte[] Body { get; init; } = [];

    public string? Header(string name) => Headers.GetValueOrDefault(name);
    public string? CSeq => Header("CSeq");
}
