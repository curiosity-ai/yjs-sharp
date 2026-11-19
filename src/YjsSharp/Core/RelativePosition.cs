using System;
using System.Collections.Generic;
using YjsSharp.Encoding;
using YjsSharp.Structs;

namespace YjsSharp;

/// <summary>
/// A position in a shared type that stays stable under concurrent edits (Yjs RelativePosition, yrs StickyIndex).
/// It refers to an element (by id) rather than an index. <see cref="Assoc"/> decides whether the position sticks
/// to the element on its right (&gt;= 0, the default) or on its left (&lt; 0).
/// </summary>
public sealed class RelativePosition : IEquatable<RelativePosition>
{
    /// <summary>Creates a relative position.</summary>
    public RelativePosition(ID? type, string? tname, ID? item, int assoc = 0)
    {
        Type  = type;
        TName = tname;
        Item  = item;
        Assoc = assoc;
    }

    /// <summary>The id of the item holding the (nested) type, when the position is at the end of a nested type.</summary>
    public ID? Type { get; }

    /// <summary>The name of the root type, when the position is at the end of a root type.</summary>
    public string? TName { get; }

    /// <summary>The id of the element the position is attached to.</summary>
    public ID? Item { get; }

    /// <summary>Association: &gt;= 0 sticks to the right element, &lt; 0 to the left element.</summary>
    public int Assoc { get; }

    private static RelativePosition Create(AbstractType type, ID? item, int assoc)
    {
        ID?     typeid = null;
        string? tname  = null;
        if (type._item == null)
        {
            tname = type.FindRootTypeKey();
        }
        else
        {
            typeid = new ID(type._item.Id.Client, type._item.Id.Clock);
        }
        return new RelativePosition(typeid, tname, item, assoc);
    }

    /// <summary>
    /// Creates a position scoped to the type itself rather than to an element: it resolves to the end of the type
    /// when <paramref name="assoc"/> &gt;= 0 and to its start otherwise.
    /// </summary>
    public static RelativePosition FromType(AbstractType type, int assoc = 0) => Create(type, null, assoc);

    /// <summary>Creates a relative position from an index in a sequence type.</summary>
    public static RelativePosition FromTypeIndex(AbstractType type, int index, int assoc = 0)
    {
        var t = type._start;
        if (assoc < 0)
        {
            if (index == 0)
            {
                return Create(type, null, assoc);
            }
            index--;
        }
        while (t != null)
        {
            if (!t.Deleted && t.Countable)
            {
                if (t.Length > index)
                {
                    return Create(type, new ID(t.Id.Client, t.Id.Clock + (uint)index), assoc);
                }
                index -= (int)t.Length;
            }
            if (t.Right == null && assoc < 0)
            {
                return Create(type, t.LastId, assoc);
            }
            t = t.Right;
        }
        return Create(type, null, assoc);
    }

    /// <summary>Creates a relative position from an index in a sequence type (Yjs <c>createRelativePositionFromTypeIndex</c>).</summary>
    public static RelativePosition CreateRelativePositionFromTypeIndex(AbstractType type, int index, int assoc = 0) => FromTypeIndex(type, index, assoc);

    /// <summary>Resolves a relative position in a document (Yjs <c>createAbsolutePositionFromRelativePosition</c>).</summary>
    public static AbsolutePosition? CreateAbsolutePositionFromRelativePosition(RelativePosition rpos, Doc doc, bool followUndoneDeletions = true) => rpos.ToAbsolute(doc, followUndoneDeletions);

    /// <summary>Writes the position in the Yjs binary format.</summary>
    public void Write(Encoder encoder)
    {
        if (Item != null)
        {
            encoder.WriteVarUInt(0);
            Item.Value.Write(encoder);
        }
        else if (TName != null)
        {
            encoder.WriteUInt8(1);
            encoder.WriteVarString(TName);
        }
        else if (Type != null)
        {
            encoder.WriteUInt8(2);
            Type.Value.Write(encoder);
        }
        else
        {
            throw new InvalidOperationException("Unexpected case: relative position without a reference");
        }
        encoder.WriteVarInt(Assoc);
    }

    /// <summary>Encodes the position in the Yjs binary format.</summary>
    public byte[] Encode()
    {
        var encoder = new Encoder();
        Write(encoder);
        return encoder.ToArray();
    }

    /// <summary>Reads a position written by <see cref="Write"/>.</summary>
    public static RelativePosition Read(Decoder decoder)
    {
        ID?     type   = null;
        string? tname  = null;
        ID?     itemId = null;
        switch (decoder.ReadVarUInt())
        {
            case 0:
                itemId = ID.Read(decoder);
                break;
            case 1:
                tname = decoder.ReadVarString();
                break;
            case 2:
                type = ID.Read(decoder);
                break;
        }
        var assoc = decoder.HasContent ? (int)decoder.ReadVarInt() : 0;
        return new RelativePosition(type, tname, itemId, assoc);
    }

