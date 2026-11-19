using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using YjsSharp.Structs;
using YjsSharp.Updates;

namespace YjsSharp;

/// <summary>A cached position in a sequence type, used to speed up index lookups.</summary>
internal sealed class ArraySearchMarker
{
    private static long _globalSearchMarkerTimestamp;

    public Item P;
    public int  Index;
    public long Timestamp;

    public ArraySearchMarker(Item p, int index)
    {
        p.Marker  = true;
        P         = p;
        Index     = index;
        Timestamp = NextTimestamp();
    }

    public static long NextTimestamp() => Interlocked.Increment(ref _globalSearchMarkerTimestamp);

    public void Refresh() => Timestamp = NextTimestamp();

    public void Overwrite(Item p, int index)
    {
        P.Marker  = false;
        P         = p;
        p.Marker  = true;
        Index     = index;
        Timestamp = NextTimestamp();
    }
}

/// <summary>
/// Base of all shared types. A plain <see cref="AbstractType"/> is a placeholder for a root type whose concrete
/// type is not known yet (it becomes concrete when requested through <see cref="Doc.GetText"/> etc.).
/// </summary>
public class AbstractType
{
    private const int MaxSearchMarker = 80;

    internal Item?                         _item;
    internal Dictionary<string, Item>      _map = new Dictionary<string, Item>();
    internal Item?                         _start;
    internal Doc?                          _doc;
    internal int                           _length;
    internal readonly EventHandlerList<YEvent>               _eH  = new EventHandlerList<YEvent>();
    internal readonly EventHandlerList<IReadOnlyList<YEvent>> _dEH = new EventHandlerList<IReadOnlyList<YEvent>>();
    internal List<ArraySearchMarker>?      _searchMarker;
    internal bool                          _hasFormatting;

    internal AbstractType()
    {
    }

    /// <summary>The document the type belongs to (null until it is integrated).</summary>
    public Doc? Doc => _doc;

    /// <summary>The item holding this type, or null for a root type.</summary>
    public Item? Item => _item;

    /// <summary>The parent type, or null for a root type.</summary>
    public AbstractType? Parent => _item?.Parent;

    /// <summary>Whether the type was deleted (its holding item is deleted).</summary>
    public bool IsDeleted => _item != null && _item.Deleted;

    internal Item? First
    {
        get
        {
            var n = _start;
            while (n != null && n.Deleted)
            {
                n = n.Right;
            }
            return n;
        }
    }

    internal virtual void Integrate(Doc y, Item? item)
    {
        _doc  = y;
        _item = item;
    }

    internal virtual AbstractType InternalCopy() => throw new NotSupportedException("Unknown type cannot be copied");

    /// <summary>Creates a copy of the type that is not attached to any document.</summary>
    public virtual AbstractType Clone() => throw new NotSupportedException("Unknown type cannot be cloned");

    internal virtual void WriteTypeRef(IUpdateEncoder encoder)
    {
    }

    internal virtual void CallObserver(Transaction transaction, HashSet<string?> parentSubs)
    {
        if (!transaction._local && _searchMarker != null)
        {
            _searchMarker.Clear();
        }
    }

    /// <summary>Converts the type to plain values.</summary>
    public virtual object? ToJson() => null;

    /// <summary>Registers a handler for changes of this type and all nested types. Dispose the result to unsubscribe.</summary>
    public IDisposable ObserveDeep(Action<IReadOnlyList<YEvent>> handler)
    {
        return _dEH.Add((events, _) => handler(events));
    }

    /// <summary>Registers a handler for changes of this type and all nested types. Dispose the result to unsubscribe.</summary>
    public IDisposable ObserveDeep(Action<IReadOnlyList<YEvent>, Transaction> handler)
    {
        return _dEH.Add(handler);
    }

    internal IDisposable ObserveRaw(Action<YEvent, Transaction> handler) => _eH.Add(handler);

    internal string FindRootTypeKey()
    {
        foreach (var kv in _doc!.Share)
        {
            if (kv.Value == this)
            {
                return kv.Key;
            }
        }
        throw new InvalidOperationException("Unexpected case: root type not found in its document");
    }

