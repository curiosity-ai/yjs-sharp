using System;
using System.Collections.Generic;
using YjsSharp.Structs;

namespace YjsSharp;

/// <summary>A unit of the undo/redo stacks: the insertions and deletions of one or more captured transactions.</summary>
public sealed class StackItem
{
    /// <summary>Creates a stack item from the ranges it covers.</summary>
    public StackItem(DeleteSet deletions, DeleteSet insertions)
    {
        Deletions  = deletions;
        Insertions = insertions;
    }

    /// <summary>
    /// Encodes the insertions and deletions (not the meta data), so that an undo stack can be persisted and restored
    /// with <see cref="UndoManagerOptions.InitUndoStack"/> / <see cref="UndoManagerOptions.InitRedoStack"/>.
    /// </summary>
    public byte[] Encode()
    {
        var encoder = new Encoding.Encoder();
        encoder.WriteVarUInt8Array(Insertions.Encode());
        encoder.WriteVarUInt8Array(Deletions.Encode());
        return encoder.ToArray();
    }

    /// <summary>Decodes a stack item encoded with <see cref="Encode"/>.</summary>
    public static StackItem Decode(byte[] data)
    {
        var decoder    = new Encoding.Decoder(data);
        var insertions = DeleteSet.Decode(decoder.ReadVarUInt8Array());
        var deletions  = DeleteSet.Decode(decoder.ReadVarUInt8Array());
        return new StackItem(deletions, insertions);
    }

    /// <summary>Ranges inserted by the captured changes.</summary>
    public DeleteSet Insertions { get; internal set; }

    /// <summary>Ranges deleted by the captured changes.</summary>
    public DeleteSet Deletions { get; internal set; }

    /// <summary>Arbitrary meta data (e.g. cursor positions to restore).</summary>
    public IDictionary<object, object?> Meta { get; } = new Dictionary<object, object?>();
}

/// <summary>Whether a stack item belongs to the undo or the redo stack.</summary>
public enum StackItemKind
{
    /// <summary>The undo stack.</summary>
    Undo,

    /// <summary>The redo stack.</summary>
    Redo,
}

/// <summary>Arguments of the stack item events of <see cref="UndoManager"/>.</summary>
public sealed class StackItemEventArgs : EventArgs
{
    internal StackItemEventArgs(StackItem stackItem, object? origin, StackItemKind kind, Transaction transaction)
    {
        StackItem   = stackItem;
        Origin      = origin;
        Kind        = kind;
        Transaction = transaction;
    }

    /// <summary>The stack item.</summary>
    public StackItem StackItem { get; }

    /// <summary>The origin of the transaction.</summary>
    public object? Origin { get; }

    /// <summary>Which stack the item is on.</summary>
    public StackItemKind Kind { get; }

    /// <summary>The transaction.</summary>
    public Transaction Transaction { get; }

    /// <summary>The types that changed in the transaction, with their events.</summary>
    public IEnumerable<KeyValuePair<AbstractType, IReadOnlyList<YEvent>>> ChangedParentTypes => Transaction.ChangedParentTypes;
}

/// <summary>Arguments of <see cref="UndoManager.StackCleared"/>.</summary>
public sealed class StackClearedEventArgs : EventArgs
{
    internal StackClearedEventArgs(bool undoStackCleared, bool redoStackCleared)
    {
        UndoStackCleared = undoStackCleared;
        RedoStackCleared = redoStackCleared;
    }

    /// <summary>Whether the undo stack was cleared.</summary>
    public bool UndoStackCleared { get; }

    /// <summary>Whether the redo stack was cleared.</summary>
    public bool RedoStackCleared { get; }
}

/// <summary>Options of an <see cref="UndoManager"/>.</summary>
public sealed class UndoManagerOptions
{
    /// <summary>Changes made within this many milliseconds are merged into one stack item (default 500).</summary>
    public long CaptureTimeout { get; set; } = 500;

    /// <summary>Decides whether a transaction is captured at all.</summary>
    public Func<Transaction, bool>? CaptureTransaction { get; set; }

    /// <summary>Decides whether an item may be deleted when undoing.</summary>
    public Func<Item, bool>? DeleteFilter { get; set; }

    /// <summary>
    /// The transaction origins to track (null means local changes without origin). Instances of a
    /// <see cref="System.Type"/> in the set track every origin of that type. Defaults to <c>{ null }</c>.
    /// </summary>
    public ISet<object?>? TrackedOrigins { get; set; }

    /// <summary>Whether undoing a map change should ignore that a remote client changed the key since.</summary>
    public bool IgnoreRemoteMapChanges { get; set; }

    /// <summary>The document (defaults to the document of the first scope type).</summary>
    public Doc? Doc { get; set; }

