using System.Text;

namespace AirPlaySender.Core.Plist;

/// <summary>
/// Minimal Apple binary property list (bplist00) encoder/decoder — exactly
/// the value types AirPlay 2's SETUP/SETUP-stream request and response
/// dictionaries use (dict / array / string / data / int / bool / real) and
/// nothing more. Object references are always emitted as 4 bytes (simpler,
/// always valid for the small dictionaries AirPlay SETUP uses).
///
/// The decoder is deliberately defensive: it is parsing a reply from a
/// network peer (a real AirPlay receiver, but "real" doesn't mean
/// "never buggy or hostile"), so every count/length that drives an
/// allocation or an array index is bounds-checked BEFORE use, and object
/// recursion is depth-limited. This mirrors the reference recipe's own
/// hardening (a crafted huge count must fail closed, not integer-overflow
/// past a bounds check into an out-of-bounds read or a multi-gigabyte alloc).
/// </summary>
public static class BinaryPlist
{
    public static byte[] Encode(PlistValue root)
    {
        var objects = new List<byte[]>();
        AddObject(root, objects);

        using var ms = new MemoryStream();
        ms.Write("bplist00"u8);

        var offsets = new List<long>(objects.Count);
        foreach (byte[] obj in objects)
        {
            offsets.Add(ms.Position);
            ms.Write(obj);
        }

        long offsetTableStart = ms.Position;
        const byte offsetSize = 4;
        foreach (long off in offsets)
            WriteBE(ms, (ulong)off, offsetSize);

        // Trailer: 6 unused, offsetIntSize, objectRefSize, numObjects(8), topObject(8), offsetTableOffset(8).
        var trailer = new byte[32];
        trailer[6] = offsetSize;
        trailer[7] = 4; // object refs are 4-byte
        PutBE64(trailer, 8, (ulong)objects.Count);
        PutBE64(trailer, 16, 0); // top object index
        PutBE64(trailer, 24, (ulong)offsetTableStart);
        ms.Write(trailer);
        return ms.ToArray();
    }

    private static int AddObject(PlistValue v, List<byte[]> objects)
    {
        int idx = objects.Count;
        objects.Add([]); // reserve slot (recursion-safe ordering)
        using var obj = new MemoryStream();
        switch (v.Type)
        {
            case PlistValue.Kind.Bool:
                obj.WriteByte((byte)(v.BoolValue ? 0x09 : 0x08));
                break;
            case PlistValue.Kind.Int:
                EncodeInt(obj, v.IntValue);
                break;
            case PlistValue.Kind.Real:
            {
                obj.WriteByte(0x23); // double (8-byte)
                ulong bits = (ulong)BitConverter.DoubleToInt64Bits(v.RealValue);
                WriteBE(obj, bits, 8);
                break;
            }
            case PlistValue.Kind.Str:
                EncodeString(obj, v.StrValue);
                break;
            case PlistValue.Kind.Data:
                EncodeMarkerLen(obj, 0x40, v.DataValue.Length);
                obj.Write(v.DataValue);
                break;
            case PlistValue.Kind.Array:
            {
                var refs = new List<int>();
                foreach (var c in v.ArrayValue) refs.Add(AddObject(c, objects));
                EncodeMarkerLen(obj, 0xA0, refs.Count);
                foreach (int r in refs) AppendRef(obj, r);
                break;
            }
            case PlistValue.Kind.Dict:
            {
                var kRefs = new List<int>();
                var vRefs = new List<int>();
                foreach (var (k, val) in v.DictValue)
                {
                    kRefs.Add(AddObject(PlistValue.Str(k), objects));
                    vRefs.Add(AddObject(val, objects));
                }
                EncodeMarkerLen(obj, 0xD0, v.DictValue.Count);
                foreach (int r in kRefs) AppendRef(obj, r);
                foreach (int r in vRefs) AppendRef(obj, r);
                break;
            }
        }
        objects[idx] = obj.ToArray();
        return idx;
    }

    private static void AppendRef(Stream obj, int r) => WriteBE(obj, (ulong)r, 4);

    private static void EncodeMarkerLen(Stream obj, byte marker, int len)
    {
        if (len < 15)
        {
            obj.WriteByte((byte)(marker | len));
        }
        else
        {
            obj.WriteByte((byte)(marker | 0x0F));
            EncodeInt(obj, len); // length follows as an int object inline
        }
    }

