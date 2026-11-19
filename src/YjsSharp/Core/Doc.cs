using System;
using System.Collections.Generic;
using System.Threading;
using YjsSharp.Structs;
using YjsSharp.Updates;

namespace YjsSharp;

/// <summary>Options for creating a <see cref="Doc"/>.</summary>
public sealed class DocOptions
{
    /// <summary>The client id; a random 32 bit number (safe for JavaScript) when null, like Yjs.</summary>
    public ulong? ClientId { get; set; }

    /// <summary>The document's globally unique id; a random UUID when null.</summary>
    public string? Guid { get; set; }

    /// <summary>Whether deleted content is garbage collected. Disable it to restore snapshots.</summary>
    public bool GC { get; set; } = true;

    /// <summary>The inverse of <see cref="GC"/>.</summary>
    public bool SkipGC => !GC;

    /// <summary>An optional collection id that sub-documents inherit.</summary>
    public string? CollectionId { get; set; }

    /// <summary>Whether a sub-document should be loaded automatically.</summary>
    public bool AutoLoad { get; set; }

    /// <summary>Whether the document should be loaded (sub-documents start unloaded otherwise).</summary>
    public bool ShouldLoad { get; set; } = true;

    /// <summary>Arbitrary meta data that is synced with sub-documents.</summary>
    public object? Meta { get; set; }

    /// <summary>A filter deciding whether a deleted item may be garbage collected.</summary>
    public Func<Item, bool>? GcFilter { get; set; }
}

/// <summary>Arguments of <see cref="Doc.Update"/> and <see cref="Doc.UpdateV2"/>.</summary>
public sealed class UpdateEventArgs : EventArgs
{
    internal UpdateEventArgs(byte[] update, object? origin, Transaction transaction)
    {
        Update      = update;
        Origin      = origin;
        Transaction = transaction;
    }

    /// <summary>The encoded update of the transaction.</summary>
    public byte[] Update { get; }

    /// <summary>The origin of the transaction.</summary>
    public object? Origin { get; }

    /// <summary>The transaction.</summary>
    public Transaction Transaction { get; }
}

/// <summary>Arguments of transaction events.</summary>
public sealed class TransactionEventArgs : EventArgs
{
    internal TransactionEventArgs(Transaction transaction)
    {
        Transaction = transaction;
    }

    /// <summary>The transaction.</summary>
    public Transaction Transaction { get; }
}

/// <summary>Arguments of <see cref="Doc.AfterAllTransactions"/>.</summary>
public sealed class TransactionsEventArgs : EventArgs
{
    internal TransactionsEventArgs(IReadOnlyList<Transaction> transactions)
    {
        Transactions = transactions;
    }

    /// <summary>The transactions that were just cleaned up.</summary>
    public IReadOnlyList<Transaction> Transactions { get; }
}

/// <summary>Arguments of <see cref="Doc.Subdocs"/>.</summary>
public sealed class SubdocsEventArgs : EventArgs
{
    internal SubdocsEventArgs(IReadOnlyCollection<Doc> loaded, IReadOnlyCollection<Doc> added, IReadOnlyCollection<Doc> removed, Transaction transaction)
    {
        Loaded      = loaded;
        Added       = added;
        Removed     = removed;
        Transaction = transaction;
    }

    /// <summary>Sub-documents that should be loaded.</summary>
    public IReadOnlyCollection<Doc> Loaded { get; }

    /// <summary>Sub-documents that were added.</summary>
    public IReadOnlyCollection<Doc> Added { get; }

    /// <summary>Sub-documents that were removed.</summary>
    public IReadOnlyCollection<Doc> Removed { get; }

    /// <summary>The transaction.</summary>
    public Transaction Transaction { get; }
}

/// <summary>Thrown when a document is used from two threads at the same time.</summary>
public sealed class ConcurrentDocumentAccessException : InvalidOperationException
{
    internal ConcurrentDocumentAccessException()
        : base("A YjsSharp Doc is single-writer: a transaction is already running on another thread. Synchronize access to the document.")
    {
    }
}

