using System;
using System.Collections.Generic;
using System.Linq;
using YjsSharp.Encoding;
using YjsSharp.Structs;
using GC = YjsSharp.Structs.GC;

namespace YjsSharp.Updates;

/// <summary>Reading and writing of document updates (port of Yjs' encoding.js).</summary>
internal static class UpdateCodec
{
    private sealed class StructRefs
    {
        public int                  I;
        public List<AbstractStruct> Refs;

        public StructRefs(List<AbstractStruct> refs)
        {
            Refs = refs;
        }
    }

    private static void WriteStructs(IUpdateEncoder encoder, List<AbstractStruct> structs, ulong client, uint clock)
    {
        clock = Math.Max(clock, structs[0].Id.Clock);
        var startNewStructs = StructStore.FindIndexSS(structs, clock);
        encoder.RestEncoder.WriteVarUInt((ulong)(structs.Count - startNewStructs));
        encoder.WriteClient(client);
        encoder.RestEncoder.WriteVarUInt(clock);
        var firstStruct = structs[startNewStructs];
        firstStruct.Write(encoder, clock - firstStruct.Id.Clock);
        for (var i = startNewStructs + 1; i < structs.Count; i++)
        {
            structs[i].Write(encoder, 0);
        }
    }

    internal static void WriteClientsStructs(IUpdateEncoder encoder, StructStore store, IReadOnlyDictionary<ulong, uint> sm0)
    {
        var sm = new Dictionary<ulong, uint>();
        foreach (var kv in sm0)
        {
            if (store.GetState(kv.Key) > kv.Value)
            {
                sm[kv.Key] = kv.Value;
            }
        }
        foreach (var client in store.Clients.Keys)
        {
            if (!sm0.ContainsKey(client))
            {
                sm[client] = 0;
            }
        }
        encoder.RestEncoder.WriteVarUInt((ulong)sm.Count);
        foreach (var kv in sm.OrderByDescending(e => e.Key))
        {
            WriteStructs(encoder, store.Clients[kv.Key], kv.Key, kv.Value);
        }
    }

    private static Dictionary<ulong, StructRefs> ReadClientsStructRefs(IUpdateDecoder decoder, Doc doc)
    {
        var clientRefs        = new Dictionary<ulong, StructRefs>();
        var numOfStateUpdates = decoder.RestDecoder.ReadVarUInt();
        for (ulong i = 0; i < numOfStateUpdates; i++)
        {
            var numberOfStructs = decoder.RestDecoder.ReadVarLength();
            var refs            = new List<AbstractStruct>(Math.Min(numberOfStructs, 4096));
            var client          = decoder.ReadClient();
            var clock           = decoder.RestDecoder.ReadVarUInt32();
            clientRefs[client] = new StructRefs(refs);
            for (var j = 0; j < numberOfStructs; j++)
            {
                var info = decoder.ReadInfo();
                switch (info & 0x1F)
                {
                    case 0:
                        {
                            var len = decoder.ReadLen();
                            refs.Add(new GC(new ID(client, clock), len));
                            clock += len;
                            break;
                        }
                    case 10:
                        {
                            var len = decoder.RestDecoder.ReadVarUInt32();
                            refs.Add(new Skip(new ID(client, clock), len));
                            clock += len;
                            break;
                        }
                    default:
                        {
                            var item = ReadItem(decoder, info, client, clock, doc);
                            refs.Add(item);
                            clock += item.Length;
                            break;
                        }
                }
            }
        }
        return clientRefs;
    }

    /// <summary>Reads an item; parents are resolved against <paramref name="doc"/>, or kept as raw ids/keys when it is null.</summary>
    internal static Item ReadItem(IUpdateDecoder decoder, byte info, ulong client, uint clock, Doc? doc)
    {
        var cantCopyParentInfo = (info & (0x40 | 0x80)) == 0;
        ID? origin      = (info & 0x80) == 0x80 ? decoder.ReadLeftID() : null;
        ID? rightOrigin = (info & 0x40) == 0x40 ? decoder.ReadRightID() : null;
        AbstractType? parent    = null;
        ID?           parentId  = null;
        string?       parentKey = null;
        if (cantCopyParentInfo)
        {
            if (decoder.ReadParentInfo())
            {
                var key = decoder.ReadString();
                if (doc != null)
                {
                    parent = doc.GetOrCreateRoot(key);
                }
                else
                {
                    parentKey = key;
                }
            }
            else
            {
                parentId = decoder.ReadLeftID();
            }
        }
        var parentSub = cantCopyParentInfo && (info & 0x20) == 0x20 ? decoder.ReadString() : null;
        var content   = ItemContent.Read(decoder, info);
        var item      = new Item(new ID(client, clock), null, origin, null, rightOrigin, parent, parentSub, content);
        item.ParentId  = parentId;
        item.ParentKey = parentKey;
        return item;
    }

