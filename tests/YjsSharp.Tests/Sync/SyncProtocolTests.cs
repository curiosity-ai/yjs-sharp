using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YjsSharp.Encoding;
using YjsSharp.Sync;

namespace YjsSharp.Tests.Sync;

/// <summary>Ports of yrs' <c>sync/protocol.rs</c> tests plus y-protocols / y-websocket handshake checks.</summary>
[TestClass]
public sealed class SyncProtocolTests
{
    private static Doc NewDoc(ulong clientId) => new Doc(new DocOptions { ClientId = clientId });

    private static AwarenessOptions NoTimer() => new AwarenessOptions { CheckInterval = TimeSpan.Zero };

    [TestMethod]
    public void MessageEncoding_RoundTrips()
    {
        var doc = new Doc();
        doc.GetText("text").Insert(0, "hello world");
        using var awareness = new Awareness(doc, NoTimer());
        awareness.SetLocalState("{\"user\":{\"name\":\"Anonymous 50\",\"color\":\"#30bced\",\"colorLight\":\"#30bced33\"}}");

        var messages = new ProtocolMessage[]
        {
            new SyncStep1Message(doc.EncodeStateVector()),
            new SyncStep2Message(doc.EncodeStateAsUpdate()),
            new SyncUpdateMessage(doc.EncodeStateAsUpdate()),
            new AwarenessMessage(AwarenessProtocol.EncodeAwarenessUpdate(awareness)),
            new AuthMessage("reason\n            }"),
            new AuthMessage(null),
            new QueryAwarenessMessage(),
            new CustomMessage(42, new byte[] { 1, 2, 3 })
        };
        foreach (var message in messages)
        {
            var encoded = message.ToArray();
            var decoded = ProtocolMessage.Decode(encoded);
            Assert.AreEqual(message, decoded, message.GetType().Name);
            CollectionAssert.AreEqual(encoded, decoded.ToArray());
        }
        var all = messages.SelectMany(m => m.ToArray()).ToArray();
        CollectionAssert.AreEqual(messages, ProtocolMessage.ReadAll(all).ToArray());
    }

    [TestMethod]
    public void ProtocolInit_SendsStep1AndAwareness()
    {
        using var awareness = new Awareness(new Doc(), NoTimer());
        var data   = SyncProtocolHandler.Default.Start(awareness);
        var reader = ProtocolMessage.ReadAll(data).ToList();
        Assert.AreEqual(2, reader.Count);
        Assert.AreEqual(new SyncStep1Message(new byte[] { 0 }), reader[0]);
        Assert.AreEqual(new AwarenessMessage(AwarenessProtocol.EncodeAwarenessUpdate(awareness)), reader[1]);
    }

    [TestMethod]
    public void ProtocolSyncSteps()
    {
        var protocol = SyncProtocolHandler.Default;
        using var a1 = new Awareness(NewDoc(1), NoTimer());
        using var a2 = new Awareness(NewDoc(2), NoTimer());
        a1.Doc.GetText("test").Insert(0, "hello");
        var expected = a1.Doc.EncodeStateAsUpdate(new byte[] { 0 });

        var result = protocol.HandleSyncStep1(a1, a2.Doc.EncodeStateVector());
        Assert.AreEqual(new SyncStep2Message(expected), result);

        var result2 = protocol.HandleSyncStep2(a2, ((SyncStep2Message)result!).Update, null);
        Assert.IsNull(result2);
        Assert.AreEqual("hello", a2.Doc.GetText("test").ToString());
    }

    [TestMethod]
    public void ProtocolSyncStepUpdate()
    {
        var protocol = SyncProtocolHandler.Default;
        using var a1 = new Awareness(NewDoc(1), NoTimer());
        using var a2 = new Awareness(NewDoc(2), NoTimer());
        byte[]? data = null;
        a1.Doc.Update += (_, e) => data = e.Update;
        a1.Doc.GetText("test").Insert(0, "hello");

        var result = protocol.HandleUpdate(a2, data!, null);
        Assert.IsNull(result);
        Assert.AreEqual("hello", a2.Doc.GetText("test").ToString());
    }