/// <summary>
/// A Yjs document: a collection of named shared types that are kept in sync through updates.
/// A document is single-writer: all reads and writes must be serialized by the caller. Starting a transaction
/// while another thread runs one throws <see cref="ConcurrentDocumentAccessException"/>.
/// </summary>
public sealed class Doc : IDisposable
{
    internal readonly StructStore                         Store = new StructStore();
    internal readonly OrderedMap<string, AbstractType>    Share = new OrderedMap<string, AbstractType>();
    internal          Transaction?                        _transaction;
    internal          List<Transaction>                   _transactionCleanups = new List<Transaction>();
    internal readonly OrderedSet<Doc>                     _subdocs = new OrderedSet<Doc>();
    internal          Item?                               _item;
    private           int                                 _ownerThread;

    /// <summary>Creates a document with default options.</summary>
    public Doc() : this(new DocOptions())
    {
    }

    /// <summary>Creates a document.</summary>
    public Doc(DocOptions options)
    {
        ClientId     = options.ClientId ?? GenerateNewClientId();
        Guid         = options.Guid ?? System.Guid.NewGuid().ToString();
        GcEnabled    = options.GC;
        GcFilter     = options.GcFilter ?? (_ => true);
        CollectionId = options.CollectionId;
        AutoLoad     = options.AutoLoad;
        ShouldLoad   = options.ShouldLoad;
        Meta         = options.Meta;
    }

    internal static ulong GenerateNewClientId()
    {
        return (ulong)Random.Shared.NextInt64(0, 1L << 32);
    }

    /// <summary>The client id used for changes made by this document.</summary>
    public ulong ClientId { get; internal set; }

    /// <summary>The globally unique id of the document.</summary>
    public string Guid { get; internal set; }

    /// <summary>An optional collection id.</summary>
    public string? CollectionId { get; internal set; }

    /// <summary>Whether garbage collection is enabled.</summary>
    public bool GcEnabled { get; }

    /// <summary>Whether garbage collection is disabled.</summary>
    public bool SkipGC => !GcEnabled;

    /// <summary>The filter deciding which deleted items may be garbage collected.</summary>
    public Func<Item, bool> GcFilter { get; }

    /// <summary>Whether a sub-document is loaded automatically.</summary>
    public bool AutoLoad { get; }

    /// <summary>Whether the document should be loaded.</summary>
    public bool ShouldLoad { get; internal set; }

    /// <summary>Meta data.</summary>
    public object? Meta { get; }

    /// <summary>Whether <see cref="Destroy"/> was called.</summary>
    public bool IsDestroyed { get; private set; }

    /// <summary>The parent document when this is a sub-document.</summary>
    public Doc? ParentDoc => _item?.Parent?.Doc;

    /// <summary>The item holding this document when it is a sub-document.</summary>
    public Item? ParentItem => _item;

    /// <summary>The currently running transaction, if any.</summary>
    public Transaction? CurrentTransaction => _transaction;

    /// <summary>Raised with the v1 update of every transaction that changed the document.</summary>
    public event EventHandler<UpdateEventArgs>? Update;

    /// <summary>Raised with the v2 update of every transaction that changed the document.</summary>
    public event EventHandler<UpdateEventArgs>? UpdateV2;

    /// <summary>Raised after a transaction's observers were called.</summary>
    public event EventHandler<TransactionEventArgs>? AfterTransaction;

    /// <summary>Raised when a transaction starts.</summary>
    public event EventHandler<TransactionEventArgs>? BeforeTransaction;

    /// <summary>Raised before the observers of a transaction are called.</summary>
    public event EventHandler<TransactionEventArgs>? BeforeObserverCalls;

    /// <summary>Raised after a transaction was cleaned up (garbage collection and merging done).</summary>
    public event EventHandler<TransactionEventArgs>? AfterTransactionCleanup;

    /// <summary>Raised when the first of a series of nested transactions starts.</summary>
    public event EventHandler? BeforeAllTransactions;

    /// <summary>Raised when all transactions of a series were cleaned up.</summary>
    public event EventHandler<TransactionsEventArgs>? AfterAllTransactions;

    /// <summary>Raised when sub-documents are added, removed or loaded.</summary>
    public event EventHandler<SubdocsEventArgs>? Subdocs;

    /// <summary>Raised when the document is destroyed.</summary>
    public event EventHandler? Destroyed;

