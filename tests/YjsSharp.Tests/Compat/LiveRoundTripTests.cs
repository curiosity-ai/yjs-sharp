using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace YjsSharp.Tests.Compat;

/// <summary>
/// Round trips through a live Yjs process (tests/fixtures-generator/runner.mjs): updates produced by YjsSharp are
/// applied in real Yjs, and a seeded three-peer fuzz mixes JS and C# peers. Inconclusive when node or the Yjs
/// packages are not installed.
/// </summary>
[TestClass]
public sealed class LiveRoundTripTests
{
    private static NodeYjs? _node;
    private static int      _docCounter;

    public static IEnumerable<object[]> Scenarios => Fixtures.DocumentScenarioKeys();

    private static NodeYjs Node => _node ??= NodeYjs.StartOrInconclusive();

    private static string NewName(string prefix) => prefix + "-" + (++_docCounter);

    [ClassCleanup]
    public static void Cleanup()
    {
        _node?.Dispose();
        _node = null;
    }

    [TestMethod]
    [DynamicData(nameof(Scenarios))]
    public void CSharpUpdates_ApplyInYjs(string key)
    {
        var scenario = Fixtures.Scenario(key);
        var roots    = (JsonObject)scenario["roots"]!;
        var gc       = (bool?)scenario["gc"] ?? true;
        var runner   = ScenarioRunner.Run(scenario);
        var node     = Node;
        for (var i = 0; i < runner.Docs.Length; i++)
        {
            var expected = ScenarioRunner.ContentOf(runner.Docs[i], roots);

            var v1 = NewName("state");
            node.NewDoc(v1, 900000, gc);
            node.Apply(v1, runner.Docs[i].EncodeStateAsUpdate());
            Assert.AreEqual(expected, node.Content(v1, roots), $"peer {i}: v1 state applied in Yjs");
            CollectionAssert.AreEqual(runner.Docs[i].EncodeStateVector(), node.StateVector(v1), $"peer {i}: state vector in Yjs");

            var v2 = NewName("state-v2");
            node.NewDoc(v2, 900000, gc);
            node.Apply(v2, runner.Docs[i].EncodeStateAsUpdateV2(), v2: true);
            Assert.AreEqual(expected, node.Content(v2, roots), $"peer {i}: v2 state applied in Yjs");
        }

        // every incremental update YjsSharp emitted, in order, rebuilds the merged document in Yjs
        var incremental = NewName("incremental");
        node.NewDoc(incremental, 900001, gc);
        foreach (var updates in runner.Updates)
        {
            foreach (var u in updates)
            {
                node.Apply(incremental, u.V1);
            }
        }
        Assert.AreEqual(ScenarioRunner.Canonical(scenario["merged"]!["content"]), node.Content(incremental, roots).Content, "incremental updates applied in Yjs");
    }

    [TestMethod]
    [DataRow(1, 300)]
    [DataRow(2, 300)]
    [DataRow(3, 300)]
    public void Fuzz_ThreePeersAlternatingJsAndCSharp(int seed, int steps)
    {
        var node  = Node;
        var roots = new JsonObject { ["t"] = "text", ["a"] = "array", ["m"] = "map" };
        var rng   = new Random(seed);
        var js    = NewName("fuzz-js");
        node.NewDoc(js, 2);

        // peer 0 and 2 are YjsSharp documents; peer 1 lives in Yjs and has a C# shadow that receives
        // exactly the updates the JS document receives (and emits), used to pick valid operations.
        var a      = new Doc(new DocOptions { ClientId = 1 });
        var c      = new Doc(new DocOptions { ClientId = 3 });
        var shadow = new Doc(new DocOptions { ClientId = 1002 });
        var docs   = new[] { a, shadow, c };
        var inbox  = new[] { new List<byte[]>(), new List<byte[]>(), new List<byte[]>() };
        var local  = new List<byte[]>();
        a.Update += (_, e) =>
        {
            if (!Equals(e.Origin, ScenarioRunner.Remote))
            {
                local.Add(e.Update);
            }
        };
        c.Update += (_, e) =>
        {
            if (!Equals(e.Origin, ScenarioRunner.Remote))
            {
                local.Add(e.Update);
            }
        };

        void Deliver(int peer, byte[] update)
        {
            if (peer == 1)
            {
                node.Apply(js, update);
            }
            docs[peer].ApplyUpdate(update, ScenarioRunner.Remote);
        }

        void SyncPair(int from, int to)
        {
            var sv = to == 1 ? node.StateVector(js) : docs[to].EncodeStateVector();
            var update = from == 1 ? node.Encode(js, sv) : docs[from].EncodeStateAsUpdate(sv);
            Deliver(to, update);
        }

        for (var step = 0; step < steps; step++)
        {
            var peer = step % 3;
            var op   = RandomOp(rng, docs[peer], peer);
            byte[][] produced;
            if (peer == 1)
            {
                produced = node.Op(js, roots, op);
                foreach (var u in produced)
                {
                    shadow.ApplyUpdate(u, ScenarioRunner.Remote);
                }
            }
            else
            {
                local.Clear();
                ScenarioRunner.ApplyDocOp(docs[peer], roots, op);
                produced = local.ToArray();
            }
            for (var other = 0; other < 3; other++)
            {
                if (other != peer)
                {
                    inbox[other].AddRange(produced);
                }
            }

            // deliver part of somebody's inbox, out of order
            if (rng.NextDouble() < 0.35)
            {
                var to = rng.Next(3);
                var n  = rng.Next(inbox[to].Count + 1);
                for (var k = 0; k < n; k++)
                {
                    var index = rng.Next(inbox[to].Count);
                    var u     = inbox[to][index];
                    inbox[to].RemoveAt(index);
                    Deliver(to, u);
                }
            }
            if (rng.NextDouble() < 0.05)
            {
                var from = rng.Next(3);
                SyncPair(from, (from + 1 + rng.Next(2)) % 3);
            }
        }

        for (var peer = 0; peer < 3; peer++)
        {
            foreach (var u in inbox[peer])
            {
                Deliver(peer, u);
            }
            inbox[peer].Clear();
        }
        for (var pass = 0; pass < 2; pass++)
        {
            for (var from = 0; from < 3; from++)
            {
                for (var to = 0; to < 3; to++)
                {
                    if (from != to)
                    {
                        SyncPair(from, to);
                    }
                }
            }
        }

        var jsContent = node.Content(js, roots);
        Assert.AreEqual(jsContent, ScenarioRunner.ContentOf(a, roots), "C# peer 0 converged with Yjs");
        Assert.AreEqual(jsContent, ScenarioRunner.ContentOf(c, roots), "C# peer 2 converged with Yjs");
        Assert.AreEqual(jsContent, ScenarioRunner.ContentOf(shadow, roots), "shadow converged with Yjs");
        var sv = node.StateVector(js);
        CollectionAssert.AreEqual(sv, a.EncodeStateVector(), "state vector of peer 0");
        CollectionAssert.AreEqual(sv, c.EncodeStateVector(), "state vector of peer 2");

        // the converged state round-trips both ways, in both formats
        var fromCs = NewName("fuzz-from-cs");
        node.NewDoc(fromCs, 77);
        node.Apply(fromCs, a.EncodeStateAsUpdateV2(), v2: true);
        Assert.AreEqual(jsContent, node.Content(fromCs, roots), "C# v2 state applied in Yjs");
        var fromJs = new Doc(new DocOptions { ClientId = 78 });
        fromJs.ApplyUpdate(node.Encode(js));
        Assert.AreEqual(jsContent, ScenarioRunner.ContentOf(fromJs, roots), "Yjs v1 state applied in C#");
    }

