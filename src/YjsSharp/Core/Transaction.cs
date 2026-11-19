using System;
using System.Collections.Generic;
using YjsSharp.Structs;
using YjsSharp.Updates;

namespace YjsSharp;

/// <summary>
/// A transaction groups changes to a document. Observers and update events fire once per transaction, when it
/// ends. Transactions are created implicitly by every mutating call, or explicitly through <see cref="Doc.Transact(Action{Transaction}, object?)"/>.
/// </summary>
public sealed class Transaction
{
    internal readonly Doc                                     _doc;
    internal readonly DeleteSet                               _deleteSet = new DeleteSet();
    internal readonly Dictionary<ulong, uint>                 _beforeState;
    internal          Dictionary<ulong, uint>                 _afterState = new Dictionary<ulong, uint>();
    internal readonly OrderedMap<AbstractType, HashSet<string?>> _changed = new OrderedMap<AbstractType, HashSet<string?>>(ReferenceEqualityComparer<AbstractType>.Instance);
    internal readonly OrderedMap<AbstractType, List<YEvent>>  _changedParentTypes = new OrderedMap<AbstractType, List<YEvent>>(ReferenceEqualityComparer<AbstractType>.Instance);
    internal readonly List<AbstractStruct>                    _mergeStructs = new List<AbstractStruct>();
    private           Dictionary<object, object?>?            _metaStore;
    internal readonly OrderedSet<Doc>                         _subdocsAdded = new OrderedSet<Doc>();
    internal readonly OrderedSet<Doc>                         _subdocsRemoved = new OrderedSet<Doc>();
    internal readonly OrderedSet<Doc>                         _subdocsLoaded = new OrderedSet<Doc>();
    internal          bool                                    _needFormattingCleanup;
    internal          bool                                    _local;

    internal Dictionary<object, object?> _meta => _metaStore ??= new Dictionary<object, object?>();

    internal Transaction(Doc doc, object? origin, bool local)
    {
        _doc         = doc;
        _beforeState = doc.Store.GetStateVector();
        Origin       = origin;
        _local       = local;
    }

    /// <summary>The document this transaction changes.</summary>
    public Doc Doc => _doc;

    /// <summary>The origin given when the transaction was started (e.g. a provider, or an undo manager).</summary>
    public object? Origin { get; }

    /// <summary>Whether the change was made locally (false when applying a remote update).</summary>
    public bool Local => _local;

    /// <summary>The ranges deleted by this transaction.</summary>
    public DeleteSet DeleteSet => _deleteSet;

    /// <summary>The state vector at the start of the transaction.</summary>
    public IReadOnlyDictionary<ulong, uint> BeforeState => _beforeState;

    /// <summary>The state vector at the end of the transaction (populated once the transaction ends).</summary>
    public IReadOnlyDictionary<ulong, uint> AfterState => _afterState;

    /// <summary>Arbitrary data observers may attach to the transaction.</summary>
    public IDictionary<object, object?> Meta => _meta;

    /// <summary>The changed types, each with the changed keys (null for sequence changes).</summary>
    public IEnumerable<KeyValuePair<AbstractType, IReadOnlyCollection<string?>>> Changed
    {
        get
        {
            foreach (var kv in _changed)
            {
                yield return new KeyValuePair<AbstractType, IReadOnlyCollection<string?>>(kv.Key, kv.Value);
            }
        }
    }

    /// <summary>The types whose (deep) content changed, with the events fired for them.</summary>
    public IEnumerable<KeyValuePair<AbstractType, IReadOnlyList<YEvent>>> ChangedParentTypes
    {
        get
        {
            foreach (var kv in _changedParentTypes)
            {
                yield return new KeyValuePair<AbstractType, IReadOnlyList<YEvent>>(kv.Key, kv.Value);
            }
        }
    }

    /// <summary>Whether the given type is among the changed parent types.</summary>
    public bool HasChangedParentType(AbstractType type) => _changedParentTypes.ContainsKey(type);