    /// <summary>Requests loading a sub-document (raises <see cref="Subdocs"/> on the parent).</summary>
    public void Load()
    {
        var item = _item;
        if (item != null && !ShouldLoad)
        {
            item.Parent!.Doc!.TransactInternal<object?>(tr =>
            {
                tr._subdocsLoaded.Add(this);
                return null;
            }, null, true);
        }
        ShouldLoad = true;
    }

    /// <summary>The sub-documents of this document.</summary>
    public IReadOnlyCollection<Doc> GetSubdocs() => _subdocs;

    /// <summary>The guids of the sub-documents.</summary>
    public ISet<string> GetSubdocGuids()
    {
        var set = new HashSet<string>();
        foreach (var d in _subdocs)
        {
            set.Add(d.Guid);
        }
        return set;
    }

    /// <summary>Runs <paramref name="action"/> in a transaction (nested calls join the running one).</summary>
    public void Transact(Action<Transaction> action, object? origin = null)
    {
        TransactInternal<object?>(t =>
        {
            action(t);
            return null;
        }, origin, true);
    }

    /// <summary>Runs <paramref name="action"/> in a transaction and returns its result.</summary>
    public T Transact<T>(Func<Transaction, T> action, object? origin = null)
    {
        return TransactInternal(action, origin, true);
    }

    internal T TransactInternal<T>(Func<Transaction, T> f, object? origin, bool local)
    {
        var tid      = Environment.CurrentManagedThreadId;
        var acquired = false;
        if (Volatile.Read(ref _ownerThread) != tid)
        {
            if (Interlocked.CompareExchange(ref _ownerThread, tid, 0) != 0)
            {
                throw new ConcurrentDocumentAccessException();
            }
            acquired = true;
        }
        try
        {
            var transactionCleanups = _transactionCleanups;
            var initialCall         = false;
            if (_transaction == null)
            {
                initialCall  = true;
                _transaction = new Transaction(this, origin, local);
                transactionCleanups.Add(_transaction);
                if (transactionCleanups.Count == 1)
                {
                    BeforeAllTransactions?.Invoke(this, EventArgs.Empty);
                }
                BeforeTransaction?.Invoke(this, new TransactionEventArgs(_transaction));
            }
            try
            {
                return f(_transaction);
            }
            finally
            {
                if (initialCall)
                {
                    var finishCleanup = _transaction == transactionCleanups[0];
                    _transaction = null;
                    if (finishCleanup)
                    {
                        Transaction.CleanupTransactions(transactionCleanups, 0);
                    }
                }
            }
        }
        finally
        {
            if (acquired)
            {
                Volatile.Write(ref _ownerThread, 0);
            }
        }
    }

    internal AbstractType GetOrCreateRoot(string name)
    {
        if (!Share.TryGetValue(name, out var type))
        {
            type        = new AbstractType();
            type.Integrate(this, null);
            Share[name] = type;
        }
        return type;
    }

    internal T Get<T>(string name, Func<T> factory)
        where T : AbstractType
    {
        if (!Share.TryGetValue(name, out var type))
        {
            var t = factory();
            t.Integrate(this, null);
            Share[name] = t;
            return t;
        }
        if (type is T typed)
        {
            return typed;
        }
        if (type.GetType() == typeof(AbstractType))
        {
            var t = factory();
            t._map = type._map;
            foreach (var n0 in type._map.Values)
            {
                for (var n = n0; n != null; n = n.Left)
                {
                    n.Parent = t;
                }
            }
            t._start = type._start;
            for (var n = t._start; n != null; n = n.Right)
            {
                n.Parent = t;
            }
            t._length   = type._length;
            Share[name] = t;
            t.Integrate(this, null);
            return t;
        }
        throw new InvalidOperationException($"Type with the name {name} has already been defined with a different constructor");
    }

    /// <summary>Gets or creates the root level text with the given name.</summary>
    public Text GetText(string name = "") => Get(name, () => new Text());

    /// <summary>Gets or creates the root level array with the given name.</summary>
    public YArray GetArray(string name = "") => Get(name, () => new YArray());

    /// <summary>Gets or creates the root level map with the given name.</summary>
    public YMap GetMap(string name = "") => Get(name, () => new YMap());

