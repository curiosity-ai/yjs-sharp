using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using YjsSharp.Encoding;
using YjsSharp.Structs;
using YjsSharp.Updates;

namespace YjsSharp;

/// <summary>Walks the XML tree below a root in document order (Y.XmlTreeWalker).</summary>
public sealed class XmlTreeWalker : IEnumerable<AbstractType>
{
    private readonly XmlFragment              _root;
    private readonly Func<AbstractType, bool> _filter;

    internal XmlTreeWalker(XmlFragment root, Func<AbstractType, bool>? filter)
    {
        _root   = root;
        _filter = filter ?? (_ => true);
    }

    /// <inheritdoc />
    public IEnumerator<AbstractType> GetEnumerator()
    {
        var n         = _root._start;
        var firstCall = true;
        while (true)
        {
            var type = (n?.ContentField as Structs.ContentType)?.Type;
            if (n != null && (!firstCall || n.Deleted || !_filter(type!)))
            {
                do
                {
                    type = ((Structs.ContentType)n!.ContentField).Type;
                    if (!n.Deleted && (type.GetType() == typeof(XmlElement) || type.GetType() == typeof(XmlFragment)) && type._start != null)
                    {
                        n = type._start;
                    }
                    else
                    {
                        while (n != null)
                        {
                            var nxt = n.Next;
                            if (nxt != null)
                            {
                                n = nxt;
                                break;
                            }
                            if (n.Parent == _root)
                            {
                                n = null;
                            }
                            else
                            {
                                n = n.Parent!._item;
                            }
                        }
                    }
                }
                while (n != null && (n.Deleted || !_filter(((Structs.ContentType)n.ContentField).Type)));
            }
            firstCall = false;
            if (n == null)
            {
                yield break;
            }
            yield return ((Structs.ContentType)n.ContentField).Type;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A shared XML fragment (Y.XmlFragment): an ordered list of XML elements and texts.</summary>
public class XmlFragment : AbstractType
{
    internal const uint TypeRefId = 4;

    internal List<object?>? _prelimContent = new List<object?>();

    /// <summary>Creates a fragment that is not attached to a document yet.</summary>
    public XmlFragment()
    {
    }

    /// <summary>The first child, or null.</summary>
    public AbstractType? FirstChild
    {
        get
        {
            var first = First;
            return first != null ? (AbstractType?)first.ContentField.GetContent()[0] : null;
        }
    }

    internal override void Integrate(Doc y, Item? item)
    {
        base.Integrate(y, item);
        var prelim = _prelimContent!;
        _prelimContent = null;
        if (prelim.Count > 0)
        {
            Insert(0, prelim);
        }
    }

    internal override AbstractType InternalCopy() => new XmlFragment();

    /// <inheritdoc />
    public override AbstractType Clone()
    {
        var el   = new XmlFragment();
        var list = new List<object?>();
        foreach (var item in ToArray())
        {
            list.Add(item is AbstractType t ? t.Clone() : item);
        }
        el.Insert(0, list);
        return el;
    }

    /// <summary>The number of children.</summary>
    public int Length => _prelimContent?.Count ?? _length;

    /// <summary>Creates a walker over all descendants (optionally filtered).</summary>
    public XmlTreeWalker CreateTreeWalker(Func<AbstractType, bool>? filter = null) => new XmlTreeWalker(this, filter);

    /// <summary>The first descendant element with the given node name (case insensitive), or null.</summary>
    public XmlElement? QuerySelector(string query)
    {
        foreach (var t in QuerySelectorAll(query))
        {
            return t;
        }
        return null;
    }

    /// <summary>All descendant elements with the given node name (case insensitive).</summary>
    public IEnumerable<XmlElement> QuerySelectorAll(string query)
    {
        var q = query.ToUpperInvariant();
        foreach (var t in new XmlTreeWalker(this, element => element is XmlElement e && e.NodeName.ToUpperInvariant() == q))
        {
            yield return (XmlElement)t;
        }
    }

    internal override void CallObserver(Transaction transaction, HashSet<string?> parentSubs)
    {
        CallTypeObservers(this, transaction, new XmlEvent(this, parentSubs, transaction));
    }

    /// <summary>Registers a handler for changes of this node. Dispose the result to unsubscribe.</summary>
    public IDisposable Observe(Action<XmlEvent> handler) => _eH.Add((e, _) => handler((XmlEvent)e));

    /// <summary>Registers a handler for changes of this node. Dispose the result to unsubscribe.</summary>
    public IDisposable Observe(Action<XmlEvent, Transaction> handler) => _eH.Add((e, t) => handler((XmlEvent)e, t));

    /// <summary>The XML string of the children.</summary>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in this.ToArray())
        {
            sb.Append(c is AbstractType t ? t.ToString() : Xml.JsString(c));
        }
        return sb.ToString();
    }

    /// <inheritdoc />
    public override object? ToJson() => ToString();

    /// <summary>Inserts children at <paramref name="index"/>.</summary>
    public void Insert(int index, IEnumerable<object?> content) => Insert(null, index, content);

    /// <summary>Inserts children at <paramref name="index"/>.</summary>
    public void Insert(int index, params AbstractType[] content) => Insert(null, index, content);

    /// <summary>Inserts children within the given transaction.</summary>
    public void Insert(Transaction? transaction, int index, IEnumerable<object?> content)
    {
        var list = new List<object?>(content);
        if (_doc != null)
        {
            Transact(transaction, t => TypeListInsertGenerics(t, this, index, list));
        }
        else
        {
            _prelimContent!.InsertRange(index, list);
        }
    }

    /// <summary>Inserts children after a reference child (null inserts at the beginning).</summary>
    public void InsertAfter(AbstractType? reference, IEnumerable<object?> content)
    {
        var list = new List<object?>(content);
        if (_doc != null)
        {
            Transact(t => TypeListInsertGenericsAfter(t, this, reference?._item, list));
        }
        else
        {
            var pc    = _prelimContent!;
            var index = reference == null ? 0 : pc.FindIndex(el => ReferenceEquals(el, reference)) + 1;
            if (index == 0 && reference != null)
            {
                throw new InvalidOperationException("Reference item not found");
            }
            pc.InsertRange(index, list);
        }
    }

    /// <summary>Deletes children.</summary>
    public void Delete(int index, int length = 1) => Delete(null, index, length);

    /// <summary>Deletes children within the given transaction.</summary>
    public void Delete(Transaction? transaction, int index, int length = 1)
    {
        if (_doc != null)
        {
            Transact(transaction, t => TypeListDelete(t, this, index, length));
        }
        else
        {
            _prelimContent!.RemoveRange(index, length);
        }
    }

    /// <summary>The children.</summary>
    public List<object?> ToArray() => _prelimContent != null ? new List<object?>(_prelimContent) : TypeListToArray();

    /// <summary>Appends children.</summary>
    public void Push(params AbstractType[] content) => Insert(Length, content);

    /// <summary>Appends children.</summary>
    public void Push(IEnumerable<object?> content) => Insert(Length, content);

    /// <summary>Prepends children.</summary>
    public void Unshift(params AbstractType[] content) => Insert(0, content);

    /// <summary>The child at <paramref name="index"/>.</summary>
    public object? Get(int index) => _prelimContent != null ? _prelimContent[index] : TypeListGet(index);

    /// <summary>The children in [start, end).</summary>
    public List<object?> Slice(int start = 0, int? end = null) => TypeListSlice(start, end ?? Length);

    internal override void WriteTypeRef(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(TypeRefId);
    }
}

/// <summary>A shared XML element (Y.XmlElement): a node name, attributes and children.</summary>
public class XmlElement : XmlFragment
{
    internal new const uint TypeRefId = 3;

    internal OrderedMap<string, object?>? _prelimAttrs = new OrderedMap<string, object?>();

    /// <summary>Creates an element that is not attached to a document yet.</summary>
    public XmlElement(string nodeName = "UNDEFINED")
    {
        NodeName = nodeName;
    }

    /// <summary>The node name (tag).</summary>
    public string NodeName { get; internal set; }

    /// <summary>The next sibling element or text, or null.</summary>
    public AbstractType? NextSibling => Xml.SiblingType(_item?.Next);

    /// <summary>The previous sibling element or text, or null.</summary>
    public AbstractType? PrevSibling => Xml.SiblingType(_item?.Prev);

    internal override void Integrate(Doc y, Item? item)
    {
        base.Integrate(y, item);
        var attrs = _prelimAttrs!;
        _prelimAttrs = null;
        foreach (var kv in attrs)
        {
            SetAttribute(kv.Key, kv.Value);
        }
    }

    internal override AbstractType InternalCopy() => new XmlElement(NodeName);

    /// <inheritdoc />
    public override AbstractType Clone()
    {
        var el = new XmlElement(NodeName);
        foreach (var kv in GetAttributes())
        {
            el.SetAttribute(kv.Key, kv.Value);
        }
        var list = new List<object?>();
        foreach (var v in ToArray())
        {
            list.Add(v is AbstractType t ? t.Clone() : v);
        }
        el.Insert(0, list);
        return el;
    }

    /// <summary>The XML string of the element.</summary>
    public override string ToString()
    {
        var attrs = GetAttributes();
        var keys  = new List<string>(attrs.Keys);
        keys.Sort(string.CompareOrdinal);
        var sb = new System.Text.StringBuilder();
        foreach (var key in keys)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }
            sb.Append(key).Append("=\"").Append(Xml.JsString(attrs[key])).Append('"');
        }
        var nodeName    = NodeName.ToLowerInvariant();
        var attrsString = sb.Length > 0 ? " " + sb : string.Empty;
        return $"<{nodeName}{attrsString}>{base.ToString()}</{nodeName}>";
    }

