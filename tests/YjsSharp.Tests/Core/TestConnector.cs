using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YjsSharp;
using YjsSharp.Encoding;
using YjsSharp.Structs;

namespace YjsSharp.Tests.Core;

internal static class RngExt
{
    /// <summary>yrs RngExt::between: a random number in [min(x, y), max(x, y)).</summary>
    public static int Between(this Random rng, int x, int y)
    {
        var a = Math.Min(x, y);
        var b = Math.Max(x, y);
        return a == b ? a : rng.Next(a, b);
    }

    public static string RandomString(this Random rng)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var len = rng.Next(1, 10);
        var c   = new char[len];
        for (var i = 0; i < len; i++)
        {
            c[i] = alphabet[rng.Next(alphabet.Length)];
        }
        return new string(c);
    }

    public static bool NextBool(this Random rng) => rng.Next(2) == 1;

    public static T Choice<T>(this Random rng, IReadOnlyList<T> items) => items[rng.Next(items.Count)];
}

/// <summary>A simulated network of peers (port of yrs test_utils.rs TestConnector / Yjs testHelper.js).</summary>
internal sealed class TestConnector
{
    private const int MsgSyncStep1 = 0;
    private const int MsgSyncStep2 = 1;
    private const int MsgSyncUpdate = 2;

    private readonly Random                       _rng;
    private readonly List<TestPeer>               _peers  = new List<TestPeer>();
    private readonly SortedDictionary<ulong, int> _all    = new SortedDictionary<ulong, int>();
    private readonly SortedDictionary<ulong, int> _online = new SortedDictionary<ulong, int>();

    public TestConnector(Random rng)
    {
        _rng = rng;
    }

    public IReadOnlyList<TestPeer> Peers => _peers;

    public static TestConnector WithPeerNum(Random rng, int peerNum)
    {
        var tc = new TestConnector(rng);
        for (ulong clientId = 0; clientId < (ulong)peerNum; clientId++)
        {
            var peer = tc.CreatePeer(clientId);
            peer.Doc.GetText("text");
            peer.Doc.GetMap("map");
        }
        tc.SyncAll();
        return tc;
    }

    public static void RunScenario(int seed, IReadOnlyList<Action<Doc, Random>> mods, int users, int iterations)
    {
        var rng = new Random(seed);
        var tc  = WithPeerNum(rng, users);
        for (var i = 0; i < iterations; i++)
        {
            if (rng.Next(100) <= 2)
            {
                if (rng.NextBool())
                {
                    tc.DisconnectRandom();
                }
                else
                {
                    tc.ReconnectRandom();
                }
            }
            else if (rng.Next(100) <= 1)
            {
                tc.FlushAll();
            }
            else if (rng.Next(100) <= 50)
            {
                tc.FlushRandom();
            }
            var peer = tc._peers[rng.Next(tc._peers.Count)];
            var test = rng.Choice(mods);
            test(peer.Doc, rng);
            tc.BroadcastPending();
        }
        tc.AssertFinalState();
    }

    public TestPeer CreatePeer(ulong clientId)
    {
        if (_all.TryGetValue(clientId, out var existing))
        {
            return _peers[existing];
        }
        var instance = new TestPeer(clientId);
        instance.Doc.Update += (_, e) =>
        {
            if (!Equals(e.Origin, T.ExchangeUpdatesOrigin))
            {
                instance.Outbox.Enqueue(e.Update);
            }
        };
        var idx = _peers.Count;
        _peers.Add(instance);
        _all[clientId]    = idx;
        _online[clientId] = idx;
        return instance;
    }

    public void BroadcastPending()
    {
        foreach (var peer in _peers)
        {
            var clientId = peer.ClientId;
            var pending  = peer.Outbox.ToList();
            peer.Outbox.Clear();
            if (!_online.ContainsKey(clientId))
            {
                continue;
            }
            foreach (var update in pending)
            {
                var encoder = new Encoder();
                encoder.WriteVarUInt(MsgSyncUpdate);
                encoder.WriteVarUInt8Array(update);
                Broadcast(clientId, encoder.ToArray());
            }
        }
    }

    private void Broadcast(ulong sender, byte[] payload)
    {
        foreach (var kv in _online)
        {
            if (kv.Key != sender)
            {
                _peers[kv.Value].Receive(sender, payload);
            }
        }
    }

