using System;
using System.Collections.Generic;
using YjsSharp.Encoding;
using YjsSharp.Structs;
using YjsSharp.Updates;

namespace YjsSharp;

/// <summary>The state of a document at a point in time: its state vector and delete set.</summary>
public sealed class Snapshot : IEquatable<Snapshot>
{
    internal readonly DeleteSet               Ds;
    internal readonly Dictionary<ulong, uint> StateVectorInternal;

    /// <summary>Creates a snapshot from a delete set and a state vector.</summary>
    public Snapshot(DeleteSet deleteSet, IReadOnlyDictionary<ulong, uint> stateVector)
    {
        Ds                  = deleteSet;
        StateVectorInternal = new Dictionary<ulong, uint>(stateVector.Count);
        foreach (var kv in stateVector)
        {
            StateVectorInternal[kv.Key] = kv.Value;
        }
    }

    internal Snapshot(DeleteSet deleteSet, Dictionary<ulong, uint> stateVector, bool noCopy)
    {
        Ds                  = deleteSet;
        StateVectorInternal = stateVector;
    }

    /// <summary>The deleted ranges.</summary>
    public DeleteSet DeleteSet => Ds;

    /// <summary>The state vector.</summary>
    public IReadOnlyDictionary<ulong, uint> StateVector => StateVectorInternal;

    /// <summary>An empty snapshot.</summary>
    public static Snapshot Empty => new Snapshot(new DeleteSet(), new Dictionary<ulong, uint>(), true);

    /// <summary>Takes a snapshot of a document.</summary>
    public static Snapshot Create(Doc doc) => new Snapshot(DeleteSet.FromStructStore(doc.Store), doc.Store.GetStateVector(), true);

    /// <summary>Whether the item is visible in the snapshot (null: visible now).</summary>
    public static bool IsVisible(Item item, Snapshot? snapshot)
    {
        if (snapshot == null)
        {
            return !item.Deleted;
        }
        return snapshot.StateVectorInternal.TryGetValue(item.Id.Client, out var c) && c > item.Id.Clock && !snapshot.Ds.IsDeleted(item.Id);
    }

    /// <summary>Encodes the snapshot (v1).</summary>
    public byte[] Encode()
    {
        var enc = new DSEncoderV1();
        Ds.Write(enc);
        UpdateCodec.WriteStateVector(enc, StateVectorInternal);
        return enc.ToArray();
    }

    /// <summary>Encodes the snapshot (v2).</summary>
    public byte[] EncodeV2()
    {
        var enc = new DSEncoderV2();
        Ds.Write(enc);
        UpdateCodec.WriteStateVector(enc, StateVectorInternal);
        return enc.ToArray();
    }

    /// <summary>Decodes a snapshot encoded with <see cref="Encode"/>.</summary>
    public static Snapshot Decode(byte[] data)
    {
        var decoder = new DSDecoderV1(new Decoder(data));
        var ds      = DeleteSet.Read(decoder);
        return new Snapshot(ds, UpdateCodec.ReadStateVector(decoder), true);
    }

    /// <summary>Decodes a snapshot encoded with <see cref="EncodeV2"/>.</summary>
    public static Snapshot DecodeV2(byte[] data)
    {
        var decoder = new DSDecoderV2(new Decoder(data));
        var ds      = DeleteSet.Read(decoder);
        return new Snapshot(ds, UpdateCodec.ReadStateVector(decoder), true);
    }

