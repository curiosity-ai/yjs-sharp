using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace YjsSharp.Encoding;

/// <summary>
/// Reads data written by <see cref="Encoder"/> (or by lib0's encoding module in JavaScript).
/// </summary>
public sealed class Decoder
{
    private readonly byte[] _buffer;
    private readonly int    _start;
    private readonly int    _end;
    private          int    _position;

    /// <summary>Creates a decoder over the whole array.</summary>
    public Decoder(byte[] data) : this(data, 0, data.Length)
    {
    }

    /// <summary>Creates a decoder over a slice of an array (no copy).</summary>
    public Decoder(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }
        _buffer   = data;
        _start    = offset;
        _end      = offset + length;
        _position = offset;
    }

    /// <summary>The read position relative to the start of the decoded slice.</summary>
    public int Position
    {
        get => _position - _start;
        set
        {
            if (value < 0 || _start + value > _end)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            _position = _start + value;
        }
    }

    /// <summary>The length of the decoded slice.</summary>
    public int Length => _end - _start;

    /// <summary>The number of bytes not read yet.</summary>
    public int Remaining => _end - _position;

    /// <summary>Whether there is anything left to read.</summary>
    public bool HasContent => _position < _end;

    private static Exception EndOfData() => new EndOfDataException();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Need(int count)
    {
        if (_end - _position < count)
        {
            throw EndOfData();
        }
    }

    /// <summary>Reads one byte.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadUInt8()
    {
        if (_position >= _end)
        {
            throw EndOfData();
        }
        return _buffer[_position++];
    }

    /// <summary>Returns the next byte without consuming it.</summary>
    public byte PeekUInt8()
    {
        Need(1);
        return _buffer[_position];
    }

    /// <summary>Reads a 16 bit unsigned integer (little endian).</summary>
    public ushort ReadUInt16()
    {
        Need(2);
        var v = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(_position));
        _position += 2;
        return v;
    }

    /// <summary>Reads a 32 bit unsigned integer (little endian).</summary>
    public uint ReadUInt32()
    {
        Need(4);
        var v = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_position));
        _position += 4;
        return v;
    }

    /// <summary>Reads a 32 bit unsigned integer (big endian).</summary>
    public uint ReadUInt32BigEndian()
    {
        Need(4);
        var v = BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(_position));
        _position += 4;
        return v;
    }

    /// <summary>Reads a lib0 variable length unsigned integer (at most 53 bits, like JavaScript).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadVarUInt()
    {
        ulong num   = 0;
        var   shift = 0;
        var   buf   = _buffer;
        while (_position < _end)
        {
            var r = buf[_position++];
            num |= (ulong)(r & 0x7F) << shift;
            if (r < 0x80)
            {
                return num;
            }
            shift += 7;
            if (shift > 63)
            {
                throw new FormatException("Integer out of range");
            }
        }
        throw EndOfData();
    }

    /// <summary>Reads a variable length unsigned integer that must fit into 32 bits.</summary>
    public uint ReadVarUInt32()
    {
        var v = ReadVarUInt();
        if (v > uint.MaxValue)
        {
            throw new FormatException("Integer out of range");
        }
        return (uint)v;
    }

    /// <summary>Reads a lib0 variable length signed integer.</summary>
    public long ReadVarInt()
    {
        var magnitude = ReadVarInt(out var negative);
        return negative ? -(long)magnitude : (long)magnitude;
    }

    /// <summary>
    /// Reads a lib0 variable length signed integer as magnitude and sign, which preserves "negative zero".
    /// </summary>
    public ulong ReadVarInt(out bool negative)
    {
        var   r     = ReadUInt8();
        ulong num   = (ulong)(r & 0x3F);
        var   shift = 6;
        negative = (r & 0x40) != 0;
        if ((r & 0x80) == 0)
        {
            return num;
        }
        var buf = _buffer;
        while (_position < _end)
        {
            r   =  buf[_position++];
            num |= (ulong)(r & 0x7F) << shift;
            if (r < 0x80)
            {
                return num;
            }
            shift += 7;
            if (shift > 63)
            {
                throw new FormatException("Integer out of range");
            }
        }
        throw EndOfData();
    }

    /// <summary>Reads a length prefixed UTF-8 string.</summary>
    public string ReadVarString()
    {
        var len = (int)ReadVarUInt();
        Need(len);
        var s = len == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(_buffer, _position, len);
        _position += len;
        return s;
    }

    /// <summary>Reads <paramref name="length"/> raw bytes (copied).</summary>
    public byte[] ReadUInt8Array(int length)
    {
        Need(length);
        var result = new byte[length];
        Buffer.BlockCopy(_buffer, _position, result, 0, length);
        _position += length;
        return result;
    }

    /// <summary>Reads <paramref name="length"/> raw bytes without copying.</summary>
    public ReadOnlySpan<byte> ReadUInt8Span(int length)
    {
        Need(length);
        var span = new ReadOnlySpan<byte>(_buffer, _position, length);
        _position += length;
        return span;
    }

    /// <summary>Reads length prefixed bytes (copied).</summary>
    public byte[] ReadVarUInt8Array()
    {
        return ReadUInt8Array((int)ReadVarUInt());
    }

    /// <summary>Reads length prefixed bytes and returns a decoder over them (no copy).</summary>
    public Decoder ReadVarUInt8ArrayAsDecoder()
    {
        var len = (int)ReadVarUInt();
        Need(len);
        var d = new Decoder(_buffer, _position, len);
        _position += len;
        return d;
    }

    /// <summary>Returns a decoder over the remaining bytes (no copy) and consumes them.</summary>
    public Decoder ReadTailAsDecoder()
    {
        var d = new Decoder(_buffer, _position, _end - _position);
        _position = _end;
        return d;
    }

    /// <summary>Reads all remaining bytes (copied).</summary>
    public byte[] ReadTailAsUInt8Array()
    {
        return ReadUInt8Array(_end - _position);
    }

    /// <summary>Reads a 32 bit float (big endian).</summary>
    public float ReadFloat32()
    {
        Need(4);
        var v = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(_buffer.AsSpan(_position)));
        _position += 4;
        return v;
    }

    /// <summary>Reads a 64 bit float (big endian).</summary>
    public double ReadFloat64()
    {
        Need(8);
        var v = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(_buffer.AsSpan(_position)));
        _position += 8;
        return v;
    }

    /// <summary>Reads a 64 bit signed integer (big endian).</summary>
    public long ReadBigInt64()
    {
        Need(8);
        var v = BinaryPrimitives.ReadInt64BigEndian(_buffer.AsSpan(_position));
        _position += 8;
        return v;
    }

    /// <summary>Reads a 64 bit unsigned integer (big endian).</summary>
    public ulong ReadBigUInt64()
    {
        Need(8);
        var v = BinaryPrimitives.ReadUInt64BigEndian(_buffer.AsSpan(_position));
        _position += 8;
        return v;
    }

    /// <summary>Reads a value in the lib0 "Any" format.</summary>
    public object? ReadAny()
    {
        return AnyEncoding.Read(this);
    }
}

/// <summary>Thrown when a decoder runs out of data.</summary>
public sealed class EndOfDataException : FormatException
{
    /// <summary>Creates the exception.</summary>
    public EndOfDataException() : base("Unexpected end of array")
    {
    }
}
