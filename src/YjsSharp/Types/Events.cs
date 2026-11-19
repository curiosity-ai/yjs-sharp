using System;
using System.Collections.Generic;
using YjsSharp.Structs;

namespace YjsSharp;

/// <summary>A list of event handlers that tolerates removal during dispatch.</summary>
internal sealed class EventHandlerList<T>
{
    private Action<T, Transaction>[] _handlers = Array.Empty<Action<T, Transaction>>();

    public int Count => _handlers.Length;

    public IDisposable Add(Action<T, Transaction> handler)
    {
        var arr = new Action<T, Transaction>[_handlers.Length + 1];
        Array.Copy(_handlers, arr, _handlers.Length);
        arr[_handlers.Length] = handler;
        _handlers = arr;
        return new Subscription(this, handler);
    }

    public bool Remove(Action<T, Transaction> handler)
    {
        var idx = Array.IndexOf(_handlers, handler);
        if (idx < 0)
        {
            return false;
        }
        var arr = new Action<T, Transaction>[_handlers.Length - 1];
        Array.Copy(_handlers, 0, arr, 0, idx);
        Array.Copy(_handlers, idx + 1, arr, idx, _handlers.Length - idx - 1);
        _handlers = arr;
        return true;
    }

    public void Clear() => _handlers = Array.Empty<Action<T, Transaction>>();

    public void Call(T arg, Transaction transaction)
    {
        var handlers = _handlers;
        if (handlers.Length == 0)
        {
            return;
        }
        var errors = new ExceptionCollector();
        foreach (var h in handlers)
        {
            errors.Run(() => h(arg, transaction));
        }
        errors.ThrowIfAny();
    }

    private sealed class Subscription : IDisposable
    {
        private EventHandlerList<T>?    _owner;
        private Action<T, Transaction>? _handler;

        public Subscription(EventHandlerList<T> owner, Action<T, Transaction> handler)
        {
            _owner   = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_owner != null && _handler != null)
            {
                _owner.Remove(_handler);
            }
            _owner   = null;
            _handler = null;
        }
    }
}

/// <summary>A change of a single key of a map-like type.</summary>
public enum EntryChangeAction
{
    /// <summary>The key was added.</summary>
    Add,

    /// <summary>The value of the key was replaced.</summary>
    Update,

    /// <summary>The key was deleted.</summary>
    Delete,
}

/// <summary>Describes how a key of a map (or the attributes of an XML element / text) changed.</summary>
public sealed class EntryChange
{
    internal EntryChange(EntryChangeAction action, object? oldValue, object? newValue)
    {
        Action   = action;
        OldValue = oldValue;
        NewValue = newValue;
    }

    /// <summary>What happened to the key.</summary>
    public EntryChangeAction Action { get; }

    /// <summary>The previous value (for <see cref="EntryChangeAction.Update"/> and <see cref="EntryChangeAction.Delete"/>).</summary>
    public object? OldValue { get; }

    /// <summary>The new value (for <see cref="EntryChangeAction.Add"/> and <see cref="EntryChangeAction.Update"/>).</summary>
    public object? NewValue { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Action}(old: {OldValue}, new: {NewValue})";
}

/// <summary>
/// One operation of a delta: an insert (of text, an embed, a nested type, or a list of array elements),
/// a retain (optionally with formatting attributes) or a delete.
/// </summary>
public sealed class Delta : IEquatable<Delta>
{
    private Delta(object? insert, int? retain, int? delete, IDictionary<string, object?>? attributes)
    {
        Insert     = insert;
        Retain     = retain;
        Delete     = delete;
        Attributes = attributes;
    }

    /// <summary>The inserted value: a string, an embed, a shared type, or (for array events) a list of values.</summary>
    public object? Insert { get; }

    /// <summary>The number of retained elements.</summary>
    public int? Retain { get; }

    /// <summary>The number of deleted elements.</summary>
    public int? Delete { get; }

    /// <summary>Formatting attributes of an insert or retain.</summary>
    public IDictionary<string, object?>? Attributes { get; }

    /// <summary>Whether this is an insert.</summary>
    public bool IsInsert => Retain == null && Delete == null;

    /// <summary>Creates an insert operation.</summary>
    public static Delta Inserted(object? value, IDictionary<string, object?>? attributes = null) => new Delta(value, null, null, attributes);

    /// <summary>Creates a retain operation.</summary>
    public static Delta Retained(int length, IDictionary<string, object?>? attributes = null) => new Delta(null, length, null, attributes);

    /// <summary>Creates a delete operation.</summary>
    public static Delta Deleted(int length) => new Delta(null, null, length, null);