    /// <summary>Decodes a position encoded with <see cref="Encode"/>.</summary>
    public static RelativePosition Decode(byte[] data) => Read(new Decoder(data));

    /// <summary>Converts the position to the JSON shape Yjs uses (<c>relativePositionToJSON</c>).</summary>
    public IDictionary<string, object?> ToJson()
    {
        var json = new Dictionary<string, object?>();
        if (Type != null)
        {
            json["type"] = new Dictionary<string, object?> { ["client"] = (long)Type.Value.Client, ["clock"] = (long)Type.Value.Clock };
        }
        if (TName != null)
        {
            json["tname"] = TName;
        }
        if (Item != null)
        {
            json["item"] = new Dictionary<string, object?> { ["client"] = (long)Item.Value.Client, ["clock"] = (long)Item.Value.Clock };
        }
        json["assoc"] = (long)Assoc;
        return json;
    }

    /// <summary>Creates a position from the JSON shape produced by <see cref="ToJson"/>.</summary>
    public static RelativePosition FromJson(IDictionary<string, object?> json)
    {
        static ID? ReadId(IDictionary<string, object?> j, string key)
        {
            if (!j.TryGetValue(key, out var v) || v is not IDictionary<string, object?> d)
            {
                return null;
            }
            return new ID(Convert.ToUInt64(d["client"], System.Globalization.CultureInfo.InvariantCulture), Convert.ToUInt32(d["clock"], System.Globalization.CultureInfo.InvariantCulture));
        }
        json.TryGetValue("tname", out var tname);
        json.TryGetValue("assoc", out var assoc);
        return new RelativePosition(
            ReadId(json, "type"),
            tname as string,
            ReadId(json, "item"),
            assoc == null ? 0 : Convert.ToInt32(assoc, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Resolves the position to an index in the given document, or null when it cannot be resolved.</summary>
    public AbsolutePosition? ToAbsolute(Doc doc, bool followUndoneDeletions = true)
    {
        var           store  = doc.Store;
        var           rightId = Item;
        var           typeId  = Type;
        AbstractType? type    = null;
        var           index   = 0;
        if (rightId != null)
        {
            if (store.GetState(rightId.Value.Client) <= rightId.Value.Clock)
            {
                return null;
            }
            AbstractStruct res;
            uint           diff;
            if (followUndoneDeletions)
            {
                (res, diff) = Structs.Item.FollowRedone(store, rightId.Value);
            }
            else
            {
                res  = store.Find(rightId.Value);
                diff = rightId.Value.Clock - res.Id.Clock;
            }
            if (res is not Item right)
            {
                return null;
            }
            type = right.Parent!;
            if (type._item == null || !type._item.Deleted)
            {
                index = right.Deleted || !right.Countable ? 0 : (int)diff + (Assoc >= 0 ? 0 : 1);
                var n = right.Left;
                while (n != null)
                {
                    if (!n.Deleted && n.Countable)
                    {
                        index += (int)n.Length;
                    }
                    n = n.Left;
                }
            }
        }
        else
        {
            if (TName != null)
            {
                type = doc.GetOrCreateRoot(TName);
            }
            else if (typeId != null)
            {
                if (store.GetState(typeId.Value.Client) <= typeId.Value.Clock)
                {
                    return null;
                }
                var item = followUndoneDeletions ? Structs.Item.FollowRedone(store, typeId.Value).Item : store.Find(typeId.Value);
                if (item is Item it && it.ContentField is ContentType ct)
                {
                    type = ct.Type;
                }
                else
                {
                    return null;
                }
            }
            else
            {
                throw new InvalidOperationException("Unexpected case: relative position without a reference");
            }
            index = Assoc >= 0 ? type._length : 0;
        }
        return new AbsolutePosition(type, index, Assoc);
    }

    /// <inheritdoc />
    public bool Equals(RelativePosition? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        return other != null && TName == other.TName && ID.Equal(Item, other.Item) && ID.Equal(Type, other.Type) && Assoc == other.Assoc;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RelativePosition r && Equals(r);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Item, Type, TName, Assoc);

    /// <inheritdoc />
    public override string ToString() => Item != null ? $"RelativePosition(item: {Item}, assoc: {Assoc})" : $"RelativePosition(type: {(object?)TName ?? Type}, assoc: {Assoc})";
}

/// <summary>A resolved position: a type and an index in it.</summary>
public sealed class AbsolutePosition
{
    internal AbsolutePosition(AbstractType type, int index, int assoc)
    {
        Type  = type;
        Index = index;
        Assoc = assoc;
    }

    /// <summary>The type the position is in.</summary>
    public AbstractType Type { get; }

    /// <summary>The index in the type.</summary>
    public int Index { get; }

    /// <summary>The association of the relative position it was resolved from.</summary>
    public int Assoc { get; }
}
