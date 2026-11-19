using System;
using System.Collections.Generic;
using YjsSharp.Updates;

namespace YjsSharp.Structs;

/// <summary>The content of an <see cref="Item"/>.</summary>
public abstract class ItemContent
{
    internal const byte RefDeleted = 1;
    internal const byte RefJson    = 2;
    internal const byte RefBinary  = 3;
    internal const byte RefString  = 4;
    internal const byte RefEmbed   = 5;
    internal const byte RefFormat  = 6;
    internal const byte RefType    = 7;
    internal const byte RefAny     = 8;
    internal const byte RefDoc     = 9;

    internal ItemContent()
    {
    }

    /// <summary>The number of clocks this content spans.</summary>
    public abstract uint Length { get; }

    /// <summary>Whether the content counts towards the length of its parent (formats and deleted content do not).</summary>
    public abstract bool Countable { get; }

    /// <summary>The values held by the content (one entry per clock for countable content).</summary>
    public abstract IReadOnlyList<object?> GetContent();

    /// <summary>The last value held by the content.</summary>
    public virtual object? GetLast()
    {
        var c = GetContent();
        return c.Count == 0 ? null : c[c.Count - 1];
    }

    internal abstract byte Ref { get; }

    internal abstract ItemContent Copy();

    internal abstract ItemContent Splice(uint offset);

    internal abstract bool MergeWith(ItemContent right);

    internal virtual void Integrate(Transaction transaction, Item item)
    {
    }

    internal virtual void Delete(Transaction transaction)
    {
    }

    internal virtual void Gc(StructStore store)
    {
    }

    internal abstract void Write(IUpdateEncoder encoder, uint offset);

    internal static ItemContent Read(IUpdateDecoder decoder, byte info)
    {
        switch (info & 0x1F)
        {
            case RefDeleted:
                return new ContentDeleted(decoder.ReadLen());
            case RefJson:
                return ContentJson.ReadFrom(decoder);
            case RefBinary:
                return new ContentBinary(decoder.ReadBuf());
            case RefString:
                return new ContentString(decoder.ReadString());
            case RefEmbed:
                return new ContentEmbed(decoder.ReadJson());
            case RefFormat:
                {
                    var key = decoder.ReadKey();
                    return new ContentFormat(key, decoder.ReadJson());
                }
            case RefType:
                return new ContentType(AbstractType.ReadType(decoder));
            case RefAny:
                {
                    var len = decoder.ReadLen();
                    var cs  = new List<object?>((int)Math.Min(len, 1024u));
                    for (var i = 0; i < len; i++)
                    {
                        cs.Add(decoder.ReadAny());
                    }
                    return new ContentAny(cs);
                }
            case RefDoc:
                {
                    var guid = decoder.ReadString();
                    var opts = decoder.ReadAny();
                    return new ContentDoc(ContentDoc.CreateDocFromOpts(guid, opts as IDictionary<string, object?>));
                }
            default:
                throw new FormatException($"Unexpected item content type {info & 0x1F}");
        }
    }
}

/// <summary>Deleted content: only its length is kept.</summary>
public sealed class ContentDeleted : ItemContent
{
    internal uint Len;

    internal ContentDeleted(uint len)
    {
        Len = len;
    }

    /// <inheritdoc />
    public override uint Length => Len;

    /// <inheritdoc />
    public override bool Countable => false;

    /// <inheritdoc />
    public override IReadOnlyList<object?> GetContent() => Array.Empty<object?>();

    internal override byte Ref => RefDeleted;

    internal override ItemContent Copy() => new ContentDeleted(Len);

    internal override ItemContent Splice(uint offset)
    {
        var right = new ContentDeleted(Len - offset);
        Len = offset;
        return right;
    }

    internal override bool MergeWith(ItemContent right)
    {
        Len += ((ContentDeleted)right).Len;
        return true;
    }

    internal override void Integrate(Transaction transaction, Item item)
    {
        transaction.DeleteSet.Add(item.Id.Client, item.Id.Clock, Len);
        item.MarkDeleted();
    }

    internal override void Write(IUpdateEncoder encoder, uint offset)
    {
        encoder.WriteLen(Len - offset);
    }
}

/// <summary>Legacy JSON content (each element stored as JSON text).</summary>
public sealed class ContentJson : ItemContent
{
    internal List<object?> Arr;

    internal ContentJson(List<object?> arr)
    {
        Arr = arr;
    }

