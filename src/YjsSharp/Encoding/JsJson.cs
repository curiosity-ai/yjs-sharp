using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace YjsSharp.Encoding;

/// <summary>
/// <c>JSON.stringify</c> / <c>JSON.parse</c> with the exact output of JavaScript, used where Yjs stores JSON text
/// (update format v1 embeds and formatting attributes, and the legacy JSON content).
/// </summary>
public static class JsJson
{
    /// <summary>Serializes a value exactly like JavaScript's <c>JSON.stringify</c>. Returns null for undefined.</summary>
    public static string? Stringify(object? value)
    {
        if (value is Undefined)
        {
            return null;
        }
        var sb = new System.Text.StringBuilder();
        if (!Write(sb, value))
        {
            return null;
        }
        return sb.ToString();
    }

    private static bool Write(System.Text.StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                return true;
            case Undefined:
                return false;
            case string s:
                WriteString(sb, s);
                return true;
            case char c:
                WriteString(sb, c.ToString());
                return true;
            case bool b:
                sb.Append(b ? "true" : "false");
                return true;
            case double d:
                sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : NumberToString(d));
                return true;
            case float f:
                sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "null" : NumberToString(f));
                return true;
            case long l:
                sb.Append(Math.Abs(l) < (1L << 53) ? l.ToString(CultureInfo.InvariantCulture) : NumberToString(l));
                return true;
            case int i:
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                return true;
            case BigInteger:
                throw new InvalidOperationException("Do not know how to serialize a BigInt");
            case byte[] bytes:
                sb.Append('{');
                for (var i = 0; i < bytes.Length; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }
                    sb.Append('"').Append(i.ToString(CultureInfo.InvariantCulture)).Append("\":").Append(bytes[i].ToString(CultureInfo.InvariantCulture));
                }
                sb.Append('}');
                return true;
            case IDictionary<string, object?> dict:
                WriteObject(sb, dict);
                return true;
            case IReadOnlyDictionary<string, object?> rdict:
                WriteObject(sb, rdict);
                return true;
            case IDictionary ndict:
                WriteObject(sb, AnyValues.ToStringKeyed(ndict));
                return true;
            case IList list:
                sb.Append('[');
                for (var i = 0; i < list.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }
                    if (!Write(sb, list[i]))
                    {
                        sb.Append("null");
                    }
                }
                sb.Append(']');
                return true;
            case System.Text.Json.JsonElement je:
                return Write(sb, AnyValues.FromJsonElement(je));
            default:
                if (AnyValues.IsNumber(value))
                {
                    sb.Append(NumberToString(AnyValues.ToDouble(value)));
                    return true;
                }
                if (value is IEnumerable enumerable)
                {
                    var tmp = new List<object?>();
                    foreach (var o in enumerable)
                    {
                        tmp.Add(o);
                    }
                    return Write(sb, tmp);
                }
                throw new ArgumentException($"Value of type {value.GetType().FullName} cannot be serialized as JSON");
        }
    }

    private static void WriteObject(System.Text.StringBuilder sb, IEnumerable<KeyValuePair<string, object?>> dict)
    {
        sb.Append('{');
        var first = true;
        foreach (var kv in AnyValues.JsKeyOrder(dict))
        {
            if (kv.Value is Undefined)
            {
                continue;
            }
            if (!first)
            {
                sb.Append(',');
            }
            var mark = sb.Length;
            WriteString(sb, kv.Key);
            sb.Append(':');
            if (!Write(sb, kv.Value))
            {
                sb.Length = mark;
                if (!first)
                {
                    sb.Length--;
                }
                continue;
            }
            first = false;
        }
        sb.Append('}');
    }

    private static void WriteString(System.Text.StringBuilder sb, string s)
    {
        sb.Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c))
                    {
                        if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                        {
                            sb.Append(c).Append(s[i + 1]);
                            i++;
                        }
                        else
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                    }
                    else if (char.IsLowSurrogate(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>Formats a number exactly like JavaScript's <c>Number.prototype.toString()</c>.</summary>
    public static string NumberToString(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }
        if (double.IsPositiveInfinity(value))
        {
            return "Infinity";
        }
        if (double.IsNegativeInfinity(value))
        {
            return "-Infinity";
        }
        if (value == 0)
        {
            return "0";
        }
        var r        = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = r[0] == '-';
        if (negative)
        {
            r = r.Substring(1);
        }
        var exp  = 0;
        var eIdx = r.IndexOfAny(new[] { 'E', 'e' });
        if (eIdx >= 0)
        {
            exp = int.Parse(r.Substring(eIdx + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            r   = r.Substring(0, eIdx);
        }
        var dot    = r.IndexOf('.');
        var intLen = dot >= 0 ? dot : r.Length;
        var digits = dot >= 0 ? r.Remove(dot, 1) : r;
        // strip leading zeros (e.g. "0.001")
        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0')
        {
            lead++;
        }
        digits = digits.Substring(lead);
        var n = intLen - lead + exp;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
        {
            return "0";
        }
        var k  = digits.Length;
        var sb = new System.Text.StringBuilder();
        if (negative)
        {
            sb.Append('-');
        }
        if (k <= n && n <= 21)
        {
            sb.Append(digits);
            sb.Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            sb.Append(digits, 0, n);
            sb.Append('.');
            sb.Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            sb.Append("0.");
            sb.Append('0', -n);
            sb.Append(digits);
        }
        else
        {
            var e = n - 1;
            sb.Append(digits[0]);
            if (k > 1)
            {
                sb.Append('.');
                sb.Append(digits, 1, k - 1);
            }
            sb.Append('e');
            sb.Append(e >= 0 ? "+" : "-");
            sb.Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parses JSON text into Any values: objects become <see cref="Dictionary{TKey,TValue}"/>, arrays
    /// <see cref="List{T}"/>, integers <see cref="long"/> and other numbers <see cref="double"/>.
    /// </summary>
    public static object? Parse(string json)
    {
        var p     = 0;
        var value = ParseValue(json, ref p);
        SkipWs(json, ref p);
        if (p != json.Length)
        {
            throw new FormatException("Unexpected trailing characters in JSON");
        }
        return value;
    }

    private static void SkipWs(string s, ref int p)
    {
        while (p < s.Length && (s[p] == ' ' || s[p] == '\t' || s[p] == '\n' || s[p] == '\r'))
        {
            p++;
        }
    }

    private static object? ParseValue(string s, ref int p)
    {
        SkipWs(s, ref p);
        if (p >= s.Length)
        {
            throw new FormatException("Unexpected end of JSON input");
        }
        var c = s[p];
        switch (c)
        {
            case '{':
                {
                    p++;
                    var dict = new Dictionary<string, object?>();
                    SkipWs(s, ref p);
                    if (p < s.Length && s[p] == '}')
                    {
                        p++;
                        return dict;
                    }
                    while (true)
                    {
                        SkipWs(s, ref p);
                        var key = ParseString(s, ref p);
                        SkipWs(s, ref p);
                        Expect(s, ref p, ':');
                        dict[key] = ParseValue(s, ref p);
                        SkipWs(s, ref p);
                        if (p < s.Length && s[p] == ',')
                        {
                            p++;
                            continue;
                        }
                        Expect(s, ref p, '}');
                        return dict;
                    }
                }
            case '[':
                {
                    p++;
                    var list = new List<object?>();
                    SkipWs(s, ref p);
                    if (p < s.Length && s[p] == ']')
                    {
                        p++;
                        return list;
                    }
                    while (true)
                    {
                        list.Add(ParseValue(s, ref p));
                        SkipWs(s, ref p);
                        if (p < s.Length && s[p] == ',')
                        {
                            p++;
                            continue;
                        }
                        Expect(s, ref p, ']');
                        return list;
                    }
                }
            case '"':
                return ParseString(s, ref p);
            case 't':
                ExpectWord(s, ref p, "true");
                return true;
            case 'f':
                ExpectWord(s, ref p, "false");
                return false;
            case 'n':
                ExpectWord(s, ref p, "null");
                return null;
            default:
                return ParseNumber(s, ref p);
        }
    }

    private static void Expect(string s, ref int p, char c)
    {
        if (p >= s.Length || s[p] != c)
        {
            throw new FormatException($"Expected '{c}' in JSON at position {p}");
        }
        p++;
    }

    private static void ExpectWord(string s, ref int p, string word)
    {
        if (string.CompareOrdinal(s, p, word, 0, word.Length) != 0)
        {
            throw new FormatException($"Unexpected token in JSON at position {p}");
        }
        p += word.Length;
    }

    private static object ParseNumber(string s, ref int p)
    {
        var start   = p;
        var isFloat = false;
        if (p < s.Length && s[p] == '-')
        {
            p++;
        }
        while (p < s.Length)
        {
            var c = s[p];
            if (c >= '0' && c <= '9')
            {
                p++;
            }
            else if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')
            {
                isFloat = true;
                p++;
            }
            else
            {
                break;
            }
        }
        if (start == p)
        {
            throw new FormatException($"Unexpected token in JSON at position {p}");
        }
        var text = s.Substring(start, p - start);
        if (!isFloat && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) && Math.Abs(l) < (1L << 53))
        {
            if (l == 0 && text[0] == '-')
            {
                return -0.0;
            }
            return l;
        }
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static string ParseString(string s, ref int p)
    {
        Expect(s, ref p, '"');
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            if (p >= s.Length)
            {
                throw new FormatException("Unterminated string in JSON");
            }
            var c = s[p++];
            if (c == '"')
            {
                return sb.ToString();
            }
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            if (p >= s.Length)
            {
                throw new FormatException("Unterminated string in JSON");
            }
            var e = s[p++];
            switch (e)
            {
                case '"':
                    sb.Append('"');
                    break;
                case '\\':
                    sb.Append('\\');
                    break;
                case '/':
                    sb.Append('/');
                    break;
                case 'b':
                    sb.Append('\b');
                    break;
                case 'f':
                    sb.Append('\f');
                    break;
                case 'n':
                    sb.Append('\n');
                    break;
                case 'r':
                    sb.Append('\r');
                    break;
                case 't':
                    sb.Append('\t');
                    break;
                case 'u':
                    if (p + 4 > s.Length)
                    {
                        throw new FormatException("Bad unicode escape in JSON");
                    }
                    sb.Append((char)int.Parse(s.Substring(p, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    p += 4;
                    break;
                default:
                    throw new FormatException("Bad escaped character in JSON");
            }
        }
    }
}