    private static void EncodeString(Stream obj, string s)
    {
        // ASCII string (0x5x). AirPlay SETUP keys/values are all ASCII.
        byte[] bytes = Encoding.ASCII.GetBytes(s);
        EncodeMarkerLen(obj, 0x50, bytes.Length);
        obj.Write(bytes);
    }

    private static void EncodeInt(Stream obj, long value)
    {
        // Choose the smallest power-of-two width that holds the value.
        if (value >= 0 && value <= 0xFF)
        {
            obj.WriteByte(0x10);
            obj.WriteByte((byte)value);
        }
        else if (value >= 0 && value <= 0xFFFF)
        {
            obj.WriteByte(0x11);
            WriteBE(obj, (ulong)value, 2);
        }
        else if (value >= 0 && value <= 0xFFFFFFFFL)
        {
            obj.WriteByte(0x12);
            WriteBE(obj, (ulong)value, 4);
        }
        else
        {
            obj.WriteByte(0x13);
            WriteBE(obj, unchecked((ulong)value), 8);
        }
    }

    private static void WriteBE(Stream s, ulong value, int bytes)
    {
        for (int i = bytes - 1; i >= 0; i--) s.WriteByte((byte)((value >> (8 * i)) & 0xFF));
    }

    private static void PutBE64(byte[] buf, int at, ulong v)
    {
        for (int i = 0; i < 8; i++) buf[at + i] = (byte)((v >> (8 * (7 - i))) & 0xFF);
    }

    // ── decode ──────────────────────────────────────────────────────

    public static PlistValue? Decode(ReadOnlySpan<byte> data)
    {
        var dec = new Decoder(data);
        return dec.ParseTrailer() ? dec.ReadObject(0, 0) : null;
    }

