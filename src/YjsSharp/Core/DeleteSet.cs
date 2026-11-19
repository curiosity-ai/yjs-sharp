using System;
using System.Collections.Generic;
using System.Linq;
using YjsSharp.Encoding;
using YjsSharp.Updates;

namespace YjsSharp;

/// <summary>A contiguous range of deleted clocks of one client.</summary>
public readonly struct DeleteItem : IEquatable<DeleteItem>
{
    /// <summary>The first deleted clock.</summary>
    public readonly uint Clock;

    /// <summary>The number of deleted clocks.</summary>
    public readonly uint Len;

    /// <summary>Creates a range.</summary>
    public DeleteItem(uint clock, uint len)
    {
        Clock = clock;
        Len   = len;
    }

    /// <summary>The clock right after the range.</summary>
    public uint End => Clock + Len;

    /// <inheritdoc />
    public bool Equals(DeleteItem other) => Clock == other.Clock && Len == other.Len;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DeleteItem other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Clock, Len);

    /// <inheritdoc />
    public override string ToString() => $"[{Clock}..{Clock + Len})";
}

/// <summary>
/// A set of deleted id ranges per client (Yjs' DeleteSet). After <see cref="SortAndMerge"/> the ranges of each
/// client are sorted and non-overlapping.
/// </summary>
public sealed class DeleteSet
{
    internal readonly Dictionary<ulong, List<DeleteItem>> Clients = new Dictionary<ulong, List<DeleteItem>>();

    /// <summary>Creates an empty delete set.</summary>
    public DeleteSet()
    {
    }

    /// <summary>The clients that have deleted ranges.</summary>
    public IEnumerable<ulong> ClientIds => Clients.Keys;

    /// <summary>Whether the set contains no ranges.</summary>
    public bool IsEmpty => Clients.Count == 0;

    /// <summary>The ranges of a client (empty when none).</summary>
    public IReadOnlyList<DeleteItem> Get(ulong client)
    {
        return Clients.TryGetValue(client, out var list) ? list : (IReadOnlyList<DeleteItem>)Array.Empty<DeleteItem>();
    }

    /// <summary>All entries.</summary>
    public IEnumerable<KeyValuePair<ulong, IReadOnlyList<DeleteItem>>> Entries
    {
        get
        {
            foreach (var kv in Clients)
            {
                yield return new KeyValuePair<ulong, IReadOnlyList<DeleteItem>>(kv.Key, kv.Value);
            }
        }
    }

    /// <summary>Adds a range (call <see cref="SortAndMerge"/> afterwards to normalize).</summary>
    public void Add(ulong client, uint clock, uint length)
    {
        if (!Clients.TryGetValue(client, out var list))
        {
            list            = new List<DeleteItem>();
            Clients[client] = list;
        }
        list.Add(new DeleteItem(clock, length));
    }

    /// <summary>Adds the range of an id plus length.</summary>
    public void Add(ID id, uint length) => Add(id.Client, id.Clock, length);

    internal static int FindIndex(List<DeleteItem> dis, uint clock)
    {
        var left  = 0;
        var right = dis.Count - 1;
        while (left <= right)
        {
            var midindex = (left + right) >> 1;
            var mid      = dis[midindex];
            var midclock = mid.Clock;
            if (midclock <= clock)
            {
                if (clock < midclock + mid.Len)
                {
                    return midindex;
                }
                left = midindex + 1;
            }
            else
            {
                right = midindex - 1;
            }
        }
        return -1;
    }

    /// <summary>Whether the id lies in one of the ranges (the set must be sorted).</summary>
    public bool IsDeleted(ID id)
    {
        return Clients.TryGetValue(id.Client, out var dis) && FindIndex(dis, id.Clock) >= 0;
    }

    /// <summary>Whether the id lies in one of the ranges (the set must be sorted).</summary>
    public bool Contains(ID id) => IsDeleted(id);

    /// <summary>Sorts the ranges of every client and merges adjacent or overlapping ones.</summary>
    public void SortAndMerge()
    {
        foreach (var dels in Clients.Values)
        {
            SortAndMergeItems(dels);
        }
    }

    private static readonly Comparison<DeleteItem> ByClock = (a, b) => a.Clock.CompareTo(b.Clock);

    internal static void SortAndMergeItems(List<DeleteItem> dels)
    {
        if (dels.Count > 1)
        {
            // Array.Sort is not stable but equal clocks merge anyway
            dels.Sort(ByClock);
        }
        int i, j;
        for (i = 1, j = 1; i < dels.Count; i++)
        {
            var left  = dels[j - 1];
            var right = dels[i];
            if (left.Clock + left.Len >= right.Clock)
            {
                dels[j - 1] = new DeleteItem(left.Clock, Math.Max(left.Len, right.Clock + right.Len - left.Clock));
            }
            else
            {
                if (j < i)
                {
                    dels[j] = right;
                }
                j++;
            }
        }
        if (j < dels.Count)
        {
            dels.RemoveRange(j, dels.Count - j);
        }
    }