    /// <summary>The clock (milliseconds) used for the capture timeout; defaults to the system clock.</summary>
    public Func<long>? Clock { get; set; }

    /// <summary>An undo stack to start with (e.g. restored from <see cref="StackItem.Decode"/>).</summary>
    public IEnumerable<StackItem>? InitUndoStack { get; set; }

    /// <summary>A redo stack to start with.</summary>
    public IEnumerable<StackItem>? InitRedoStack { get; set; }
}

/// <summary>
/// Tracks changes to a set of types and undoes / redoes them (Yjs UndoManager). Changes are captured per
/// transaction when their origin is tracked; changes within <see cref="CaptureTimeout"/> are merged.
/// </summary>
public sealed class UndoManager : IDisposable
{
    private static readonly object NullOrigin = new object();

    internal readonly List<StackItem> _undoStack = new List<StackItem>();
    internal readonly List<StackItem> _redoStack = new List<StackItem>();
    private  readonly List<object>    _scope     = new List<object>();
    private  readonly HashSet<object> _trackedOrigins = new HashSet<object>();
    private  readonly Func<Transaction, bool> _captureTransaction;
    private  readonly Func<Item, bool>        _deleteFilter;
    private  readonly Func<long>              _clock;
    private           bool            _undoing;
    private           bool            _redoing;
    private           StackItem?      _currStackItem;
    private           long            _lastChange;
    private           bool            _disposed;

    /// <summary>Creates an undo manager tracking one type.</summary>
    public UndoManager(AbstractType scope, UndoManagerOptions? options = null)
        : this(new object[] { scope }, options)
    {
    }

    /// <summary>Creates an undo manager tracking several types.</summary>
    public UndoManager(IEnumerable<AbstractType> scope, UndoManagerOptions? options = null)
        : this(ToObjects(scope), options)
    {
    }

    /// <summary>Creates an undo manager tracking a whole document.</summary>
    public UndoManager(Doc doc, UndoManagerOptions? options = null)
        : this(new object[] { doc }, options)
    {
    }

    private static object[] ToObjects(IEnumerable<AbstractType> scope)
    {
        var list = new List<object>();
        foreach (var s in scope)
        {
            list.Add(s);
        }
        return list.ToArray();
    }

    private UndoManager(object[] scope, UndoManagerOptions? options)
    {
        options ??= new UndoManagerOptions();
        if (scope.Length == 0)
        {
            throw new ArgumentException("The undo manager needs at least one type in its scope", nameof(scope));
        }
        Doc = options.Doc ?? (scope[0] is Doc d ? d : ((AbstractType)scope[0]).Doc) ?? throw new ArgumentException("The scope types must be integrated into a document");
        AddToScopeObjects(scope);
        _deleteFilter       = options.DeleteFilter ?? (_ => true);
        _captureTransaction = options.CaptureTransaction ?? (_ => true);
        _clock              = options.Clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (options.TrackedOrigins != null)
        {
            foreach (var o in options.TrackedOrigins)
            {
                _trackedOrigins.Add(o ?? NullOrigin);
            }
        }
        else
        {
            _trackedOrigins.Add(NullOrigin);
        }
        _trackedOrigins.Add(this);
        IgnoreRemoteMapChanges = options.IgnoreRemoteMapChanges;
        CaptureTimeout         = options.CaptureTimeout;
        if (options.InitUndoStack != null)
        {
            _undoStack.AddRange(options.InitUndoStack);
        }
        if (options.InitRedoStack != null)
        {
            _redoStack.AddRange(options.InitRedoStack);
        }
        Doc.AfterTransaction  += OnAfterTransaction;
        Doc.Destroyed         += OnDocDestroyed;
    }

    /// <summary>The document.</summary>
    public Doc Doc { get; }

    /// <summary>Changes within this many milliseconds are merged into one stack item.</summary>
    public long CaptureTimeout { get; set; }

    /// <summary>See <see cref="UndoManagerOptions.IgnoreRemoteMapChanges"/>.</summary>
    public bool IgnoreRemoteMapChanges { get; set; }

    /// <summary>The tracked origins (null stands for "no origin").</summary>
    public IEnumerable<object?> TrackedOrigins
    {
        get
        {
            foreach (var o in _trackedOrigins)
            {
                yield return ReferenceEquals(o, NullOrigin) ? null : o;
            }
        }
    }

    /// <summary>The undo stack (last element is undone first).</summary>
    public IReadOnlyList<StackItem> UndoStack => _undoStack;

    /// <summary>The redo stack (last element is redone first).</summary>
    public IReadOnlyList<StackItem> RedoStack => _redoStack;

    /// <summary>Whether an undo is in progress.</summary>
    public bool IsUndoing => _undoing;

