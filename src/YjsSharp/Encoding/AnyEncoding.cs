using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;

namespace YjsSharp.Encoding;

/// <summary>Reads and writes the lib0 "Any" format with JavaScript compatible semantics.</summary>
internal static class AnyEncoding
{
    internal const byte TypeUndefined  = 127;
    internal const byte TypeNull       = 126;
    internal const byte TypeInteger    = 125;
    internal const byte TypeFloat32    = 124;
    internal const byte TypeFloat64    = 123;
    internal const byte TypeBigInt     = 122;
    internal const byte TypeFalse      = 121;
    internal const byte TypeTrue       = 120;
    internal const byte TypeString     = 119;
    internal const byte TypeObject     = 118;
    internal const byte TypeArray      = 117;
    internal const byte TypeUint8Array = 116;

    private const long Bits31 = 0x7FFFFFFF;

    public static void Write(Encoder encoder, object? data)
    {
        switch (data)
        {
            case null:
                encoder.WriteUInt8(TypeNull);
                return;
            case string s:
                encoder.WriteUInt8(TypeString);
                encoder.WriteVarString(s);
                return;
            case bool b:
                encoder.WriteUInt8(b ? TypeTrue : TypeFalse);
                return;
            case double d:
                WriteNumber(encoder, d);
                return;
            case long l:
                WriteInteger(encoder, l);
                return;
            case int i:
                WriteInteger(encoder, i);
                return;
            case byte[] bytes:
                encoder.WriteUInt8(TypeUint8Array);
                encoder.WriteVarUInt8Array(bytes);
                return;
            case Undefined:
                encoder.WriteUInt8(TypeUndefined);
                return;
            case float f:
                WriteNumber(encoder, f);
                return;
            case short sh:
                WriteInteger(encoder, sh);
                return;
            case ushort ush:
                WriteInteger(encoder, ush);
                return;
            case sbyte sb:
                WriteInteger(encoder, sb);
                return;
            case byte by:
                WriteInteger(encoder, by);
                return;
            case uint ui:
                WriteInteger(encoder, ui);
                return;
            case ulong ul:
                if (ul <= long.MaxValue)
                {
                    WriteInteger(encoder, (long)ul);
                }
                else
                {
                    WriteNumber(encoder, ul);
                }
                return;
            case decimal dec:
                WriteNumber(encoder, (double)dec);
                return;
            case char c:
                encoder.WriteUInt8(TypeString);
                encoder.WriteVarString(c.ToString());
                return;
            case BigInteger bi:
                encoder.WriteUInt8(TypeBigInt);
                encoder.WriteBigInt64(unchecked((long)(ulong)(bi & ulong.MaxValue)));
                return;
            case ReadOnlyMemory<byte> rom:
                encoder.WriteUInt8(TypeUint8Array);
                encoder.WriteVarUInt8Array(rom.Span);
                return;
            case Memory<byte> mem:
                encoder.WriteUInt8(TypeUint8Array);
                encoder.WriteVarUInt8Array(mem.Span);
                return;
            case IDictionary<string, object?> dict:
                WriteObject(encoder, dict);
                return;
            case IReadOnlyDictionary<string, object?> rdict:
                WriteObject(encoder, rdict);
                return;
            case IDictionary ndict:
                WriteObject(encoder, AnyValues.ToStringKeyed(ndict));
                return;
            case IList list:
                encoder.WriteUInt8(TypeArray);
                encoder.WriteVarUInt((ulong)list.Count);
                for (var i = 0; i < list.Count; i++)
                {
                    Write(encoder, list[i]);
                }
                return;
            case System.Text.Json.JsonElement je:
                Write(encoder, AnyValues.FromJsonElement(je));
                return;
            case IEnumerable enumerable:
                {
                    var tmp = new List<object?>();
                    foreach (var o in enumerable)
                    {
                        tmp.Add(o);
                    }
                    Write(encoder, tmp);
                    return;
                }
            default:
                throw new ArgumentException($"Value of type {data.GetType().FullName} cannot be encoded as a Yjs value");
        }
    }

    private static void WriteObject(Encoder encoder, IEnumerable<KeyValuePair<string, object?>> dict)
    {
        encoder.WriteUInt8(TypeObject);
        var ordered = AnyValues.JsKeyOrder(dict);
        encoder.WriteVarUInt((ulong)ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            encoder.WriteVarString(ordered[i].Key);
            Write(encoder, ordered[i].Value);
        }
    }

    private static void WriteInteger(Encoder encoder, long value)
    {
        if (value <= Bits31 && value >= -Bits31)
        {
            encoder.WriteUInt8(TypeInteger);
            encoder.WriteVarInt(value);
        }
        else
        {
            WriteNumber(encoder, value);
        }
    }

    internal static bool IsFloat32(double value)
    {
        return (double)(float)value == value;
    }

    private static void WriteNumber(Encoder encoder, double value)
    {
        if (!double.IsNaN(value) && !double.IsInfinity(value) && Math.Floor(value) == value && Math.Abs(value) <= Bits31)
        {
            encoder.WriteUInt8(TypeInteger);
            if (value == 0 && double.IsNegative(value))
            {
                encoder.WriteVarInt(0UL, true);
            }
            else
            {
                encoder.WriteVarInt((long)value);
            }
        }
        else if (IsFloat32(value))
        {
            encoder.WriteUInt8(TypeFloat32);
            encoder.WriteFloat32((float)value);
        }
        else
        {
            encoder.WriteUInt8(TypeFloat64);
            encoder.WriteFloat64(value);
        }
    }

    public static object? Read(Decoder decoder)
    {
        var tag = decoder.ReadUInt8();
        switch (tag)
        {
            case TypeUndefined:
                return Undefined.Value;
            case TypeNull:
                return null;
            case TypeInteger:
                {
                    var magnitude = decoder.ReadVarInt(out var negative);
                    if (negative)
                    {
                        if (magnitude == 0)
                        {
                            return -0.0;
                        }
                        return -(long)magnitude;
                    }
                    return (long)magnitude;
                }
            case TypeFloat32:
                return (double)decoder.ReadFloat32();
            case TypeFloat64:
                return decoder.ReadFloat64();
            case TypeBigInt:
                return new BigInteger(decoder.ReadBigInt64());
            case TypeFalse:
                return false;
            case TypeTrue:
                return true;
            case TypeString:
                return decoder.ReadVarString();
            case TypeObject:
                {
                    var len = (int)decoder.ReadVarUInt();
                    var obj = new Dictionary<string, object?>(len);
                    for (var i = 0; i < len; i++)
                    {
                        var key = decoder.ReadVarString();
                        obj[key] = Read(decoder);
                    }
                    return obj;
                }
            case TypeArray:
                {
                    var len = (int)decoder.ReadVarUInt();
                    var arr = new List<object?>(Math.Min(len, 1024));
                    for (var i = 0; i < len; i++)
                    {
                        arr.Add(Read(decoder));
                    }
                    return arr;
                }
            case TypeUint8Array:
                return decoder.ReadVarUInt8Array();
            default:
                throw new FormatException($"Unknown lib0 Any type tag {tag}");
        }
    }
}
