using System;
using System.Collections;
using System.Collections.Generic;
using YjsSharp.Structs;
using YjsSharp.Updates;

namespace YjsSharp;

/// <summary>A shared map with string keys (Y.Map).</summary>
public class YMap : AbstractType, IEnumerable<KeyValuePair<string, object?>>
{
    internal const uint TypeRefId = 1;

    private OrderedMap<string, object?>? _prelimContent = new OrderedMap<string, object?>();

    /// <summary>Creates a map that is not attached to a document yet.</summary>
    public YMap()
    {
    }

    /// <summary>Creates a prelim map with initial entries.</summary>
    public YMap(IEnumerable<KeyValuePair<string, object?>> entries)
    {
        foreach (var kv in entries)
        {
            _prelimContent![kv.Key] = kv.Value;
        }
    }

    internal override void Integrate(Doc y, Item? item)
    {
        base.Integrate(y, item);
        var prelim = _prelimContent!;
        _prelimContent = null;
        foreach (var kv in prelim)
        {
            Set(kv.Key, kv.Value);
        }
    }

    internal override AbstractType InternalCopy() => new YMap();

    /// <inheritdoc />
    public override AbstractType Clone()
    {
        var map = new YMap();
        foreach (var kv in this)
        {
            map.Set(kv.Key, kv.Value is AbstractType t ? t.Clone() : kv.Value);
        }
        return map;
    }

    internal override void CallObserver(Transaction transaction, HashSet<string?> parentSubs)
    {
        CallTypeObservers(this, transaction, new MapEvent(this, transaction, parentSubs));
    }

    /// <summary>Registers a handler for changes of this map. Dispose the result to unsubscribe.</summary>
    public IDisposable Observe(Action<MapEvent> handler) => _eH.Add((e, _) => handler((MapEvent)e));

    /// <summary>Registers a handler for changes of this map. Dispose the result to unsubscribe.</summary>
    public IDisposable Observe(Action<MapEvent, Transaction> handler) => _eH.Add((e, t) => handler((MapEvent)e, t));

    /// <summary>The entries converted to plain values (nested types converted recursively).</summary>
    public override object? ToJson()
    {
        var map = new Dictionary<string, object?>();
        foreach (var kv in this)
        {
            map[kv.Key] = ValueToJson(kv.Value);
        }
        return map;
    }

    /// <summary>The number of entries.</summary>
    public int Count
    {
        get
        {
            if (_prelimContent != null)
            {
                return _prelimContent.Count;
            }
            var n = 0;
            foreach (var kv in _map)
            {
                if (!kv.Value.Deleted)
                {
                    n++;
                }
            }
            return n;
        }
    }

    /// <summary>The number of entries (Y.Map size).</summary>
    public int Size => Count;

    /// <summary>The keys.</summary>
    public IEnumerable<string> Keys
    {
        get
        {
            foreach (var kv in this)
            {
                yield return kv.Key;
            }
        }
    }

    /// <summary>The values.</summary>
    public IEnumerable<object?> Values
    {
        get
        {
            foreach (var kv in this)
            {
                yield return kv.Value;
            }
        }
    }

    /// <summary>The entries.</summary>
    public IEnumerable<KeyValuePair<string, object?>> Entries => this;

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        if (_prelimContent != null)
        {
            foreach (var kv in _prelimContent.ToList())
            {
                yield return kv;
            }
            yield break;
        }
        foreach (var kv in TypeMapEntries())
        {
            yield return new KeyValuePair<string, object?>(kv.Key, kv.Value.ContentField.GetLast());
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Deletes a key.</summary>
    public void Delete(string key) => Delete(null, key);

    /// <summary>Deletes a key within the given transaction.</summary>
    public void Delete(Transaction? transaction, string key)
    {
        if (_doc != null)
        {
            Transact(transaction, t => TypeMapDelete(t, this, key));
        }
        else
        {
            _prelimContent!.Remove(key);
        }
    }

    /// <summary>Deletes a key and returns the value it had (null when it was not set).</summary>
    public object? Remove(string key)
    {
        TryGet(key, out var previous);
        Delete(key);
        return previous;
    }

    /// <summary>
    /// Returns the shared type stored under <paramref name="key"/>, creating (and storing) a new one when the key
    /// is not set or holds a value of another kind.
    /// </summary>
    public T GetOrInit<T>(string key)
        where T : AbstractType, new()
    {
        if (TryGet(key, out var existing) && existing is T typed)
        {
            return typed;
        }
        var created = new T();
        Set(key, created);
        return created;
    }

    /// <summary>Sets the key only when its current value differs. Returns whether the map changed.</summary>
    public bool TryUpdate(string key, object? value)
    {
        if (TryGet(key, out var existing) && existing is not AbstractType && value is not AbstractType && Encoding.AnyValues.DeepEquals(existing, value))
        {
            return false;
        }
        Set(key, value);
        return true;
    }

    /// <summary>Sets a key to a value and returns the value.</summary>
    public T Set<T>(string key, T value) => Set(null, key, value);

    /// <summary>Sets a key within the given transaction and returns the value.</summary>
    public T Set<T>(Transaction? transaction, string key, T value)
    {
        if (_doc != null)
        {
            Transact(transaction, t => TypeMapSet(t, this, key, value));
        }
        else
        {
            _prelimContent![key] = value;
        }
        return value;
    }

    /// <summary>The value of a key, or null when the key is not set.</summary>
    public object? Get(string key)
    {
        if (_prelimContent != null)
        {
            return _prelimContent.TryGetValue(key, out var v) ? v : null;
        }
        return TypeMapGet(key);
    }

    /// <summary>The value of a key as <typeparamref name="T"/>.</summary>
    public T Get<T>(string key) => (T)Get(key)!;

    /// <summary>Gets the value of a key.</summary>
    public bool TryGet(string key, out object? value)
    {
        if (_prelimContent != null)
        {
            return _prelimContent.TryGetValue(key, out value);
        }
        return TypeMapTryGet(key, out value);
    }

    /// <summary>The value of a key, or null.</summary>
    public object? this[string key]
    {
        get => Get(key);
        set => Set(key, value);
    }

    /// <summary>Whether the key is set.</summary>
    public bool Has(string key)
    {
        if (_prelimContent != null)
        {
            return _prelimContent.ContainsKey(key);
        }
        return TypeMapHas(key);
    }

    /// <summary>Whether the key is set.</summary>
    public bool ContainsKey(string key) => Has(key);

    /// <summary>Removes all entries.</summary>
    public void Clear() => Clear(null);

    /// <summary>Removes all entries within the given transaction.</summary>
    public void Clear(Transaction? transaction)
    {
        if (_doc != null)
        {
            Transact(transaction, t =>
            {
                foreach (var kv in new List<KeyValuePair<string, Item>>(TypeMapEntries()))
                {
                    TypeMapDelete(t, this, kv.Key);
                }
            });
        }
        else
        {
            _prelimContent!.Clear();
        }
    }

    /// <summary>The entries visible in a snapshot.</summary>
    public IDictionary<string, object?> ToDictionary(Snapshot snapshot) => TypeMapGetAllSnapshot(snapshot);

    internal override void WriteTypeRef(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(TypeRefId);
    }

    /// <inheritdoc />
    public override string ToString() => Encoding.JsJson.Stringify(ToJson()) ?? string.Empty;
}