    /// <summary>Gets or creates the root level XML fragment with the given name.</summary>
    public XmlFragment GetXmlFragment(string name = "") => Get(name, () => new XmlFragment());

    /// <summary>Gets or creates the root level XML element with the given name.</summary>
    public XmlElement GetXmlElement(string name = "") => Get(name, () => new XmlElement());

    /// <summary>Gets or creates the root level XML text with the given name.</summary>
    public XmlText GetXmlText(string name = "") => Get(name, () => new XmlText());

    /// <summary>The root level types defined on this document.</summary>
    public IEnumerable<KeyValuePair<string, AbstractType>> RootTypes => Share;

    /// <summary>Converts all root level types to plain values.</summary>
    public IDictionary<string, object?> ToJson()
    {
        var doc = new Dictionary<string, object?>();
        foreach (var kv in Share)
        {
            doc[kv.Key] = kv.Value.ToJson();
        }
        return doc;
    }

    /// <summary>Encodes the state vector of the document.</summary>
    public byte[] EncodeStateVector() => UpdateCodec.EncodeStateVector(Store.GetStateVector());

    /// <summary>The state vector of the document.</summary>
    public IReadOnlyDictionary<ulong, uint> StateVector => Store.GetStateVector();

    /// <summary>Encodes everything the remote side described by the state vector is missing (update v1).</summary>
    public byte[] EncodeStateAsUpdate(byte[]? encodedTargetStateVector = null) => UpdateCodec.EncodeStateAsUpdate(this, encodedTargetStateVector, false);

    /// <summary>Encodes everything the remote side described by the state vector is missing (update v2).</summary>
    public byte[] EncodeStateAsUpdateV2(byte[]? encodedTargetStateVector = null) => UpdateCodec.EncodeStateAsUpdate(this, encodedTargetStateVector, true);

    /// <summary>Applies an update (v1).</summary>
    public void ApplyUpdate(byte[] update, object? origin = null) => UpdateCodec.ApplyUpdate(this, update, origin, false);

    /// <summary>Applies an update (v2).</summary>
    public void ApplyUpdateV2(byte[] update, object? origin = null) => UpdateCodec.ApplyUpdate(this, update, origin, true);

    /// <summary>Encodes the state of the document at the time of the snapshot as an update (v1). Requires GC to be disabled.</summary>
    public byte[] EncodeStateFromSnapshot(Snapshot snapshot) => Snapshot.EncodeStateFromSnapshot(this, snapshot, false);

    /// <summary>Encodes the state of the document at the time of the snapshot as an update (v2). Requires GC to be disabled.</summary>
    public byte[] EncodeStateFromSnapshotV2(Snapshot snapshot) => Snapshot.EncodeStateFromSnapshot(this, snapshot, true);

    /// <summary>
    /// Garbage collects deleted content now, even when GC is disabled for the document: the deleted items in
    /// <paramref name="deleteSet"/> (all deleted items when null) lose their content. Items kept by an undo manager are
    /// skipped. Snapshots taken before the collected deletions can no longer be restored.
    /// </summary>
    public void ForceGc(DeleteSet? deleteSet = null)
    {
        TransactInternal<object?>(_ =>
        {
            var ds = deleteSet?.Clone() ?? DeleteSet.FromStructStore(Store);
            ds.SortAndMerge();
            foreach (var client in new List<ulong>(ds.Clients.Keys))
            {
                if (!Store.Clients.ContainsKey(client))
                {
                    ds.Clients.Remove(client);
                }
            }
            Transaction.TryGc(ds, Store, GcFilter);
            return null;
        }, null, true);
    }

    /// <summary>
    /// Removes the pending (not yet applicable) structs and deletions from the document and returns them as one
    /// update (v1), or null when nothing is pending.
    /// </summary>
    public byte[]? PrunePending()
    {
        var parts = new List<byte[]>();
        if (Store.PendingStructs != null)
        {
            parts.Add(Store.PendingStructs.Update);
        }
        if (Store.PendingDs != null)
        {
            parts.Add(Store.PendingDs);
        }
        Store.PendingStructs = null;
        Store.PendingDs      = null;
        if (parts.Count == 0)
        {
            return null;
        }
        return UpdateUtils.ConvertUpdateFormatV2ToV1(UpdateUtils.MergeUpdatesV2(parts));
    }

