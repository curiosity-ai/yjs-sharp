using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YjsSharp.Encoding;
using YjsSharp.Sync;

namespace YjsSharp.Tests.Compat;

/// <summary>
/// Checks the sync and awareness protocols against byte streams recorded with y-protocols 1.0.7 and the
/// y-websocket framing (tests/fixtures-generator/generate.mjs).
/// </summary>
[TestClass]
public sealed class ProtocolCompatTests
{
    public static IEnumerable<object[]> SyncScenarios => Fixtures.Scenarios("sync").Select(s => new object[] { (string)s["name"]! });

    private static void AssertBytes(JsonNode? expected, byte[]? actual, string what)
    {
        if (expected == null)
        {
            Assert.IsNull(actual, what + " should be empty");
            return;
        }
        Assert.IsNotNull(actual, what);
        var e = Fixtures.Bytes(expected);
        if (!e.AsSpan().SequenceEqual(actual))
        {
            Assert.Fail($"{what} differs from y-protocols\n  js: {Convert.ToBase64String(e)}\n  cs: {Convert.ToBase64String(actual)}");
        }
    }

    private static AwarenessOptions NoTimer() => new AwarenessOptions { CheckInterval = TimeSpan.Zero };

    [TestMethod]
    [DynamicData(nameof(SyncScenarios))]
    public void SyncHandshake_IsByteIdentical(string name)
    {
        var scenario = Fixtures.Scenarios("sync").Single(s => (string)s["name"]! == name);
        var runner   = ScenarioRunner.Run(scenario);
        var docs     = runner.Docs;
        var exchange = scenario["exchange"]!.AsArray();

        // the client's opening step 1, then every recorded frame is handled by our side of the connection
        AssertBytes(exchange[0]!["message"], Messages.EncodeSyncStep1(docs[0]), "client step 1");
        AssertBytes(exchange[2]!["message"], Messages.EncodeSyncStep1(docs[1]), "server step 1");
        var handshake = exchange.Take(4).ToList();
        foreach (var frame in handshake)
        {
            var to      = (int)frame!["to"]!;
            var message = Fixtures.Bytes(frame["message"]);
            Assert.AreEqual((int)frame["syncType"]!, (int)new Decoder(message, 1, message.Length - 1).ReadVarUInt());
            var reply = Messages.Handle(message, docs[to], ScenarioRunner.Remote);
            AssertBytes(frame["reply"], reply, $"reply of peer {to} to {frame["syncType"]}");
        }

        // the client keeps editing: every update is framed like y-websocket does
        var before = runner.Updates[0].Count;
        foreach (var op in scenario["after"]!.AsArray())
        {
            runner.Apply((JsonObject)op!);
        }
        var updates = runner.Updates[0].Skip(before).ToList();
        var rest    = exchange.Skip(4).ToList();
        Assert.AreEqual(rest.Count, updates.Count, "update frames");
        for (var i = 0; i < rest.Count; i++)
        {
            AssertBytes(rest[i]!["message"], Messages.EncodeUpdate(updates[i].V1), $"update frame {i}");
            Assert.IsNull(Messages.Handle(Fixtures.Bytes(rest[i]!["message"]), docs[1], ScenarioRunner.Remote));
        }

        var roots = (JsonObject)scenario["roots"]!;
        for (var i = 0; i < 2; i++)
        {
            Assert.AreEqual(ScenarioRunner.Canonical(scenario["content"]![i]), ScenarioRunner.ContentOf(docs[i], roots).Content, $"peer {i} content");
            AssertBytes(scenario["stateVectors"]![i], docs[i].EncodeStateVector(), $"peer {i} state vector");
        }
        AssertBytes(scenario["explicitStep2"], Messages.EncodeSyncStep2(docs[0], new Doc().EncodeStateVector()), "step 2 against an empty state vector");
        AssertBytes(scenario["fullStep2"], Messages.EncodeSyncStep2(docs[0]), "step 2 without a state vector");
    }

    [TestMethod]
    [DynamicData(nameof(SyncScenarios))]
    public void SyncHandshake_JsFramesSyncFreshDocs(string name)
    {
        // A C# peer that only ever sees the JS side's frames ends up with the JS content.
        var scenario = Fixtures.Scenarios("sync").Single(s => (string)s["name"]! == name);
        var roots    = (JsonObject)scenario["roots"]!;
        var doc      = new Doc(new DocOptions { ClientId = 4242, GC = (bool?)scenario["gc"] ?? true });
        foreach (var frame in scenario["exchange"]!.AsArray().Where(f => (int)f!["to"]! == 1))
        {
            Messages.Handle(Fixtures.Bytes(frame!["message"]), doc, null);
        }
        Assert.IsNull(Messages.Handle(Fixtures.Bytes(scenario["fullStep2"]), doc, null));
        Assert.AreEqual(ScenarioRunner.Canonical(scenario["content"]![0]), ScenarioRunner.ContentOf(doc, roots).Content);
    }