    /// <summary>Sub-documents added in this transaction.</summary>
    public IReadOnlyCollection<Doc> SubdocsAdded => _subdocsAdded;

    /// <summary>Sub-documents removed in this transaction.</summary>
    public IReadOnlyCollection<Doc> SubdocsRemoved => _subdocsRemoved;

    /// <summary>Sub-documents loaded in this transaction.</summary>
    public IReadOnlyCollection<Doc> SubdocsLoaded => _subdocsLoaded;

    internal uint GetBeforeState(ulong client) => _beforeState.TryGetValue(client, out var c) ? c : 0;

    internal ID NextId()
    {
        var y = _doc;
        return new ID(y.ClientId, y.Store.GetState(y.ClientId));
    }

    internal void AddChangedType(AbstractType type, string? parentSub)
    {
        var item = type._item;
        if (item == null || (item.Id.Clock < GetBeforeState(item.Id.Client) && !item.Deleted))
        {
            if (!_changed.TryGetValue(type, out var set))
            {
                set            = new HashSet<string?>();
                _changed[type] = set;
            }
            set.Add(parentSub);
        }
    }

    /// <summary>Encodes the changes of this transaction as an update (v1). Returns null when nothing changed.</summary>
    public byte[]? EncodeUpdate()
    {
        var enc = new UpdateEncoderV1();
        return WriteUpdateMessage(enc) ? enc.ToArray() : null;
    }

    /// <summary>Encodes the changes of this transaction as an update (v2). Returns null when nothing changed.</summary>
    public byte[]? EncodeUpdateV2()
    {
        var enc = new UpdateEncoderV2();
        return WriteUpdateMessage(enc) ? enc.ToArray() : null;
    }

    internal bool WriteUpdateMessage(IUpdateEncoder encoder)
    {
        var afterState = _afterState.Count == 0 && _doc._transaction == this ? _doc.Store.GetStateVector() : _afterState;
        if (_deleteSet.Clients.Count == 0)
        {
            var any = false;
            foreach (var kv in afterState)
            {
                if (!_beforeState.TryGetValue(kv.Key, out var c) || c != kv.Value)
                {
                    any = true;
                    break;
                }
            }
            if (!any)
            {
                return false;
            }
        }
        _deleteSet.SortAndMerge();
        UpdateCodec.WriteClientsStructs(encoder, _doc.Store, _beforeState);
        _deleteSet.Write(encoder);
        return true;
    }

    // Merges structs[pos] with the mergeable run to its left. The run is merged left to right, which is
    // equivalent to Yjs' right-to-left merging but appends each piece once instead of re-copying the merged tail.
    private static int TryToMergeWithLefts(List<AbstractStruct> structs, int pos)
    {
        var k = pos;
        while (k > 0)
        {
            var left  = structs[k - 1];
            var right = structs[k];
            if (left.Deleted == right.Deleted && left.GetType() == right.GetType() && left.CanMergeWith(right))
            {
                k--;
                continue;
            }
            break;
        }
        var merged = pos - k;
        if (merged == 0)
        {
            return 0;
        }
        var target = structs[k];
        for (var j = k + 1; j <= pos; j++)
        {
            var right = structs[j];
            if (!target.MergeWith(right))
            {
                throw new InvalidOperationException("Unexpected case: mergeable structs did not merge");
            }
            if (right is Item ri && ri.ParentSub != null && ri.Parent!._map.TryGetValue(ri.ParentSub, out var cur) && cur == ri)
            {
                ri.Parent._map[ri.ParentSub] = (Item)target;
            }
        }
        structs.RemoveRange(k + 1, merged);
        return merged;
    }