    internal static AbstractType ReadType(IUpdateDecoder decoder)
    {
        var typeRef = decoder.ReadTypeRef();
        switch (typeRef)
        {
            case YArray.TypeRefId:
                return new YArray();
            case YMap.TypeRefId:
                return new YMap();
            case Text.TypeRefId:
                return new Text();
            case XmlElement.TypeRefId:
                return new XmlElement(decoder.ReadKey());
            case XmlFragment.TypeRefId:
                return new XmlFragment();
            case XmlHook.TypeRefId:
                return new XmlHook(decoder.ReadKey());
            case XmlText.TypeRefId:
                return new XmlText();
            default:
                throw new FormatException($"Unknown type reference {typeRef}");
        }
    }

    internal Doc RequireDoc()
    {
        return _doc ?? throw new InvalidOperationException("Invalid access: add the type to a document before reading or changing its data.");
    }

    internal T Transact<T>(Func<Transaction, T> f)
    {
        return RequireDoc().TransactInternal(f, null, true);
    }

    internal void Transact(Action<Transaction> f)
    {
        RequireDoc().TransactInternal<object?>(t =>
        {
            f(t);
            return null;
        }, null, true);
    }

    internal void Transact(Transaction? txn, Action<Transaction> f)
    {
        if (txn != null)
        {
            if (txn.Doc != _doc)
            {
                throw new ArgumentException("The transaction belongs to another document", nameof(txn));
            }
            if (_doc!._transaction != txn)
            {
                throw new InvalidOperationException("The transaction is not active anymore");
            }
            f(txn);
            return;
        }
        Transact(f);
    }

    internal static void CallTypeObservers(AbstractType type, Transaction transaction, YEvent ev)
    {
        var changedType        = type;
        var changedParentTypes = transaction._changedParentTypes;
        while (true)
        {
            if (!changedParentTypes.TryGetValue(type, out var list))
            {
                list                     = new List<YEvent>();
                changedParentTypes[type] = list;
            }
            list.Add(ev);
            if (type._item == null)
            {
                break;
            }
            type = type._item.Parent!;
        }
        changedType._eH.Call(ev, transaction);
    }

    // ---- search markers ----

    private static ArraySearchMarker MarkPosition(List<ArraySearchMarker> searchMarker, Item p, int index)
    {
        if (searchMarker.Count >= MaxSearchMarker)
        {
            var marker = searchMarker[0];
            for (var i = 1; i < searchMarker.Count; i++)
            {
                if (searchMarker[i].Timestamp < marker.Timestamp)
                {
                    marker = searchMarker[i];
                }
            }
            marker.Overwrite(p, index);
            return marker;
        }
        var pm = new ArraySearchMarker(p, index);
        searchMarker.Add(pm);
        return pm;
    }

    internal static ArraySearchMarker? FindMarker(AbstractType yarray, int index)
    {
        if (yarray._start == null || index == 0 || yarray._searchMarker == null)
        {
            return null;
        }
        ArraySearchMarker? marker = null;
        if (yarray._searchMarker.Count > 0)
        {
            marker = yarray._searchMarker[0];
            for (var i = 1; i < yarray._searchMarker.Count; i++)
            {
                var b = yarray._searchMarker[i];
                if (!(Math.Abs(index - marker.Index) < Math.Abs(index - b.Index)))
                {
                    marker = b;
                }
            }
        }
        var p      = yarray._start;
        var pindex = 0;
        if (marker != null)
        {
            p      = marker.P;
            pindex = marker.Index;
            marker.Refresh();
        }
        while (p.Right != null && pindex < index)
        {
            if (!p.Deleted && p.Countable)
            {
                if (index < pindex + p.Length)
                {
                    break;
                }
                pindex += (int)p.Length;
            }
            p = p.Right;
        }
        while (p.Left != null && pindex > index)
        {
            p = p.Left;
            if (!p.Deleted && p.Countable)
            {
                pindex -= (int)p.Length;
            }
        }
        while (p.Left != null && p.Left.Id.Client == p.Id.Client && p.Left.Id.Clock + p.Left.Length == p.Id.Clock)
        {
            p = p.Left;
            if (!p.Deleted && p.Countable)
            {
                pindex -= (int)p.Length;
            }
        }
        if (marker != null && Math.Abs(marker.Index - pindex) < (double)p.Parent!._length / MaxSearchMarker)
        {
            marker.Overwrite(p, pindex);
            return marker;
        }
        return MarkPosition(yarray._searchMarker, p, pindex);
    }