    /// <summary>Whether a redo is in progress.</summary>
    public bool IsRedoing => _redoing;

    /// <summary>Raised when a new stack item was pushed.</summary>
    public event EventHandler<StackItemEventArgs>? StackItemAdded;

    /// <summary>Raised when changes were merged into the last stack item.</summary>
    public event EventHandler<StackItemEventArgs>? StackItemUpdated;

    /// <summary>Raised when a stack item was undone or redone.</summary>
    public event EventHandler<StackItemEventArgs>? StackItemPopped;

    /// <summary>Raised when stacks were cleared.</summary>
    public event EventHandler<StackClearedEventArgs>? StackCleared;

    private bool InScope(Transaction transaction, Item item)
    {
        foreach (var type in _scope)
        {
            if (type == transaction.Doc || (type is AbstractType t && IsParentOf(t, item)))
            {
                return true;
            }
        }
        return false;
    }

    internal static bool IsParentOf(AbstractType parent, Item? child)
    {
        while (child != null)
        {
            if (child.Parent == parent)
            {
                return true;
            }
            child = child.Parent!._item;
        }
        return false;
    }

    private void AddToScopeObjects(IEnumerable<object> ytypes)
    {
        foreach (var ytype in ytypes)
        {
            if (!_scope.Contains(ytype))
            {
                if (ytype is AbstractType t ? t.Doc != Doc : ytype != Doc)
                {
                    throw new ArgumentException("All types in the scope of an undo manager must belong to the same document");
                }
                _scope.Add(ytype);
            }
        }
    }

    /// <summary>Adds types to the scope.</summary>
    public void AddToScope(params AbstractType[] ytypes) => AddToScopeObjects(ytypes);

    /// <summary>Adds types to the scope.</summary>
    public void AddToScope(IEnumerable<AbstractType> ytypes) => AddToScopeObjects(ToObjects(ytypes));

    /// <summary>Starts tracking transactions with the given origin.</summary>
    public void AddTrackedOrigin(object? origin) => _trackedOrigins.Add(origin ?? NullOrigin);

    /// <summary>Stops tracking transactions with the given origin.</summary>
    public void RemoveTrackedOrigin(object? origin) => _trackedOrigins.Remove(origin ?? NullOrigin);

    private bool IsTracked(object? origin)
    {
        if (_trackedOrigins.Contains(origin ?? NullOrigin))
        {
            return true;
        }
        return origin != null && _trackedOrigins.Contains(origin.GetType());
    }

    private void OnAfterTransaction(object? sender, TransactionEventArgs args)
    {
        var transaction = args.Transaction;
        var inScope     = false;
        foreach (var type in _scope)
        {
            if (type == Doc || (type is AbstractType t && transaction._changedParentTypes.ContainsKey(t)))
            {
                inScope = true;
                break;
            }
        }
        if (!_captureTransaction(transaction) || !inScope || !IsTracked(transaction.Origin))
        {
            return;
        }
        var undoing = _undoing;
        var redoing = _redoing;
        var stack   = undoing ? _redoStack : _undoStack;
        if (undoing)
        {
            StopCapturing();
        }
        else if (!redoing)
        {
            Clear(false, true);
        }
        var insertions = new DeleteSet();
        foreach (var kv in transaction._afterState)
        {
            var startClock = transaction.GetBeforeState(kv.Key);
            var len        = kv.Value - startClock;
            if (kv.Value > startClock)
            {
                insertions.Add(kv.Key, startClock, len);
            }
        }
        var now    = _clock();
        var didAdd = false;
        if (_lastChange > 0 && now - _lastChange < CaptureTimeout && stack.Count > 0 && !undoing && !redoing)
        {
            var lastOp = stack[stack.Count - 1];
            lastOp.Deletions  = DeleteSet.Merge(new[] { lastOp.Deletions, transaction._deleteSet });
            lastOp.Insertions = DeleteSet.Merge(new[] { lastOp.Insertions, insertions });
        }
        else
        {
            stack.Add(new StackItem(transaction._deleteSet, insertions));
            didAdd = true;
        }
        if (!undoing && !redoing)
        {
            _lastChange = now;
        }
        StructStore.IterateDeletedStructs(transaction, transaction._deleteSet, item =>
        {
            if (item is Item it && InScope(transaction, it))
            {
                Item.KeepItem(it, true);
            }
        });
        var e = new StackItemEventArgs(stack[stack.Count - 1], transaction.Origin, undoing ? StackItemKind.Redo : StackItemKind.Undo, transaction);
        if (didAdd)
        {
            StackItemAdded?.Invoke(this, e);
        }
        else
        {
            StackItemUpdated?.Invoke(this, e);
        }
    }