    private static void TryGcDeleteSet(DeleteSet ds, StructStore store, Func<Item, bool> gcFilter)
    {
        foreach (var kv in ds.Clients)
        {
            var structs     = store.Clients[kv.Key];
            var deleteItems = kv.Value;
            for (var di = deleteItems.Count - 1; di >= 0; di--)
            {
                var deleteItem          = deleteItems[di];
                var endDeleteItemClock  = deleteItem.Clock + deleteItem.Len;
                for (var si = StructStore.FindIndexSS(structs, deleteItem.Clock); si < structs.Count; si++)
                {
                    var str = structs[si];
                    if (str.Id.Clock >= endDeleteItemClock)
                    {
                        break;
                    }
                    if (str is Item item && item.Deleted && !item.Keep && gcFilter(item))
                    {
                        item.Gc(store, false);
                    }
                }
            }
        }
    }

    private static void TryMergeDeleteSet(DeleteSet ds, StructStore store)
    {
        foreach (var kv in ds.Clients)
        {
            var structs     = store.Clients[kv.Key];
            var deleteItems = kv.Value;
            for (var di = deleteItems.Count - 1; di >= 0; di--)
            {
                var deleteItem           = deleteItems[di];
                var mostRightIndexToCheck = Math.Min(structs.Count - 1, 1 + StructStore.FindIndexSS(structs, deleteItem.Clock + deleteItem.Len - 1));
                for (var si = mostRightIndexToCheck; si > 0 && structs[si].Id.Clock >= deleteItem.Clock;)
                {
                    si -= 1 + TryToMergeWithLefts(structs, si);
                }
            }
        }
    }

    internal static void TryGc(DeleteSet ds, StructStore store, Func<Item, bool> gcFilter)
    {
        TryGcDeleteSet(ds, store, gcFilter);
        TryMergeDeleteSet(ds, store);
    }

