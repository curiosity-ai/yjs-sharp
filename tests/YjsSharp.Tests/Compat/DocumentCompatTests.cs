using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace YjsSharp.Tests.Compat;

/// <summary>
/// Replays the document scenarios recorded with real Yjs (tests/fixtures-generator/scenarios.mjs):
/// (a) the updates Yjs produced must apply here and give the same content and state vectors, and
/// (b) running the same operations with the same client ids must produce byte-identical encodings.
/// </summary>
[TestClass]
public sealed class DocumentCompatTests
{
    public static IEnumerable<object[]> Scenarios => Fixtures.DocumentScenarioKeys();

    private static Doc FreshDoc(JsonObject scenario) => new Doc(new DocOptions { ClientId = 999999, GC = (bool?)scenario["gc"] ?? true });

    private static string Content(Doc doc, JsonObject scenario) => ScenarioRunner.ContentOf(doc, (JsonObject)scenario["roots"]!).Content;

    private static void AssertBytes(JsonNode? expected, byte[] actual, string what)
    {
        var e = Fixtures.Bytes(expected);
        if (!e.AsSpan().SequenceEqual(actual))
        {
            Assert.Fail($"{what} differs from Yjs\n  yjs:      {Convert.ToBase64String(e)}\n  yjssharp: {Convert.ToBase64String(actual)}");
        }
    }

    // ---------------------------------------------------------------- (a) Yjs updates apply here

    [TestMethod]
    [DynamicData(nameof(Scenarios))]
    public void JsStateAsUpdate_AppliesWithSameContentAndStateVector(string key)
    {
        var scenario = Fixtures.Scenario(key);
        foreach (var peer in scenario["peers"]!.AsArray())
        {
            var doc = FreshDoc(scenario);
            doc.ApplyUpdate(Fixtures.Bytes(peer!["update"]));
            Assert.AreEqual(ScenarioRunner.Canonical(peer["content"]), Content(doc, scenario), "content after applying the v1 state");
            AssertBytes(peer["stateVector"], doc.EncodeStateVector(), "state vector after applying the v1 state");

            var doc2 = FreshDoc(scenario);
            doc2.ApplyUpdateV2(Fixtures.Bytes(peer["updateV2"]));
            Assert.AreEqual(ScenarioRunner.Canonical(peer["content"]), Content(doc2, scenario), "content after applying the v2 state");
            AssertBytes(peer["stateVector"], doc2.EncodeStateVector(), "state vector after applying the v2 state");
        }
    }

    [TestMethod]
    [DynamicData(nameof(Scenarios))]
    public void JsStateAsUpdate_TextDeltasMatch(string key)
    {
        var scenario = Fixtures.Scenario(key);
        foreach (var peer in scenario["peers"]!.AsArray())
        {
            var doc = FreshDoc(scenario);
            doc.ApplyUpdate(Fixtures.Bytes(peer!["update"]));
            Assert.AreEqual(ScenarioRunner.Canonical(peer["deltas"]), ScenarioRunner.ContentOf(doc, (JsonObject)scenario["roots"]!).Deltas);
        }
    }

    [TestMethod]
    [DynamicData(nameof(Scenarios))]
    public void JsIncrementalUpdates_ApplyInRecordedOrder(string key)
    {
        var scenario = Fixtures.Scenario(key);
        var v1       = FreshDoc(scenario);
        var v2       = FreshDoc(scenario);
        foreach (var peer in scenario["peers"]!.AsArray())
        {
            foreach (var u in peer!["updates"]!.AsArray())
            {
                v1.ApplyUpdate(Fixtures.Bytes(u!["v1"]));
                v2.ApplyUpdateV2(Fixtures.Bytes(u["v2"]));
            }
        }
        var merged = scenario["merged"]!;
        Assert.AreEqual(ScenarioRunner.Canonical(merged["content"]), Content(v1, scenario), "v1");
        Assert.AreEqual(ScenarioRunner.Canonical(merged["content"]), Content(v2, scenario), "v2");
        AssertBytes(merged["stateVector"], v1.EncodeStateVector(), "state vector");
    }

    [TestMethod]
    [DynamicData(nameof(Scenarios))]
    public void JsMergedUpdates_Apply(string key)
    {
        var scenario = Fixtures.Scenario(key);
        var merged   = scenario["merged"]!;
        var v1       = FreshDoc(scenario);
        v1.ApplyUpdate(Fixtures.Bytes(merged["v1"]));
        Assert.AreEqual(ScenarioRunner.Canonical(merged["content"]), Content(v1, scenario));
        var v2 = FreshDoc(scenario);
        v2.ApplyUpdateV2(Fixtures.Bytes(merged["v2"]));
        Assert.AreEqual(ScenarioRunner.Canonical(merged["contentV2"]), Content(v2, scenario));
    }

    // ------------------------------------------------- (b) same operations, byte-identical output

