using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace YjsSharp.Encoding;

/// <summary>Helpers for working with "Any" values (the JSON-like values Yjs stores).</summary>
public static class AnyValues
{
    /// <summary>
    /// Orders dictionary entries the way JavaScript's <c>Object.keys</c> does: array index keys first, ascending,
    /// then all other keys in insertion order.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, object?>> JsKeyOrder(IEnumerable<KeyValuePair<string, object?>> dict)
    {
        var list       = new List<KeyValuePair<string, object?>>();
        var hasIndexes = false;
        foreach (var kv in dict)
        {
            list.Add(kv);
            if (!hasIndexes && IsArrayIndex(kv.Key, out _))
            {
                hasIndexes = true;
            }
        }
        if (!hasIndexes)
        {
            return list;
        }
        var indexes = new List<(uint Index, KeyValuePair<string, object?> Entry)>();
        var others  = new List<KeyValuePair<string, object?>>();
        foreach (var kv in list)
        {
            if (IsArrayIndex(kv.Key, out var idx))
            {
                indexes.Add((idx, kv));
            }
            else
            {
                others.Add(kv);
            }
        }
        indexes.Sort((a, b) => a.Index.CompareTo(b.Index));
        var result = new List<KeyValuePair<string, object?>>(list.Count);
        foreach (var e in indexes)
        {
            result.Add(e.Entry);
        }
        result.AddRange(others);
        return result;
    }

