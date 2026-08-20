namespace AirPlaySender.Core.Audio;

/// <summary>NTP 64-bit timestamp helpers (seconds-since-1900 in the high word, a 2^32-scaled fraction in the low word) and the NTP&lt;-&gt;RTP-timestamp conversions RAOP's clock sync rides on.</summary>
public static class Ntp
{
    private const ulong UnixToNtpEpochSeconds = 0x83AA7E80UL; // 1970-01-01 minus 1900-01-01, in seconds

    /// <summary>Current wall-clock time as a 64-bit NTP timestamp.</summary>
    public static ulong Now()
    {
        long unixTicks = DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks; // 100ns units
        ulong micros = (ulong)(unixTicks / 10);
        ulong sec = micros / 1_000_000UL;
        ulong frac = micros % 1_000_000UL;
        return ((sec + UnixToNtpEpochSeconds) << 32) | ((frac << 32) / 1_000_000UL);
    }

    public static ulong ToRtpTimestamp(ulong ntp, uint sampleRate) => ((ntp >> 16) * sampleRate) >> 16;

    public static ulong FromRtpTimestamp(ulong rtpTimestamp, uint sampleRate) => ((rtpTimestamp << 16) / sampleRate) << 16;
}
