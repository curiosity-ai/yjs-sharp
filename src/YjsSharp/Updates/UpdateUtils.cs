using System;
using System.Collections.Generic;
using YjsSharp.Encoding;
using YjsSharp.Structs;
using GC = YjsSharp.Structs.GC;

namespace YjsSharp.Updates;

/// <summary>Reads the structs of an update one by one without integrating them.</summary>
internal sealed class LazyStructReader
{
    private readonly IUpdateDecoder _decoder;
    private readonly bool           _filterSkips;
    private          ulong          _numOfStateUpdates;
    private          ulong          _stateUpdateIndex;
    private          long           _structsLeft;
    private          ulong          _client;
    private          uint           _clock;

    public AbstractStruct? Curr;

    public LazyStructReader(IUpdateDecoder decoder, bool filterSkips)
    {
        _decoder           = decoder;
        _filterSkips       = filterSkips;
        _numOfStateUpdates = decoder.RestDecoder.ReadVarUInt();
        Next();
    }

    private AbstractStruct? ReadNext()
    {
        while (true)
        {
            var s = ReadNextRaw();
            if (s == null || s.Length > 0)
            {
                return s;
            }
            if (s is not Item)
            {
                throw new FormatException("Malformed update: empty block");
            }
        }
    }

    private AbstractStruct? ReadNextRaw()
    {
        while (_structsLeft == 0)
        {
            if (_stateUpdateIndex >= _numOfStateUpdates)
            {
                return null;
            }
            _stateUpdateIndex++;
            _structsLeft = _decoder.RestDecoder.ReadVarLength();
            _client      = _decoder.ReadClient();
            _clock       = _decoder.RestDecoder.ReadVarUInt32();
        }
        _structsLeft--;
        var info = _decoder.ReadInfo();
        if (info == 10)
        {
            var len = _decoder.RestDecoder.ReadVarUInt32();
            var s   = new Skip(new ID(_client, _clock), len);
            _clock += len;
            return s;
        }
        if ((info & 0x1F) != 0)
        {
            var item = UpdateCodec.ReadItem(_decoder, info, _client, _clock, null);
            _clock += item.Length;
            return item;
        }
        var gcLen = _decoder.ReadLen();
        var gc    = new GC(new ID(_client, _clock), gcLen);
        _clock += gcLen;
        return gc;
    }

    public AbstractStruct? Next()
    {
        do
        {
            Curr = ReadNext();
        }
        while (_filterSkips && Curr is Skip);
        return Curr;
    }
}

/// <summary>Writes structs of possibly several clients as one update section.</summary>
internal sealed class LazyStructWriter
{
    public ulong                                     CurrClient;
    public uint                                      StartClock;
    public int                                       Written;
    public readonly IUpdateEncoder                   Encoder;
    public readonly List<(int Written, byte[] RestBytes)> ClientStructs = new List<(int, byte[])>();

    public LazyStructWriter(IUpdateEncoder encoder)
    {
        Encoder = encoder;
    }
}

/// <summary>Update utilities that work without a document (port of Yjs' updates.js).</summary>
internal static class UpdateUtils
{
    private static IUpdateDecoder CreateDecoder(byte[] update, bool v2)
    {
        var d = new Decoder(update);
        return v2 ? new UpdateDecoderV2(d) : new UpdateDecoderV1(d);
    }

    private static IUpdateEncoder CreateEncoder(bool v2) => v2 ? new UpdateEncoderV2() : new UpdateEncoderV1();

    internal static (List<AbstractStruct> Structs, DeleteSet Ds) DecodeUpdate(byte[] update, bool v2)
    {
        var structs       = new List<AbstractStruct>();
        var updateDecoder = CreateDecoder(update, v2);
        var lazyDecoder   = new LazyStructReader(updateDecoder, false);
        for (var curr = lazyDecoder.Curr; curr != null; curr = lazyDecoder.Next())
        {
            structs.Add(curr);
        }
        return (structs, DeleteSet.Read(updateDecoder));
    }

    public static byte[] MergeUpdates(IReadOnlyList<byte[]> updates) => MergeUpdatesImpl(updates, false);

    public static byte[] MergeUpdatesV2(IReadOnlyList<byte[]> updates) => MergeUpdatesImpl(updates, true);

