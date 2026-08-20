namespace AirPlaySender.Core.Plist;

/// <summary>
/// A tiny tagged-union bplist value tree — enough of Apple's binary
/// property-list grammar to round-trip the AirPlay 2 SETUP request/response
/// dictionaries (dict / array / string / data / int / bool / real). Not a
/// general plist replacement.
/// </summary>
public sealed class PlistValue
{
    public enum Kind { Bool, Int, Real, Str, Data, Array, Dict }

    public Kind Type { get; }
    public bool BoolValue { get; private init; }
    public long IntValue { get; private init; }
    public double RealValue { get; private init; }
    public string StrValue { get; private init; } = "";
    public byte[] DataValue { get; private init; } = [];
    public List<PlistValue> ArrayValue { get; private init; } = [];
    /// <summary>Insertion-ordered.</summary>
    public List<KeyValuePair<string, PlistValue>> DictValue { get; private init; } = [];

    private PlistValue(Kind type) => Type = type;

    public static PlistValue Boolean(bool v) => new(Kind.Bool) { BoolValue = v };
    public static PlistValue Integer(long v) => new(Kind.Int) { IntValue = v };
    public static PlistValue Real(double v) => new(Kind.Real) { RealValue = v };
    public static PlistValue Str(string v) => new(Kind.Str) { StrValue = v };
    public static PlistValue Bytes(byte[] v) => new(Kind.Data) { DataValue = v };
    public static PlistValue Array(List<PlistValue> v) => new(Kind.Array) { ArrayValue = v };
    public static PlistValue Object(List<KeyValuePair<string, PlistValue>> v) => new(Kind.Dict) { DictValue = v };

    public PlistValue? Find(string key)
    {
        if (Type != Kind.Dict) return null;
        foreach (var (k, v) in DictValue)
            if (k == key) return v;
        return null;
    }

    public long AsInt(long def = 0) => Type == Kind.Int ? IntValue : def;
    public string AsStr(string def = "") => Type == Kind.Str ? StrValue : def;
}

/// <summary>Ordered builder for a plist dictionary — keeps call sites terse.</summary>
public sealed class PlistDictBuilder
{
    private readonly List<KeyValuePair<string, PlistValue>> _items = [];

    public PlistDictBuilder Add(string key, PlistValue value)
    {
        _items.Add(new KeyValuePair<string, PlistValue>(key, value));
        return this;
    }

    public PlistDictBuilder Add(string key, string value) => Add(key, PlistValue.Str(value));
    public PlistDictBuilder Add(string key, long value) => Add(key, PlistValue.Integer(value));
    public PlistDictBuilder Add(string key, bool value) => Add(key, PlistValue.Boolean(value));
    public PlistDictBuilder Add(string key, byte[] value) => Add(key, PlistValue.Bytes(value));

    public PlistValue Build() => PlistValue.Object(_items);
}