    /// <inheritdoc />
    public bool Equals(Delta? other)
    {
        if (other is null)
        {
            return false;
        }
        if (Retain != other.Retain || Delete != other.Delete)
        {
            return false;
        }
        if (!Encoding.AnyValues.DeepEquals(Insert, other.Insert) && !ReferenceEquals(Insert, other.Insert))
        {
            return false;
        }
        var a = Attributes != null && Attributes.Count > 0 ? Attributes : null;
        var b = other.Attributes != null && other.Attributes.Count > 0 ? other.Attributes : null;
        if (a == null || b == null)
        {
            return a == null && b == null;
        }
        return Encoding.AnyValues.DeepEquals(a, b);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Delta d && Equals(d);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Retain, Delete);

    /// <inheritdoc />
    public override string ToString()
    {
        var attrs = Attributes != null && Attributes.Count > 0 ? ", attributes: " + Encoding.JsJson.Stringify(Attributes) : string.Empty;
        if (Retain != null)
        {
            return $"{{ retain: {Retain}{attrs} }}";
        }
        if (Delete != null)
        {
            return $"{{ delete: {Delete} }}";
        }
        string ins;
        try
        {
            ins = Insert is AbstractType t ? t.GetType().Name : Encoding.JsJson.Stringify(Insert) ?? "undefined";
        }
        catch (ArgumentException)
        {
            ins = Insert?.ToString() ?? "null";
        }
        return $"{{ insert: {ins}{attrs} }}";
    }
}

/// <summary>The changes of a sequence-like type: added and deleted items, the delta and the changed keys.</summary>
public sealed class YChanges
{
    internal YChanges(ISet<Item> added, ISet<Item> deleted, IReadOnlyList<Delta> delta, IReadOnlyDictionary<string, EntryChange> keys)
    {
        Added   = added;
        Deleted = deleted;
        Delta   = delta;
        Keys    = keys;
    }

    /// <summary>Items added in the transaction.</summary>
    public ISet<Item> Added { get; }

    /// <summary>Items deleted in the transaction.</summary>
    public ISet<Item> Deleted { get; }

    /// <summary>The sequence changes.</summary>
    public IReadOnlyList<Delta> Delta { get; }

    /// <summary>The changed keys.</summary>
    public IReadOnlyDictionary<string, EntryChange> Keys { get; }
}

/// <summary>Base class of the events fired for changes of a shared type.</summary>
public class YEvent
{
    internal IReadOnlyList<object>?                     _path;
    internal YChanges?                                  _changes;
    internal IReadOnlyDictionary<string, EntryChange>?  _keys;
    internal IReadOnlyList<Delta>?                      _delta;

    internal YEvent(AbstractType target, Transaction transaction)
    {
        Target        = target;
        CurrentTarget = target;
        Transaction   = transaction;
    }

    /// <summary>The type that changed.</summary>
    public AbstractType Target { get; }

    /// <summary>The type the (deep) observer is registered on.</summary>
    public AbstractType CurrentTarget { get; internal set; }

    /// <summary>The transaction that caused the change.</summary>
    public Transaction Transaction { get; }

    /// <summary>
    /// The path from <see cref="CurrentTarget"/> to <see cref="Target"/>: map keys (strings) and array indexes (ints).
    /// </summary>
    public IReadOnlyList<object> Path => _path ??= GetPathTo(CurrentTarget, Target);

    /// <summary>Whether the transaction deleted the given struct.</summary>
    public bool Deletes(AbstractStruct str) => Transaction._deleteSet.IsDeleted(str.Id);

    /// <summary>Whether the transaction added the given struct.</summary>
    public bool Adds(AbstractStruct str) => str.Id.Clock >= Transaction.GetBeforeState(str.Id.Client);

    private void CheckCanCompute()
    {
        if (Transaction.Doc._transactionCleanups.Count == 0)
        {
            throw new InvalidOperationException("You must not compute changes after the event-handler fired.");
        }
    }

    /// <summary>The keys that changed (map entries, XML attributes or text attributes).</summary>
    public IReadOnlyDictionary<string, EntryChange> Keys
    {
        get
        {
            if (_keys == null)
            {
                CheckCanCompute();
                var keys   = new Dictionary<string, EntryChange>();
                var target = Target;
                if (Transaction._changed.TryGetValue(target, out var changed))
                {
                    foreach (var key in changed)
                    {
                        if (key == null)
                        {
                            continue;
                        }
                        var item = target._map[key];
                        EntryChangeAction action;
                        object?           oldValue = null;
                        object?           newValue = null;
                        if (Adds(item))
                        {
                            var prev = item.Left;
                            while (prev != null && Adds(prev))
                            {
                                prev = prev.Left;
                            }
                            if (Deletes(item))
                            {
                                if (prev != null && Deletes(prev))
                                {
                                    action   = EntryChangeAction.Delete;
                                    oldValue = prev.ContentField.GetLast();
                                }
                                else
                                {
                                    continue;
                                }
                            }
                            else
                            {
                                newValue = item.ContentField.GetLast();
                                if (prev != null && Deletes(prev))
                                {
                                    action   = EntryChangeAction.Update;
                                    oldValue = prev.ContentField.GetLast();
                                }
                                else
                                {
                                    action = EntryChangeAction.Add;
                                }
                            }
                        }
                        else
                        {
                            if (Deletes(item))
                            {
                                action   = EntryChangeAction.Delete;
                                oldValue = item.ContentField.GetLast();
                            }
                            else
                            {
                                continue;
                            }
                        }
                        keys[key] = new EntryChange(action, oldValue, newValue);
                    }
                }
                _keys = keys;
            }
            return _keys;
        }
    }

