using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace YjsSharp.Encoding;

/// <summary>
/// A growable binary writer implementing the lib0 encoding used by Yjs (variable length integers,
/// length-prefixed strings and byte arrays, and the lib0 "Any" format).
/// </summary>
public sealed class Encoder
{
    private byte[] _buffer;
    private int    _position;

    /// <summary>Creates an empty encoder.</summary>
    public Encoder() : this(64)
    {
    }

    /// <summary>Creates an empty encoder with the given initial capacity.</summary>
    public Encoder(int initialCapacity)
    {
        _buffer = new byte[Math.Max(initialCapacity, 8)];
    }

    /// <summary>The number of bytes written so far.</summary>
    public int Length => _position;

    /// <summary>Whether anything has been written.</summary>
    public bool HasContent => _position > 0;

    /// <summary>The bytes written so far, without copying.</summary>
    public ReadOnlySpan<byte> WrittenSpan => new ReadOnlySpan<byte>(_buffer, 0, _position);

    /// <summary>Returns a copy of the written bytes.</summary>
    public byte[] ToArray()
    {
        var result = new byte[_position];
        Buffer.BlockCopy(_buffer, 0, result, 0, _position);
        return result;
    }

    /// <summary>Discards everything written.</summary>
    public void Clear()
    {
        _position = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Ensure(int additional)
    {
        if (_buffer.Length - _position < additional)
        {
            Grow(additional);
        }
    }

    private void Grow(int additional)
    {
        var newSize = Math.Max(_buffer.Length * 2, _position + additional);
        var nb      = new byte[newSize];
        Buffer.BlockCopy(_buffer, 0, nb, 0, _position);
        _buffer = nb;
    }

    /// <summary>Writes a single byte.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt8(byte value)
    {
        if (_position == _buffer.Length)
        {
            Grow(1);
        }
        _buffer[_position++] = value;
    }

    /// <summary>Overwrites a byte at an already written position.</summary>
    public void SetUInt8(int position, byte value)
    {
        if ((uint)position >= (uint)_position)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }
        _buffer[position] = value;
    }

    /// <summary>Writes a 16 bit unsigned integer (little endian).</summary>
    public void WriteUInt16(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_position), value);
        _position += 2;
    }

    /// <summary>Writes a 32 bit unsigned integer (little endian).</summary>
    public void WriteUInt32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    /// <summary>Writes a 32 bit unsigned integer (big endian).</summary>
    public void WriteUInt32BigEndian(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_position), value);
        _position += 4;
    }

    /// <summary>Writes an unsigned integer with the lib0 variable length encoding (7 bits per byte).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteVarUInt(ulong value)
    {
        Ensure(10);
        var buf = _buffer;
        var pos = _position;
        while (value > 0x7F)
        {
            buf[pos++] =   (byte)(0x80 | (value & 0x7F));
            value      >>= 7;
        }
        buf[pos++] = (byte)value;
        _position  = pos;
    }

    /// <summary>Writes a signed integer with the lib0 variable length encoding (sign bit in the first byte).</summary>
    public void WriteVarInt(long value)
    {
        if (value < 0)
        {
            WriteVarInt((ulong)(-value), true);
        }
        else
        {
            WriteVarInt((ulong)value, false);
        }
    }

    /// <summary>
    /// Writes a signed integer given as magnitude and sign. This can express "negative zero", which lib0
    /// uses as a marker in its run length encoders.
    /// </summary>
    public void WriteVarInt(ulong magnitude, bool negative)
    {
        Ensure(11);
        var buf = _buffer;
        var pos = _position;
        buf[pos++] =   (byte)((magnitude > 0x3F ? 0x80 : 0) | (negative ? 0x40 : 0) | (int)(magnitude & 0x3F));
        magnitude  >>= 6;
        while (magnitude > 0)
        {
            buf[pos++] =   (byte)((magnitude > 0x7F ? 0x80 : 0) | (int)(magnitude & 0x7F));
            magnitude  >>= 7;
        }
        _position = pos;
    }

    /// <summary>Writes a UTF-8 string prefixed with its byte length.</summary>
    public void WriteVarString(string value)
    {
        var utf8 = System.Text.Encoding.UTF8;
        if (value.Length < 1024)
        {
            var max = value.Length * 3;
            Ensure(max + 5);
            // reserve one byte for the length if the string is short enough, otherwise compute exactly
            if (max < 128)
            {
                var written = utf8.GetBytes(value, 0, value.Length, _buffer, _position + 1);
                _buffer[_position] = (byte)written;
                _position += written + 1;
                return;
            }
        }
        var count = utf8.GetByteCount(value);
        WriteVarUInt((ulong)count);
        Ensure(count);
        utf8.GetBytes(value, 0, value.Length, _buffer, _position);
        _position += count;
    }

    /// <summary>Writes raw bytes without a length prefix.</summary>
    public void WriteUInt8Array(ReadOnlySpan<byte> value)
    {
        Ensure(value.Length);
        value.CopyTo(_buffer.AsSpan(_position));
        _position += value.Length;
    }

    /// <summary>Writes bytes prefixed with their length.</summary>
    public void WriteVarUInt8Array(ReadOnlySpan<byte> value)
    {
        WriteVarUInt((ulong)value.Length);
        WriteUInt8Array(value);
    }

    /// <summary>Writes a 32 bit float (big endian, like lib0).</summary>
    public void WriteFloat32(float value)
    {
        Ensure(4);
        BinaryPrimitives.WriteInt32BigEndian(_buffer.AsSpan(_position), BitConverter.SingleToInt32Bits(value));
        _position += 4;
    }

    /// <summary>Writes a 64 bit float (big endian, like lib0).</summary>
    public void WriteFloat64(double value)
    {
        Ensure(8);
        BinaryPrimitives.WriteInt64BigEndian(_buffer.AsSpan(_position), BitConverter.DoubleToInt64Bits(value));
        _position += 8;
    }

    /// <summary>Writes a 64 bit signed integer (big endian, like lib0 writeBigInt64).</summary>
    public void WriteBigInt64(long value)
    {
        Ensure(8);
        BinaryPrimitives.WriteInt64BigEndian(_buffer.AsSpan(_position), value);
        _position += 8;
    }

    /// <summary>Writes a 64 bit unsigned integer (big endian, like lib0 writeBigUint64).</summary>
    public void WriteBigUInt64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(_position), value);
        _position += 8;
    }

    /// <summary>
    /// Writes a value in the lib0 "Any" format. Supported values: <c>null</c>, <see cref="Undefined"/>, booleans,
    /// numbers, <see cref="System.Numerics.BigInteger"/>, strings, byte arrays, lists and string keyed dictionaries.
    /// </summary>
    public void WriteAny(object? value)
    {
        AnyEncoding.Write(this, value);
    }

    /// <summary>Writes the content of another encoder (no length prefix).</summary>
    public void WriteEncoder(Encoder other)
    {
        WriteUInt8Array(other.WrittenSpan);
    }

    /// <summary>Writes the content of another encoder prefixed with its length.</summary>
    public void WriteVarEncoder(Encoder other)
    {
        WriteVarUInt8Array(other.WrittenSpan);
    }
}