    /// <summary>Removes an attribute.</summary>
    public void RemoveAttribute(string attributeName)
    {
        if (_doc != null)
        {
            Transact(t => TypeMapDelete(t, this, attributeName));
        }
        else
        {
            _prelimAttrs!.Remove(attributeName);
        }
    }

    /// <summary>Sets an attribute (usually a string, but any value is allowed).</summary>
    public void SetAttribute(string attributeName, object? attributeValue)
    {
        if (_doc != null)
        {
            Transact(t => TypeMapSet(t, this, attributeName, attributeValue));
        }
        else
        {
            _prelimAttrs![attributeName] = attributeValue;
        }
    }

    /// <summary>The value of an attribute, or null.</summary>
    public object? GetAttribute(string attributeName)
    {
        if (_prelimAttrs != null)
        {
            return _prelimAttrs.TryGetValue(attributeName, out var v) ? v : null;
        }
        return TypeMapGet(attributeName);
    }

    /// <summary>Whether the attribute is set.</summary>
    public bool HasAttribute(string attributeName) => _prelimAttrs != null ? _prelimAttrs.ContainsKey(attributeName) : TypeMapHas(attributeName);

    /// <summary>All attributes.</summary>
    public IDictionary<string, object?> GetAttributes()
    {
        if (_prelimAttrs != null)
        {
            var d = new Dictionary<string, object?>();
            foreach (var kv in _prelimAttrs)
            {
                d[kv.Key] = kv.Value;
            }
            return d;
        }
        return TypeMapGetAll();
    }