    private sealed class RestStructs
    {
        public Dictionary<ulong, uint> Missing = null!;
        public byte[]                  Update  = null!;
    }

    private static RestStructs? IntegrateStructs(Transaction transaction, StructStore store, Dictionary<ulong, StructRefs> clientsStructRefs)
    {
        var stack = new List<AbstractStruct>();
        var clientsStructRefsIds = clientsStructRefs.Keys.OrderBy(c => c).ToList();
        if (clientsStructRefsIds.Count == 0)
        {
            return null;
        }
        StructRefs? GetNextStructTarget()
        {
            if (clientsStructRefsIds.Count == 0)
            {
                return null;
            }
            var nextStructsTarget = clientsStructRefs[clientsStructRefsIds[clientsStructRefsIds.Count - 1]];
            while (nextStructsTarget.Refs.Count == nextStructsTarget.I)
            {
                clientsStructRefsIds.RemoveAt(clientsStructRefsIds.Count - 1);
                if (clientsStructRefsIds.Count > 0)
                {
                    nextStructsTarget = clientsStructRefs[clientsStructRefsIds[clientsStructRefsIds.Count - 1]];
                }
                else
                {
                    return null;
                }
            }
            return nextStructsTarget;
        }
        var curStructsTarget = GetNextStructTarget();
        if (curStructsTarget == null)
        {
            return null;
        }
        var restStructs = new StructStore();
        var missingSV   = new Dictionary<ulong, uint>();
        void UpdateMissingSv(ulong client, uint clock)
        {
            if (!missingSV.TryGetValue(client, out var mclock) || mclock > clock)
            {
                missingSV[client] = clock;
            }
        }
        var stackHead = curStructsTarget.Refs[curStructsTarget.I++];
        var state     = new Dictionary<ulong, uint>();
        void AddStackToRestSS()
        {
            foreach (var item in stack)
            {
                var client = item.Id.Client;
                if (clientsStructRefs.TryGetValue(client, out var inapplicableItems))
                {
                    inapplicableItems.I--;
                    restStructs.Clients[client] = inapplicableItems.Refs.GetRange(inapplicableItems.I, inapplicableItems.Refs.Count - inapplicableItems.I);
                    clientsStructRefs.Remove(client);
                    inapplicableItems.I    = 0;
                    inapplicableItems.Refs = new List<AbstractStruct>();
                }
                else
                {
                    restStructs.Clients[client] = new List<AbstractStruct> { item };
                }
                clientsStructRefsIds.RemoveAll(c => c == client);
            }
            stack.Clear();
        }
        while (true)
        {
            if (stackHead is not Skip)
            {
                if (!state.TryGetValue(stackHead.Id.Client, out var localClock))
                {
                    localClock                 = store.GetState(stackHead.Id.Client);
                    state[stackHead.Id.Client] = localClock;
                }
                var offset = (long)localClock - stackHead.Id.Clock;
                if (offset < 0)
                {
                    stack.Add(stackHead);
                    UpdateMissingSv(stackHead.Id.Client, stackHead.Id.Clock - 1);
                    AddStackToRestSS();
                }
                else
                {
                    var missing = stackHead.GetMissing(transaction, store);
                    if (missing != null)
                    {
                        stack.Add(stackHead);
                        if (!clientsStructRefs.TryGetValue(missing.Value, out var structRefs))
                        {
                            structRefs = new StructRefs(new List<AbstractStruct>());
                        }
                        if (structRefs.Refs.Count == structRefs.I)
                        {
                            UpdateMissingSv(missing.Value, store.GetState(missing.Value));
                            AddStackToRestSS();
                        }
                        else
                        {
                            stackHead = structRefs.Refs[structRefs.I++];
                            continue;
                        }
                    }
                    else if (offset == 0 || offset < stackHead.Length)
                    {
                        stackHead.Integrate(transaction, (uint)offset);
                        state[stackHead.Id.Client] = stackHead.Id.Clock + stackHead.Length;
                    }
                }
            }
            if (stack.Count > 0)
            {
                stackHead = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
            }
            else if (curStructsTarget != null && curStructsTarget.I < curStructsTarget.Refs.Count)
            {
                stackHead = curStructsTarget.Refs[curStructsTarget.I++];
            }
            else
            {
                curStructsTarget = GetNextStructTarget();
                if (curStructsTarget == null)
                {
                    break;
                }
                stackHead = curStructsTarget.Refs[curStructsTarget.I++];
            }
        }
        if (restStructs.Clients.Count > 0)
        {
            var encoder = new UpdateEncoderV2();
            WriteClientsStructs(encoder, restStructs, new Dictionary<ulong, uint>());
            encoder.RestEncoder.WriteVarUInt(0);
            return new RestStructs { Missing = missingSV, Update = encoder.ToArray() };
        }
        return null;
    }