    private void ClearStackItem(Transaction tr, StackItem stackItem)
    {
        StructStore.IterateDeletedStructs(tr, stackItem.Deletions, item =>
        {
            if (item is Item it && InScope(tr, it))
            {
                Item.KeepItem(it, false);
            }
        });
    }

    /// <summary>Clears the undo and/or the redo stack.</summary>
    public void Clear(bool clearUndoStack = true, bool clearRedoStack = true)
    {
        if ((clearUndoStack && CanUndo()) || (clearRedoStack && CanRedo()))
        {
            Doc.Transact(tr =>
            {
                if (clearUndoStack)
                {
                    foreach (var item in _undoStack)
                    {
                        ClearStackItem(tr, item);
                    }
                    _undoStack.Clear();
                }
                if (clearRedoStack)
                {
                    foreach (var item in _redoStack)
                    {
                        ClearStackItem(tr, item);
                    }
                    _redoStack.Clear();
                }
                StackCleared?.Invoke(this, new StackClearedEventArgs(clearUndoStack, clearRedoStack));
            });
        }
    }

    /// <summary>Makes the next change start a new stack item even within the capture timeout.</summary>
    public void StopCapturing()
    {
        _lastChange = 0;
    }

    /// <summary>Undoes the last stack item. Returns it, or null when nothing was undone.</summary>
    public StackItem? Undo()
    {
        _undoing = true;
        try
        {
            return PopStackItem(_undoStack, StackItemKind.Undo);
        }
        finally
        {
            _undoing = false;
        }
    }

    /// <summary>Redoes the last undone stack item. Returns it, or null when nothing was redone.</summary>
    public StackItem? Redo()
    {
        _redoing = true;
        try
        {
            return PopStackItem(_redoStack, StackItemKind.Redo);
        }
        finally
        {
            _redoing = false;
        }
    }

    /// <summary>Whether there is something to undo.</summary>
    public bool CanUndo() => _undoStack.Count > 0;

    /// <summary>Whether there is something to redo.</summary>
    public bool CanRedo() => _redoStack.Count > 0;

    private StackItem? PopStackItem(List<StackItem> stack, StackItemKind eventType)
    {
        Transaction? tr  = null;
        var          doc = Doc;
        doc.TransactInternal<object?>(transaction =>
        {
            while (stack.Count > 0 && _currStackItem == null)
            {
                var store       = doc.Store;
                var stackItem   = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                var itemsToRedo   = new HashSet<Item>();
                var itemsToRedoOrdered = new List<Item>();
                var itemsToDelete = new List<Item>();
                var performedChange = false;
                StructStore.IterateDeletedStructs(transaction, stackItem.Insertions, str =>
                {
                    if (str is Item it)
                    {
                        if (it.Redone != null)
                        {
                            var (item, diff) = Item.FollowRedone(store, it.Id);
                            if (diff > 0)
                            {
                                item = StructStore.GetItemCleanStart(transaction, new ID(item.Id.Client, item.Id.Clock + diff));
                            }
                            str = item;
                        }
                        if (str is Item s && !s.Deleted && InScope(transaction, s))
                        {
                            itemsToDelete.Add(s);
                        }
                    }
                });
                StructStore.IterateDeletedStructs(transaction, stackItem.Deletions, str =>
                {
                    if (str is Item it && InScope(transaction, it) && !stackItem.Insertions.IsDeleted(it.Id))
                    {
                        if (itemsToRedo.Add(it))
                        {
                            itemsToRedoOrdered.Add(it);
                        }
                    }
                });
                foreach (var str in itemsToRedoOrdered)
                {
                    performedChange = Item.RedoItem(transaction, str, itemsToRedo, stackItem.Insertions, IgnoreRemoteMapChanges, this) != null || performedChange;
                }
                for (var i = itemsToDelete.Count - 1; i >= 0; i--)
                {
                    var item = itemsToDelete[i];
                    if (_deleteFilter(item))
                    {
                        item.Delete(transaction);
                        performedChange = true;
                    }
                }
                _currStackItem = performedChange ? stackItem : null;
            }
            foreach (var kv in transaction._changed)
            {
                if (kv.Value.Contains(null) && kv.Key._searchMarker != null)
                {
                    kv.Key._searchMarker.Clear();
                }
            }
            tr = transaction;
            return null;
        }, this, true);
        var res = _currStackItem;
        if (res != null)
        {
            StackItemPopped?.Invoke(this, new StackItemEventArgs(res, this, eventType, tr!));
            _currStackItem = null;
        }
        return res;
    }

    private void OnDocDestroyed(object? sender, EventArgs e) => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _trackedOrigins.Remove(this);
        Doc.AfterTransaction -= OnAfterTransaction;
        Doc.Destroyed        -= OnDocDestroyed;
    }
}