    internal static byte[] EncodeStateVectorFromUpdate(byte[] update, bool v2)
    {
        IDSEncoder encoder       = v2 ? new DSEncoderV2() : new DSEncoderV1();
        var        updateDecoder = new LazyStructReader(CreateDecoder(update, v2), false);
        var        curr          = updateDecoder.Curr;
        if (curr != null)
        {
            var size          = 0;
            var currClient    = curr.Id.Client;
            var stopCounting  = curr.Id.Clock != 0;
            var currClock     = stopCounting ? 0 : curr.Id.Clock + curr.Length;
            for (; curr != null; curr = updateDecoder.Next())
            {
                if (currClient != curr.Id.Client)
                {
                    if (currClock != 0)
                    {
                        size++;
                        encoder.RestEncoder.WriteVarUInt(currClient);
                        encoder.RestEncoder.WriteVarUInt(currClock);
                    }
                    currClient   = curr.Id.Client;
                    currClock    = 0;
                    stopCounting = curr.Id.Clock != 0;
                }
                if (curr is Skip)
                {
                    stopCounting = true;
                }
                if (!stopCounting)
                {
                    currClock = curr.Id.Clock + curr.Length;
                }
            }
            if (currClock != 0)
            {
                size++;
                encoder.RestEncoder.WriteVarUInt(currClient);
                encoder.RestEncoder.WriteVarUInt(currClock);
            }
            var enc = new Encoder();
            enc.WriteVarUInt((ulong)size);
            enc.WriteEncoder(encoder.RestEncoder);
            encoder.RestEncoder = enc;
            return encoder.ToArray();
        }
        encoder.RestEncoder.WriteVarUInt(0);
        return encoder.ToArray();
    }

    internal static (Dictionary<ulong, uint> From, Dictionary<ulong, uint> To) ParseUpdateMeta(byte[] update, bool v2)
    {
        var from          = new Dictionary<ulong, uint>();
        var to            = new Dictionary<ulong, uint>();
        var updateDecoder = new LazyStructReader(CreateDecoder(update, v2), false);
        var curr          = updateDecoder.Curr;
        if (curr != null)
        {
            var currClient = curr.Id.Client;
            var currClock  = curr.Id.Clock;
            from[currClient] = currClock;
            for (; curr != null; curr = updateDecoder.Next())
            {
                if (currClient != curr.Id.Client)
                {
                    to[currClient]        = currClock;
                    from[curr.Id.Client] = curr.Id.Clock;
                    currClient            = curr.Id.Client;
                }
                currClock = curr.Id.Clock + curr.Length;
            }
            to[currClient] = currClock;
        }
        return (from, to);
    }

    private static AbstractStruct SliceStruct(AbstractStruct left, uint diff)
    {
        var client = left.Id.Client;
        var clock  = left.Id.Clock;
        switch (left)
        {
            case GC:
                return new GC(new ID(client, clock + diff), left.Length - diff);
            case Skip:
                return new Skip(new ID(client, clock + diff), left.Length - diff);
            default:
                {
                    var leftItem = (Item)left;
                    var item = new Item(
                        new ID(client, clock + diff),
                        null,
                        new ID(client, clock + diff - 1),
                        null,
                        leftItem.RightOrigin,
                        leftItem.Parent,
                        leftItem.ParentSub,
                        leftItem.ContentField.Splice(diff));
                    item.ParentId  = leftItem.ParentId;
                    item.ParentKey = leftItem.ParentKey;
                    return item;
                }
        }
    }

    private sealed class CurrWrite
    {
        public AbstractStruct Struct;
        public uint           Offset;

        public CurrWrite(AbstractStruct s, uint offset)
        {
            Struct = s;
            Offset = offset;
        }
    }

    private static int CompareDecoders(LazyStructReader dec1, LazyStructReader dec2)
    {
        var c1 = dec1.Curr!;
        var c2 = dec2.Curr!;
        if (c1.Id.Client == c2.Id.Client)
        {
            var clockDiff = (long)c1.Id.Clock - c2.Id.Clock;
            if (clockDiff == 0)
            {
                if (c1.GetType() == c2.GetType())
                {
                    return 0;
                }
                return c1 is Skip ? 1 : -1;
            }
            return clockDiff < 0 ? -1 : 1;
        }
        return c2.Id.Client.CompareTo(c1.Id.Client);
    }

