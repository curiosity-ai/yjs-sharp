using System.Collections;
using System.Collections.Generic;

namespace YjsSharp;

/// <summary>A set that enumerates in insertion order, like a JavaScript Set.</summary>
internal sealed class OrderedSet<T> : IReadOnlyCollection<T>
    where T : class
{
    private readonly OrderedMap<T, bool> _map = new OrderedMap<T, bool>(ReferenceEqualityComparer<T>.Instance);

    public int Count => _map.Count;

    public bool Add(T item)
    {
        if (_map.ContainsKey(item))
        {
            return false;
        }
        _map[item] = true;
        return true;
    }

    public bool Remove(T item) => _map.Remove(item);

    public bool Contains(T item) => _map.ContainsKey(item);

    public T[] ToArray()
    {
        var arr = new T[_map.Count];
        var i   = 0;
        foreach (var k in _map.Keys)
        {
            arr[i++] = k;
        }
        return arr;
    }

    public IEnumerator<T> GetEnumerator() => _map.Keys.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