    /// <summary>The attributes visible in a snapshot.</summary>
    public IDictionary<string, object?> GetAttributes(Snapshot snapshot) => TypeMapGetAllSnapshot(snapshot);

    internal override void WriteTypeRef(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(TypeRefId);
        encoder.WriteKey(NodeName);
    }
}

/// <summary>Event of an <see cref="XmlText"/>.</summary>
public sealed class XmlTextEvent : TextEvent
{
    internal XmlTextEvent(XmlText target, Transaction transaction, HashSet<string?> subs) : base(target, transaction, subs)
    {
    }
}

/// <summary>Shared rich text inside an XML tree (Y.XmlText).</summary>
public sealed class XmlText : TextBase
{
    internal const uint TypeRefId = 6;

    /// <summary>Creates a text node that is not attached to a document yet.</summary>
    public XmlText(string? initial = null) : base(initial)
    {
    }

    /// <summary>The next sibling element or text, or null.</summary>
    public AbstractType? NextSibling => Xml.SiblingType(_item?.Next);

    /// <summary>The previous sibling element or text, or null.</summary>
    public AbstractType? PrevSibling => Xml.SiblingType(_item?.Prev);

    internal override AbstractType InternalCopy() => new XmlText();

    /// <inheritdoc />
    public override AbstractType Clone()
    {
        var text = new XmlText();
        text.ApplyDelta(ToDelta());
        return text;
    }

    internal override TextEvent CreateEvent(Transaction transaction, HashSet<string?> parentSubs) => new XmlTextEvent(this, transaction, parentSubs);

    /// <summary>Registers a handler for changes of this text. Dispose the result to unsubscribe.</summary>
    public IDisposable Observe(Action<XmlTextEvent> handler) => _eH.Add((e, _) => handler((XmlTextEvent)e));