    [TestMethod]
    public void ProtocolAwarenessSync()
    {
        var protocol = SyncProtocolHandler.Default;
        using var a1 = new Awareness(NewDoc(1), NoTimer());
        using var a2 = new Awareness(NewDoc(2), NoTimer());
        a1.SetLocalState("{\"x\":3}");
        var result = protocol.HandleAwarenessQuery(a1);
        Assert.AreEqual(new AwarenessMessage(AwarenessProtocol.EncodeAwarenessUpdate(a1)), result);

        var result2 = protocol.HandleAwarenessUpdate(a2, ((AwarenessMessage)result!).Update, null);
        Assert.IsNull(result2);
        Assert.AreEqual("{\"x\":3}", a2.GetState(1));
    }

    [TestMethod]
    public void ProtocolHandle_AnswersEveryMessageInAPayload()
    {
        var protocol = SyncProtocolHandler.Default;
        using var server = new Awareness(NewDoc(1), NoTimer());
        using var client = new Awareness(NewDoc(2), NoTimer());
        server.Doc.GetText("t").Insert(0, "server");
        client.Doc.GetText("t").Insert(0, "client");

        // client -> server: step 1 + awareness; server answers the step 1
        var replies = protocol.Handle(server, protocol.Start(client), "client");
        Assert.AreEqual(1, replies.Count);
        Assert.IsInstanceOfType(replies[0], typeof(SyncStep2Message));
        Assert.AreEqual("{}", server.GetState(2));

        // client applies the answer and the server's own start
        Assert.AreEqual(0, protocol.Handle(client, replies[0].ToArray(), "server").Count);
        var back = protocol.Handle(client, protocol.Start(server), "server");
        Assert.AreEqual(1, back.Count);
        protocol.Handle(server, back[0].ToArray(), "client");

        Assert.AreEqual(server.Doc.GetText("t").ToString(), client.Doc.GetText("t").ToString());
        CollectionAssert.AreEqual(server.Doc.EncodeStateVector(), client.Doc.EncodeStateVector());
    }

    [TestMethod]
    public void ProtocolHandle_PermissionDeniedThrows()
    {
        using var aw = new Awareness(new Doc(), NoTimer());
        var ex = Assert.ThrowsException<PermissionDeniedException>(() => SyncProtocolHandler.Default.Handle(aw, new AuthMessage("nope").ToArray()));
        Assert.AreEqual("nope", ex.Reason);
        Assert.AreEqual(0, SyncProtocolHandler.Default.Handle(aw, new AuthMessage(null).ToArray()).Count);
        Assert.ThrowsException<NotSupportedException>(() => SyncProtocolHandler.Default.Handle(aw, new CustomMessage(9, new byte[0]).ToArray()));
    }

    [TestMethod]
    public void ReadSyncMessage_StepsAndOrigin()
    {
        var a = NewDoc(1);
        var b = NewDoc(2);
        a.GetText("t").Insert(0, "abc");
        var origins = new List<object?>();
        b.Update += (_, e) => origins.Add(e.Origin);

        var step1 = new Encoder();
        SyncProtocol.WriteSyncStep1(step1, b);
        var reply = new Encoder();
        Assert.AreEqual(SyncProtocol.MessageYjsSyncStep1, SyncProtocol.ReadSyncMessage(new Decoder(step1.ToArray()), reply, a, null));
        Assert.IsTrue(reply.Length > 0);

        var none = new Encoder();
        Assert.AreEqual(SyncProtocol.MessageYjsSyncStep2, SyncProtocol.ReadSyncMessage(new Decoder(reply.ToArray()), none, b, "origin-a"));
        Assert.AreEqual(0, none.Length);
        Assert.AreEqual("abc", b.GetText("t").ToString());
        CollectionAssert.AreEqual(new object?[] { "origin-a" }, origins);

        byte[]? update = null;
        a.Update += (_, e) => update = e.Update;
        a.GetText("t").Insert(3, "d");
        var upd = new Encoder();
        SyncProtocol.WriteUpdate(upd, update!);
        Assert.AreEqual(SyncProtocol.MessageYjsUpdate, SyncProtocol.ReadSyncMessage(new Decoder(upd.ToArray()), none, b, "origin-a"));
        Assert.AreEqual("abcd", b.GetText("t").ToString());

        var bad = new Encoder();
        bad.WriteVarUInt(7);
        Assert.ThrowsException<InvalidOperationException>(() => SyncProtocol.ReadSyncMessage(new Decoder(bad.ToArray()), none, b, null));
    }

