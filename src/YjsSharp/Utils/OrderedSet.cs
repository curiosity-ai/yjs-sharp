using System;
using System.Collections;
using System.Collections.Generic;

namespace YjsSharp;

/// <summary>A set that enumerates in insertion order, like a JavaScript Set. Allocates nothing until used.</summary>
internal sealed class OrderedSet<T> : IReadOnlyCollection<T>
    where T : class
{
    private OrderedMap<T, bool>? _map;

    public int Count => _map?.Count ?? 0;

    public bool Add(T item)
    {
        _map ??= new OrderedMap<T, bool>(ReferenceEqualityComparer<T>.Instance);
        if (_map.ContainsKey(item))
        {
            return false;
        }
        _map[item] = true;
        return true;
    }

    public bool Remove(T item) => _map != null && _map.Remove(item);

    public bool Contains(T item) => _map != null && _map.ContainsKey(item);

    public T[] ToArray()
    {
        if (_map == null)
        {
            return Array.Empty<T>();
        }
        var arr = new T[_map.Count];
        var i   = 0;
        foreach (var k in _map.Keys)
        {
            arr[i++] = k;
        }
        return arr;
    }

    public IEnumerator<T> GetEnumerator()
    {
        if (_map == null)
        {
            yield break;
        }
        foreach (var k in _map.Keys)
        {
            yield return k;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