    /// <inheritdoc />
    public bool Equals(Snapshot? other)
    {
        if (other is null)
        {
            return false;
        }
        var ds1 = Ds.Clients;
        var ds2 = other.Ds.Clients;
        var sv1 = StateVectorInternal;
        var sv2 = other.StateVectorInternal;
        if (sv1.Count != sv2.Count || ds1.Count != ds2.Count)
        {
            return false;
        }
        foreach (var kv in sv1)
        {
            if (!sv2.TryGetValue(kv.Key, out var v) || v != kv.Value)
            {
                return false;
            }
        }
        foreach (var kv in ds1)
        {
            ds2.TryGetValue(kv.Key, out var dsitems2);
            dsitems2 ??= new List<DeleteItem>();
            if (kv.Value.Count != dsitems2.Count)
            {
                return false;
            }
            for (var i = 0; i < kv.Value.Count; i++)
            {
                if (!kv.Value[i].Equals(dsitems2[i]))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Snapshot s && Equals(s);

    /// <inheritdoc />
    public override int GetHashCode() => StateVectorInternal.Count;

    internal static void SplitSnapshotAffectedStructs(Transaction transaction, Snapshot snapshot)
    {
        if (!transaction._meta.TryGetValue(typeof(Snapshot), out var metaObj) || metaObj is not HashSet<Snapshot> meta)
        {
            meta                             = new HashSet<Snapshot>(ReferenceEqualityComparer<Snapshot>.Instance);
            transaction._meta[typeof(Snapshot)] = meta;
        }
        var store = transaction.Doc.Store;
        if (!meta.Contains(snapshot))
        {
            foreach (var kv in snapshot.StateVectorInternal)
            {
                if (kv.Value < store.GetState(kv.Key))
                {
                    StructStore.GetItemCleanStart(transaction, new ID(kv.Key, kv.Value));
                }
            }
            StructStore.IterateDeletedStructs(transaction, snapshot.Ds, _ => { });
            meta.Add(snapshot);
        }
    }

    /// <summary>Restores the state captured by the snapshot into a new document. The origin must have GC disabled.</summary>
    public static Doc CreateDocFromSnapshot(Doc originDoc, Snapshot snapshot, Doc? newDoc = null)
    {
        if (originDoc.GcEnabled)
        {
            throw new InvalidOperationException("Garbage-collection must be disabled in `originDoc`!");
        }
        newDoc ??= new Doc();
        var sv      = snapshot.StateVectorInternal;
        var ds      = snapshot.Ds;
        var encoder = new UpdateEncoderV2();
        originDoc.Transact(transaction =>
        {
            var size = 0;
            foreach (var clock in sv.Values)
            {
                if (clock > 0)
                {
                    size++;
                }
            }
            encoder.RestEncoder.WriteVarUInt((ulong)size);
            foreach (var kv in sv)
            {
                var client = kv.Key;
                var clock  = kv.Value;
                if (clock == 0)
                {
                    continue;
                }
                if (clock < originDoc.Store.GetState(client))
                {
                    StructStore.GetItemCleanStart(transaction, new ID(client, clock));
                }
                originDoc.Store.Clients.TryGetValue(client, out var structs);
                structs ??= new List<AbstractStruct>();
                var lastStructIndex = StructStore.FindIndexSS(structs, clock - 1);
                encoder.RestEncoder.WriteVarUInt((ulong)(lastStructIndex + 1));
                encoder.WriteClient(client);
                encoder.RestEncoder.WriteVarUInt(0);
                for (var i = 0; i <= lastStructIndex; i++)
                {
                    structs[i].Write(encoder, 0);
                }
            }
            ds.Write(encoder);
        });
        newDoc.ApplyUpdateV2(encoder.ToArray(), "snapshot");
        return newDoc;
    }

    /// <summary>Whether the snapshot contains everything in the (v1) update.</summary>
    public bool ContainsUpdate(byte[] update) => ContainsUpdateImpl(update, false);

    /// <summary>Whether the snapshot contains everything in the (v2) update.</summary>
    public bool ContainsUpdateV2(byte[] update) => ContainsUpdateImpl(update, true);

    private bool ContainsUpdateImpl(byte[] update, bool v2)
    {
        var d             = new Decoder(update);
        IUpdateDecoder updateDecoder = v2 ? new UpdateDecoderV2(d) : new UpdateDecoderV1(d);
        var lazyDecoder   = new LazyStructReader(updateDecoder, false);
        for (var curr = lazyDecoder.Curr; curr != null; curr = lazyDecoder.Next())
        {
            StateVectorInternal.TryGetValue(curr.Id.Client, out var c);
            if (c < curr.Id.Clock + curr.Length)
            {
                return false;
            }
        }
        var mergedDS = DeleteSet.Merge(new[] { Ds, DeleteSet.Read(updateDecoder) });
        return DeleteSet.Equal(Ds, mergedDS);
    }
}
