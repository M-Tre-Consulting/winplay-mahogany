namespace AirPlaySender.Core.Audio;

/// <summary>MSB-first bit packer. Apple's ALAC bitstream (both real and the "uncompressed escape" frames RAOP realtime uses) is read MSB-first, byte-aligned only at the very end of a frame.</summary>
internal sealed class MsbBitWriter
{
    private readonly List<byte> _out = [];
    private byte _current;
    private int _filled; // bits currently in _current, 0..7

    public void Write(uint value, int bits)
    {
        for (int i = bits - 1; i >= 0; i--)
        {
            _current = (byte)(((uint)_current << 1) | ((value >> i) & 1u));
            if (++_filled == 8)
            {
                _out.Add(_current);
                _current = 0;
                _filled = 0;
            }
        }
    }

    public byte[] ToArray()
    {
        if (_filled == 0) return [.. _out];
        var result = new byte[_out.Count + 1];
        _out.CopyTo(result);
        result[^1] = (byte)(_current << (8 - _filled));
        return result;
    }
}
