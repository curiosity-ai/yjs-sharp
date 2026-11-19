using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using YjsSharp.Encoding;

namespace YjsSharp.Tests.Compat;

/// <summary>
/// The C# twin of tests/fixtures-generator/dsl.mjs: executes the same operation scripts on YjsSharp documents,
/// recording the same things the generator records with Yjs, so the two can be compared byte for byte.
/// </summary>
internal sealed class ScenarioRunner
{
    /// <summary>Origin used when applying updates that came from another peer, so they are not logged.</summary>
    public const string Remote = "remote";

    public ScenarioRunner(JsonObject scenario)
    {
        Roots = (JsonObject)scenario["roots"]!;
        GC    = (bool?)scenario["gc"] ?? true;
        var ids = scenario["clientIds"]!.AsArray().Select(n => (ulong)n!).ToArray();
        Docs    = new Doc[ids.Length];
        Updates = new List<(byte[] V1, byte[] V2)>[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            var doc     = new Doc(new DocOptions { ClientId = ids[i], GC = GC });
            var updates = new List<(byte[] V1, byte[] V2)>();
            var pending = (byte[]?)null;
            doc.Update += (_, e) =>
            {
                if (!Equals(e.Origin, Remote))
                {
                    pending = e.Update;
                }
            };
            doc.UpdateV2 += (_, e) =>
            {
                if (!Equals(e.Origin, Remote))
                {
                    updates.Add((pending!, e.Update));
                }
            };
            Docs[i]    = doc;
            Updates[i] = updates;
        }
    }

    public JsonObject Roots { get; }

    public bool GC { get; }

    public Doc[] Docs { get; }

    /// <summary>The local update events of every peer, v1 and v2.</summary>
    public List<(byte[] V1, byte[] V2)>[] Updates { get; }

    public List<(string Id, int Peer, byte[] StateVector)> Checkpoints { get; } = new List<(string, int, byte[])>();

    public List<(string Id, int Peer, Snapshot Snapshot)> Snapshots { get; } = new List<(string, int, Snapshot)>();

    public List<(string Id, int Peer, RelativePosition Position)> RelativePositions { get; } = new List<(string, int, RelativePosition)>();

    public static ScenarioRunner Run(JsonObject scenario)
    {
        var runner = new ScenarioRunner(scenario);
        foreach (var op in scenario["ops"]!.AsArray())
        {
            runner.Apply((JsonObject)op!);
        }
        return runner;
    }

    public void Apply(JsonObject op)
    {
        switch ((string)op["op"]!)
        {
            case "sync":
            {
                var from = Docs[(int)op["from"]!];
                var to   = Docs[(int)op["to"]!];
                to.ApplyUpdate(from.EncodeStateAsUpdate(to.EncodeStateVector()), Remote);
                return;
            }
            case "syncAll":
                for (var pass = 0; pass < 2; pass++)
                {
                    for (var a = 0; a < Docs.Length; a++)
                    {
                        for (var b = 0; b < Docs.Length; b++)
                        {
                            if (a != b)
                            {
                                Docs[b].ApplyUpdate(Docs[a].EncodeStateAsUpdate(Docs[b].EncodeStateVector()), Remote);
                            }
                        }
                    }
                }
                return;
            case "deliver":
            {
                var to = Docs[(int)op["to"]!];
                var v2 = (bool?)op["v2"] ?? false;
                foreach (var index in op["indices"]!.AsArray())
                {
                    var u = Updates[(int)op["from"]!][(int)index!];
                    if (v2)
                    {
                        to.ApplyUpdateV2(u.V2, Remote);
                    }
                    else
                    {
                        to.ApplyUpdate(u.V1, Remote);
                    }
                }
                return;
            }
            case "checkpoint":
                Checkpoints.Add(((string)op["id"]!, (int)op["peer"]!, Docs[(int)op["peer"]!].EncodeStateVector()));
                return;
            case "snapshot":
                Snapshots.Add(((string)op["id"]!, (int)op["peer"]!, Y.Snapshot(Docs[(int)op["peer"]!])));
                return;
            case "relPos":
            {
                var peer = (int)op["peer"]!;
                var type = Resolve(Docs[peer], Roots, op["target"]!);
                var pos  = Y.CreateRelativePositionFromTypeIndex((AbstractType)type, (int)op["index"]!, (int?)op["assoc"] ?? 0);
                RelativePositions.Add(((string)op["id"]!, peer, pos));
                return;
            }
            default:
                ApplyDocOp(Docs[(int)op["peer"]!], Roots, op);
                return;
        }
    }