    [TestMethod]
    public void Framing_IsByteIdentical()
    {
        var fixture = Fixtures.Scenarios("messages").Single();
        AssertBytes(fixture["queryAwareness"], Messages.EncodeQueryAwareness(), "query awareness");
        AssertBytes(fixture["permissionDenied"], Messages.EncodePermissionDenied((string)fixture["permissionDeniedReason"]!), "permission denied");
        var decoded = ProtocolMessage.Decode(Fixtures.Bytes(fixture["permissionDenied"]));
        Assert.AreEqual(new AuthMessage((string)fixture["permissionDeniedReason"]!), decoded);

        var clientId = (ulong)fixture["awarenessClientId"]!;
        using var aw = new Awareness(new Doc(new DocOptions { ClientId = clientId }), NoTimer());
        aw.SetLocalState((string)fixture["awarenessState"]!);
        AssertBytes(fixture["awareness"], Messages.EncodeAwarenessUpdate(aw, new[] { clientId }), "awareness frame");

        using var remote = new Awareness(new Doc(new DocOptions { ClientId = 1 }), NoTimer());
        Assert.IsNull(Messages.Handle(Fixtures.Bytes(fixture["awareness"]), remote, "ws"));
        Assert.AreEqual((string)fixture["awarenessState"]!, remote.GetState(clientId));
        var reply = Messages.Handle(Fixtures.Bytes(fixture["queryAwareness"]), remote, "ws");
        Assert.AreEqual(Messages.MessageAwareness, Messages.ReadMessageType(reply!));
    }

    [TestMethod]
    [DataRow("awareness-local-states")]
    [DataRow("awareness-big-client")]
    public void AwarenessLocalStates_AreByteIdentical(string name)
    {
        var scenario = Fixtures.Scenarios("awareness").Single(s => (string)s["name"]! == name);
        var clientId = (ulong)scenario["clientId"]!;
        using var aw     = new Awareness(new Doc(new DocOptions { ClientId = clientId }), NoTimer());
        using var remote = new Awareness(new Doc(new DocOptions { ClientId = clientId + 1000 }), NoTimer());
        AwarenessChangeEventArgs? update       = null;
        AwarenessChangeEventArgs? change       = null;
        AwarenessChangeEventArgs? remoteChange = null;
        aw.Update     += (_, e) => update = e;
        aw.Change     += (_, e) => change = e;
        remote.Change += (_, e) => remoteChange = e;

        var stepIndex = 0;
        foreach (var step in scenario["steps"]!.AsArray())
        {
            var label  = $"step {stepIndex++}";
            var action = step!["action"]!;
            update       = null;
            change       = null;
            remoteChange = null;
            switch ((string)action["kind"]!)
            {
                case "set": aw.SetLocalState((string?)action["state"]); break;
                case "field": aw.SetLocalStateField((string)action["field"]!, JsJson.Parse((string)action["value"]!)); break;
            }
            AssertEvent(step["update"], update, label + " update event");
            AssertEvent(step["change"], change, label + " change event");
            var encoded = AwarenessProtocol.EncodeAwarenessUpdate(aw, update?.All ?? Array.Empty<ulong>());
            AssertBytes(step["encoded"], encoded, label + " encoded update");
            Assert.AreEqual((string?)step["localState"], aw.GetLocalState(), label + " local state");
            Assert.AreEqual((ulong)step["localClock"]!, aw.Meta[clientId].Clock, label + " local clock");

            AwarenessProtocol.ApplyAwarenessUpdate(remote, Fixtures.Bytes(step["encoded"]), "remote");
            AssertEvent(step["remoteChange"], remoteChange, label + " remote change event");
            AssertStates(step["remoteStates"]!.AsObject(), remote.States, label + " remote states");
            AssertClocks(step["remoteClocks"]!.AsObject(), remote.Meta, label + " remote clocks");
        }
        AssertBytes(scenario["all"], AwarenessProtocol.EncodeAwarenessUpdate(aw, aw.Meta.Keys), "all clients");
    }

