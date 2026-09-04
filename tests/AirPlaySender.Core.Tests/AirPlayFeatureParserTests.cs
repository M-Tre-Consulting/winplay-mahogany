using AirPlaySender.Core.Discovery;
using Xunit;

namespace AirPlaySender.Core.Tests;

public class AirPlayFeatureParserTests
{
    [Fact]
    public void ParsesSingleWordFeatureString()
    {
        // Bit 9 (SupportsAirPlayAudio) only.
        AirPlayFlags f = AirPlayFeatureParser.ParseFeatures("0x200");
        Assert.Equal(AirPlayFlags.SupportsAirPlayAudio, f);
    }

    [Fact]
    public void ParsesTwoWordFeatureStringWithHighBitsInSecondToken()
    {
        // Real AirPlay TXT records look like "0x<low32>,0x<high32>". Bit 43
        // (SupportsSystemPairing) lives in the high word: 43-32=11 -> 1<<11 = 0x800.
        AirPlayFlags f = AirPlayFeatureParser.ParseFeatures("0x00000000,0x00000800");
        Assert.True(f.HasFlag(AirPlayFlags.SupportsSystemPairing));
    }

    [Theory]
    [InlineData("not-hex")]
    [InlineData("")]
    public void ReturnsNoneForUnparsableFeatureStrings(string input) =>
        Assert.Equal(AirPlayFlags.None, AirPlayFeatureParser.ParseFeatures(input));

    /// <summary>
    /// Regression test for a real bug found by code review: combining the
    /// two 32-bit halves via string concatenation (instead of numeric
    /// shift+OR) only produced the right answer when the low half happened
    /// to be a full, zero-padded 8-digit string — which every other test
    /// above already used, hiding the bug. A device is free to omit leading
    /// zeros on the low half; this pins the numerically-correct combination
    /// down explicitly with an unpadded low half.
    /// </summary>
    [Fact]
    public void ParsesTwoWordFeatureStringWithUnpaddedLowHalf()
    {
        // Same bit 43 (SupportsSystemPairing) as the padded-low-half test
        // above, but the low half is "0x1F0" (3 digits) instead of
        // "0x000001F0" (8 digits) — string concatenation would have shifted
        // the high half down by 5 hex digits (20 bits) and corrupted
        // everything; numeric combination must not care about the padding.
        AirPlayFlags f = AirPlayFeatureParser.ParseFeatures("0x1F0,0x800");
        Assert.True(f.HasFlag(AirPlayFlags.SupportsSystemPairing));
    }

    [Fact]
    public void DevicesAdvertisingSystemPairingUseTransientAuth()
    {
        var props = new Dictionary<string, string> { ["features"] = "0x00000000,0x00000800" };
        var device = new AirPlayDevice { Name = "Test", Host = "127.0.0.1", Port = 7000, DeviceId = "id", Properties = props };
        Assert.Equal(AirPlayAuthMethod.HapTransient, device.DetermineAuthMethod());
    }

    [Fact]
    public void DevicesWithPinRequiredStatusFlagAndNoTransientSupportUseHapPin()
    {
        // sf bit 0x8 (PIN_REQUIRED), no transient-pairing feature bits set.
        var props = new Dictionary<string, string> { ["sf"] = "0x8", ["features"] = "0x0" };
        var device = new AirPlayDevice { Name = "Test", Host = "127.0.0.1", Port = 7000, DeviceId = "id", Properties = props };
        Assert.Equal(AirPlayAuthMethod.HapPin, device.DetermineAuthMethod());
    }

    [Fact]
    public void OpenDeviceWithNoFlagsRequiresNoAuth()
    {
        var props = new Dictionary<string, string>();
        var device = new AirPlayDevice { Name = "Test", Host = "127.0.0.1", Port = 7000, DeviceId = "id", Properties = props };
        Assert.Equal(AirPlayAuthMethod.None, device.DetermineAuthMethod());
    }

    [Fact]
    public void PasswordFlagIsDetected()
    {
        var props = new Dictionary<string, string> { ["pw"] = "true" };
        Assert.True(AirPlayFeatureParser.IsPasswordRequired(props));
    }

    [Fact]
    public void EncryptionTypesAreParsedAsFlags()
    {
        var props = new Dictionary<string, string> { ["et"] = "0,1,3" };
        EncryptionType et = AirPlayFeatureParser.GetEncryptionTypes(props);
        Assert.True(et.HasFlag(EncryptionType.Unencrypted));
        Assert.True(et.HasFlag(EncryptionType.Rsa));
        Assert.True(et.HasFlag(EncryptionType.FairPlay));
        Assert.False(et.HasFlag(EncryptionType.MFiSAP));
    }
}