    internal static void UpdateMarkerChanges(List<ArraySearchMarker> searchMarker, int index, int len)
    {
        for (var i = searchMarker.Count - 1; i >= 0; i--)
        {
            var m = searchMarker[i];
            if (len > 0)
            {
                Item? p = m.P;
                p.Marker = false;
                while (p != null && (p.Deleted || !p.Countable))
                {
                    p = p.Left;
                    if (p != null && !p.Deleted && p.Countable)
                    {
                        m.Index -= (int)p.Length;
                    }
                }
                if (p == null || p.Marker)
                {
                    searchMarker.RemoveAt(i);
                    continue;
                }
                m.P      = p;
                p.Marker = true;
            }
            if (index < m.Index || (len > 0 && index == m.Index))
            {
                m.Index = Math.Max(index, m.Index + len);
            }
        }
    }

    // ---- sequence helpers ----

    internal List<Item> GetTypeChildren()
    {
        var arr = new List<Item>();
        for (var s = _start; s != null; s = s.Right)
        {
            arr.Add(s);
        }
        return arr;
    }

    internal List<object?> TypeListSlice(int start, int end)
    {
        if (start < 0)
        {
            start = _length + start;
        }
        if (end < 0)
        {
            end = _length + end;
        }
        var len = end - start;
        var cs  = new List<object?>();
        var n   = _start;
        while (n != null && len > 0)
        {
            if (n.Countable && !n.Deleted)
            {
                var c = n.ContentField.GetContent();
                if (c.Count <= start)
                {
                    start -= c.Count;
                }
                else
                {
                    for (var i = start; i < c.Count && len > 0; i++)
                    {
                        cs.Add(c[i]);
                        len--;
                    }
                    start = 0;
                }
            }
            n = n.Right;
        }
        return cs;
    }

    internal List<object?> TypeListToArray()
    {
        var cs = new List<object?>();
        for (var n = _start; n != null; n = n.Right)
        {
            if (n.Countable && !n.Deleted)
            {
                cs.AddRange(n.ContentField.GetContent());
            }
        }
        return cs;
    }

    internal List<object?> TypeListToArraySnapshot(Snapshot snapshot)
    {
        var cs = new List<object?>();
        for (var n = _start; n != null; n = n.Right)
        {
            if (n.Countable && Snapshot.IsVisible(n, snapshot))
            {
                cs.AddRange(n.ContentField.GetContent());
            }
        }
        return cs;
    }

    internal IEnumerable<object?> TypeListEnumerate()
    {
        for (var n = _start; n != null; n = n.Right)
        {
            if (n.Countable && !n.Deleted)
            {
                var c = n.ContentField.GetContent();
                for (var i = 0; i < c.Count; i++)
                {
                    yield return c[i];
                }
            }
        }
    }

    internal object? TypeListGet(int index)
    {
        var marker = FindMarker(this, index);
        var n      = _start;
        if (marker != null)
        {
            n     =  marker.P;
            index -= marker.Index;
        }
        for (; n != null; n = n.Right)
        {
            if (!n.Deleted && n.Countable)
            {
                if (index < n.Length)
                {
                    return n.ContentField.GetContent()[index];
                }
                index -= (int)n.Length;
            }
        }
        throw new ArgumentOutOfRangeException(nameof(index));
    }

    internal static bool IsAnyValue(object? c)
    {
        return c is not (byte[] or YjsSharp.Doc or AbstractType);
    }