    /// <summary>Whether the document holds updates that could not be applied yet (missing dependencies).</summary>
    public bool HasPendingUpdates => Store.PendingStructs != null || Store.PendingDs != null;

    /// <summary>The state vector of the missing dependencies of pending structs (empty when nothing is pending).</summary>
    public IReadOnlyDictionary<ulong, uint> PendingMissing => Store.PendingStructs?.Missing ?? new Dictionary<ulong, uint>();

    /// <summary>The pending structs as an update (v2), or null.</summary>
    public byte[]? PendingStructsUpdateV2 => Store.PendingStructs?.Update;

    /// <summary>The pending delete set as an update (v2), or null.</summary>
    public byte[]? PendingDeleteSetUpdateV2 => Store.PendingDs;

    /// <summary>Destroys the document (and its sub-documents).</summary>
    public void Destroy()
    {
        IsDestroyed = true;
        foreach (var subdoc in _subdocs.ToArray())
        {
            subdoc.Destroy();
        }
        var item = _item;
        if (item != null)
        {
            _item = null;
            // A subdocument removed in the same transaction is destroyed after the delete set was garbage
            // collected, so its item may already hold ContentDeleted (Yjs assigns content.doc on it regardless).
            var content = item.ContentField as ContentDoc;
            var options = new DocOptions { Guid = Guid, ShouldLoad = false };
            var opts    = content?.Opts ?? new Dictionary<string, object?>();
            if (opts.TryGetValue("gc", out var gc) && gc is bool g)
            {
                options.GC = g;
            }
            if (opts.TryGetValue("autoLoad", out var al) && al is bool a)
            {
                options.AutoLoad = a;
            }
            if (opts.TryGetValue("meta", out var meta))
            {
                options.Meta = meta;
            }
            var replacement = new Doc(options);
            replacement._item = item;
            if (content != null)
            {
                content.DocField = replacement;
            }
            item.Parent!.Doc!.TransactInternal<object?>(transaction =>
            {
                var doc = replacement;
                if (!item.Deleted)
                {
                    transaction._subdocsAdded.Add(doc);
                }
                transaction._subdocsRemoved.Add(this);
                return null;
            }, null, true);
        }
        Destroyed?.Invoke(this, EventArgs.Empty);
        Update                  = null;
        UpdateV2                = null;
        AfterTransaction        = null;
        BeforeTransaction       = null;
        BeforeObserverCalls     = null;
        AfterTransactionCleanup = null;
        BeforeAllTransactions   = null;
        AfterAllTransactions    = null;
        Subdocs                 = null;
        Destroyed               = null;
    }

    /// <inheritdoc />
    public void Dispose() => Destroy();

    internal void RaiseBeforeObserverCalls(Transaction t) => BeforeObserverCalls?.Invoke(this, new TransactionEventArgs(t));

    internal void RaiseAfterTransaction(Transaction t) => AfterTransaction?.Invoke(this, new TransactionEventArgs(t));

    internal void RaiseAfterTransactionCleanup(Transaction t) => AfterTransactionCleanup?.Invoke(this, new TransactionEventArgs(t));

    internal void RaiseAfterAllTransactions(List<Transaction> ts) => AfterAllTransactions?.Invoke(this, new TransactionsEventArgs(ts));

    internal void RaiseSubdocs(Transaction t) => Subdocs?.Invoke(this, new SubdocsEventArgs(t._subdocsLoaded, t._subdocsAdded, t._subdocsRemoved, t));

    internal void RaiseUpdates(Transaction transaction)
    {
        var update = Update;
        if (update != null)
        {
            var encoder = new UpdateEncoderV1();
            if (transaction.WriteUpdateMessage(encoder))
            {
                update(this, new UpdateEventArgs(encoder.ToArray(), transaction.Origin, transaction));
            }
        }
        var updateV2 = UpdateV2;
        if (updateV2 != null)
        {
            var encoder = new UpdateEncoderV2();
            if (transaction.WriteUpdateMessage(encoder))
            {
                updateV2(this, new UpdateEventArgs(encoder.ToArray(), transaction.Origin, transaction));
            }
        }
    }

    /// <inheritdoc />
    public override string ToString() => $"Doc({Guid}, client {ClientId})";
}