    public TestPeer? Get(ulong clientId) => _all.TryGetValue(clientId, out var idx) ? _peers[idx] : null;

    public void Disconnect(ulong clientId)
    {
        Get(clientId)?.Receiving.Clear();
        _online.Remove(clientId);
    }

    public void Connect(ulong clientId)
    {
        if (!_online.ContainsKey(clientId))
        {
            _online[clientId] = _all[clientId];
        }
        var clientIdx = _all[clientId];
        Broadcast(clientId, WriteStep1(_peers[clientIdx]));
        foreach (var kv in _online.ToList())
        {
            if (kv.Key != clientId)
            {
                _peers[clientIdx].Receive(kv.Key, WriteStep1(_peers[kv.Value]));
            }
        }
    }

    public void ReconnectAll()
    {
        foreach (var id in _all.Keys.ToList())
        {
            Connect(id);
        }
    }

    public void DisconnectAll()
    {
        foreach (var id in _all.Keys.ToList())
        {
            Disconnect(id);
        }
    }

    public void SyncAll()
    {
        ReconnectAll();
        FlushAll();
    }

    public bool FlushAll()
    {
        var didSomething = false;
        while (FlushRandom())
        {
            didSomething = true;
        }
        return didSomething;
    }

    public bool FlushRandom()
    {
        while (true)
        {
            var pairs = new List<(int Receiver, int Sender)>();
            for (var r = 0; r < _peers.Count; r++)
            {
                foreach (var key in _peers[r].Receiving.Keys)
                {
                    pairs.Add((r, _all[key]));
                }
            }
            if (pairs.Count == 0)
            {
                return false;
            }
            var (receiverIdx, senderIdx) = _rng.Choice(pairs);
            var receiver = _peers[receiverIdx];
            var sender   = _peers[senderIdx];
            if (receiver.Receiving.TryGetValue(sender.ClientId, out var queue) && queue.Count > 0)
            {
                var m       = queue.Dequeue();
                var encoder = new Encoder();
                ReadSyncMessage(receiver, new Decoder(m), encoder);
                if (encoder.Length > 0)
                {
                    sender.Receive(receiver.ClientId, encoder.ToArray());
                }
                var decoder = new Decoder(m);
                var msgType = (int)decoder.ReadVarUInt();
                if (msgType == MsgSyncStep2 || msgType == MsgSyncUpdate)
                {
                    receiver.Updates.Add(decoder.ReadVarUInt8Array());
                }
                return true;
            }
            receiver.Receiving.Remove(sender.ClientId);
        }
    }

    public bool DisconnectRandom()
    {
        var keys = _online.Keys.ToList();
        if (keys.Count == 0)
        {
            return false;
        }
        Disconnect(_rng.Choice(keys));
        return true;
    }

    public bool ReconnectRandom()
    {
        var reconnectable = _all.Keys.Where(id => !_online.ContainsKey(id)).ToList();
        if (reconnectable.Count == 0)
        {
            return false;
        }
        Connect(_rng.Choice(reconnectable));
        return true;
    }

    private static void ReadSyncMessage(TestPeer peer, Decoder decoder, Encoder encoder)
    {
        var msgType = (int)decoder.ReadVarUInt();
        switch (msgType)
        {
            case MsgSyncStep1:
                {
                    var sv = decoder.ReadVarUInt8Array();
                    encoder.WriteVarUInt(MsgSyncStep2);
                    encoder.WriteVarUInt8Array(peer.Doc.EncodeStateAsUpdate(sv));
                    break;
                }
            case MsgSyncStep2:
            case MsgSyncUpdate:
                peer.Doc.ApplyUpdate(decoder.ReadVarUInt8Array(), T.ExchangeUpdatesOrigin);
                break;
            default:
                throw new InvalidOperationException($"Unknown message type: {msgType}");
        }
    }

    private static byte[] WriteStep1(TestPeer peer)
    {
        var encoder = new Encoder();
        encoder.WriteVarUInt(MsgSyncStep1);
        encoder.WriteVarUInt8Array(peer.Doc.EncodeStateVector());
        return encoder.ToArray();
    }