    internal static bool IsArrayIndex(string key, out uint index)
    {
        index = 0;
        if (key.Length == 0 || key.Length > 10)
        {
            return false;
        }
        if (key.Length > 1 && key[0] == '0')
        {
            return false;
        }
        ulong v = 0;
        foreach (var c in key)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
            v = v * 10 + (ulong)(c - '0');
        }
        if (v >= uint.MaxValue)
        {
            return false;
        }
        index = (uint)v;
        return true;
    }

    internal static Dictionary<string, object?> ToStringKeyed(IDictionary dict)
    {
        var result = new Dictionary<string, object?>(dict.Count);
        foreach (DictionaryEntry e in dict)
        {
            result[Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? string.Empty] = e.Value;
        }
        return result;
    }

    /// <summary>Converts a <see cref="System.Text.Json.JsonElement"/> to Any values.</summary>
    public static object? FromJsonElement(System.Text.Json.JsonElement element)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Null:
            case System.Text.Json.JsonValueKind.Undefined:
                return null;
            case System.Text.Json.JsonValueKind.True:
                return true;
            case System.Text.Json.JsonValueKind.False:
                return false;
            case System.Text.Json.JsonValueKind.String:
                return element.GetString();
            case System.Text.Json.JsonValueKind.Number:
                if (element.TryGetInt64(out var l))
                {
                    return l;
                }
                return element.GetDouble();
            case System.Text.Json.JsonValueKind.Array:
                {
                    var list = new List<object?>();
                    foreach (var e in element.EnumerateArray())
                    {
                        list.Add(FromJsonElement(e));
                    }
                    return list;
                }
            case System.Text.Json.JsonValueKind.Object:
                {
                    var dict = new Dictionary<string, object?>();
                    foreach (var p in element.EnumerateObject())
                    {
                        dict[p.Name] = FromJsonElement(p.Value);
                    }
                    return dict;
                }
            default:
                return null;
        }
    }

    internal static bool IsNumber(object? v)
    {
        return v is double || v is long || v is int || v is float || v is short || v is ushort || v is sbyte
               || v is byte || v is uint || v is ulong || v is decimal;
    }

    internal static double ToDouble(object v)
    {
        return Convert.ToDouble(v, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// JavaScript strict equality (<c>===</c>) for Any values: primitives by value (numbers compared numerically),
    /// objects, arrays and byte arrays by reference.
    /// </summary>
    public static bool StrictEquals(object? a, object? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a is null || b is null)
        {
            return false;
        }
        if (a is string sa)
        {
            return b is string sb && string.Equals(sa, sb, StringComparison.Ordinal);
        }
        if (a is bool ba)
        {
            return b is bool bb && ba == bb;
        }
        if (IsNumber(a))
        {
            if (!IsNumber(b))
            {
                return false;
            }
            if (a is long la && b is long lb)
            {
                return la == lb;
            }
            return ToDouble(a) == ToDouble(b);
        }
        if (a is BigInteger ia)
        {
            return b is BigInteger ib && ia == ib;
        }
        if (a is Undefined)
        {
            return b is Undefined;
        }
        if (a is char ca)
        {
            return (b is char cb && ca == cb) || (b is string s2 && s2.Length == 1 && s2[0] == ca);
        }
        return false;
    }

    /// <summary>
    /// Yjs' <c>equalAttrs</c>: strict equality, or for two objects a shallow comparison of their entries.
    /// </summary>
    public static bool EqualAttrs(object? a, object? b)
    {
        if (StrictEquals(a, b))
        {
            return true;
        }
        if (a is null || b is null)
        {
            return false;
        }
        if (IsObject(a) && IsObject(b))
        {
            return EqualFlat(a, b);
        }
        if (IsObjectLike(a) && IsObjectLike(b))
        {
            return EqualFlat(a, b);
        }
        return false;
    }

    private static bool IsObject(object v) => v is IDictionary<string, object?> || v is IReadOnlyDictionary<string, object?> || v is IDictionary;

    private static bool IsObjectLike(object v) => v is IList || IsObject(v);

    private static IEnumerable<KeyValuePair<string, object?>> Entries(object v)
    {
        switch (v)
        {
            case IDictionary<string, object?> d:
                return d;
            case IReadOnlyDictionary<string, object?> r:
                return r;
            case IDictionary nd:
                return ToStringKeyed(nd);
            case IList list:
                {
                    var res = new List<KeyValuePair<string, object?>>();
                    for (var i = 0; i < list.Count; i++)
                    {
                        res.Add(new KeyValuePair<string, object?>(i.ToString(CultureInfo.InvariantCulture), list[i]));
                    }
                    return res;
                }
            default:
                return Array.Empty<KeyValuePair<string, object?>>();
        }
    }

    private static bool EqualFlat(object a, object b)
    {
        var db = new Dictionary<string, object?>();
        foreach (var kv in Entries(b))
        {
            db[kv.Key] = kv.Value;
        }
        var count = 0;
        foreach (var kv in Entries(a))
        {
            count++;
            if (!db.TryGetValue(kv.Key, out var vb) || !StrictEquals(kv.Value, vb))
            {
                return false;
            }
        }
        return count == db.Count;
    }

    /// <summary>Structural (deep) equality of Any values, comparing numbers numerically.</summary>
    public static bool DeepEquals(object? a, object? b)
    {
        if (StrictEquals(a, b))
        {
            return true;
        }
        if (a is null || b is null)
        {
            return false;
        }
        if (a is byte[] ba && b is byte[] bb)
        {
            return ba.AsSpan().SequenceEqual(bb);
        }
        if (IsObject(a) && IsObject(b))
        {
            var da = new Dictionary<string, object?>();
            foreach (var kv in Entries(a))
            {
                da[kv.Key] = kv.Value;
            }
            var count = 0;
            foreach (var kv in Entries(b))
            {
                count++;
                if (!da.TryGetValue(kv.Key, out var va) || !DeepEquals(va, kv.Value))
                {
                    return false;
                }
            }
            return count == da.Count;
        }
        if (a is IList la && b is IList lb && !(a is byte[]) && !(b is byte[]))
        {
            if (la.Count != lb.Count)
            {
                return false;
            }
            for (var i = 0; i < la.Count; i++)
            {
                if (!DeepEquals(la[i], lb[i]))
                {
                    return false;
                }
            }
            return true;
        }
        return false;
    }
}