    /// <summary>The sequence changes as a delta.</summary>
    public virtual IReadOnlyList<Delta> Delta => Changes.Delta;

    /// <summary>The changes (added and deleted items, delta, keys).</summary>
    public virtual YChanges Changes
    {
        get
        {
            if (_changes == null)
            {
                CheckCanCompute();
                var target  = Target;
                var added   = new HashSet<Item>();
                var deleted = new HashSet<Item>();
                var delta   = new List<Delta>();
                if (Transaction._changed.TryGetValue(target, out var changed) && changed.Contains(null))
                {
                    string?        lastKind   = null;
                    int            lastCount  = 0;
                    List<object?>? lastInsert = null;
                    void PackOp()
                    {
                        switch (lastKind)
                        {
                            case "delete":
                                delta.Add(YjsSharp.Delta.Deleted(lastCount));
                                break;
                            case "insert":
                                delta.Add(YjsSharp.Delta.Inserted(lastInsert));
                                break;
                            case "retain":
                                delta.Add(YjsSharp.Delta.Retained(lastCount));
                                break;
                        }
                    }
                    for (var item = target._start; item != null; item = item.Right)
                    {
                        if (item.Deleted)
                        {
                            if (Deletes(item) && !Adds(item))
                            {
                                if (lastKind != "delete")
                                {
                                    PackOp();
                                    lastKind  = "delete";
                                    lastCount = 0;
                                }
                                lastCount += (int)item.Length;
                                deleted.Add(item);
                            }
                        }
                        else
                        {
                            if (Adds(item))
                            {
                                if (lastKind != "insert")
                                {
                                    PackOp();
                                    lastKind   = "insert";
                                    lastInsert = new List<object?>();
                                }
                                lastInsert!.AddRange(item.ContentField.GetContent());
                                added.Add(item);
                            }
                            else
                            {
                                if (lastKind != "retain")
                                {
                                    PackOp();
                                    lastKind  = "retain";
                                    lastCount = 0;
                                }
                                lastCount += (int)item.Length;
                            }
                        }
                    }
                    if (lastKind != null && lastKind != "retain")
                    {
                        PackOp();
                    }
                }
                _changes = new YChanges(added, deleted, delta, Keys);
            }
            return _changes;
        }
    }

    private static IReadOnlyList<object> GetPathTo(AbstractType parent, AbstractType child)
    {
        var path = new List<object>();
        while (child._item != null && child != parent)
        {
            if (child._item.ParentSub != null)
            {
                path.Add(child._item.ParentSub);
            }
            else
            {
                var i = 0;
                var c = child._item.Parent!._start;
                while (c != child._item && c != null)
                {
                    if (!c.Deleted && c.Countable)
                    {
                        i += (int)c.Length;
                    }
                    c = c.Right;
                }
                path.Add(i);
            }
            child = child._item.Parent!;
        }
        path.Reverse();
        return path;
    }
}

/// <summary>Event of a <see cref="YArray"/>.</summary>
public sealed class ArrayEvent : YEvent
{
    internal ArrayEvent(YArray target, Transaction transaction) : base(target, transaction)
    {
    }

    /// <summary>The array that changed.</summary>
    public new YArray Target => (YArray)base.Target;
}

/// <summary>Event of a <see cref="YMap"/>.</summary>
public class MapEvent : YEvent
{
    internal MapEvent(YMap target, Transaction transaction, ISet<string?> subs) : base(target, transaction)
    {
        var keys = new HashSet<string>();
        foreach (var s in subs)
        {
            if (s != null)
            {
                keys.Add(s);
            }
        }
        KeysChanged = keys;
    }

    /// <summary>The keys that were touched in the transaction.</summary>
    public ISet<string> KeysChanged { get; }

    /// <summary>The map that changed.</summary>
    public new YMap Target => (YMap)base.Target;
}

/// <summary>Event of an <see cref="XmlFragment"/> or <see cref="XmlElement"/>.</summary>
public sealed class XmlEvent : YEvent
{
    internal XmlEvent(AbstractType target, ISet<string?> subs, Transaction transaction) : base(target, transaction)
    {
        var attrs = new HashSet<string>();
        foreach (var s in subs)
        {
            if (s == null)
            {
                ChildListChanged = true;
            }
            else
            {
                attrs.Add(s);
            }
        }
        AttributesChanged = attrs;
    }

    /// <summary>Whether children were added or removed.</summary>
    public bool ChildListChanged { get; }

    /// <summary>The attributes that changed.</summary>
    public ISet<string> AttributesChanged { get; }
}
