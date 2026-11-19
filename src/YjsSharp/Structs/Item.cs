using System;
using System.Collections.Generic;
using YjsSharp.Updates;

namespace YjsSharp.Structs;

/// <summary>
/// An inserted element (or a run of elements from one client) in the document: the unit the YATA algorithm
/// integrates.
/// </summary>
public sealed class Item : AbstractStruct
{
    private const byte BitKeep      = 1;
    private const byte BitCountable = 2;
    private const byte BitDeleted   = 4;
    private const byte BitMarker    = 8;

    internal ID?           Origin;
    internal Item?         Left;
    internal Item?         Right;
    internal ID?           RightOrigin;
    internal AbstractType? Parent;
    internal ID?           ParentId;
    internal string?       ParentKey;
    internal string?       ParentSub;
    internal ID?           Redone;
    internal ItemContent   ContentField;
    internal byte          Info;

    internal Item(
        ID           id,
        Item?        left,
        ID?          origin,
        Item?        right,
        ID?          rightOrigin,
        AbstractType? parent,
        string?      parentSub,
        ItemContent  content) : base(id, content.Length)
    {
        Origin       = origin;
        Left         = left;
        Right        = right;
        RightOrigin  = rightOrigin;
        Parent       = parent;
        ParentSub    = parentSub;
        ContentField = content;
        Info         = content.Countable ? BitCountable : (byte)0;
    }

    /// <summary>The content of the item.</summary>
    public ItemContent Content => ContentField;

    /// <summary>The type the item belongs to.</summary>
    public AbstractType? ParentType => Parent;

    /// <summary>The map key of the item, or null when it is part of a sequence.</summary>
    public string? Key => ParentSub;

    /// <summary>The item to the left at integration time.</summary>
    public Item? LeftItem => Left;

    /// <summary>The item to the right.</summary>
    public Item? RightItem => Right;

    /// <summary>The id of the element this item was inserted after.</summary>
    public ID? OriginId => Origin;

    /// <summary>The id of the element this item was inserted before.</summary>
    public ID? RightOriginId => RightOrigin;

    /// <summary>The id of the item that redid this item (undo manager), if any.</summary>
    public ID? RedoneId => Redone;

    internal bool Marker
    {
        get => (Info & BitMarker) != 0;
        set
        {
            if (value)
            {
                Info |= BitMarker;
            }
            else
            {
                Info &= unchecked((byte)~BitMarker);
            }
        }
    }

    /// <summary>Whether the item must not be garbage collected (used by the undo manager).</summary>
    public bool Keep
    {
        get => (Info & BitKeep) != 0;
        internal set
        {
            if (value)
            {
                Info |= BitKeep;
            }
            else
            {
                Info &= unchecked((byte)~BitKeep);
            }
        }
    }

    /// <summary>Whether the content counts towards the length of the parent.</summary>
    public bool Countable => (Info & BitCountable) != 0;

    /// <inheritdoc />
    public override bool Deleted => (Info & BitDeleted) != 0;

    internal void MarkDeleted()
    {
        Info |= BitDeleted;
    }

    /// <summary>The id of the last element of the item.</summary>
    public ID LastId => _length == 1 ? _id : new ID(_id.Client, _id.Clock + _length - 1);

    /// <summary>The next non-deleted item to the right.</summary>
    public Item? Next
    {
        get
        {
            var n = Right;
            while (n != null && n.Deleted)
            {
                n = n.Right;
            }
            return n;
        }
    }

    /// <summary>The previous non-deleted item to the left.</summary>
    public Item? Prev
    {
        get
        {
            var n = Left;
            while (n != null && n.Deleted)
            {
                n = n.Left;
            }
            return n;
        }
    }

    internal static (AbstractStruct Item, uint Diff) FollowRedone(StructStore store, ID id)
    {
        var            nextId = (ID?)id;
        uint           diff   = 0;
        AbstractStruct item;
        do
        {
            if (diff > 0)
            {
                nextId = new ID(nextId!.Value.Client, nextId.Value.Clock + diff);
            }
            item   = store.Find(nextId!.Value);
            diff   = nextId.Value.Clock - item.Id.Clock;
            nextId = item is Item it ? it.Redone : null;
        }
        while (nextId != null && item is Item);
        return (item, diff);
    }

    internal static void KeepItem(Item? item, bool keep)
    {
        while (item != null && item.Keep != keep)
        {
            item.Keep = keep;
            item      = item.Parent!._item;
        }
    }