    /// <inheritdoc />
    public override uint Length => (uint)Arr.Count;

    /// <inheritdoc />
    public override bool Countable => true;

    /// <inheritdoc />
    public override IReadOnlyList<object?> GetContent() => Arr;

    internal override byte Ref => RefJson;

    internal override ItemContent Copy() => new ContentJson(Arr);

    internal override ItemContent Splice(uint offset)
    {
        var right = new ContentJson(Arr.GetRange((int)offset, Arr.Count - (int)offset));
        Arr = Arr.GetRange(0, (int)offset);
        return right;
    }

    internal override bool MergeWith(ItemContent right)
    {
        var merged = new List<object?>(Arr.Count + ((ContentJson)right).Arr.Count);
        merged.AddRange(Arr);
        merged.AddRange(((ContentJson)right).Arr);
        Arr = merged;
        return true;
    }

    internal override void Write(IUpdateEncoder encoder, uint offset)
    {
        var len = Arr.Count;
        encoder.WriteLen((uint)(len - (int)offset));
        for (var i = (int)offset; i < len; i++)
        {
            var c = Arr[i];
            encoder.WriteString(c is Undefined ? "undefined" : Encoding.JsJson.Stringify(c) ?? "undefined");
        }
    }

    internal static ContentJson ReadFrom(IUpdateDecoder decoder)
    {
        var len = decoder.ReadLen();
        var cs  = new List<object?>((int)Math.Min(len, 1024u));
        for (var i = 0; i < len; i++)
        {
            var c = decoder.ReadString();
            cs.Add(c == "undefined" ? Undefined.Value : Encoding.JsJson.Parse(c));
        }
        return new ContentJson(cs);
    }
}

/// <summary>Binary content (a byte array).</summary>
public sealed class ContentBinary : ItemContent
{
    internal byte[] Content;

    internal ContentBinary(byte[] content)
    {
        Content = content;
    }

    /// <inheritdoc />
    public override uint Length => 1;

    /// <inheritdoc />
    public override bool Countable => true;

    /// <inheritdoc />
    public override IReadOnlyList<object?> GetContent() => new object?[] { Content };

    /// <inheritdoc />
    public override object? GetLast() => Content;

    internal override byte Ref => RefBinary;

    internal override ItemContent Copy() => new ContentBinary(Content);

    internal override ItemContent Splice(uint offset) => throw new NotSupportedException();

    internal override bool MergeWith(ItemContent right) => false;

    internal override void Write(IUpdateEncoder encoder, uint offset)
    {
        encoder.WriteBuf(Content);
    }
}

/// <summary>A run of text (UTF-16 code units).</summary>
public sealed class ContentString : ItemContent
{
    internal string Str;

    internal ContentString(string str)
    {
        Str = str;
    }

    /// <summary>The text.</summary>
    public string Value => Str;

    /// <inheritdoc />
    public override uint Length => (uint)Str.Length;

    /// <inheritdoc />
    public override bool Countable => true;

    /// <inheritdoc />
    public override IReadOnlyList<object?> GetContent()
    {
        var arr = new object?[Str.Length];
        for (var i = 0; i < Str.Length; i++)
        {
            arr[i] = Str[i].ToString();
        }
        return arr;
    }

    /// <inheritdoc />
    public override object? GetLast() => Str.Length == 0 ? null : Str[Str.Length - 1].ToString();

    internal override byte Ref => RefString;

    internal override ItemContent Copy() => new ContentString(Str);

    internal override ItemContent Splice(uint offset)
    {
        var o     = (int)offset;
        var right = new ContentString(Str.Substring(o));
        Str = Str.Substring(0, o);
        if (o > 0)
        {
            var firstCharCode = Str[o - 1];
            if (firstCharCode >= 0xD800 && firstCharCode <= 0xDBFF)
            {
                // splitting a surrogate pair: replace both halves with U+FFFD like Yjs does
                Str       = Str.Substring(0, o - 1) + "�";
                right.Str = "�" + right.Str.Substring(1);
            }
        }
        return right;
    }

    internal override bool MergeWith(ItemContent right)
    {
        Str += ((ContentString)right).Str;
        return true;
    }

    internal override void Write(IUpdateEncoder encoder, uint offset)
    {
        encoder.WriteString(offset == 0 ? Str : Str.Substring((int)offset));
    }
}

/// <summary>An embedded value inside a text (e.g. an image description).</summary>
public sealed class ContentEmbed : ItemContent
{
    internal object? Embed;

