using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YjsSharp;
using static YjsSharp.Tests.Core.T;

namespace YjsSharp.Tests.Core;

/// <summary>Tests of the public surface that has no direct yrs test counterpart (Yjs-only features and helpers).</summary>
[TestClass]
public class ApiTests
{
    [TestMethod]
    public void SnapshotsRestoreState()
    {
        var doc = DocNoGc(1);
        var txt = doc.GetText("t");
        var map = doc.GetMap("m");
        txt.Insert(0, "hello");
        map.Set("a", 1);
        var s1 = Y.Snapshot(doc);
        txt.Delete(0, 2);
        map.Set("a", 2);
        map.Set("b", 3);
        var s2 = Y.Snapshot(doc);
        Assert.IsFalse(Y.EqualSnapshots(s1, s2));
        Assert.IsTrue(Y.EqualSnapshots(s1, Y.DecodeSnapshot(Y.EncodeSnapshot(s1))));
        Assert.IsTrue(Y.EqualSnapshots(s2, Y.DecodeSnapshotV2(Y.EncodeSnapshotV2(s2))));
        var restored = Y.CreateDocFromSnapshot(doc, s1);
        Assert.AreEqual("hello", restored.GetText("t").ToString());
        JsonEqual(Map(("a", 1)), restored.GetMap("m").ToJson());
        JsonEqual(Map(("a", 1)), map.ToDictionary(s1));
        Assert.AreEqual("llo", Y.CreateDocFromSnapshot(doc, s2).GetText("t").ToString());
        Assert.ThrowsException<InvalidOperationException>(() => Y.CreateDocFromSnapshot(new Doc(), s1));
        Assert.IsTrue(Y.SnapshotContainsUpdate(s2, doc.EncodeStateAsUpdate()));
        Assert.IsFalse(Y.SnapshotContainsUpdate(s1, doc.EncodeStateAsUpdate()));
        Assert.IsTrue(Y.SnapshotContainsUpdateV2(s2, doc.EncodeStateAsUpdateV2()));
        Assert.AreEqual(0, Y.EmptySnapshot.StateVector.Count);
    }

    [TestMethod]
    public void UpdateUtilitiesWithoutADocument()
    {
        var d1 = Doc(1);
        var d2 = Doc(2);
        var u  = new List<byte[]>();
        var u2 = new List<byte[]>();
        d1.Update   += (_, e) => u.Add(e.Update);
        d1.UpdateV2 += (_, e) => u2.Add(e.Update);
        d1.GetText("t").Insert(0, "abc");
        d1.GetText("t").Insert(3, "def");
        d1.GetText("t").Delete(1, 2);
        var merged   = Y.MergeUpdates(u);
        var mergedV2 = Y.MergeUpdatesV2(u2);
        CollectionAssert.AreEqual(mergedV2, Y.ConvertUpdateFormatV1ToV2(merged));
        CollectionAssert.AreEqual(merged, Y.ConvertUpdateFormatV2ToV1(mergedV2));
        d2.ApplyUpdate(merged);
        Assert.AreEqual("adef", d2.GetText("t").ToString());
        CollectionAssert.AreEqual(d1.EncodeStateVector(), Y.EncodeStateVectorFromUpdate(merged));
        CollectionAssert.AreEqual(d1.EncodeStateVector(), Y.EncodeStateVectorFromUpdateV2(mergedV2));
        var sv   = Y.EncodeStateVector(new Dictionary<ulong, uint> { [1] = 3 });
        var diff = Y.DiffUpdate(merged, sv);
        var d3   = Doc(3);
        d3.ApplyUpdate(u[0]);
        d3.ApplyUpdate(diff);
        Assert.AreEqual("adef", d3.GetText("t").ToString());
        var (from, to) = Y.ParseUpdateMeta(merged);
        Assert.AreEqual(0u, from[1]);
        Assert.AreEqual(6u, to[1]);
        var (fromV2, toV2) = Y.ParseUpdateMetaV2(mergedV2);
        Assert.AreEqual(0u, fromV2[1]);
        Assert.AreEqual(6u, toV2[1]);
    }

    [TestMethod]
    public void RelativePositionsFollowConcurrentEdits()
    {
        var d1  = Doc(1);
        var d2  = Doc(2);
        var t1  = d1.GetText("t");
        t1.Insert(0, "hello world");
        Exchange(d1, d2);
        var rpos  = RelativePosition.CreateRelativePositionFromTypeIndex(t1, 6);
        var bytes = Y.EncodeRelativePosition(rpos);
        d2.GetText("t").Insert(0, ">> ");
        Exchange(d1, d2);
        var abs = RelativePosition.CreateAbsolutePositionFromRelativePosition(Y.DecodeRelativePosition(bytes), d2)!;
        Assert.AreEqual(9, abs.Index);
        Assert.AreSame(d2.GetText("t"), abs.Type);
        Assert.IsTrue(Y.CompareRelativePositions(Y.DecodeRelativePosition(bytes), Y.DecodeRelativePosition(bytes)));
        var arr = d1.GetArray("a");
        arr.Push(1, 2, 3);
        var end = Y.CreateRelativePositionFromTypeIndex(arr, 3);
        arr.Push(4);
        Assert.AreEqual(4, Y.CreateAbsolutePositionFromRelativePosition(end, d1)!.Index);
        Assert.IsNull(new RelativePosition(null, null, new ID(99, 0)).ToAbsolute(d1));
    }