    internal static Item SplitItem(Transaction transaction, Item leftItem, uint diff)
    {
        var client = leftItem._id.Client;
        var clock  = leftItem._id.Clock;
        var rightItem = new Item(
            new ID(client, clock + diff),
            leftItem,
            new ID(client, clock + diff - 1),
            leftItem.Right,
            leftItem.RightOrigin,
            leftItem.Parent,
            leftItem.ParentSub,
            leftItem.ContentField.Splice(diff));
        rightItem.ParentId  = leftItem.ParentId;
        rightItem.ParentKey = leftItem.ParentKey;
        if (leftItem.Deleted)
        {
            rightItem.MarkDeleted();
        }
        if (leftItem.Keep)
        {
            rightItem.Keep = true;
        }
        if (leftItem.Redone != null)
        {
            rightItem.Redone = new ID(leftItem.Redone.Value.Client, leftItem.Redone.Value.Clock + diff);
        }
        leftItem.Right = rightItem;
        if (rightItem.Right != null)
        {
            rightItem.Right.Left = rightItem;
        }
        transaction._mergeStructs.Add(rightItem);
        if (rightItem.ParentSub != null && rightItem.Right == null)
        {
            rightItem.Parent!._map[rightItem.ParentSub] = rightItem;
        }
        leftItem._length = diff;
        return rightItem;
    }

    private static bool IsDeletedByUndoStack(List<StackItem> stack, ID id)
    {
        foreach (var s in stack)
        {
            if (s.Deletions.IsDeleted(id))
            {
                return true;
            }
        }
        return false;
    }

    internal static Item? RedoItem(
        Transaction   transaction,
        Item          item,
        HashSet<Item> redoitems,
        DeleteSet     itemsToDelete,
        bool          ignoreRemoteMapChanges,
        UndoManager   um)
    {
        var doc           = transaction.Doc;
        var store         = doc.Store;
        var ownClientId   = doc.ClientId;
        var redone        = item.Redone;
        if (redone != null)
        {
            return (Item)StructStore.GetItemCleanStart(transaction, redone.Value);
        }
        var   parentItem = item.Parent!._item;
        Item? left       = null;
        Item? right;
        if (parentItem != null && parentItem.Deleted)
        {
            if (parentItem.Redone == null && (!redoitems.Contains(parentItem) || RedoItem(transaction, parentItem, redoitems, itemsToDelete, ignoreRemoteMapChanges, um) == null))
            {
                return null;
            }
            while (parentItem.Redone != null)
            {
                parentItem = (Item)StructStore.GetItemCleanStart(transaction, parentItem.Redone.Value);
            }
        }
        var parentType = parentItem == null ? item.Parent! : ((ContentType)parentItem.ContentField).Type;
        if (item.ParentSub == null)
        {
            left  = item.Left;
            right = item;
            while (left != null)
            {
                Item? leftTrace = left;
                while (leftTrace != null && leftTrace.Parent!._item != parentItem)
                {
                    leftTrace = leftTrace.Redone == null ? null : (Item)StructStore.GetItemCleanStart(transaction, leftTrace.Redone.Value);
                }
                if (leftTrace != null && leftTrace.Parent!._item == parentItem)
                {
                    left = leftTrace;
                    break;
                }
                left = left.Left;
            }
            while (right != null)
            {
                Item? rightTrace = right;
                while (rightTrace != null && rightTrace.Parent!._item != parentItem)
                {
                    rightTrace = rightTrace.Redone == null ? null : (Item)StructStore.GetItemCleanStart(transaction, rightTrace.Redone.Value);
                }
                if (rightTrace != null && rightTrace.Parent!._item == parentItem)
                {
                    right = rightTrace;
                    break;
                }
                right = right.Right;
            }
        }
        else
        {
            right = null;
            if (item.Right != null && !ignoreRemoteMapChanges)
            {
                left = item;
                while (left != null && left.Right != null && (left.Right.Redone != null || itemsToDelete.IsDeleted(left.Right._id) || IsDeletedByUndoStack(um._undoStack, left.Right._id) || IsDeletedByUndoStack(um._redoStack, left.Right._id)))
                {
                    left = left.Right;
                    while (left.Redone != null)
                    {
                        left = (Item)StructStore.GetItemCleanStart(transaction, left.Redone.Value);
                    }
                }
                if (left != null && left.Right != null)
                {
                    return null;
                }
            }
            else
            {
                left = parentType._map.TryGetValue(item.ParentSub, out var l) ? l : null;
            }
            if (left != null && left.Parent!._item != parentItem)
            {
                left = parentType._map.TryGetValue(item.ParentSub, out var l2) ? l2 : null;
            }
        }
        var nextClock = store.GetState(ownClientId);
        var nextId    = new ID(ownClientId, nextClock);
        var redoneItem = new Item(
            nextId,
            left,
            left?.LastId,
            right,
            right?._id,
            parentType,
            item.ParentSub,
            item.ContentField.Copy());
        item.Redone = nextId;
        KeepItem(redoneItem, true);
        redoneItem.Integrate(transaction, 0);
        return redoneItem;
    }

