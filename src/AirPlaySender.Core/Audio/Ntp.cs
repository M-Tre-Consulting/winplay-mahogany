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

    /// <summary>
    /// A 64-bit NTP fixed-point timestamp (seconds in the high 32 bits, a 2^32-scaled
    /// fraction in the low 32) to whole nanoseconds. Byte-for-byte the same math as
    /// UxPlay's <c>raop_ntp_timestamp_to_nano_seconds</c> with
    /// <c>account_for_epoch_diff=false</c> — the mirror packet header carries the
    /// per-frame presentation time in exactly this format at offset 8 (NOT a raw
    /// nanosecond count: reading it as one made every frame's timestamp advance ~4.29x
    /// too slowly, so a 60fps stream played back at ~14fps and frames piled up).
    /// </summary>
    public static ulong ToNanoseconds(ulong ntpTimestamp)
    {
        ulong seconds = ntpTimestamp >> 32;
        ulong fraction = ntpTimestamp & 0xFFFFFFFF;
        return seconds * 1_000_000_000UL + ((fraction * 1_000_000_000UL) >> 32);
    }
}