    private static readonly string[] Words = { "a", "b", "hello", " ", "world", "ä", "ß", "漢字", "😀", "👩‍👩‍👧", "x\ny", "é", "🇩🇪", "Ω" };

    /// <summary>A random operation that is valid for the current state of <paramref name="doc"/>.</summary>
    private static JsonObject RandomOp(Random rng, Doc doc, int peer)
    {
        switch (rng.Next(3))
        {
            case 0:
            {
                var len = doc.GetText("t").Length;
                var r   = rng.NextDouble();
                if (r < 0.55 || len == 0)
                {
                    var op = new JsonObject { ["op"] = "textInsert", ["peer"] = peer, ["target"] = "t", ["index"] = rng.Next(len + 1), ["text"] = Words[rng.Next(Words.Length)] + Words[rng.Next(Words.Length)] };
                    if (rng.NextDouble() < 0.2)
                    {
                        op["attrs"] = new JsonObject { ["bold"] = true };
                    }
                    return op;
                }
                var index = rng.Next(len);
                if (r < 0.85)
                {
                    return new JsonObject { ["op"] = "textDelete", ["peer"] = peer, ["target"] = "t", ["index"] = index, ["length"] = 1 + rng.Next(Math.Min(4, len - index)) };
                }
                var attrs = rng.Next(4) switch
                {
                    0 => new JsonObject { ["bold"] = true },
                    1 => new JsonObject { ["italic"] = true },
                    2 => new JsonObject { ["bold"] = null },
                    _ => new JsonObject { ["color"] = "#ff0000" }
                };
                return new JsonObject { ["op"] = "textFormat", ["peer"] = peer, ["target"] = "t", ["index"] = index, ["length"] = 1 + rng.Next(Math.Min(5, len - index)), ["attrs"] = attrs };
            }
            case 1:
            {
                var len = doc.GetArray("a").Length;
                if (rng.NextDouble() < 0.65 || len == 0)
                {
                    JsonNode? value = rng.Next(8) switch
                    {
                        0 => rng.Next(1000),
                        1 => "v" + rng.Next(100),
                        2 => true,
                        3 => null,
                        4 => new JsonObject { ["k"] = rng.Next(10) },
                        5 => new JsonArray(1, 2),
                        6 => 1.5,
                        _ => new JsonObject { ["$y"] = "bytes", ["b64"] = Convert.ToBase64String(new[] { (byte)rng.Next(256) }) }
                    };
                    return new JsonObject { ["op"] = "arrayInsert", ["peer"] = peer, ["target"] = "a", ["index"] = rng.Next(len + 1), ["values"] = new JsonArray(value) };
                }
                var index = rng.Next(len);
                return new JsonObject { ["op"] = "arrayDelete", ["peer"] = peer, ["target"] = "a", ["index"] = index, ["length"] = 1 + rng.Next(Math.Min(3, len - index)) };
            }
            default:
            {
                var key = "k" + rng.Next(8);
                if (rng.NextDouble() < 0.75)
                {
                    JsonNode? value = rng.Next(4) switch
                    {
                        0 => rng.Next(100),
                        1 => "str" + rng.Next(5),
                        2 => false,
                        _ => new JsonObject { ["nested"] = new JsonArray(rng.Next(3)) }
                    };
                    return new JsonObject { ["op"] = "mapSet", ["peer"] = peer, ["target"] = "m", ["key"] = key, ["value"] = value };
                }
                return new JsonObject { ["op"] = "mapDelete", ["peer"] = peer, ["target"] = "m", ["key"] = key };
            }
        }
    }
}
