namespace AirPlaySender.Core.Tlv;

/// <summary>HomeKit TLV8 tag numbers used by AirPlay 2 pairing.</summary>
public enum Tlv8Type : byte
{
    Method = 0x00,
    Identifier = 0x01,
    Salt = 0x02,
    PublicKey = 0x03,
    Proof = 0x04,
    EncryptedData = 0x05,
    State = 0x06, // "SeqNo"
    Error = 0x07,
    Signature = 0x0A,
    Permissions = 0x0B,
    Name = 0x11,
    Flags = 0x13,
}

/// <summary>
/// HomeKit TLV8 (type-length-value, 255-byte fragmenting). One level only
/// (HAP never nests). A repeated tag whose value exceeds 255 bytes is split
/// into consecutive same-tag chunks on write and rejoined on read — required
/// for the 384-byte SRP PublicKey.
/// </summary>
public static class Tlv8
{
    /// <summary>Insertion-ordered (tag, value) list — some receivers care about record order.</summary>
    public sealed class Map : List<KeyValuePair<byte, byte[]>>
    {
        public void Add(Tlv8Type tag, byte[] value) => Add(new KeyValuePair<byte, byte[]>((byte)tag, value));
        public void Add(Tlv8Type tag, byte value) => Add(tag, [value]);

        public byte[]? Get(Tlv8Type tag)
        {
            foreach (var (t, v) in this)
                if (t == (byte)tag) return v;
            return null;
        }
    }

    public static byte[] Encode(Map items)
    {
        using var ms = new MemoryStream();
        foreach (var (tag, value) in items)
        {
            int pos = 0;
            do
            {
                int chunk = Math.Min(255, value.Length - pos);
                ms.WriteByte(tag);
                ms.WriteByte((byte)chunk);
                ms.Write(value, pos, chunk);
                pos += chunk;
            } while (pos < value.Length); // an empty value still emits one zero-length record
        }
        return ms.ToArray();
    }

    public static Map Decode(ReadOnlySpan<byte> data)
    {
        var outp = new Map();
        int i = 0;
        while (i + 2 <= data.Length)
        {
            byte tag = data[i];
            byte len = data[i + 1];
            if (i + 2 + len > data.Length) break;
            byte[] chunk = data.Slice(i + 2, len).ToArray();
            if (outp.Count > 0 && outp[^1].Key == tag)
            {
                // Join a repeated tag that immediately follows (fragmented value).
                var merged = new byte[outp[^1].Value.Length + chunk.Length];
                outp[^1].Value.CopyTo(merged, 0);
                chunk.CopyTo(merged, outp[^1].Value.Length);
                outp[^1] = new KeyValuePair<byte, byte[]>(tag, merged);
            }
            else
            {
                outp.Add(new KeyValuePair<byte, byte[]>(tag, chunk));
            }
            i += 2 + len;
        }
        return outp;
    }
}