    [TestMethod]
    public void WriteSyncStep2_NullStateVectorIsWholeDocument()
    {
        var a = NewDoc(1);
        a.GetMap("m").Set("k", "v");
        var e1 = new Encoder();
        SyncProtocol.WriteSyncStep2(e1, a, null);
        var d = new Decoder(e1.ToArray());
        Assert.AreEqual((ulong)SyncProtocol.MessageYjsSyncStep2, d.ReadVarUInt());
        CollectionAssert.AreEqual(a.EncodeStateAsUpdate(), d.ReadVarUInt8Array());
    }

    [TestMethod]
    public void WebsocketHandshake_ConvergesBothWays()
    {
        using var server = new Awareness(NewDoc(10), NoTimer());
        using var client = new Awareness(NewDoc(20), NoTimer());
        server.Doc.GetArray("a").Push(new object?[] { "s" });
        client.Doc.GetArray("a").Push(new object?[] { "c" });
        client.SetLocalState("{\"user\":\"c\"}");

        var toServer = new Queue<byte[]>();
        var toClient = new Queue<byte[]>();
        server.Doc.Update += (_, e) =>
        {
            if (!Equals(e.Origin, "client"))
            {
                toClient.Enqueue(Messages.EncodeUpdate(e.Update));
            }
        };
        client.Update += (_, e) => toServer.Enqueue(Messages.EncodeAwarenessUpdate(client, e.All));

        toServer.Enqueue(Messages.EncodeSyncStep1(client.Doc));
        toClient.Enqueue(Messages.EncodeSyncStep1(server.Doc));
        toClient.Enqueue(Messages.EncodeAwarenessUpdate(server, server.States.Keys));
        toServer.Enqueue(Messages.EncodeAwarenessUpdate(client, client.States.Keys));
        toServer.Enqueue(Messages.EncodeQueryAwareness());

        for (var round = 0; round < 10 && (toServer.Count > 0 || toClient.Count > 0); round++)
        {
            while (toServer.Count > 0)
            {
                var reply = Messages.Handle(toServer.Dequeue(), server, "client");
                if (reply != null)
                {
                    toClient.Enqueue(reply);
                }
            }
            while (toClient.Count > 0)
            {
                var reply = Messages.Handle(toClient.Dequeue(), client, "server");
                if (reply != null)
                {
                    toServer.Enqueue(reply);
                }
            }
        }
        CollectionAssert.AreEqual(server.Doc.EncodeStateVector(), client.Doc.EncodeStateVector());
        Assert.AreEqual(2, client.Doc.GetArray("a").Length);
        Assert.AreEqual("{\"user\":\"c\"}", server.GetState(20));
        Assert.AreEqual("{}", client.GetState(10));
    }

    [TestMethod]
    public void Messages_FramingConstants()
    {
        Assert.AreEqual(0, Messages.MessageSync);
        Assert.AreEqual(1, Messages.MessageAwareness);
        Assert.AreEqual(2, Messages.MessageAuth);
        Assert.AreEqual(3, Messages.MessageQueryAwareness);
        CollectionAssert.AreEqual(new byte[] { 3 }, Messages.EncodeQueryAwareness());
        CollectionAssert.AreEqual(new byte[] { 2, 0, 2, (byte)'n', (byte)'o' }, Messages.EncodePermissionDenied("no"));
        Assert.AreEqual(Messages.MessageAuth, Messages.ReadMessageType(Messages.EncodePermissionDenied("x")));

        var doc    = new Doc();
        string? denied = null;
        Assert.IsNull(Messages.Handle(Messages.EncodePermissionDenied("go away"), doc, null, r => denied = r));
        Assert.AreEqual("go away", denied);
        Assert.ThrowsException<PermissionDeniedException>(() => Messages.Handle(Messages.EncodePermissionDenied("go away"), doc, null));
        Assert.ThrowsException<FormatException>(() => Messages.Handle(new byte[] { 9 }, doc, null));
        Assert.ThrowsException<FormatException>(() => Messages.Handle(Messages.EncodeQueryAwareness(), doc, null));
    }
}
