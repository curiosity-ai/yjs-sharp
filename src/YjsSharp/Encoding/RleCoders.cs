using System.Collections.Generic;

namespace YjsSharp.Encoding;

/// <summary>lib0 RleEncoder specialised for bytes.</summary>
internal sealed class RleByteEncoder
{
    public readonly Encoder Encoder = new Encoder();
    private         int     _state  = -1;
    private         uint    _count;

    public void Write(byte value)
    {
        if (_state == value)
        {
            _count++;
        }
        else
        {
            if (_count > 0)
            {
                Encoder.WriteVarUInt(_count - 1);
            }
            _count = 1;
            Encoder.WriteUInt8(value);
            _state = value;
        }
    }
}

/// <summary>lib0 RleDecoder specialised for bytes.</summary>
internal sealed class RleByteDecoder
{
    private readonly Decoder _decoder;
    private          byte    _state;
    private          long    _count;

    public RleByteDecoder(Decoder decoder)
    {
        _decoder = decoder;
    }

    public byte Read()
    {
        if (_count == 0)
        {
            _state = _decoder.ReadUInt8();
            if (_decoder.HasContent)
            {
                _count = (long)_decoder.ReadVarUInt() + 1;
            }
            else
            {
                _count = -1;
            }
        }
        _count--;
        return _state;
    }
}

/// <summary>lib0 UintOptRleEncoder.</summary>
internal sealed class UIntOptRleEncoder
{
    public readonly Encoder Encoder = new Encoder();
    private         ulong   _state;
    private         uint    _count;

    public void Write(ulong value)
    {
        if (_state == value)
        {
            _count++;
        }
        else
        {
            Flush();
            _count = 1;
            _state = value;
        }
    }

    private void Flush()
    {
        if (_count > 0)
        {
            // a negative sign (which may be a negative zero) signals that a count follows
            Encoder.WriteVarInt(_state, _count > 1);
            if (_count > 1)
            {
                Encoder.WriteVarUInt(_count - 2);
            }
        }
    }

    public byte[] ToArray()
    {
        Flush();
        return Encoder.ToArray();
    }

    public void FlushInto(Encoder target)
    {
        Flush();
        target.WriteVarUInt8Array(Encoder.WrittenSpan);
    }
}

/// <summary>lib0 UintOptRleDecoder.</summary>
internal sealed class UIntOptRleDecoder
{
    private readonly Decoder _decoder;
    private          ulong   _state;
    private          ulong   _count;

    public UIntOptRleDecoder(Decoder decoder)
    {
        _decoder = decoder;
    }

    public ulong Read()
    {
        if (_count == 0)
        {
            _state = _decoder.ReadVarInt(out var negative);
            _count = 1;
            if (negative)
            {
                _count = _decoder.ReadVarUInt() + 2;
            }
        }
        _count--;
        return _state;
    }
}

/// <summary>lib0 IntDiffOptRleEncoder.</summary>
internal sealed class IntDiffOptRleEncoder
{
    public readonly Encoder Encoder = new Encoder();
    private         long    _state;
    private         uint    _count;
    private         long    _diff;

    public void Write(long value)
    {
        if (_diff == value - _state)
        {
            _state = value;
            _count++;
        }
        else
        {
            Flush();
            _count = 1;
            _diff  = value - _state;
            _state = value;
        }
    }

    private void Flush()
    {
        if (_count > 0)
        {
            var encodedDiff = _diff * 2 + (_count == 1 ? 0 : 1);
            Encoder.WriteVarInt(encodedDiff);
            if (_count > 1)
            {
                Encoder.WriteVarUInt(_count - 2);
            }
        }
    }

    public void FlushInto(Encoder target)
    {
        Flush();
        target.WriteVarUInt8Array(Encoder.WrittenSpan);
    }
}

/// <summary>lib0 IntDiffOptRleDecoder.</summary>
internal sealed class IntDiffOptRleDecoder
{
    private readonly Decoder _decoder;
    private          long    _state;
    private          ulong   _count;
    private          long    _diff;

    public IntDiffOptRleDecoder(Decoder decoder)
    {
        _decoder = decoder;
    }

    public long Read()
    {
        if (_count == 0)
        {
            var diff     = _decoder.ReadVarInt();
            var hasCount = (diff & 1) != 0;
            _diff  = diff >> 1;
            _count = 1;
            if (hasCount)
            {
                _count = _decoder.ReadVarUInt() + 2;
            }
        }
        _state += _diff;
        _count--;
        return _state;
    }
}

/// <summary>lib0 StringEncoder: all strings concatenated plus a run length encoded list of UTF-16 lengths.</summary>
internal sealed class StringEncoder
{
    private readonly System.Text.StringBuilder _builder = new System.Text.StringBuilder();
    private readonly UIntOptRleEncoder         _lengths = new UIntOptRleEncoder();

    public void Write(string value)
    {
        _builder.Append(value);
        _lengths.Write((ulong)value.Length);
    }

    public void FlushInto(Encoder target)
    {
        var inner = new Encoder();
        inner.WriteVarString(_builder.ToString());
        inner.WriteUInt8Array(_lengths.ToArray());
        target.WriteVarUInt8Array(inner.WrittenSpan);
    }
}

/// <summary>lib0 StringDecoder.</summary>
internal sealed class StringDecoder
{
    private readonly UIntOptRleDecoder _lengths;
    private readonly string            _str;
    private          int               _pos;

    public StringDecoder(Decoder decoder)
    {
        _str     = decoder.ReadVarString();
        _lengths = new UIntOptRleDecoder(decoder);
    }

    public string Read()
    {
        var len = (int)_lengths.Read();
        var end = _pos + len;
        if (end > _str.Length)
        {
            throw new System.FormatException("String decoder out of range");
        }
        var res = _str.Substring(_pos, len);
        _pos = end;
        return res;
    }
}
