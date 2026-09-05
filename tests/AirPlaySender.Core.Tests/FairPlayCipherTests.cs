using AirPlaySender.Core.Receiving;
using Xunit;

namespace AirPlaySender.Core.Tests;

/// <summary>
/// <see cref="FairPlayCipher.Decrypt"/> had zero regression coverage
/// despite being the largest single piece of ported code in this project —
/// only ever manually confirmed against real hardware during live debugging
/// sessions, never pinned down. These are exactly the bytes captured from a
/// real iPhone 13 Pro Max (iOS 26.6.1) SETUP request during one such
/// session: the 164-byte <c>/fp-setup</c> round-2 key message and the
/// 72-byte <c>ekey</c> from the same connection, with the 16-byte raw key
/// this project's own <see cref="FairPlayCipher"/> produced for them at the
/// time (logged, not recomputed after the fact) as the expected output. If
/// a future edit to the ported cipher changes this result, this is what
/// would have caught it — the real-hardware "decrypts a real session key
/// with zero errors" claim in README.md was true only for as long as nobody
/// touched this file afterward without a test like this one.
/// </summary>
public class FairPlayCipherTests
{
    [Fact]
    public void DecryptsARealCapturedIPhoneSessionKey()
    {
        byte[] keyMessage164 = Convert.FromHexString(
            "46504C590301030000000098028F1A9C04F4A91EC8C40A7B37C505D68D44FF1E2A41D5C09D6C60385F8F1FB911023C657545029383A1B7F58AE6BA788132BB1B1B085F99433B55CD731BF35D1E3719403CA1CE4D0AC4F6D56A77E3F2882E66A3536A0728BDD6694A6FB61C3AFC808078B278423ED07E2064FEA5E3879DBE2DD4226EFF7EB0B54E0CC547A0D1D04BA11DB9FD1BBB0FA8F432CFE074D7097E36A0EC78F425");
        byte[] ekey72 = Convert.FromHexString(
            "46504C59010201000000003C000000002FB042ECA4BB6602DCE4F65EA653134A000000105942329474D58893F2E81910D62E6C91E54C57F0558B5597E7BB82B775CD32C06ABFD5FC");
        byte[] expectedRawKey = Convert.FromHexString("E5C096E9A6D8D767E9C9B186660128E1");

        Assert.Equal(164, keyMessage164.Length);
        Assert.Equal(72, ekey72.Length);

        byte[] rawKey = FairPlayCipher.Decrypt(keyMessage164, ekey72);

        Assert.Equal(expectedRawKey, rawKey);
    }
}