    internal static void TypeListInsertGenericsAfter(Transaction transaction, AbstractType parent, Item? referenceItem, IEnumerable content)
    {
        var           left        = referenceItem;
        var           doc         = transaction.Doc;
        var           ownClientId = doc.ClientId;
        var           store       = doc.Store;
        var           right       = referenceItem == null ? parent._start : referenceItem.Right;
        List<object?>? jsonContent = null;
        void PackJsonContent()
        {
            if (jsonContent != null && jsonContent.Count > 0)
            {
                left = new Item(new ID(ownClientId, store.GetState(ownClientId)), left, left?.LastId, right, right?.Id, parent, null, new ContentAny(jsonContent));
                left.Integrate(transaction, 0);
                jsonContent = null;
            }
        }
        foreach (var c in content)
        {
            if (IsAnyValue(c))
            {
                jsonContent ??= new List<object?>();
                jsonContent.Add(c);
                continue;
            }
            PackJsonContent();
            ItemContent ic = c switch
            {
                byte[] bytes      => new ContentBinary(bytes),
                YjsSharp.Doc subdoc       => new ContentDoc(subdoc),
                AbstractType type => new ContentType(type),
                _                 => throw new ArgumentException("Unexpected content type in insert operation"),
            };
            if (c is YjsSharp.Doc d && d._item != null)
            {
                throw new InvalidOperationException("This document was already integrated as a sub-document. Create a second instance with the same guid instead.");
            }
            if (c is AbstractType at && at._doc != null)
            {
                throw new InvalidOperationException("This type was already integrated into a document. Insert a new instance or a clone instead.");
            }
            left = new Item(new ID(ownClientId, store.GetState(ownClientId)), left, left?.LastId, right, right?.Id, parent, null, ic);
            left.Integrate(transaction, 0);
        }
        PackJsonContent();
    }

    internal static void TypeListInsertGenerics(Transaction transaction, AbstractType parent, int index, IList content)
    {
        if (index > parent._length || index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Length exceeded!");
        }
        if (index == 0)
        {
            if (parent._searchMarker != null)
            {
                UpdateMarkerChanges(parent._searchMarker, index, content.Count);
            }
            TypeListInsertGenericsAfter(transaction, parent, null, content);
            return;
        }
        var startIndex = index;
        var marker     = FindMarker(parent, index);
        var n          = parent._start;
        if (marker != null)
        {
            n     =  marker.P;
            index -= marker.Index;
            if (index == 0)
            {
                n     =  n.Prev;
                index += n != null && n.Countable && !n.Deleted ? (int)n.Length : 0;
            }
        }
        for (; n != null; n = n.Right)
        {
            if (!n.Deleted && n.Countable)
            {
                if (index <= n.Length)
                {
                    if (index < n.Length)
                    {
                        StructStore.GetItemCleanStart(transaction, new ID(n.Id.Client, n.Id.Clock + (uint)index));
                    }
                    break;
                }
                index -= (int)n.Length;
            }
        }
        if (parent._searchMarker != null)
        {
            UpdateMarkerChanges(parent._searchMarker, startIndex, content.Count);
        }
        TypeListInsertGenericsAfter(transaction, parent, n, content);
    }

    internal static void TypeListPushGenerics(Transaction transaction, AbstractType parent, IList content)
    {
        ArraySearchMarker? maxMarker = null;
        if (parent._searchMarker != null)
        {
            foreach (var m in parent._searchMarker)
            {
                if (m.Index > (maxMarker?.Index ?? 0))
                {
                    maxMarker = m;
                }
            }
        }
        var n = maxMarker != null ? maxMarker.P : parent._start;
        if (n != null)
        {
            while (n.Right != null)
            {
                n = n.Right;
            }
        }
        TypeListInsertGenericsAfter(transaction, parent, n, content);
    }

