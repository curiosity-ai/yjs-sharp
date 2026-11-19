using System.Collections;
using System.Collections.Generic;

namespace YjsSharp;

/// <summary>
/// A dictionary that enumerates in insertion order even after removals, like a JavaScript Map.
/// </summary>
internal sealed class OrderedMap<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : notnull
{
    private readonly Dictionary<TKey, int> _index;
    private readonly List<Entry>           _entries = new List<Entry>();
    private          int                   _removed;

    private struct Entry
    {
        public TKey   Key;
        public TValue Value;
        public bool   Alive;
    }

    public OrderedMap()
    {
        _index = new Dictionary<TKey, int>();
    }

    public OrderedMap(IEqualityComparer<TKey> comparer)
    {
        _index = new Dictionary<TKey, int>(comparer);
    }

    public int Count => _index.Count;

    public bool ContainsKey(TKey key) => _index.ContainsKey(key);

    public bool TryGetValue(TKey key, out TValue value)
    {
        if (_index.TryGetValue(key, out var i))
        {
            value = _entries[i].Value;
            return true;
        }
        value = default!;
        return false;
    }

    public TValue this[TKey key]
    {
        get => _entries[_index[key]].Value;
        set
        {
            if (_index.TryGetValue(key, out var i))
            {
                var e = _entries[i];
                e.Value     = value;
                _entries[i] = e;
            }
            else
            {
                _index[key] = _entries.Count;
                _entries.Add(new Entry { Key = key, Value = value, Alive = true });
            }
        }
    }

    public bool Remove(TKey key)
    {
        if (!_index.TryGetValue(key, out var i))
        {
            return false;
        }
        _index.Remove(key);
        _entries[i] = default;
        _removed++;
        if (_removed > 16 && _removed > _entries.Count / 2)
        {
            Compact();
        }
        return true;
    }

    private void Compact()
    {
        var alive = new List<Entry>(_index.Count);
        foreach (var e in _entries)
        {
            if (e.Alive)
            {
                _index[e.Key] = alive.Count;
                alive.Add(e);
            }
        }
        _entries.Clear();
        _entries.AddRange(alive);
        _removed = 0;
    }

    public void Clear()
    {
        _index.Clear();
        _entries.Clear();
        _removed = 0;
    }

    public IEnumerable<TKey> Keys
    {
        get
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Alive)
                {
                    yield return _entries[i].Key;
                }
            }
        }
    }

    public IEnumerable<TValue> Values
    {
        get
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Alive)
                {
                    yield return _entries[i].Value;
                }
            }
        }
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Alive)
            {
                yield return new KeyValuePair<TKey, TValue>(_entries[i].Key, _entries[i].Value);
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public List<KeyValuePair<TKey, TValue>> ToList()
    {
        var list = new List<KeyValuePair<TKey, TValue>>(_index.Count);
        foreach (var kv in this)
        {
            list.Add(kv);
        }
        return list;
    }
}