    public static object GetRoot(Doc doc, string name, string kind)
    {
        switch (kind)
        {
            case "text": return doc.GetText(name);
            case "array": return doc.GetArray(name);
            case "map": return doc.GetMap(name);
            case "xmlFragment": return doc.GetXmlFragment(name);
            default: throw new ArgumentException("unknown root kind " + kind);
        }
    }

    public static object Resolve(Doc doc, JsonObject roots, JsonNode target)
    {
        var path = target is JsonArray arr ? arr.ToList() : new List<JsonNode?> { target };
        var root = (string)path[0]!;
        object? cur = GetRoot(doc, root, (string)roots[root]!);
        for (var i = 1; i < path.Count; i++)
        {
            var step = path[i]!;
            cur = cur switch
            {
                YMap m => m.Get((string)step!),
                YArray a => a.Get((int)step!),
                XmlElement e => e.Get((int)step!),
                XmlFragment f => f.Get((int)step!),
                _ => throw new InvalidOperationException($"cannot step into {cur} with {step.ToJsonString()}")
            };
        }
        return cur ?? throw new InvalidOperationException("target resolved to null: " + target.ToJsonString());
    }

    /// <summary>Turns a script value into a value YjsSharp accepts (see dsl.mjs toYValue).</summary>
    public static object? ToYValue(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonArray array:
                return array.Select(ToYValue).ToList();
            case JsonObject obj when obj["$y"] is JsonValue kind:
                switch ((string)kind!)
                {
                    case "text": return new Text();
                    case "array": return new YArray();
                    case "map": return new YMap();
                    case "xmlText": return new XmlText();
                    case "xmlFragment": return new XmlFragment();
                    case "xmlElement": return new XmlElement((string)obj["name"]!);
                    case "bytes": return Convert.FromBase64String((string)obj["b64"]!);
                    case "doc":
                    {
                        var options = new DocOptions { Guid = (string)obj["guid"]! };
                        if (obj.ContainsKey("meta"))
                        {
                            options.Meta = ToYValue(obj["meta"]);
                        }
                        if (obj.ContainsKey("autoLoad"))
                        {
                            options.AutoLoad = (bool)obj["autoLoad"]!;
                        }
                        return new Doc(options);
                    }
                    default: throw new ArgumentException("unknown $y " + kind);
                }
            case JsonObject obj:
            {
                var dict = new Dictionary<string, object?>();
                foreach (var kv in obj)
                {
                    dict[kv.Key] = ToYValue(kv.Value);
                }
                return dict;
            }
            case JsonValue value:
                if (value.TryGetValue<string>(out var s))
                {
                    return s;
                }
                if (value.TryGetValue<bool>(out var b))
                {
                    return b;
                }
                var raw = value.ToJsonString();
                if (long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) && Math.Abs(l) <= (1L << 53))
                {
                    return l;
                }
                return double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            default:
                throw new ArgumentException("unexpected node " + node);
        }
    }

    private static IDictionary<string, object?>? Attrs(JsonNode? node) => node == null ? null : (IDictionary<string, object?>)ToYValue(node)!;

    public static void ApplyDocOp(Doc doc, JsonObject roots, JsonObject op)
    {
        switch ((string)op["op"]!)
        {
            case "transact":
                doc.Transact(_ =>
                {
                    foreach (var inner in op["ops"]!.AsArray())
                    {
                        ApplyDocOp(doc, roots, (JsonObject)inner!);
                    }
                });
                return;
            case "textInsert":
                ((TextBase)Resolve(doc, roots, op["target"]!)).Insert((int)op["index"]!, (string)op["text"]!, Attrs(op["attrs"]));
                return;
            case "textDelete":
                ((TextBase)Resolve(doc, roots, op["target"]!)).Delete((int)op["index"]!, (int)op["length"]!);
                return;
            case "textFormat":
                ((TextBase)Resolve(doc, roots, op["target"]!)).Format((int)op["index"]!, (int)op["length"]!, Attrs(op["attrs"])!);
                return;
            case "textInsertEmbed":
                ((TextBase)Resolve(doc, roots, op["target"]!)).InsertEmbed((int)op["index"]!, ToYValue(op["embed"])!, Attrs(op["attrs"]));
                return;
            case "textApplyDelta":
                ((TextBase)Resolve(doc, roots, op["target"]!)).ApplyDelta(op["delta"]!.AsArray().Select(d => ToDelta((JsonObject)d!)).ToList());
                return;
            case "arrayInsert":
                ((YArray)Resolve(doc, roots, op["target"]!)).Insert((int)op["index"]!, op["values"]!.AsArray().Select(ToYValue).ToList());
                return;
            case "arrayPush":
                ((YArray)Resolve(doc, roots, op["target"]!)).Push(op["values"]!.AsArray().Select(ToYValue).ToList());
                return;
            case "arrayDelete":
                ((YArray)Resolve(doc, roots, op["target"]!)).Delete((int)op["index"]!, (int)op["length"]!);
                return;
            case "mapSet":
                ((YMap)Resolve(doc, roots, op["target"]!)).Set((string)op["key"]!, ToYValue(op["value"]));
                return;
            case "mapDelete":
                ((YMap)Resolve(doc, roots, op["target"]!)).Delete((string)op["key"]!);
                return;
            case "mapClear":
                ((YMap)Resolve(doc, roots, op["target"]!)).Clear();
                return;
            case "xmlInsert":
            {
                var nodes = op["nodes"]!.AsArray().Select(n => ToYValue(n)).ToList();
                var target = Resolve(doc, roots, op["target"]!);
                if (target is XmlElement element)
                {
                    element.Insert((int)op["index"]!, nodes);
                }
                else
                {
                    ((XmlFragment)target).Insert((int)op["index"]!, nodes);
                }
                return;
            }
            case "xmlDelete":
            {
                var target = Resolve(doc, roots, op["target"]!);
                if (target is XmlElement element)
                {
                    element.Delete((int)op["index"]!, (int)op["length"]!);
                }
                else
                {
                    ((XmlFragment)target).Delete((int)op["index"]!, (int)op["length"]!);
                }
                return;
            }
            case "xmlSetAttribute":
                ((XmlElement)Resolve(doc, roots, op["target"]!)).SetAttribute((string)op["name"]!, (string)op["value"]!);
                return;
            case "xmlRemoveAttribute":
                ((XmlElement)Resolve(doc, roots, op["target"]!)).RemoveAttribute((string)op["name"]!);
                return;
            default:
                throw new ArgumentException("unknown op " + op["op"]);
        }
    }

    private static Delta ToDelta(JsonObject d)
    {
        if (d.ContainsKey("insert"))
        {
            return Delta.Inserted(ToYValue(d["insert"])!, Attrs(d["attributes"]));
        }
        if (d.ContainsKey("delete"))
        {
            return Delta.Deleted((int)d["delete"]!);
        }
        return Delta.Retained((int)d["retain"]!, Attrs(d["attributes"]));
    }

    // ------------------------------------------------------------------------------- content

    /// <summary>The canonical JSON of every root (dsl.mjs contentOf), map keys sorted.</summary>
    public static (string Content, string Deltas) ContentOf(Doc doc, JsonObject roots, Snapshot? snapshot = null)
    {
        var content = new StringBuilder("{");
        var deltas  = new StringBuilder("{");
        var first   = true;
        var firstD  = true;
        foreach (var name in roots.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
        {
            var kind = (string)roots[name]!;
            var root = GetRoot(doc, name, kind);
            if (!first)
            {
                content.Append(',');
            }
            first = false;
            content.Append(JsJson.Stringify(name)).Append(':');
            WritePlain(content, root);
            if (kind == "text")
            {
                if (!firstD)
                {
                    deltas.Append(',');
                }
                firstD = false;
                deltas.Append(JsJson.Stringify(name)).Append(':');
                WriteDelta(deltas, snapshot == null ? ((Text)root).ToDelta() : ((Text)root).ToDelta(snapshot));
            }
        }
        return (content.Append('}').ToString(), deltas.Append('}').ToString());
    }

    public static void WriteDelta(StringBuilder sb, IReadOnlyList<Delta> delta)
    {
        var list = new List<object?>();
        foreach (var d in delta)
        {
            var entry = new Dictionary<string, object?>();
            if (d.Insert != null)
            {
                entry["insert"] = d.Insert;
            }
            if (d.Delete != null)
            {
                entry["delete"] = (long)d.Delete.Value;
            }
            if (d.Retain != null)
            {
                entry["retain"] = (long)d.Retain.Value;
            }
            if (d.Attributes != null && d.Attributes.Count > 0)
            {
                entry["attributes"] = d.Attributes;
            }
            list.Add(entry);
        }
        WritePlain(sb, list);
    }

    /// <summary>Writes a YjsSharp value as canonical JSON (dsl.mjs toPlain).</summary>
    public static void WritePlain(StringBuilder sb, object? v)
    {
        switch (v)
        {
            case null:
            case Undefined:
                sb.Append("null");
                return;
            case XmlText or XmlElement or XmlFragment or XmlHook:
                sb.Append(JsJson.Stringify(v.ToString()));
                return;
            case Text t:
                sb.Append(JsJson.Stringify(t.ToString()));
                return;
            case YArray a:
                WriteList(sb, a.ToArray());
                return;
            case YMap m:
                WriteObject(sb, m.Keys.Select(k => new KeyValuePair<string, object?>(k, m.Get(k))));
                return;
            case Doc d:
                sb.Append("{\"$doc\":").Append(JsJson.Stringify(d.Guid)).Append('}');
                return;
            case byte[] bytes:
                sb.Append("{\"$bytes\":").Append(JsJson.Stringify(Convert.ToBase64String(bytes))).Append('}');
                return;
            case string s:
                sb.Append(JsJson.Stringify(s));
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case IDictionary<string, object?> dict:
                WriteObject(sb, dict);
                return;
            case IReadOnlyDictionary<string, object?> rdict:
                WriteObject(sb, rdict);
                return;
            case IList list:
                WriteList(sb, list.Cast<object?>());
                return;
            default:
                if (AnyValues.IsNumber(v))
                {
                    sb.Append(JsJson.NumberToString(AnyValues.ToDouble(v)));
                    return;
                }
                throw new ArgumentException("cannot write " + v.GetType());
        }
    }

    private static void WriteList(StringBuilder sb, IEnumerable<object?> items)
    {
        sb.Append('[');
        var first = true;
        foreach (var item in items)
        {
            if (!first)
            {
                sb.Append(',');
            }
            first = false;
            WritePlain(sb, item);
        }
        sb.Append(']');
    }

    private static void WriteObject(StringBuilder sb, IEnumerable<KeyValuePair<string, object?>> entries)
    {
        sb.Append('{');
        var first = true;
        foreach (var kv in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                sb.Append(',');
            }
            first = false;
            sb.Append(JsJson.Stringify(kv.Key)).Append(':');
            WritePlain(sb, kv.Value);
        }
        sb.Append('}');
    }

    /// <summary>Canonical JSON of a fixture node, written like <see cref="WritePlain"/> (sorted keys, JS numbers).</summary>
    public static string Canonical(JsonNode? node)
    {
        var sb = new StringBuilder();
        WriteCanonical(sb, node);
        return sb.ToString();
    }

    private static void WriteCanonical(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;
            case JsonArray array:
                sb.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }
                    WriteCanonical(sb, array[i]);
                }
                sb.Append(']');
                return;
            case JsonObject obj:
            {
                sb.Append('{');
                var first = true;
                foreach (var kv in obj.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    sb.Append(JsJson.Stringify(kv.Key)).Append(':');
                    WriteCanonical(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            case JsonValue value:
                if (value.TryGetValue<string>(out var s))
                {
                    sb.Append(JsJson.Stringify(s));
                }
                else if (value.TryGetValue<bool>(out var b))
                {
                    sb.Append(b ? "true" : "false");
                }
                else
                {
                    sb.Append(JsJson.NumberToString(value.GetValue<double>()));
                }
                return;
        }
    }
}