    internal static void CleanupTransactions(List<Transaction> transactionCleanups, int i)
    {
        var errors = new ExceptionCollector();
        while (i < transactionCleanups.Count)
        {
            var transaction  = transactionCleanups[i];
            var doc          = transaction._doc;
            var store        = doc.Store;
            var ds           = transaction._deleteSet;
            var mergeStructs = transaction._mergeStructs;
            try
            {
                ds.SortAndMerge();
                transaction._afterState = store.GetStateVector();
                errors.Run(() => doc.RaiseBeforeObserverCalls(transaction));
                var fs = new List<Action>();
                foreach (var kv in transaction._changed.ToList())
                {
                    var itemtype = kv.Key;
                    var subs     = kv.Value;
                    fs.Add(() =>
                    {
                        if (itemtype._item == null || !itemtype._item.Deleted)
                        {
                            itemtype.CallObserver(transaction, subs);
                        }
                    });
                }
                fs.Add(() =>
                {
                    foreach (var kv in transaction._changedParentTypes.ToList())
                    {
                        var type   = kv.Key;
                        var events = kv.Value;
                        if (type._dEH.Count > 0 && (type._item == null || !type._item.Deleted))
                        {
                            fs.Add(() =>
                            {
                                var filtered = new List<YEvent>(events.Count);
                                foreach (var ev in events)
                                {
                                    if (ev.Target._item == null || !ev.Target._item.Deleted)
                                    {
                                        filtered.Add(ev);
                                    }
                                }
                                foreach (var ev in filtered)
                                {
                                    ev.CurrentTarget = type;
                                    ev._path         = null;
                                }
                                // stable sort by path length, top-level events first
                                var sorted = StableSortByPathLength(filtered);
                                type._dEH.Call(sorted, transaction);
                            });
                        }
                    }
                    fs.Add(() => doc.RaiseAfterTransaction(transaction));
                    fs.Add(() =>
                    {
                        if (transaction._needFormattingCleanup)
                        {
                            TextBase.CleanupYTextAfterTransaction(transaction);
                        }
                    });
                });
                for (var fi = 0; fi < fs.Count; fi++)
                {
                    errors.Run(fs[fi]);
                }
            }
            finally
            {
                if (doc.GcEnabled)
                {
                    TryGcDeleteSet(ds, store, doc.GcFilter);
                }
                TryMergeDeleteSet(ds, store);
                foreach (var kv in transaction._afterState)
                {
                    var client      = kv.Key;
                    var clock       = kv.Value;
                    var beforeClock = transaction.GetBeforeState(client);
                    if (beforeClock != clock)
                    {
                        var structs         = store.Clients[client];
                        var firstChangePos  = Math.Max(StructStore.FindIndexSS(structs, beforeClock), 1);
                        for (var si = structs.Count - 1; si >= firstChangePos;)
                        {
                            si -= 1 + TryToMergeWithLefts(structs, si);
                        }
                    }
                }
                for (var mi = mergeStructs.Count - 1; mi >= 0; mi--)
                {
                    var id                = mergeStructs[mi].Id;
                    var structs           = store.Clients[id.Client];
                    var replacedStructPos = StructStore.FindIndexSS(structs, id.Clock);
                    if (replacedStructPos + 1 < structs.Count)
                    {
                        if (TryToMergeWithLefts(structs, replacedStructPos + 1) > 1)
                        {
                            continue;
                        }
                    }
                    if (replacedStructPos > 0)
                    {
                        TryToMergeWithLefts(structs, replacedStructPos);
                    }
                }
                if (!transaction._local)
                {
                    transaction._afterState.TryGetValue(doc.ClientId, out var after);
                    transaction._beforeState.TryGetValue(doc.ClientId, out var before);
                    if (after != before)
                    {
                        doc.ClientId = Doc.GenerateNewClientId();
                    }
                }
                errors.Run(() => doc.RaiseAfterTransactionCleanup(transaction));
                errors.Run(() => doc.RaiseUpdates(transaction));
                if (transaction._subdocsAdded.Count > 0 || transaction._subdocsRemoved.Count > 0 || transaction._subdocsLoaded.Count > 0)
                {
                    foreach (var subdoc in transaction._subdocsAdded)
                    {
                        subdoc.ClientId = doc.ClientId;
                        subdoc.CollectionId ??= doc.CollectionId;
                        doc._subdocs.Add(subdoc);
                    }
                    foreach (var subdoc in transaction._subdocsRemoved)
                    {
                        doc._subdocs.Remove(subdoc);
                    }
                    errors.Run(() => doc.RaiseSubdocs(transaction));
                    foreach (var subdoc in transaction._subdocsRemoved.ToArray())
                    {
                        errors.Run(subdoc.Destroy);
                    }
                }
                if (transactionCleanups.Count <= i + 1)
                {
                    doc._transactionCleanups = new List<Transaction>();
                    errors.Run(() => doc.RaiseAfterAllTransactions(transactionCleanups));
                }
            }
            i++;
        }
        errors.ThrowIfAny();
    }

    private static List<YEvent> StableSortByPathLength(List<YEvent> events)
    {
        var indexed = new List<(int Len, int Index, YEvent Event)>(events.Count);
        for (var k = 0; k < events.Count; k++)
        {
            indexed.Add((events[k].Path.Count, k, events[k]));
        }
        indexed.Sort((a, b) => a.Len != b.Len ? a.Len.CompareTo(b.Len) : a.Index.CompareTo(b.Index));
        var result = new List<YEvent>(events.Count);
        foreach (var e in indexed)
        {
            result.Add(e.Event);
        }
        return result;
    }
}

/// <summary>Runs callbacks, remembering the first exception so that every callback still runs.</summary>
internal sealed class ExceptionCollector
{
    private Exception? _first;

    public void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            _first ??= e;
        }
    }

    public void ThrowIfAny()
    {
        if (_first != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_first).Throw();
        }
    }
}

/// <summary>Reference equality comparer.</summary>
internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T>
    where T : class
{
    public static readonly ReferenceEqualityComparer<T> Instance = new ReferenceEqualityComparer<T>();

    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

    public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}