    internal ContentEmbed(object? embed)
    {
        Embed = embed;
    }

    /// <summary>The embedded value.</summary>
    public object? Value => Embed;

    /// <inheritdoc />
    public override uint Length => 1;

    /// <inheritdoc />
    public override bool Countable => true;

    /// <inheritdoc />
    public override IReadOnlyList<object?> GetContent() => new[] { Embed };

    /// <inheritdoc />
    public override object? GetLast() => Embed;

    internal override byte Ref => RefEmbed;

    internal override ItemContent Copy() => new ContentEmbed(Embed);

    internal override ItemContent Splice(uint offset) => throw new NotSupportedException();

    internal override bool MergeWith(ItemContent right) => false;

    internal override void Write(IUpdateEncoder encoder, uint offset)
    {
        encoder.WriteJson(Embed);
    }
}

/// <summary>A formatting marker inside a text: from here on attribute <see cref="Key"/> has <see cref="Value"/> (null ends it).</summary>
public sealed class ContentFormat : ItemContent
{
    internal string  KeyField;
    internal object? ValueField;

    internal ContentFormat(string key, object? value)
    {
        KeyField   = key;
        ValueField = value;
    }

    /// <summary>The attribute name.</summary>
    public string Key => KeyField;

    /// <summary>The attribute value (null removes the attribute).</summary>
    public object? Value => ValueField;

    /// <inheritdoc />
    public override uint Length => 1;

    /// <inheritdoc />
    public override bool Countable => false;

    /// <inheritdoc />
    public override IReadOnlyList<object?> GetContent() => Array.Empty<object?>();

    internal override byte Ref => RefFormat;

    internal override ItemContent Copy() => new ContentFormat(KeyField, ValueField);

    internal override ItemContent Splice(uint offset) => throw new NotSupportedException();

    internal override bool MergeWith(ItemContent right) => false;

    internal override void Integrate(Transaction transaction, Item item)
    {
        var p = item.Parent!;
        p._searchMarker  = null;
        p._hasFormatting = true;
    }

    internal override void Write(IUpdateEncoder encoder, uint offset)
    {
        encoder.WriteKey(KeyField);
        encoder.WriteJson(ValueField);
    }
}

/// <summary>A nested shared type.</summary>
public sealed class ContentType : ItemContent
{
    internal readonly AbstractType Type;

    internal ContentType(AbstractType type)
    {
        Type = type;
    }

    /// <summary>The nested type.</summary>
    public AbstractType Value => Type;

    /// <inheritdoc />
    public override uint Length => 1;

    /// <inheritdoc />
    public override bool Countable => true;

    /// <inheritdoc />
    public override IReadOnlyList<object?> GetContent() => new object?[] { Type };

    /// <inheritdoc />
    public override object? GetLast() => Type;

    internal override byte Ref => RefType;

    internal override ItemContent Copy() => new ContentType(Type.InternalCopy());

    internal override ItemContent Splice(uint offset) => throw new NotSupportedException();

    internal override bool MergeWith(ItemContent right) => false;

    internal override void Integrate(Transaction transaction, Item item)
    {
        Type.Integrate(transaction.Doc, item);
    }

    internal override void Delete(Transaction transaction)
    {
        var item = Type._start;
        while (item != null)
        {
            if (!item.Deleted)
            {
                item.Delete(transaction);
            }
            else if (item.Id.Clock < transaction.GetBeforeState(item.Id.Client))
            {
                transaction._mergeStructs.Add(item);
            }
            item = item.Right;
        }
        foreach (var mapItem in Type._map.Values)
        {
            if (!mapItem.Deleted)
            {
                mapItem.Delete(transaction);
            }
            else if (mapItem.Id.Clock < transaction.GetBeforeState(mapItem.Id.Client))
            {
                transaction._mergeStructs.Add(mapItem);
            }
        }
        transaction._changed.Remove(Type);
    }

    internal override void Gc(StructStore store)
    {
        var item = Type._start;
        while (item != null)
        {
            item.Gc(store, true);
            item = item.Right;
        }
        Type._start = null;
        foreach (var mapItem in Type._map.Values)
        {
            Item? i = mapItem;
            while (i != null)
            {
                i.Gc(store, true);
                i = i.Left;
            }
        }
        Type._map = new Dictionary<string, Item>();
    }

    internal override void Write(IUpdateEncoder encoder, uint offset)
    {
        Type.WriteTypeRef(encoder);
    }
}