    private static byte[]? ReadAndApplyDeleteSet(IDSDecoder decoder, Transaction transaction, StructStore store)
    {
        var unappliedDS = new DeleteSet();
        var numClients  = decoder.RestDecoder.ReadVarUInt();
        for (ulong i = 0; i < numClients; i++)
        {
            decoder.ResetDsCurVal();
            var client          = decoder.RestDecoder.ReadVarUInt();
            var numberOfDeletes = decoder.RestDecoder.ReadVarUInt();
            store.Clients.TryGetValue(client, out var structs);
            var state = store.GetState(client);
            for (ulong j = 0; j < numberOfDeletes; j++)
            {
                var clock    = decoder.ReadDsClock();
                var clockEnd = clock + decoder.ReadDsLen();
                if (clock < state)
                {
                    if (state < clockEnd)
                    {
                        unappliedDS.Add(client, state, clockEnd - state);
                    }
                    var index = StructStore.FindIndexSS(structs!, clock);
                    var str   = structs![index];
                    if (!str.Deleted && str.Id.Clock < clock)
                    {
                        structs.Insert(index + 1, Item.SplitItem(transaction, (Item)str, clock - str.Id.Clock));
                        index++;
                    }
                    while (index < structs.Count)
                    {
                        str = structs[index++];
                        if (str.Id.Clock < clockEnd)
                        {
                            if (!str.Deleted)
                            {
                                var item = (Item)str;
                                if (clockEnd < str.Id.Clock + str.Length)
                                {
                                    structs.Insert(index, Item.SplitItem(transaction, item, clockEnd - str.Id.Clock));
                                }
                                item.Delete(transaction);
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                }
                else
                {
                    unappliedDS.Add(client, clock, clockEnd - clock);
                }
            }
        }
        if (unappliedDS.Clients.Count > 0)
        {
            var ds = new UpdateEncoderV2();
            ds.RestEncoder.WriteVarUInt(0);
            unappliedDS.Write(ds);
            return ds.ToArray();
        }
        return null;
    }

    internal static void ReadUpdateV2(Decoder decoder, Doc ydoc, object? transactionOrigin, IUpdateDecoder structDecoder)
    {
        ydoc.TransactInternal<object?>(transaction =>
        {
            try
            {
                ReadUpdateBody(transaction, structDecoder);
            }
            catch (Exception e) when (e is not FormatException && e is not ConcurrentDocumentAccessException)
            {
                throw new FormatException("Malformed update: " + e.Message, e);
            }
            return null;
        }, transactionOrigin, false);
    }

    private static void ReadUpdateBody(Transaction transaction, IUpdateDecoder structDecoder)
    {
        {
            transaction._local = false;
            var retry       = false;
            var doc         = transaction.Doc;
            var store       = doc.Store;
            var ss          = ReadClientsStructRefs(structDecoder, doc);
            var restStructs = IntegrateStructs(transaction, store, ss);
            var pending     = store.PendingStructs;
            if (pending != null)
            {
                foreach (var kv in pending.Missing)
                {
                    if (kv.Value < store.GetState(kv.Key))
                    {
                        retry = true;
                        break;
                    }
                }
                if (restStructs != null)
                {
                    foreach (var kv in restStructs.Missing)
                    {
                        if (!pending.Missing.TryGetValue(kv.Key, out var mclock) || mclock > kv.Value)
                        {
                            pending.Missing[kv.Key] = kv.Value;
                        }
                    }
                    pending.Update = UpdateUtils.MergeUpdatesV2(new[] { pending.Update, restStructs.Update });
                }
            }
            else
            {
                store.PendingStructs = restStructs == null ? null : new PendingStructs(restStructs.Missing, restStructs.Update);
            }
            var dsRest = ReadAndApplyDeleteSet(structDecoder, transaction, store);
            if (store.PendingDs != null)
            {
                var pendingDSUpdate = new UpdateDecoderV2(new Decoder(store.PendingDs));
                pendingDSUpdate.RestDecoder.ReadVarUInt();
                var dsRest2 = ReadAndApplyDeleteSet(pendingDSUpdate, transaction, store);
                if (dsRest != null && dsRest2 != null)
                {
                    store.PendingDs = UpdateUtils.MergeUpdatesV2(new[] { dsRest, dsRest2 });
                }
                else
                {
                    store.PendingDs = dsRest ?? dsRest2;
                }
            }
            else
            {
                store.PendingDs = dsRest;
            }
            if (retry)
            {
                var update = store.PendingStructs!.Update;
                store.PendingStructs = null;
                ApplyUpdate(transaction.Doc, update, null, true);
            }
        }
    }

    internal static void ApplyUpdate(Doc doc, byte[] update, object? origin, bool v2)
    {
        var decoder = new Decoder(update);
        IUpdateDecoder structDecoder = v2 ? new UpdateDecoderV2(decoder) : new UpdateDecoderV1(decoder);
        ReadUpdateV2(decoder, doc, origin, structDecoder);
    }

    internal static void WriteStateAsUpdate(IUpdateEncoder encoder, Doc doc, IReadOnlyDictionary<ulong, uint> targetStateVector)
    {
        WriteClientsStructs(encoder, doc.Store, targetStateVector);
        DeleteSet.FromStructStore(doc.Store).Write(encoder);
    }

    internal static byte[] EncodeStateAsUpdate(Doc doc, byte[]? encodedTargetStateVector, bool v2)
    {
        encodedTargetStateVector ??= new byte[] { 0 };
        var targetStateVector = DecodeStateVector(encodedTargetStateVector);
        IUpdateEncoder encoder = v2 ? new UpdateEncoderV2() : new UpdateEncoderV1();
        WriteStateAsUpdate(encoder, doc, targetStateVector);
        var updates = new List<byte[]> { encoder.ToArray() };
        if (doc.Store.PendingDs != null)
        {
            updates.Add(doc.Store.PendingDs);
        }
        if (doc.Store.PendingStructs != null)
        {
            updates.Add(UpdateUtils.DiffUpdateV2(doc.Store.PendingStructs.Update, encodedTargetStateVector));
        }
        if (updates.Count > 1)
        {
            if (!v2)
            {
                var converted = new List<byte[]>(updates.Count) { updates[0] };
                for (var i = 1; i < updates.Count; i++)
                {
                    converted.Add(UpdateUtils.ConvertUpdateFormatV2ToV1(updates[i]));
                }
                return UpdateUtils.MergeUpdates(converted);
            }
            return UpdateUtils.MergeUpdatesV2(updates);
        }
        return updates[0];
    }

    internal static Dictionary<ulong, uint> ReadStateVector(IDSDecoder decoder)
    {
        var ss       = new Dictionary<ulong, uint>();
        var ssLength = decoder.RestDecoder.ReadVarUInt();
        for (ulong i = 0; i < ssLength; i++)
        {
            var client = decoder.RestDecoder.ReadVarUInt();
            var clock  = decoder.RestDecoder.ReadVarUInt32();
            ss[client] = clock;
        }
        return ss;
    }

    internal static Dictionary<ulong, uint> DecodeStateVector(byte[] decodedState)
    {
        return ReadStateVector(new DSDecoderV1(new Decoder(decodedState)));
    }

    internal static void WriteStateVector(IDSEncoder encoder, IReadOnlyDictionary<ulong, uint> sv)
    {
        encoder.RestEncoder.WriteVarUInt((ulong)sv.Count);
        foreach (var kv in sv.OrderByDescending(e => e.Key))
        {
            encoder.RestEncoder.WriteVarUInt(kv.Key);
            encoder.RestEncoder.WriteVarUInt(kv.Value);
        }
    }

    internal static byte[] EncodeStateVector(IReadOnlyDictionary<ulong, uint> sv)
    {
        var enc = new DSEncoderV1();
        WriteStateVector(enc, sv);
        return enc.ToArray();
    }

    internal static byte[] EncodeStateVectorV2(IReadOnlyDictionary<ulong, uint> sv)
    {
        var enc = new DSEncoderV2();
        WriteStateVector(enc, sv);
        return enc.ToArray();
    }
}