    /// <summary>Merges several delete sets into a new, normalized one.</summary>
    public static DeleteSet Merge(IReadOnlyList<DeleteSet> dss)
    {
        var merged = new DeleteSet();
        for (var dssI = 0; dssI < dss.Count; dssI++)
        {
            foreach (var kv in dss[dssI].Clients)
            {
                var client = kv.Key;
                if (!merged.Clients.ContainsKey(client))
                {
                    var dels = new List<DeleteItem>(kv.Value);
                    for (var i = dssI + 1; i < dss.Count; i++)
                    {
                        if (dss[i].Clients.TryGetValue(client, out var other))
                        {
                            dels.AddRange(other);
                        }
                    }
                    merged.Clients[client] = dels;
                }
            }
        }
        merged.SortAndMerge();
        return merged;
    }

    /// <summary>Creates a delete set describing all deleted structs of a store.</summary>
    internal static DeleteSet FromStructStore(StructStore ss)
    {
        var ds = new DeleteSet();
        foreach (var kv in ss.Clients)
        {
            var structs = kv.Value;
            List<DeleteItem>? dsitems = null;
            for (var i = 0; i < structs.Count; i++)
            {
                var str = structs[i];
                if (str.Deleted)
                {
                    var clock = str.Id.Clock;
                    var len   = str.Length;
                    while (i + 1 < structs.Count && structs[i + 1].Deleted)
                    {
                        i++;
                        len += structs[i].Length;
                    }
                    dsitems ??= new List<DeleteItem>();
                    dsitems.Add(new DeleteItem(clock, len));
                }
            }
            if (dsitems != null)
            {
                ds.Clients[kv.Key] = dsitems;
            }
        }
        return ds;
    }

    internal void Write(IDSEncoder encoder)
    {
        encoder.RestEncoder.WriteVarUInt((ulong)Clients.Count);
        foreach (var kv in Clients.OrderByDescending(e => e.Key))
        {
            encoder.ResetDsCurVal();
            encoder.RestEncoder.WriteVarUInt(kv.Key);
            var len = kv.Value.Count;
            encoder.RestEncoder.WriteVarUInt((ulong)len);
            for (var i = 0; i < len; i++)
            {
                var item = kv.Value[i];
                encoder.WriteDsClock(item.Clock);
                encoder.WriteDsLen(item.Len);
            }
        }
    }

    internal static DeleteSet Read(IDSDecoder decoder)
    {
        var ds         = new DeleteSet();
        var numClients = decoder.RestDecoder.ReadVarUInt();
        for (ulong i = 0; i < numClients; i++)
        {
            decoder.ResetDsCurVal();
            var client          = decoder.RestDecoder.ReadVarUInt();
            var numberOfDeletes = decoder.RestDecoder.ReadVarUInt();
            if (numberOfDeletes > 0)
            {
                if (!ds.Clients.TryGetValue(client, out var dsField))
                {
                    dsField            = new List<DeleteItem>();
                    ds.Clients[client] = dsField;
                }
                for (ulong j = 0; j < numberOfDeletes; j++)
                {
                    var clock = decoder.ReadDsClock();
                    var len   = decoder.ReadDsLen();
                    dsField.Add(new DeleteItem(clock, len));
                }
            }
        }
        return ds;
    }

    /// <summary>Encodes the delete set (update v1 delete-set layout).</summary>
    public byte[] Encode()
    {
        var enc = new DSEncoderV1();
        Write(enc);
        return enc.ToArray();
    }

    /// <summary>Encodes the delete set (update v2 delete-set layout).</summary>
    public byte[] EncodeV2()
    {
        var enc = new DSEncoderV2();
        Write(enc);
        return enc.ToArray();
    }

    /// <summary>Decodes a delete set encoded with <see cref="Encode"/>.</summary>
    public static DeleteSet Decode(byte[] data) => Read(new DSDecoderV1(new Decoder(data)));

    /// <summary>Decodes a delete set encoded with <see cref="EncodeV2"/>.</summary>
    public static DeleteSet DecodeV2(byte[] data) => Read(new DSDecoderV2(new Decoder(data)));

    /// <summary>Whether two delete sets contain exactly the same ranges in the same order.</summary>
    public static bool Equal(DeleteSet ds1, DeleteSet ds2)
    {
        if (ds1.Clients.Count != ds2.Clients.Count)
        {
            return false;
        }
        foreach (var kv in ds1.Clients)
        {
            if (!ds2.Clients.TryGetValue(kv.Key, out var items2) || kv.Value.Count != items2.Count)
            {
                return false;
            }
            for (var i = 0; i < kv.Value.Count; i++)
            {
                if (!kv.Value[i].Equals(items2[i]))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>Creates a copy.</summary>
    public DeleteSet Clone()
    {
        var ds = new DeleteSet();
        foreach (var kv in Clients)
        {
            ds.Clients[kv.Key] = new List<DeleteItem>(kv.Value);
        }
        return ds;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return "{" + string.Join(", ", Clients.Select(kv => kv.Key + ": [" + string.Join(", ", kv.Value) + "]")) + "}";
    }
}
