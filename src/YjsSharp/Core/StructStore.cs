using System;
using System.Collections.Generic;
using YjsSharp.Structs;

namespace YjsSharp;

/// <summary>Pending structs that could not be integrated yet because they depend on missing updates.</summary>
internal sealed class PendingStructs
{
    public PendingStructs(Dictionary<ulong, uint> missing, byte[] update)
    {
        Missing = missing;
        Update  = update;
    }

    public Dictionary<ulong, uint> Missing { get; }

    public byte[] Update { get; set; }
}

/// <summary>All structs of a document, per client and sorted by clock.</summary>
internal sealed class StructStore
{
    public readonly Dictionary<ulong, List<AbstractStruct>> Clients = new Dictionary<ulong, List<AbstractStruct>>();

    public PendingStructs? PendingStructs;

    public byte[]? PendingDs;

    public Dictionary<ulong, uint> GetStateVector()
    {
        var sm = new Dictionary<ulong, uint>(Clients.Count);
        foreach (var kv in Clients)
        {
            var s = kv.Value[kv.Value.Count - 1];
            sm[kv.Key] = s.Id.Clock + s.Length;
        }
        return sm;
    }

    public uint GetState(ulong client)
    {
        if (!Clients.TryGetValue(client, out var structs))
        {
            return 0;
        }
        var last = structs[structs.Count - 1];
        return last.Id.Clock + last.Length;
    }

    public void IntegrityCheck()
    {
        foreach (var structs in Clients.Values)
        {
            for (var i = 1; i < structs.Count; i++)
            {
                var l = structs[i - 1];
                var r = structs[i];
                if (l.Id.Clock + l.Length != r.Id.Clock)
                {
                    throw new InvalidOperationException("StructStore failed integrity check");
                }
            }
        }
    }

    public void AddStruct(AbstractStruct str)
    {
        if (!Clients.TryGetValue(str.Id.Client, out var structs))
        {
            structs                  = new List<AbstractStruct>();
            Clients[str.Id.Client] = structs;
        }
        else
        {
            var last = structs[structs.Count - 1];
            if (last.Id.Clock + last.Length != str.Id.Clock)
            {
                throw new InvalidOperationException("Unexpected case: struct clock does not follow the last struct of its client");
            }
        }
        structs.Add(str);
    }

    public static int FindIndexSS(List<AbstractStruct> structs, uint clock)
    {
        var left     = 0;
        var right    = structs.Count - 1;
        var mid      = structs[right];
        var midclock = mid.Id.Clock;
        if (midclock == clock)
        {
            return right;
        }
        var pivot    = Math.Floor((double)clock / ((double)midclock + mid.Length - 1) * right);
        var midindex = pivot >= 0 && pivot <= right ? (int)pivot : (left + right) / 2;
        while (left <= right)
        {
            mid      = structs[midindex];
            midclock = mid.Id.Clock;
            if (midclock <= clock)
            {
                if (clock < midclock + mid.Length)
                {
                    return midindex;
                }
                left = midindex + 1;
            }
            else
            {
                right = midindex - 1;
            }
            midindex = (left + right) / 2;
        }
        throw new InvalidOperationException($"Unexpected case: struct with clock {clock} not found");
    }

    public AbstractStruct Find(ID id)
    {
        var structs = Clients[id.Client];
        return structs[FindIndexSS(structs, id.Clock)];
    }

    public Item GetItem(ID id) => (Item)Find(id);

    public static int FindIndexCleanStart(Transaction transaction, List<AbstractStruct> structs, uint clock)
    {
        var index = FindIndexSS(structs, clock);
        var str   = structs[index];
        if (str.Id.Clock < clock && str is Item item)
        {
            structs.Insert(index + 1, Item.SplitItem(transaction, item, clock - item.Id.Clock));
            return index + 1;
        }
        return index;
    }

    public static AbstractStruct GetItemCleanStart(Transaction transaction, ID id)
    {
        var structs = transaction.Doc.Store.Clients[id.Client];
        return structs[FindIndexCleanStart(transaction, structs, id.Clock)];
    }

    public static AbstractStruct GetItemCleanEnd(Transaction transaction, StructStore store, ID id)
    {
        var structs = store.Clients[id.Client];
        var index   = FindIndexSS(structs, id.Clock);
        var str     = structs[index];
        if (id.Clock != str.Id.Clock + str.Length - 1 && str is Item item)
        {
            structs.Insert(index + 1, Item.SplitItem(transaction, item, id.Clock - item.Id.Clock + 1));
        }
        return str;
    }

    public void ReplaceStruct(AbstractStruct str, AbstractStruct newStruct)
    {
        var structs = Clients[str.Id.Client];
        structs[FindIndexSS(structs, str.Id.Clock)] = newStruct;
    }

    public static void IterateStructs(Transaction transaction, List<AbstractStruct> structs, uint clockStart, uint len, Action<AbstractStruct> f)
    {
        if (len == 0)
        {
            return;
        }
        var clockEnd = clockStart + len;
        var index    = FindIndexCleanStart(transaction, structs, clockStart);
        AbstractStruct str;
        do
        {
            str = structs[index++];
            if (clockEnd < str.Id.Clock + str.Length)
            {
                FindIndexCleanStart(transaction, structs, clockEnd);
            }
            f(str);
        }
        while (index < structs.Count && structs[index].Id.Clock < clockEnd);
    }

    public static void IterateDeletedStructs(Transaction transaction, DeleteSet ds, Action<AbstractStruct> f)
    {
        foreach (var kv in ds.Clients)
        {
            if (transaction.Doc.Store.Clients.TryGetValue(kv.Key, out var structs))
            {
                var lastStruct = structs[structs.Count - 1];
                var clockState = lastStruct.Id.Clock + lastStruct.Length;
                var deletes    = kv.Value;
                for (var i = 0; i < deletes.Count && deletes[i].Clock < clockState; i++)
                {
                    var del = deletes[i];
                    IterateStructs(transaction, structs, del.Clock, del.Len, f);
                }
            }
        }
    }
}