/// <summary>A list of Any values.</summary>
public sealed class ContentAny : ItemContent
{
    internal List<object?> Arr;

    internal ContentAny(List<object?> arr)
    {
        Arr = arr;
    }

    /// <inheritdoc />
    public override uint Length => (uint)Arr.Count;

    /// <inheritdoc />
    public override bool Countable => true;

    /// <inheritdoc />
    public override IReadOnlyList<object?> GetContent() => Arr;

    /// <inheritdoc />
    public override object? GetLast() => Arr.Count == 0 ? null : Arr[Arr.Count - 1];

    internal override byte Ref => RefAny;

    internal override ItemContent Copy() => new ContentAny(Arr);

    internal override ItemContent Splice(uint offset)
    {
        var right = new ContentAny(Arr.GetRange((int)offset, Arr.Count - (int)offset));
        Arr = Arr.GetRange(0, (int)offset);
        return right;
    }

    internal override bool MergeWith(ItemContent right)
    {
        var r      = ((ContentAny)right).Arr;
        var merged = new List<object?>(Arr.Count + r.Count);
        merged.AddRange(Arr);
        merged.AddRange(r);
        Arr = merged;
        return true;
    }

    internal override void Write(IUpdateEncoder encoder, uint offset)
    {
        var len = Arr.Count;
        encoder.WriteLen((uint)(len - (int)offset));
        for (var i = (int)offset; i < len; i++)
        {
            encoder.WriteAny(Arr[i]);
        }
    }
}

/// <summary>A sub-document.</summary>
public sealed class ContentDoc : ItemContent
{
    internal Doc                         DocField;
    internal Dictionary<string, object?> Opts;

    internal ContentDoc(Doc doc)
    {
        DocField = doc;
        Opts     = new Dictionary<string, object?>();
        if (!doc.GcEnabled)
        {
            Opts["gc"] = false;
        }
        if (doc.AutoLoad)
        {
            Opts["autoLoad"] = true;
        }
        if (doc.Meta != null)
        {
            Opts["meta"] = doc.Meta;
        }
    }

    /// <summary>The sub-document.</summary>
    public Doc Value => DocField;

    internal static Doc CreateDocFromOpts(string guid, IDictionary<string, object?>? opts)
    {
        var options = new DocOptions { Guid = guid };
        var autoLoad   = false;
        var shouldLoad = false;
        if (opts != null)
        {
            if (opts.TryGetValue("gc", out var gc) && gc is bool g)
            {
                options.GC = g;
            }
            if (opts.TryGetValue("autoLoad", out var al) && al is bool a)
            {
                autoLoad = a;
            }
            if (opts.TryGetValue("shouldLoad", out var sl) && sl is bool s)
            {
                shouldLoad = s;
            }
            if (opts.TryGetValue("meta", out var meta))
            {
                options.Meta = meta;
            }
            if (opts.TryGetValue("collectionid", out var cid) && cid is string c)
            {
                options.CollectionId = c;
            }
        }
        options.AutoLoad   = autoLoad;
        options.ShouldLoad = shouldLoad || autoLoad;
        return new Doc(options);
    }

    /// <inheritdoc />
    public override uint Length => 1;

    /// <inheritdoc />
    public override bool Countable => true;

    /// <inheritdoc />
    public override IReadOnlyList<object?> GetContent() => new object?[] { DocField };

    /// <inheritdoc />
    public override object? GetLast() => DocField;

    internal override byte Ref => RefDoc;

    internal override ItemContent Copy()
    {
        var copy = new ContentDoc(CreateDocFromOpts(DocField.Guid, Opts));
        return copy;
    }

    internal override ItemContent Splice(uint offset) => throw new NotSupportedException();

    internal override bool MergeWith(ItemContent right) => false;

    internal override void Integrate(Transaction transaction, Item item)
    {
        DocField._item = item;
        transaction._subdocsAdded.Add(DocField);
        if (DocField.ShouldLoad)
        {
            transaction._subdocsLoaded.Add(DocField);
        }
    }

    internal override void Delete(Transaction transaction)
    {
        if (transaction._subdocsAdded.Contains(DocField))
        {
            transaction._subdocsAdded.Remove(DocField);
        }
        else
        {
            transaction._subdocsRemoved.Add(DocField);
        }
    }

    internal override void Write(IUpdateEncoder encoder, uint offset)
    {
        encoder.WriteString(DocField.Guid);
        encoder.WriteAny(Opts);
    }
}