    /// <summary>Registers a handler for changes of this text. Dispose the result to unsubscribe.</summary>
    public IDisposable Observe(Action<XmlTextEvent, Transaction> handler) => _eH.Add((e, t) => handler((XmlTextEvent)e, t));

    /// <summary>The text as XML: formatting attributes become nested tags.</summary>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var delta in ToDelta())
        {
            var nestedNodes = new List<(string NodeName, List<(string Key, object? Value)> Attrs)>();
            if (delta.Attributes != null)
            {
                foreach (var kv in AnyValues.JsKeyOrder(delta.Attributes))
                {
                    var attrs = new List<(string, object?)>();
                    switch (kv.Value)
                    {
                        case IDictionary<string, object?> d:
                            foreach (var a in AnyValues.JsKeyOrder(d))
                            {
                                attrs.Add((a.Key, a.Value));
                            }
                            break;
                        case string s:
                            for (var i = 0; i < s.Length; i++)
                            {
                                attrs.Add((i.ToString(CultureInfo.InvariantCulture), s[i].ToString()));
                            }
                            break;
                        case IList list when kv.Value is not byte[]:
                            for (var i = 0; i < list.Count; i++)
                            {
                                attrs.Add((i.ToString(CultureInfo.InvariantCulture), list[i]));
                            }
                            break;
                    }
                    attrs.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1) < 0 ? -1 : 1);
                    nestedNodes.Add((kv.Key, attrs));
                }
            }
            nestedNodes.Sort((a, b) => string.CompareOrdinal(a.NodeName, b.NodeName) < 0 ? -1 : 1);
            foreach (var node in nestedNodes)
            {
                sb.Append('<').Append(node.NodeName);
                foreach (var attr in node.Attrs)
                {
                    sb.Append(' ').Append(attr.Key).Append("=\"").Append(Xml.JsString(attr.Value)).Append('"');
                }
                sb.Append('>');
            }
            sb.Append(delta.Insert is AbstractType t ? t.ToString() : Xml.JsString(delta.Insert));
            for (var i = nestedNodes.Count - 1; i >= 0; i--)
            {
                sb.Append("</").Append(nestedNodes[i].NodeName).Append('>');
            }
        }
        return sb.ToString();
    }

    /// <inheritdoc />
    public override object? ToJson() => ToString();

    internal override void WriteTypeRef(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(TypeRefId);
    }
}

/// <summary>A custom node in an XML tree backed by a map (Y.XmlHook).</summary>
public sealed class XmlHook : YMap
{
    internal new const uint TypeRefId = 5;

    /// <summary>Creates a hook that is not attached to a document yet.</summary>
    public XmlHook(string hookName)
    {
        HookName = hookName;
    }

    /// <summary>The name of the hook.</summary>
    public string HookName { get; internal set; }

    internal override AbstractType InternalCopy() => new XmlHook(HookName);

    /// <inheritdoc />
    public override AbstractType Clone()
    {
        var el = new XmlHook(HookName);
        foreach (var kv in this)
        {
            el.Set(kv.Key, kv.Value);
        }
        return el;
    }

    internal override void WriteTypeRef(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(TypeRefId);
        encoder.WriteKey(HookName);
    }
}

internal static class Xml
{
    internal static AbstractType? SiblingType(Item? n)
    {
        return n?.ContentField is Structs.ContentType ct ? ct.Type : null;
    }

    /// <summary>JavaScript's <c>String(value)</c>.</summary>
    internal static string JsString(object? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case Undefined:
                return "undefined";
            case string s:
                return s;
            case bool b:
                return b ? "true" : "false";
            case AbstractType t:
                return t.ToString() ?? string.Empty;
            case byte[] bytes:
                return string.Join(",", Array.ConvertAll(bytes, x => x.ToString(CultureInfo.InvariantCulture)));
            case IDictionary<string, object?>:
            case IReadOnlyDictionary<string, object?>:
            case IDictionary:
                return "[object Object]";
            case IList list:
                {
                    var parts = new string[list.Count];
                    for (var i = 0; i < list.Count; i++)
                    {
                        parts[i] = list[i] is null or Undefined ? string.Empty : JsString(list[i]);
                    }
                    return string.Join(",", parts);
                }
            default:
                if (AnyValues.IsNumber(value))
                {
                    return JsJson.NumberToString(AnyValues.ToDouble(value));
                }
                return value.ToString() ?? string.Empty;
        }
    }
}