    [TestMethod]
    public void PermanentUserDataAttributesDeletions()
    {
        var doc = Doc(5);
        var pud = new PermanentUserData(doc);
        pud.SetUserMapping(doc, doc.ClientId, "alice");
        var txt = doc.GetText("t");
        var s0  = doc.StateVector[5];
        txt.Insert(0, "abc");
        txt.Delete(1, 1);
        Assert.AreEqual("alice", pud.GetUserByClientId(5));
        var deleted = new ID(5, s0 + 1);
        Assert.AreEqual("alice", pud.GetUserByDeletedId(deleted));
        Assert.IsNull(pud.GetUserByDeletedId(new ID(5, s0)));
        var other = Doc(6);
        other.ApplyUpdate(doc.EncodeStateAsUpdate());
        var pud2 = new PermanentUserData(other);
        Assert.AreEqual("alice", pud2.GetUserByClientId(5));
        Assert.AreEqual("alice", pud2.GetUserByDeletedId(deleted));
    }

    [TestMethod]
    public void RootTypePlaceholderBecomesConcrete()
    {
        var d1 = Doc(1);
        d1.GetText("t").Insert(0, "x");
        d1.GetMap("m").Set("k", "v");
        var d2 = Doc(2);
        d2.ApplyUpdate(d1.EncodeStateAsUpdate());
        Assert.AreEqual("x", d2.GetText("t").ToString());
        Assert.AreEqual("v", d2.GetMap("m").Get("k"));
        Assert.ThrowsException<InvalidOperationException>(() => d2.GetArray("t"));
    }

    [TestMethod]
    public void ExplicitTransactionOverloads()
    {
        var doc  = Doc(1);
        var txt  = doc.GetText("t");
        var arr  = doc.GetArray("a");
        var map  = doc.GetMap("m");
        var upds = 0;
        doc.Update += (_, _) => upds++;
        doc.Transact(t =>
        {
            txt.Insert(t, 0, "abc");
            txt.Format(t, 0, 1, Attrs(("b", true)));
            txt.Delete(t, 2, 1);
            arr.Insert(t, 0, new object?[] { 1, 2 });
            arr.Push(t, new object?[] { 3 });
            arr.Delete(t, 0, 1);
            map.Set(t, "k", "v");
            map.Delete(t, "k");
            map.Set(t, "x", 1);
        });
        Assert.AreEqual(1, upds);
        Assert.AreEqual("ab", txt.ToString());
        JsonEqual(List(2, 3), arr.ToArray());
        JsonEqual(Map(("x", 1)), map.ToJson());
        var other = new Doc();
        Assert.ThrowsException<ArgumentException>(() => other.Transact(t => txt.Insert(t, 0, "z")));
    }

    [TestMethod]
    public void JsonElementValuesAreStoredAsAny()
    {
        var doc  = Doc(1);
        var map  = doc.GetMap("m");
        var json = System.Text.Json.JsonDocument.Parse("{\"a\":[1,2.5,\"x\",null,{\"b\":true}]}").RootElement;
        map.Set("j", json);
        var d2 = Doc(2);
        d2.ApplyUpdate(doc.EncodeStateAsUpdate());
        JsonEqual(Map(("a", List(1, 2.5, "x", null, Map(("b", true))))), d2.GetMap("m").Get("j"));
    }

    [TestMethod]
    public void ObserveDeepOnNestedTypes()
    {
        var doc    = Doc(1);
        var root   = doc.GetMap("root");
        var events = new List<(string Type, string Path)>();
        root.ObserveDeep(evs =>
        {
            foreach (var e in evs)
            {
                events.Add((e.GetType().Name, string.Join("/", e.Path)));
            }
        });
        var list = root.Set("list", new YArray());
        var text = new Text("x");
        list.Push(text);
        text.Insert(1, "y");
        var xml = root.Set("xml", new XmlFragment());
        xml.Push(new XmlElement("p"));
        CollectionAssert.AreEqual(new[]
        {
            ("MapEvent", ""),
            ("ArrayEvent", "list"),
            ("TextEvent", "list/0"),
            ("MapEvent", ""),
            ("XmlEvent", "xml"),
        }, events);
    }
}