    [TestMethod]
    public void AwarenessApply_MatchesYProtocols()
    {
        var scenario = Fixtures.Scenarios("awareness-apply").Single();
        var localId  = (ulong)scenario["localClientId"]!;
        using var aw = new Awareness(new Doc(new DocOptions { ClientId = localId }), NoTimer());
        AwarenessChangeEventArgs? update = null;
        AwarenessChangeEventArgs? change = null;
        aw.Update += (_, e) => update = e;
        aw.Change += (_, e) => change = e;
        foreach (var step in scenario["steps"]!.AsArray())
        {
            var label = (string)step!["label"]!;
            update = null;
            change = null;
            AwarenessProtocol.ApplyAwarenessUpdate(aw, Fixtures.Bytes(step["update"]), "remote-origin");
            AssertEvent(step["updateEvent"], update, label + ": update event");
            AssertEvent(step["changeEvent"], change, label + ": change event");
            if (update != null)
            {
                Assert.AreEqual("remote-origin", update.Origin);
            }
            AssertStates(step["states"]!.AsObject(), aw.States, label + ": states");
            AssertClocks(step["clocks"]!.AsObject(), aw.Meta, label + ": clocks");
            AssertBytes(step["reencoded"], AwarenessProtocol.EncodeAwarenessUpdate(aw, aw.Meta.Keys), label + ": re-encoded");
        }

        var remove = scenario["removeAwarenessStates"]!;
        update = null;
        AwarenessProtocol.RemoveAwarenessStates(aw, remove["clients"]!.AsArray().Select(n => (ulong)n!), "timeout");
        AssertEvent(remove["updateEvent"], update, "removeAwarenessStates event");
        AssertStates(remove["states"]!.AsObject(), aw.States, "states after removeAwarenessStates");
        AssertClocks(remove["clocks"]!.AsObject(), aw.Meta, "clocks after removeAwarenessStates");
        AssertBytes(remove["encoded"], AwarenessProtocol.EncodeAwarenessUpdate(aw, new ulong[] { 3, localId }), "encoded after removeAwarenessStates");
    }

    [TestMethod]
    public void ModifyAwarenessUpdate_IsByteIdentical()
    {
        var fixture = Fixtures.Scenarios("awareness-modify").Single();
        var source  = Fixtures.Bytes(fixture["source"]);
        var renamed = AwarenessProtocol.ModifyAwarenessUpdate(source, json =>
        {
            if (json == "null")
            {
                return json;
            }
            var state = (IDictionary<string, object?>)JsJson.Parse(json)!;
            var user  = (IDictionary<string, object?>)state["user"]!;
            user["name"] = "Server";
            return JsJson.Stringify(state)!;
        });
        AssertBytes(fixture["renamed"], renamed, "renamed");
        AssertBytes(fixture["identity"], AwarenessProtocol.ModifyAwarenessUpdate(source, s => s), "identity");

        foreach (var pair in fixture["json"]!.AsArray())
        {
            var input  = (string)pair!["input"]!;
            var update = AwarenessProtocol.ModifyAwarenessUpdate(AwarenessTestsWriter.Write(1, 1, input), s => s);
            Assert.AreEqual((string)pair["stringified"]!, AwarenessProtocol.DecodeAwarenessUpdate(update)[0].State, "JSON.stringify(JSON.parse(" + input + "))");
        }
    }

    private static void AssertEvent(JsonNode? expected, AwarenessChangeEventArgs? actual, string what)
    {
        if (expected == null)
        {
            Assert.IsNull(actual, what + " should not be raised");
            return;
        }
        Assert.IsNotNull(actual, what + " should be raised");
        CollectionAssert.AreEqual(expected["added"]!.AsArray().Select(n => (ulong)n!).ToArray(), actual.Added.ToArray(), what + " added");
        CollectionAssert.AreEqual(expected["updated"]!.AsArray().Select(n => (ulong)n!).ToArray(), actual.Updated.ToArray(), what + " updated");
        CollectionAssert.AreEqual(expected["removed"]!.AsArray().Select(n => (ulong)n!).ToArray(), actual.Removed.ToArray(), what + " removed");
    }

    private static void AssertStates(JsonObject expected, IReadOnlyDictionary<ulong, string> actual, string what)
    {
        Assert.AreEqual(expected.Count, actual.Count, what + " count");
        foreach (var kv in expected)
        {
            Assert.IsTrue(actual.TryGetValue(ulong.Parse(kv.Key), out var state), what + ": missing " + kv.Key);
            Assert.AreEqual((string)kv.Value!, state, what + ": " + kv.Key);
        }
    }

    private static void AssertClocks(JsonObject expected, IReadOnlyDictionary<ulong, AwarenessMeta> actual, string what)
    {
        Assert.AreEqual(expected.Count, actual.Count, what + " count");
        foreach (var kv in expected)
        {
            Assert.AreEqual((ulong)kv.Value!, actual[ulong.Parse(kv.Key)].Clock, what + ": " + kv.Key);
        }
    }
}

internal static class AwarenessTestsWriter
{
    public static byte[] Write(ulong client, ulong clock, string json) => YjsSharp.Tests.Sync.AwarenessTests.Write((client, clock, json));
}