    [TestMethod]
    [DynamicData(nameof(Scenarios))]
    public void Replay_UpdateEventsAreByteIdentical(string key)
    {
        var scenario = Fixtures.Scenario(key);
        var runner   = ScenarioRunner.Run(scenario);
        var peers    = scenario["peers"]!.AsArray();
        for (var i = 0; i < peers.Count; i++)
        {
            var expected = peers[i]!["updates"]!.AsArray();
            Assert.AreEqual(expected.Count, runner.Updates[i].Count, $"peer {i}: number of update events");
            for (var u = 0; u < expected.Count; u++)
            {
                AssertBytes(expected[u]!["v1"], runner.Updates[i][u].V1, $"peer {i} update {u} (v1)");
                AssertBytes(expected[u]!["v2"], runner.Updates[i][u].V2, $"peer {i} update {u} (v2)");
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(Scenarios))]
    public void Replay_ContentMatches(string key)
    {
        var scenario = Fixtures.Scenario(key);
        var runner   = ScenarioRunner.Run(scenario);
        var peers    = scenario["peers"]!.AsArray();
        for (var i = 0; i < peers.Count; i++)
        {
            var (content, deltas) = ScenarioRunner.ContentOf(runner.Docs[i], (JsonObject)scenario["roots"]!);
            Assert.AreEqual(ScenarioRunner.Canonical(peers[i]!["content"]), content, $"peer {i} content");
            Assert.AreEqual(ScenarioRunner.Canonical(peers[i]!["deltas"]), deltas, $"peer {i} deltas");
        }
    }

    [TestMethod]
    [DynamicData(nameof(Scenarios))]
    public void Replay_StateEncodingsAreByteIdentical(string key)
    {
        var scenario = Fixtures.Scenario(key);
        var runner   = ScenarioRunner.Run(scenario);
        var peers    = scenario["peers"]!.AsArray();
        for (var i = 0; i < peers.Count; i++)
        {
            AssertBytes(peers[i]!["stateVector"], runner.Docs[i].EncodeStateVector(), $"peer {i} state vector");
            AssertBytes(peers[i]!["update"], runner.Docs[i].EncodeStateAsUpdate(), $"peer {i} encodeStateAsUpdate");
            AssertBytes(peers[i]!["updateV2"], runner.Docs[i].EncodeStateAsUpdateV2(), $"peer {i} encodeStateAsUpdateV2");
        }
    }

    [TestMethod]
    [DynamicData(nameof(Scenarios))]
    public void MergeUpdates_AreByteIdentical(string key)
    {
        var scenario = Fixtures.Scenario(key);
        var all      = scenario["peers"]!.AsArray().SelectMany(p => p!["updates"]!.AsArray()).ToList();
        var merged   = scenario["merged"]!;
        AssertBytes(merged["v1"], Y.MergeUpdates(all.Select(u => Fixtures.Bytes(u!["v1"])).ToList()), "mergeUpdates");
        AssertBytes(merged["v2"], Y.MergeUpdatesV2(all.Select(u => Fixtures.Bytes(u!["v2"])).ToList()), "mergeUpdatesV2");
    }

    [TestMethod]
    [DynamicData(nameof(Scenarios))]
    public void StateVectorFromUpdate_IsByteIdentical(string key)
    {
        var scenario = Fixtures.Scenario(key);
        var merged   = scenario["merged"]!;
        AssertBytes(merged["stateVector"], Y.EncodeStateVectorFromUpdate(Fixtures.Bytes(merged["v1"])), "encodeStateVectorFromUpdate");
        AssertBytes(merged["stateVectorV2"], Y.EncodeStateVectorFromUpdateV2(Fixtures.Bytes(merged["v2"])), "encodeStateVectorFromUpdateV2");
    }

    [TestMethod]
    [DynamicData(nameof(Scenarios))]
    public void ConvertUpdateFormat_IsByteIdentical(string key)
    {
        var scenario    = Fixtures.Scenario(key);
        var peer0       = scenario["peers"]!.AsArray()[0]!;
        var conversions = scenario["conversions"]!;
        var merged      = scenario["merged"]!;
        AssertBytes(conversions["v1ToV2"], Y.ConvertUpdateFormatV1ToV2(Fixtures.Bytes(peer0["update"])), "convertUpdateFormatV1ToV2(state)");
        AssertBytes(conversions["v2ToV1"], Y.ConvertUpdateFormatV2ToV1(Fixtures.Bytes(peer0["updateV2"])), "convertUpdateFormatV2ToV1(state)");
        AssertBytes(merged["v1ToV2"], Y.ConvertUpdateFormatV1ToV2(Fixtures.Bytes(merged["v1"])), "convertUpdateFormatV1ToV2(merged)");
        AssertBytes(merged["v2ToV1"], Y.ConvertUpdateFormatV2ToV1(Fixtures.Bytes(merged["v2"])), "convertUpdateFormatV2ToV1(merged)");
    }

    [TestMethod]
    [DynamicData(nameof(Scenarios))]
    public void Diffs_AreByteIdentical(string key)
    {
        var scenario = Fixtures.Scenario(key);
        var diffs    = scenario["diffs"]!.AsArray();
        if (diffs.Count == 0)
        {
            return;
        }
        var merged = scenario["merged"]!;
        var runner = ScenarioRunner.Run(scenario);
        foreach (var diff in diffs)
        {
            var sv = Fixtures.Bytes(diff!["stateVector"]);
            var id = (string)diff["id"]!;
            AssertBytes(diff["stateVector"], runner.Checkpoints.Single(c => c.Id == id).StateVector, $"checkpoint {id} state vector");
            AssertBytes(diff["v1"], Y.DiffUpdate(Fixtures.Bytes(merged["v1"]), sv), $"diffUpdate({id})");
            AssertBytes(diff["v2"], Y.DiffUpdateV2(Fixtures.Bytes(merged["v2"]), sv), $"diffUpdateV2({id})");
            AssertBytes(diff["fromDoc"], runner.Docs[0].EncodeStateAsUpdate(sv), $"encodeStateAsUpdate(doc, {id})");
            AssertBytes(diff["fromDocV2"], runner.Docs[0].EncodeStateAsUpdateV2(sv), $"encodeStateAsUpdateV2(doc, {id})");
        }
    }

    [TestMethod]
    [DynamicData(nameof(Scenarios))]
    public void Snapshots_AreByteIdenticalAndRestore(string key)
    {
        var scenario  = Fixtures.Scenario(key);
        var snapshots = scenario["snapshots"]!.AsArray();
        if (snapshots.Count == 0)
        {
            return;
        }
        var runner = ScenarioRunner.Run(scenario);
        var roots  = (JsonObject)scenario["roots"]!;
        foreach (var expected in snapshots)
        {
            var id     = (string)expected!["id"]!;
            var actual = runner.Snapshots.Single(s => s.Id == id);
            AssertBytes(expected["v1"], Y.EncodeSnapshot(actual.Snapshot), $"encodeSnapshot({id})");
            AssertBytes(expected["v2"], Y.EncodeSnapshotV2(actual.Snapshot), $"encodeSnapshotV2({id})");
            Assert.IsTrue(Y.EqualSnapshots(actual.Snapshot, Y.DecodeSnapshot(Fixtures.Bytes(expected["v1"]))), $"decodeSnapshot({id})");
            Assert.IsTrue(Y.EqualSnapshots(actual.Snapshot, Y.DecodeSnapshotV2(Fixtures.Bytes(expected["v2"]))), $"decodeSnapshotV2({id})");
            if (expected["restored"] != null)
            {
                var restored = Y.CreateDocFromSnapshot(runner.Docs[actual.Peer], actual.Snapshot);
                Assert.AreEqual(ScenarioRunner.Canonical(expected["restored"]), ScenarioRunner.ContentOf(restored, roots).Content, $"createDocFromSnapshot({id})");
                var deltas = ScenarioRunner.ContentOf(runner.Docs[actual.Peer], roots, actual.Snapshot).Deltas;
                Assert.AreEqual(ScenarioRunner.Canonical(expected["deltas"]), deltas, $"toDelta(snapshot {id})");
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(Scenarios))]
    public void RelativePositions_AreByteIdenticalAndResolve(string key)
    {
        var scenario  = Fixtures.Scenario(key);
        var positions = scenario["relPositions"]!.AsArray();
        if (positions.Count == 0)
        {
            return;
        }
        var runner = ScenarioRunner.Run(scenario);
        var roots  = (JsonObject)scenario["roots"]!;
        foreach (var expected in positions)
        {
            var id     = (string)expected!["id"]!;
            var actual = runner.RelativePositions.Single(p => p.Id == id);
            AssertBytes(expected["encoded"], Y.EncodeRelativePosition(actual.Position), $"encodeRelativePosition({id})");
            var decoded  = Y.DecodeRelativePosition(Fixtures.Bytes(expected["encoded"]));
            var resolved = expected["resolved"]!.AsArray();
            for (var i = 0; i < runner.Docs.Length; i++)
            {
                var abs = Y.CreateAbsolutePositionFromRelativePosition(decoded, runner.Docs[i]);
                if (resolved[i] == null)
                {
                    Assert.IsNull(abs, $"{id} in peer {i}");
                    continue;
                }
                Assert.IsNotNull(abs, $"{id} in peer {i}");
                Assert.AreEqual((int)resolved[i]!["index"]!, abs.Index, $"{id} index in peer {i}");
                Assert.AreEqual((int)resolved[i]!["assoc"]!, abs.Assoc, $"{id} assoc in peer {i}");
                Assert.AreSame(ScenarioRunner.Resolve(runner.Docs[i], roots, expected["target"]!), abs.Type, $"{id} type in peer {i}");
            }
        }
    }
}