    internal static void TypeListDelete(Transaction transaction, AbstractType parent, int index, int length)
    {
        if (length == 0)
        {
            return;
        }
        if (index < 0 || length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        var startIndex  = index;
        var startLength = length;
        var marker      = FindMarker(parent, index);
        var n           = parent._start;
        if (marker != null)
        {
            n     =  marker.P;
            index -= marker.Index;
        }
        for (; n != null && index > 0; n = n.Right)
        {
            if (!n.Deleted && n.Countable)
            {
                if (index < n.Length)
                {
                    StructStore.GetItemCleanStart(transaction, new ID(n.Id.Client, n.Id.Clock + (uint)index));
                }
                index -= (int)n.Length;
            }
        }
        while (length > 0 && n != null)
        {
            if (!n.Deleted)
            {
                if (length < n.Length)
                {
                    StructStore.GetItemCleanStart(transaction, new ID(n.Id.Client, n.Id.Clock + (uint)length));
                }
                n.Delete(transaction);
                length -= (int)n.Length;
            }
            n = n.Right;
        }
        if (length > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Length exceeded!");
        }
        if (parent._searchMarker != null)
        {
            UpdateMarkerChanges(parent._searchMarker, startIndex, -startLength + length);
        }
    }

    // ---- map helpers ----

    internal static void TypeMapDelete(Transaction transaction, AbstractType parent, string key)
    {
        if (parent._map.TryGetValue(key, out var c))
        {
            c.Delete(transaction);
        }
    }

    internal static void TypeMapSet(Transaction transaction, AbstractType parent, string key, object? value)
    {
        parent._map.TryGetValue(key, out var left);
        var doc         = transaction.Doc;
        var ownClientId = doc.ClientId;
        ItemContent content;
        switch (value)
        {
            case byte[] bytes:
                content = new ContentBinary(bytes);
                break;
            case YjsSharp.Doc subdoc:
                if (subdoc._item != null)
                {
                    throw new InvalidOperationException("This document was already integrated as a sub-document. Create a second instance with the same guid instead.");
                }
                content = new ContentDoc(subdoc);
                break;
            case AbstractType type:
                if (type._doc != null)
                {
                    throw new InvalidOperationException("This type was already integrated into a document. Insert a new instance or a clone instead.");
                }
                content = new ContentType(type);
                break;
            default:
                content = new ContentAny(new List<object?>(1) { value });
                break;
        }
        new Item(new ID(ownClientId, doc.Store.GetState(ownClientId)), left, left?.LastId, null, null, parent, key, content).Integrate(transaction, 0);
    }

    internal object? TypeMapGet(string key)
    {
        return _map.TryGetValue(key, out var val) && !val.Deleted ? val.ContentField.GetLast() : null;
    }

    internal bool TypeMapTryGet(string key, out object? value)
    {
        if (_map.TryGetValue(key, out var val) && !val.Deleted)
        {
            value = val.ContentField.GetLast();
            return true;
        }
        value = null;
        return false;
    }

    internal Dictionary<string, object?> TypeMapGetAll()
    {
        var res = new Dictionary<string, object?>();
        foreach (var kv in _map)
        {
            if (!kv.Value.Deleted)
            {
                res[kv.Key] = kv.Value.ContentField.GetLast();
            }
        }
        return res;
    }

    internal bool TypeMapHas(string key)
    {
        return _map.TryGetValue(key, out var val) && !val.Deleted;
    }

    internal object? TypeMapGetSnapshot(string key, Snapshot snapshot)
    {
        _map.TryGetValue(key, out var v);
        while (v != null && (!snapshot.StateVectorInternal.TryGetValue(v.Id.Client, out var c) || v.Id.Clock >= c))
        {
            v = v.Left;
        }
        return v != null && Snapshot.IsVisible(v, snapshot) ? v.ContentField.GetLast() : null;
    }

    internal Dictionary<string, object?> TypeMapGetAllSnapshot(Snapshot snapshot)
    {
        var res = new Dictionary<string, object?>();
        foreach (var kv in _map)
        {
            Item? v = kv.Value;
            while (v != null && (!snapshot.StateVectorInternal.TryGetValue(v.Id.Client, out var c) || v.Id.Clock >= c))
            {
                v = v.Left;
            }
            if (v != null && Snapshot.IsVisible(v, snapshot))
            {
                res[kv.Key] = v.ContentField.GetLast();
            }
        }
        return res;
    }

    internal IEnumerable<KeyValuePair<string, Item>> TypeMapEntries()
    {
        foreach (var kv in _map)
        {
            if (!kv.Value.Deleted)
            {
                yield return kv;
            }
        }
    }

    internal static object? ValueToJson(object? v)
    {
        return v is AbstractType t ? t.ToJson() : v;
    }
}