    private ref struct Decoder(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _d = data;
        private byte _offsetSize, _refSize;
        private ulong _numObjects;
        private ulong _offsetTableStart;
        private ulong[] _offsets = [];
        private bool _ok = true;

        private ulong ReadBE(ulong at, int n)
        {
            ulong v = 0;
            for (int i = 0; i < n; i++)
            {
                if (at + (ulong)i >= (ulong)_d.Length) { _ok = false; return 0; }
                v = (v << 8) | _d[(int)(at + (ulong)i)];
            }
            return v;
        }

        public bool ParseTrailer()
        {
            if (_d.Length < 8 + 32) return false;
            if (!_d[..8].SequenceEqual("bplist00"u8)) return false;
            ulong tr = (ulong)(_d.Length - 32);
            _offsetSize = _d[(int)tr + 6];
            _refSize = _d[(int)tr + 7];
            _numObjects = ReadBE(tr + 8, 8);
            _offsetTableStart = ReadBE(tr + 24, 8);
            if (!_ok) return false;

            // Validate size fields ∈ {1,2,4,8} and bound numObjects against
            // the file (each object needs >=1 byte) BEFORE any multiply, so
            // a crafted huge numObjects can't wrap the bounds check.
            static bool ValidSize(ulong s) => s is 1 or 2 or 4 or 8;
            if (!ValidSize(_offsetSize) || !ValidSize(_refSize)) return false;
            if (_offsetTableStart > (ulong)_d.Length) return false;
            if (_numObjects > (ulong)_d.Length) return false;
            if (_numObjects > ((ulong)_d.Length - _offsetTableStart) / _offsetSize) return false;

            _offsets = new ulong[_numObjects];
            for (ulong i = 0; i < _numObjects; i++)
                _offsets[i] = ReadBE(_offsetTableStart + i * _offsetSize, _offsetSize);
            return _ok;
        }

        // An ordinary instance method (not a local function): local functions
        // inside a ref struct's methods cannot capture the implicit 'this',
        // so the "count" field the C++ reads inline is threaded through
        // explicitly here via `ref pos`.
        private ulong ReadCount(byte lo, ref ulong pos)
        {
            if (lo != 0x0F) return lo;
            if (pos >= (ulong)_d.Length) { _ok = false; return 0; }
            byte im = _d[(int)pos];
            pos++;
            int n = 1 << (im & 0x0F);
            ulong c = ReadBE(pos, n);
            pos += (ulong)n;
            return c;
        }

        public PlistValue? ReadObject(ulong reference, int depth)
        {
            if (depth > 32 || reference >= _numObjects) return null;
            ulong pos = _offsets[reference];
            if (pos >= (ulong)_d.Length) return null;
            byte marker = _d[(int)pos];
            pos++;
            byte hi = (byte)(marker & 0xF0);
            byte lo = (byte)(marker & 0x0F);

            switch (hi)
            {
                case 0x00: // bool / null / fill
                    return PlistValue.Boolean(marker == 0x09);
                case 0x10: // int
                {
                    int n = 1 << lo;
                    long v = SignExtend(ReadBE(pos, n), n);
                    return PlistValue.Integer(v);
                }
                case 0x20: // real
                {
                    int n = 1 << lo;
                    ulong bits = ReadBE(pos, n);
                    double r = n == 8 ? BitConverter.Int64BitsToDouble((long)bits)
                             : n == 4 ? BitConverter.Int32BitsToSingle((int)bits)
                             : 0.0;
                    return PlistValue.Real(r);
                }
                case 0x40: // data
                {
                    ulong cnt = ReadCount(lo, ref pos);
                    if (!_ok || cnt > (ulong)_d.Length || pos + cnt > (ulong)_d.Length) return null;
                    return PlistValue.Bytes(_d.Slice((int)pos, (int)cnt).ToArray());
                }
                case 0x50: // ASCII string
                {
                    ulong cnt = ReadCount(lo, ref pos);
                    if (!_ok || cnt > (ulong)_d.Length || pos + cnt > (ulong)_d.Length) return null;
                    return PlistValue.Str(Encoding.ASCII.GetString(_d.Slice((int)pos, (int)cnt)));
                }
                case 0x60: // UTF-16BE string — keep the ASCII-range low byte per unit (SETUP dicts are all ASCII)
                {
                    ulong cnt = ReadCount(lo, ref pos);
                    if (!_ok || cnt > (ulong)_d.Length) return null;
                    var sb = new StringBuilder();
                    for (ulong i = 0; i < cnt; i++)
                    {
                        ulong at = pos + i * 2;
                        if (at + 1 >= (ulong)_d.Length) break;
                        sb.Append((char)_d[(int)at + 1]);
                    }
                    return PlistValue.Str(sb.ToString());
                }
                case 0xA0: // array
                {
                    ulong cnt = ReadCount(lo, ref pos);
                    if (!_ok || cnt > _numObjects || pos + cnt * _refSize > (ulong)_d.Length) return null;
                    var arr = new List<PlistValue>((int)cnt);
                    for (ulong i = 0; i < cnt; i++)
                    {
                        ulong r = ReadBE(pos + i * _refSize, _refSize);
                        var child = ReadObject(r, depth + 1);
                        if (child is null) return null;
                        arr.Add(child);
                    }
                    return PlistValue.Array(arr);
                }
                case 0xD0: // dict
                {
                    ulong cnt = ReadCount(lo, ref pos);
                    if (!_ok || cnt > _numObjects || pos + 2 * cnt * _refSize > (ulong)_d.Length) return null;
                    var dd = new List<KeyValuePair<string, PlistValue>>((int)cnt);
                    for (ulong i = 0; i < cnt; i++)
                    {
                        ulong kr = ReadBE(pos + i * _refSize, _refSize);
                        ulong vr = ReadBE(pos + (cnt + i) * _refSize, _refSize);
                        var k = ReadObject(kr, depth + 1);
                        var val = ReadObject(vr, depth + 1);
                        if (k is null || val is null) return null;
                        dd.Add(new KeyValuePair<string, PlistValue>(k.AsStr(), val));
                    }
                    return PlistValue.Object(dd);
                }
                default:
                    return null;
            }
        }

        private static long SignExtend(ulong v, int byteWidth)
        {
            // bplist ints narrower than 8 bytes are unsigned magnitudes in practice
            // for AirPlay's use (ports, counts); an 8-byte int is a genuine signed value.
            if (byteWidth >= 8) return unchecked((long)v);
            return (long)v;
        }
    }
}
