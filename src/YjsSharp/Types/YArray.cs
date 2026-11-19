using System;
using System.Collections;
using System.Collections.Generic;
using YjsSharp.Structs;
using YjsSharp.Updates;

namespace YjsSharp;

/// <summary>
/// A shared array (Y.Array). Elements are Any values (null, bool, numbers, strings, byte arrays, lists, string keyed
/// dictionaries), nested shared types or sub-documents.
/// </summary>
public class YArray : AbstractType, IEnumerable<object?>
{
    internal const uint TypeRefId = 0;

    private List<object?>? _prelimContent = new List<object?>();

    /// <summary>Creates an array that is not attached to a document yet (it can be inserted into another type).</summary>
    public YArray()
    {
        _searchMarker = new List<ArraySearchMarker>();
    }

    /// <summary>Creates a prelim array with initial content.</summary>
    public YArray(IEnumerable<object?> content) : this()
    {
        _prelimContent!.AddRange(content);
    }

    /// <summary>Creates a prelim array with initial content (like Y.Array.from).</summary>
    public static YArray From(IEnumerable<object?> items) => new YArray(items);

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

    internal override AbstractType InternalCopy() => new YArray();

    /// <inheritdoc />
    public override AbstractType Clone()
    {
        var arr  = new YArray();
        var list = new List<object?>();
        foreach (var el in ToArray())
        {
            list.Add(el is AbstractType t ? t.Clone() : el);
        }
        arr.Insert(0, list);
        return arr;
    }

    /// <summary>The number of elements.</summary>
    public int Length => _prelimContent?.Count ?? _length;

    /// <summary>The number of elements.</summary>
    public int Count => Length;

    internal override void CallObserver(Transaction transaction, HashSet<string?> parentSubs)
    {
        base.CallObserver(transaction, parentSubs);
        CallTypeObservers(this, transaction, new ArrayEvent(this, transaction));
    }

    /// <summary>Registers a handler for changes of this array. Dispose the result to unsubscribe.</summary>
    public IDisposable Observe(Action<ArrayEvent> handler) => _eH.Add((e, _) => handler((ArrayEvent)e));

    /// <summary>Registers a handler for changes of this array. Dispose the result to unsubscribe.</summary>
    public IDisposable Observe(Action<ArrayEvent, Transaction> handler) => _eH.Add((e, t) => handler((ArrayEvent)e, t));

    /// <summary>Inserts elements at <paramref name="index"/>.</summary>
    public void Insert(int index, IEnumerable<object?> content) => Insert(null, index, content);

    /// <summary>Inserts elements at <paramref name="index"/>.</summary>
    public void Insert(int index, params object?[] content) => Insert(null, index, content);

    /// <summary>Inserts elements at <paramref name="index"/> within the given transaction.</summary>
    public void Insert(Transaction? transaction, int index, IEnumerable<object?> content)
    {
        var list = AsContentList(content);
        if (_doc != null)
        {
            Transact(transaction, t => TypeListInsertGenerics(t, this, index, list));
        }
        else
        {
            if (index > _prelimContent!.Count || index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Length exceeded!");
            }
            var items = new List<object?>();
            foreach (var o in list)
            {
                items.Add(o);
            }
            _prelimContent.InsertRange(index, items);
        }
    }

    // A shared type passed as content is one element, even though YArray itself is enumerable.
    private static IList AsContentList(IEnumerable<object?> content)
    {
        if (content is AbstractType)
        {
            return new object?[] { content };
        }
        return content as IList ?? new List<object?>(content);
    }

    /// <summary>Appends elements.</summary>
    public void Push(IEnumerable<object?> content) => Push(null, content);

    /// <summary>Appends elements.</summary>
    public void Push(params object?[] content) => Push(null, content);

    /// <summary>Appends elements within the given transaction.</summary>
    public void Push(Transaction? transaction, IEnumerable<object?> content)
    {
        var list = AsContentList(content);
        if (_doc != null)
        {
            Transact(transaction, t => TypeListPushGenerics(t, this, list));
        }
        else
        {
            foreach (var o in list)
            {
                _prelimContent!.Add(o);
            }
        }
    }

    /// <summary>Prepends elements.</summary>
    public void Unshift(IEnumerable<object?> content) => Insert(0, content);

    /// <summary>Prepends elements.</summary>
    public void Unshift(params object?[] content) => Insert(0, content);

    /// <summary>Deletes <paramref name="length"/> elements starting at <paramref name="index"/>.</summary>
    public void Delete(int index, int length = 1) => Delete(null, index, length);

    /// <summary>Deletes elements within the given transaction.</summary>
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

    /// <summary>The element at <paramref name="index"/>.</summary>
    public object? Get(int index)
    {
        if (_prelimContent != null)
        {
            return _prelimContent[index];
        }
        return TypeListGet(index);
    }

    /// <summary>The element at <paramref name="index"/>.</summary>
    public object? this[int index] => Get(index);

    /// <summary>The element at <paramref name="index"/> as <typeparamref name="T"/>.</summary>
    public T Get<T>(int index) => (T)Get(index)!;

    /// <summary>All elements.</summary>
    public List<object?> ToArray() => _prelimContent != null ? new List<object?>(_prelimContent) : TypeListToArray();

    /// <summary>The elements visible in a snapshot.</summary>
    public List<object?> ToArray(Snapshot snapshot) => TypeListToArraySnapshot(snapshot);

    /// <summary>The elements in [start, end); negative values count from the end.</summary>
    public List<object?> Slice(int start = 0, int? end = null)
    {
        if (_prelimContent != null)
        {
            var e = end ?? _prelimContent.Count;
            if (start < 0)
            {
                start += _prelimContent.Count;
            }
            if (e < 0)
            {
                e += _prelimContent.Count;
            }
            return _prelimContent.GetRange(start, Math.Max(0, e - start));
        }
        return TypeListSlice(start, end ?? _length);
    }

    /// <summary>The elements converted to plain values (nested types converted recursively).</summary>
    public override object? ToJson()
    {
        var res = new List<object?>();
        foreach (var c in this)
        {
            res.Add(ValueToJson(c));
        }
        return res;
    }

    /// <inheritdoc />
    public IEnumerator<object?> GetEnumerator()
    {
        if (_prelimContent != null)
        {
            return _prelimContent.GetEnumerator();
        }
        return TypeListEnumerate().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal override void WriteTypeRef(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(TypeRefId);
    }

    /// <inheritdoc />
    public override string ToString() => Encoding.JsJson.Stringify(ToJson()) ?? string.Empty;
}