    private static byte[] MergeUpdatesImpl(IReadOnlyList<byte[]> updates, bool v2)
    {
        if (updates.Count == 1)
        {
            return updates[0];
        }
        var updateDecoders     = new List<IUpdateDecoder>(updates.Count);
        var lazyStructDecoders = new List<LazyStructReader>(updates.Count);
        foreach (var update in updates)
        {
            var d = CreateDecoder(update, v2);
            updateDecoders.Add(d);
            lazyStructDecoders.Add(new LazyStructReader(d, true));
        }
        CurrWrite? currWrite         = null;
        var        updateEncoder     = CreateEncoder(v2);
        var        lazyStructEncoder = new LazyStructWriter(updateEncoder);
        while (true)
        {
            lazyStructDecoders.RemoveAll(dec => dec.Curr == null);
            StableSort(lazyStructDecoders);
            if (lazyStructDecoders.Count == 0)
            {
                break;
            }
            var currDecoder = lazyStructDecoders[0];
            var firstClient = currDecoder.Curr!.Id.Client;
            if (currWrite != null)
            {
                var curr     = currDecoder.Curr;
                var iterated = false;
                while (curr != null && curr.Id.Clock + curr.Length <= currWrite.Struct.Id.Clock + currWrite.Struct.Length && curr.Id.Client >= currWrite.Struct.Id.Client)
                {
                    curr     = currDecoder.Next();
                    iterated = true;
                }
                if (curr == null
                    || curr.Id.Client != firstClient
                    || (iterated && curr.Id.Clock > currWrite.Struct.Id.Clock + currWrite.Struct.Length))
                {
                    continue;
                }
                if (firstClient != currWrite.Struct.Id.Client)
                {
                    WriteStructToLazyStructWriter(lazyStructEncoder, currWrite.Struct, currWrite.Offset);
                    currWrite = new CurrWrite(curr, 0);
                    currDecoder.Next();
                }
                else
                {
                    if (currWrite.Struct.Id.Clock + currWrite.Struct.Length < curr.Id.Clock)
                    {
                        if (currWrite.Struct is Skip)
                        {
                            currWrite.Struct._length = curr.Id.Clock + curr.Length - currWrite.Struct.Id.Clock;
                        }
                        else
                        {
                            WriteStructToLazyStructWriter(lazyStructEncoder, currWrite.Struct, currWrite.Offset);
                            var diff = curr.Id.Clock - currWrite.Struct.Id.Clock - currWrite.Struct.Length;
                            var str  = new Skip(new ID(firstClient, currWrite.Struct.Id.Clock + currWrite.Struct.Length), diff);
                            currWrite = new CurrWrite(str, 0);
                        }
                    }
                    else
                    {
                        var diff = (long)currWrite.Struct.Id.Clock + currWrite.Struct.Length - curr.Id.Clock;
                        if (diff > 0)
                        {
                            if (currWrite.Struct is Skip)
                            {
                                currWrite.Struct._length -= (uint)diff;
                            }
                            else
                            {
                                curr = SliceStruct(curr, (uint)diff);
                            }
                        }
                        if (!currWrite.Struct.MergeWith(curr))
                        {
                            WriteStructToLazyStructWriter(lazyStructEncoder, currWrite.Struct, currWrite.Offset);
                            currWrite = new CurrWrite(curr, 0);
                            currDecoder.Next();
                        }
                    }
                }
            }
            else
            {
                currWrite = new CurrWrite(currDecoder.Curr!, 0);
                currDecoder.Next();
            }
            for (var next = currDecoder.Curr;
                 next != null && next.Id.Client == firstClient && next.Id.Clock == currWrite.Struct.Id.Clock + currWrite.Struct.Length && next is not Skip;
                 next = currDecoder.Next())
            {
                WriteStructToLazyStructWriter(lazyStructEncoder, currWrite.Struct, currWrite.Offset);
                currWrite = new CurrWrite(next, 0);
            }
        }
        if (currWrite != null)
        {
            WriteStructToLazyStructWriter(lazyStructEncoder, currWrite.Struct, currWrite.Offset);
        }
        FinishLazyStructWriting(lazyStructEncoder);
        var dss = new List<DeleteSet>(updateDecoders.Count);
        foreach (var decoder in updateDecoders)
        {
            dss.Add(DeleteSet.Read(decoder));
        }
        var ds = DeleteSet.Merge(dss);
        ds.Write(updateEncoder);
        return updateEncoder.ToArray();
    }

    private static void StableSort(List<LazyStructReader> list)
    {
        // insertion sort: stable, and the list is short and mostly sorted
        for (var i = 1; i < list.Count; i++)
        {
            var x = list[i];
            var j = i - 1;
            while (j >= 0 && CompareDecoders(list[j], x) > 0)
            {
                list[j + 1] = list[j];
                j--;
            }
            list[j + 1] = x;
        }
    }