    internal override ulong? GetMissing(Transaction transaction, StructStore store)
    {
        if (Origin != null && Origin.Value.Client != _id.Client && Origin.Value.Clock >= store.GetState(Origin.Value.Client))
        {
            return Origin.Value.Client;
        }
        if (RightOrigin != null && RightOrigin.Value.Client != _id.Client && RightOrigin.Value.Clock >= store.GetState(RightOrigin.Value.Client))
        {
            return RightOrigin.Value.Client;
        }
        if (ParentId != null && _id.Client != ParentId.Value.Client && ParentId.Value.Clock >= store.GetState(ParentId.Value.Client))
        {
            return ParentId.Value.Client;
        }
        var gcNeighbour = false;
        if (Origin != null)
        {
            var l = StructStore.GetItemCleanEnd(transaction, store, Origin.Value);
            if (l is Item li)
            {
                Left   = li;
                Origin = li.LastId;
            }
            else
            {
                Left        = null;
                gcNeighbour = true;
            }
        }
        if (RightOrigin != null)
        {
            var r = StructStore.GetItemCleanStart(transaction, RightOrigin.Value);
            if (r is Item ri)
            {
                Right       = ri;
                RightOrigin = ri._id;
            }
            else
            {
                Right       = null;
                gcNeighbour = true;
            }
        }
        if (gcNeighbour)
        {
            Parent   = null;
            ParentId = null;
        }
        else if (Parent == null && ParentId == null)
        {
            if (Left != null)
            {
                Parent    = Left.Parent;
                ParentSub = Left.ParentSub;
            }
            else if (Right != null)
            {
                Parent    = Right.Parent;
                ParentSub = Right.ParentSub;
            }
        }
        else if (ParentId != null)
        {
            var parentItem = store.Find(ParentId.Value);
            ParentId = null;
            if (parentItem is Item pi && pi.ContentField is ContentType ct)
            {
                Parent = ct.Type;
            }
            else
            {
                Parent = null;
            }
        }
        return null;
    }

    internal override void Integrate(Transaction transaction, uint offset)
    {
        if (offset > 0)
        {
            _id = new ID(_id.Client, _id.Clock + offset);
            var l = StructStore.GetItemCleanEnd(transaction, transaction.Doc.Store, new ID(_id.Client, _id.Clock - 1));
            ContentField =  ContentField.Splice(offset);
            _length      -= offset;
            if (l is Item li)
            {
                Left   = li;
                Origin = li.LastId;
            }
            else
            {
                Left   = null;
                Origin = l.Id;
                Parent = null;
            }
        }
        if (Parent != null)
        {
            var parent = Parent;
            if ((Left == null && (Right == null || Right.Left != null)) || (Left != null && Left.Right != Right))
            {
                var   left = Left;
                Item? o;
                if (left != null)
                {
                    o = left.Right;
                }
                else if (ParentSub != null)
                {
                    o = parent._map.TryGetValue(ParentSub, out var m) ? m : null;
                    while (o != null && o.Left != null)
                    {
                        o = o.Left;
                    }
                }
                else
                {
                    o = parent._start;
                }
                HashSet<Item>? conflictingItems  = null;
                HashSet<Item>? itemsBeforeOrigin = null;
                var store = transaction.Doc.Store;
                while (o != null && o != Right)
                {
                    conflictingItems  ??= new HashSet<Item>(ReferenceEqualityComparer<Item>.Instance);
                    itemsBeforeOrigin ??= new HashSet<Item>(ReferenceEqualityComparer<Item>.Instance);
                    itemsBeforeOrigin.Add(o);
                    conflictingItems.Add(o);
                    if (ID.Equal(Origin, o.Origin))
                    {
                        if (o._id.Client < _id.Client)
                        {
                            left = o;
                            conflictingItems.Clear();
                        }
                        else if (ID.Equal(RightOrigin, o.RightOrigin))
                        {
                            break;
                        }
                    }
                    else if (o.Origin != null && store.Find(o.Origin.Value) is Item oo && itemsBeforeOrigin.Contains(oo))
                    {
                        if (!conflictingItems.Contains(oo))
                        {
                            left = o;
                            conflictingItems.Clear();
                        }
                    }
                    else
                    {
                        break;
                    }
                    o = o.Right;
                }
                Left = left;
            }
            if (Left != null)
            {
                var right = Left.Right;
                Right      = right;
                Left.Right = this;
            }
            else
            {
                Item? r;
                if (ParentSub != null)
                {
                    r = parent._map.TryGetValue(ParentSub, out var m) ? m : null;
                    while (r != null && r.Left != null)
                    {
                        r = r.Left;
                    }
                }
                else
                {
                    r             = parent._start;
                    parent._start = this;
                }
                Right = r;
            }
            if (Right != null)
            {
                Right.Left = this;
            }
            else if (ParentSub != null)
            {
                parent._map[ParentSub] = this;
                if (Left != null)
                {
                    Left.Delete(transaction);
                }
            }
            if (ParentSub == null && Countable && !Deleted)
            {
                parent._length += (int)_length;
            }
            transaction.Doc.Store.AddStruct(this);
            ContentField.Integrate(transaction, this);
            transaction.AddChangedType(parent, ParentSub);
            if ((parent._item != null && parent._item.Deleted) || (ParentSub != null && Right != null))
            {
                Delete(transaction);
            }
        }
        else
        {
            new GC(_id, _length).Integrate(transaction, 0);
        }
    }