    public void AssertFinalState()
    {
        ReconnectAll();
        while (FlushAll())
        {
        }
        for (var i = 0; i < _peers.Count - 1; i++)
        {
            AssertDocsEqual(_peers[i].Doc, _peers[i + 1].Doc);
        }
        // merging the received updates must produce the same document
        foreach (var peer in _peers)
        {
            if (peer.Updates.Count == 0)
            {
                continue;
            }
            var merged = new Doc();
            merged.ApplyUpdate(Y.MergeUpdates(peer.Updates));
            var mergedV2 = new Doc();
            mergedV2.ApplyUpdateV2(Y.MergeUpdatesV2(peer.Updates.Select(Y.ConvertUpdateFormatV1ToV2)));
            T.JsonEqual(merged.ToJson(), mergedV2.ToJson());
        }
    }

    public static void AssertDocsEqual(Doc a, Doc b)
    {
        var astore     = a.Store;
        var bstore     = b.Store;
        var allClients = new SortedSet<ulong>(astore.Clients.Keys.Concat(bstore.Clients.Keys));
        foreach (var client in allClients)
        {
            Assert.IsTrue(astore.Clients.ContainsKey(client) && bstore.Clients.ContainsKey(client), $"client {client} missing in one of the stores");
            var al = BlockRuns(astore.Clients[client]);
            var bl = BlockRuns(bstore.Clients[client]);
            CollectionAssert.AreEqual(al, bl, $"block store mismatch for client {client}");
        }
        Assert.AreEqual(astore.PendingStructs == null, bstore.PendingStructs == null);
        Assert.AreEqual(astore.PendingDs == null, bstore.PendingDs == null);
        foreach (var kv in a.RootTypes.ToList())
        {
            AbstractType? other = kv.Value switch
            {
                YArray      => b.GetArray(kv.Key),
                XmlHook     => null,
                YMap        => b.GetMap(kv.Key),
                XmlText     => b.GetXmlText(kv.Key),
                Text        => b.GetText(kv.Key),
                XmlElement  => b.GetXmlElement(kv.Key),
                XmlFragment => b.GetXmlFragment(kv.Key),
                _           => null,
            };
            if (other == null)
            {
                continue;
            }
            T.JsonEqual(Normalize(kv.Value), Normalize(other));
        }
        CollectionAssert.AreEqual(a.EncodeStateVector(), b.EncodeStateVector());
        var ds1 = DeleteSet.FromStructStore(astore);
        var ds2 = DeleteSet.FromStructStore(bstore);
        Assert.IsTrue(DeleteSet.Equal(ds1, ds2), $"delete sets differ: {ds1} vs {ds2}");
    }

    private static object? Normalize(AbstractType t)
    {
        if (t is TextBase text)
        {
            return text.ToDelta().Select(d => (object?)d.ToString()).ToList();
        }
        return t.ToJson();
    }

    private static List<string> BlockRuns(List<AbstractStruct> blocks)
    {
        var runs = new List<(uint Start, uint End, string Kind)>();
        foreach (var block in blocks)
        {
            var kind = block switch
            {
                Item item => item.Deleted ? "deleted" : "item",
                YjsSharp.Structs.GC => "gc",
                _         => "skip",
            };
            var start = block.Id.Clock;
            var end   = start + block.Length;
            if (runs.Count > 0 && runs[^1].End == start && runs[^1].Kind == kind)
            {
                runs[^1] = (runs[^1].Start, end, kind);
            }
            else
            {
                runs.Add((start, end, kind));
            }
        }
        return runs.Select(r => $"{r.Start}-{r.End}:{r.Kind}").ToList();
    }
}

internal sealed class TestPeer
{
    public TestPeer(ulong clientId)
    {
        Doc = new Doc(new DocOptions { ClientId = clientId });
    }

    public Doc Doc { get; }

    public ulong ClientId => Doc.ClientId;

    public SortedDictionary<ulong, Queue<byte[]>> Receiving { get; } = new SortedDictionary<ulong, Queue<byte[]>>();

    public List<byte[]> Updates { get; } = new List<byte[]>();

    public Queue<byte[]> Outbox { get; } = new Queue<byte[]>();

    public void Receive(ulong from, byte[] message)
    {
        if (!Receiving.TryGetValue(from, out var q))
        {
            q               = new Queue<byte[]>();
            Receiving[from] = q;
        }
        q.Enqueue(message);
    }
}