    internal static byte[] DiffUpdate(byte[] update, byte[] sv) => DiffUpdateImpl(update, sv, false);

    internal static byte[] DiffUpdateV2(byte[] update, byte[] sv) => DiffUpdateImpl(update, sv, true);

    private static byte[] DiffUpdateImpl(byte[] update, byte[] sv, bool v2)
    {
        var state            = UpdateCodec.DecodeStateVector(sv);
        var encoder          = CreateEncoder(v2);
        var lazyStructWriter = new LazyStructWriter(encoder);
        var decoder          = CreateDecoder(update, v2);
        var reader           = new LazyStructReader(decoder, false);
        while (reader.Curr != null)
        {
            var curr       = reader.Curr;
            var currClient = curr.Id.Client;
            state.TryGetValue(currClient, out var svClock);
            if (reader.Curr is Skip)
            {
                reader.Next();
                continue;
            }
            if (curr.Id.Clock + curr.Length > svClock)
            {
                WriteStructToLazyStructWriter(lazyStructWriter, curr, (uint)Math.Max((long)svClock - curr.Id.Clock, 0));
                reader.Next();
                while (reader.Curr != null && reader.Curr.Id.Client == currClient)
                {
                    WriteStructToLazyStructWriter(lazyStructWriter, reader.Curr, 0);
                    reader.Next();
                }
            }
            else
            {
                while (reader.Curr != null && reader.Curr.Id.Client == currClient && reader.Curr.Id.Clock + reader.Curr.Length <= svClock)
                {
                    reader.Next();
                }
            }
        }
        FinishLazyStructWriting(lazyStructWriter);
        var ds = DeleteSet.Read(decoder);
        ds.Write(encoder);
        return encoder.ToArray();
    }

    private static void FlushLazyStructWriter(LazyStructWriter lazyWriter)
    {
        if (lazyWriter.Written > 0)
        {
            lazyWriter.ClientStructs.Add((lazyWriter.Written, lazyWriter.Encoder.RestEncoder.ToArray()));
            lazyWriter.Encoder.RestEncoder = new Encoder();
            lazyWriter.Written             = 0;
        }
    }

    private static void WriteStructToLazyStructWriter(LazyStructWriter lazyWriter, AbstractStruct str, uint offset)
    {
        if (lazyWriter.Written > 0 && lazyWriter.CurrClient != str.Id.Client)
        {
            FlushLazyStructWriter(lazyWriter);
        }
        if (lazyWriter.Written == 0)
        {
            lazyWriter.CurrClient = str.Id.Client;
            lazyWriter.Encoder.WriteClient(str.Id.Client);
            lazyWriter.Encoder.RestEncoder.WriteVarUInt(str.Id.Clock + offset);
        }
        str.Write(lazyWriter.Encoder, offset);
        lazyWriter.Written++;
    }

    private static void FinishLazyStructWriting(LazyStructWriter lazyWriter)
    {
        FlushLazyStructWriter(lazyWriter);
        var restEncoder = lazyWriter.Encoder.RestEncoder;
        restEncoder.WriteVarUInt((ulong)lazyWriter.ClientStructs.Count);
        foreach (var partStructs in lazyWriter.ClientStructs)
        {
            restEncoder.WriteVarUInt((ulong)partStructs.Written);
            restEncoder.WriteUInt8Array(partStructs.RestBytes);
        }
    }

    internal static byte[] ConvertUpdateFormat(byte[] update, Func<AbstractStruct, AbstractStruct> blockTransformer, bool fromV2, bool toV2)
    {
        var updateDecoder = CreateDecoder(update, fromV2);
        var lazyDecoder   = new LazyStructReader(updateDecoder, false);
        var updateEncoder = CreateEncoder(toV2);
        var lazyWriter    = new LazyStructWriter(updateEncoder);
        for (var curr = lazyDecoder.Curr; curr != null; curr = lazyDecoder.Next())
        {
            WriteStructToLazyStructWriter(lazyWriter, blockTransformer(curr), 0);
        }
        FinishLazyStructWriting(lazyWriter);
        var ds = DeleteSet.Read(updateDecoder);
        ds.Write(updateEncoder);
        return updateEncoder.ToArray();
    }

    internal static byte[] ConvertUpdateFormatV1ToV2(byte[] update) => ConvertUpdateFormat(update, s => s, false, true);

    internal static byte[] ConvertUpdateFormatV2ToV1(byte[] update) => ConvertUpdateFormat(update, s => s, true, false);
}