    internal override bool CanMergeWith(AbstractStruct rightStruct)
    {
        return rightStruct is Item right
               && ID.Equal(right.Origin, LastId)
               && Right == right
               && ID.Equal(RightOrigin, right.RightOrigin)
               && _id.Client == right._id.Client
               && _id.Clock + _length == right._id.Clock
               && Deleted == right.Deleted
               && Redone == null
               && right.Redone == null
               && ContentField.GetType() == right.ContentField.GetType()
               && ContentField is ContentString or ContentAny or ContentJson or ContentDeleted;
    }

    internal override bool MergeWith(AbstractStruct rightStruct)
    {
        if (rightStruct is Item right
            && ID.Equal(right.Origin, LastId)
            && Right == right
            && ID.Equal(RightOrigin, right.RightOrigin)
            && _id.Client == right._id.Client
            && _id.Clock + _length == right._id.Clock
            && Deleted == right.Deleted
            && Redone == null
            && right.Redone == null
            && ContentField.GetType() == right.ContentField.GetType()
            && ContentField.MergeWith(right.ContentField))
        {
            var searchMarker = Parent?._searchMarker;
            if (searchMarker != null)
            {
                foreach (var marker in searchMarker)
                {
                    if (marker.P == right)
                    {
                        marker.P = this;
                        if (!Deleted && Countable)
                        {
                            marker.Index -= (int)_length;
                        }
                    }
                }
            }
            if (right.Keep)
            {
                Keep = true;
            }
            Right = right.Right;
            if (Right != null)
            {
                Right.Left = this;
            }
            _length += right._length;
            return true;
        }
        return false;
    }

    internal void Delete(Transaction transaction)
    {
        if (!Deleted)
        {
            var parent = Parent!;
            if (Countable && ParentSub == null)
            {
                parent._length -= (int)_length;
            }
            MarkDeleted();
            transaction.DeleteSet.Add(_id.Client, _id.Clock, _length);
            transaction.AddChangedType(parent, ParentSub);
            ContentField.Delete(transaction);
        }
    }

    internal void Gc(StructStore store, bool parentGCd)
    {
        if (!Deleted)
        {
            throw new InvalidOperationException("Unexpected case: cannot garbage collect an item that is not deleted");
        }
        ContentField.Gc(store);
        if (parentGCd)
        {
            store.ReplaceStruct(this, new GC(_id, _length));
        }
        else
        {
            ContentField = new ContentDeleted(_length);
        }
    }

    internal override void Write(IUpdateEncoder encoder, uint offset)
    {
        var origin      = offset > 0 ? new ID(_id.Client, _id.Clock + offset - 1) : Origin;
        var rightOrigin = RightOrigin;
        var parentSub   = ParentSub;
        var info = (byte)((ContentField.Ref & 0x1F)
                          | (origin == null ? 0 : 0x80)
                          | (rightOrigin == null ? 0 : 0x40)
                          | (parentSub == null ? 0 : 0x20));
        encoder.WriteInfo(info);
        if (origin != null)
        {
            encoder.WriteLeftID(origin.Value);
        }
        if (rightOrigin != null)
        {
            encoder.WriteRightID(rightOrigin.Value);
        }
        if (origin == null && rightOrigin == null)
        {
            var parent = Parent;
            if (parent != null)
            {
                var parentItem = parent._item;
                if (parentItem == null)
                {
                    var ykey = parent.FindRootTypeKey();
                    encoder.WriteParentInfo(true);
                    encoder.WriteString(ykey);
                }
                else
                {
                    encoder.WriteParentInfo(false);
                    encoder.WriteLeftID(parentItem._id);
                }
            }
            else if (ParentKey != null)
            {
                encoder.WriteParentInfo(true);
                encoder.WriteString(ParentKey);
            }
            else if (ParentId != null)
            {
                encoder.WriteParentInfo(false);
                encoder.WriteLeftID(ParentId.Value);
            }
            else
            {
                throw new InvalidOperationException("Unexpected case: item has no parent information");
            }
            if (parentSub != null)
            {
                encoder.WriteString(parentSub);
            }
        }
        ContentField.Write(encoder, offset);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"Item({_id}, len: {_length}, deleted: {Deleted}, content: {ContentField.GetType().Name})";
    }
}
