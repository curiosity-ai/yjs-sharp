using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YjsSharp;
using static YjsSharp.Tests.Core.T;

namespace YjsSharp.Tests.Core;

/// <summary>Port of the unit tests in yrs/src/types/map.rs.</summary>
[TestClass]
public class MapTests
{
    // yrs: map_basic
    [TestMethod]
    public void MapBasic()
    {
        var d1 = Doc(1);
        var m1 = d1.GetMap("map");
        var d2 = Doc(2);
        var m2 = d2.GetMap("map");
        d1.Transact(_ =>
        {
            m1.Set("number", 1);
            m1.Set("string", "hello Y");
            m1.Set("object", Map(("key", Map(("key2", "value")))));
            m1.Set("boolean1", true);
            m1.Set("boolean0", false);
        });
        void CompareAll(YMap m)
        {
            Assert.AreEqual(5, m.Count);
            JsonEqual(1.0, m.Get("number"));
            Assert.AreEqual(false, m.Get("boolean0"));
            Assert.AreEqual(true, m.Get("boolean1"));
            Assert.AreEqual("hello Y", m.Get("string"));
            JsonEqual(Map(("key", Map(("key2", "value")))), m.Get("object"));
        }
        CompareAll(m1);
        d2.ApplyUpdate(d1.EncodeStateAsUpdate());
        CompareAll(m2);
    }

    // yrs: map_get_set
    [TestMethod]
    public void MapGetSet()
    {
        var d1 = Doc(1);
        var m1 = d1.GetMap("map");
        m1.Set("stuff", "stuffy");
        m1.Set<object?>("null", null);
        var d2 = Doc(2);
        var m2 = d2.GetMap("map");
        d2.ApplyUpdate(d1.EncodeStateAsUpdate());
        Assert.AreEqual("stuffy", m2.Get("stuff"));
        Assert.IsTrue(m2.TryGet("null", out var v));
        Assert.IsNull(v);
    }

    // yrs: map_get_set_sync_with_conflicts
    [TestMethod]
    public void MapGetSetSyncWithConflicts()
    {
        var d1 = Doc(1);
        var m1 = d1.GetMap("map");
        var d2 = Doc(2);
        var m2 = d2.GetMap("map");
        m1.Set("stuff", "c0");
        m2.Set("stuff", "c1");
        var u1 = d1.EncodeStateAsUpdate();
        var u2 = d2.EncodeStateAsUpdate();
        d1.ApplyUpdate(u2);
        d2.ApplyUpdate(u1);
        Assert.AreEqual("c1", m1.Get("stuff"));
        Assert.AreEqual("c1", m2.Get("stuff"));
    }

    // yrs: map_len_remove
    [TestMethod]
    public void MapLenRemove()
    {
        var d1 = Doc(1);
        var m1 = d1.GetMap("map");
        m1.Set("stuff", "c0");
        m1.Set("other-stuff", "c1");
        Assert.AreEqual(2, m1.Count);
        Assert.AreEqual("c0", m1.Remove("stuff"));
        Assert.AreEqual(1, m1.Count);
        Assert.IsNull(m1.Remove("stuff"));
        Assert.AreEqual(1, m1.Count);
        Assert.AreEqual("c1", m1.Remove("other-stuff"));
        Assert.AreEqual(0, m1.Count);
    }

    // yrs: map_clear
    [TestMethod]
    public void MapClear()
    {
        var d1 = Doc(1);
        var m1 = d1.GetMap("map");
        m1.Set("key1", "c0");
        m1.Set("key2", "c1");
        m1.Clear();
        Assert.AreEqual(0, m1.Count);
        Assert.IsNull(m1.Get("key1"));
        Assert.IsNull(m1.Get("key2"));
        var d2 = Doc(2);
        var m2 = d2.GetMap("map");
        d2.ApplyUpdate(d1.EncodeStateAsUpdate());
        Assert.AreEqual(0, m2.Count);
        Assert.IsFalse(m2.Has("key1"));
        Assert.IsFalse(m2.Has("key2"));
    }

    // yrs: map_clear_sync
    [TestMethod]
    public void MapClearSync()
    {
        var d1 = Doc(1);
        var d2 = Doc(2);
        var d3 = Doc(3);
        var d4 = Doc(4);
        d1.GetMap("map").Set("key1", "c0");
        d2.Transact(_ =>
        {
            d2.GetMap("map").Set("key1", "c1");
            d2.GetMap("map").Set("key1", "c2");
        });
        d3.GetMap("map").Set("key1", "c3");
        Exchange(d1, d2, d3, d4);
        d1.GetMap("map").Set("key2", "c0");
        d2.Transact(_ =>
        {
            d2.GetMap("map").Set("key2", "c1");
            d2.GetMap("map").Set("key2", "c2");
        });
        d3.Transact(_ =>
        {
            d3.GetMap("map").Set("key2", "c3");
            d3.GetMap("map").Clear();
        });
        Exchange(d1, d2, d3, d4);
        foreach (var doc in new[] { d1, d2, d3, d4 })
        {
            var map = doc.GetMap("map");
            Assert.IsFalse(map.Has("key1"), $"'key1' entry for peer {doc.ClientId} should be removed");
            Assert.IsFalse(map.Has("key2"), $"'key2' entry for peer {doc.ClientId} should be removed");
            Assert.AreEqual(0, map.Count, $"all entries for peer {doc.ClientId} should be removed");
        }
    }

    // yrs: map_get_set_with_3_way_conflicts
    [TestMethod]
    public void MapGetSetWith3WayConflicts()
    {
        var d1 = Doc(1);
        var d2 = Doc(2);
        var d3 = Doc(3);
        d1.GetMap("map").Set("stuff", "c0");
        d2.Transact(_ =>
        {
            d2.GetMap("map").Set("stuff", "c1");
            d2.GetMap("map").Set("stuff", "c2");
        });
        d3.GetMap("map").Set("stuff", "c3");
        Exchange(d1, d2, d3);
        foreach (var doc in new[] { d1, d2, d3 })
        {
            Assert.AreEqual("c3", doc.GetMap("map").Get("stuff"), $"peer {doc.ClientId} - map entry resolved to unexpected value");
        }
    }

    // yrs: map_get_set_remove_with_3_way_conflicts
    [TestMethod]
    public void MapGetSetRemoveWith3WayConflicts()
    {
        var d1 = Doc(1);
        var d2 = Doc(2);
        var d3 = Doc(3);
        var d4 = Doc(4);
        d1.GetMap("map").Set("key1", "c0");
        d2.Transact(_ =>
        {
            d2.GetMap("map").Set("key1", "c1");
            d2.GetMap("map").Set("key1", "c2");
        });
        d3.GetMap("map").Set("key1", "c3");
        Exchange(d1, d2, d3, d4);
        d1.GetMap("map").Set("key1", "deleteme");
        d2.GetMap("map").Set("key1", "c1");
        d3.GetMap("map").Set("key1", "c2");
        d4.Transact(_ =>
        {
            d4.GetMap("map").Set("key1", "c3");
            d4.GetMap("map").Delete("key1");
        });
        Exchange(d1, d2, d3, d4);
        foreach (var doc in new[] { d1, d2, d3, d4 })
        {
            Assert.IsFalse(doc.GetMap("map").Has("key1"), $"entry 'key1' on peer {doc.ClientId} should be removed");
        }
    }

    private static void AssertKeys(IReadOnlyDictionary<string, EntryChange>? keys, params (string Key, EntryChangeAction Action, object? Old, object? New)[] expected)
    {
        Assert.IsNotNull(keys);
        Assert.AreEqual(expected.Length, keys!.Count, string.Join(", ", keys.Select(k => k.Key + ":" + k.Value)));
        foreach (var (key, action, old, @new) in expected)
        {
            Assert.IsTrue(keys.TryGetValue(key, out var change), $"key {key} missing");
            Assert.AreEqual(action, change!.Action);
            JsonEqual(old, change.OldValue);
            JsonEqual(@new, change.NewValue);
        }
    }

    // yrs: insert_and_remove_events
    [TestMethod]
    public void InsertAndRemoveEvents()
    {
        var                                        d1      = Doc(1);
        var                                        m1      = d1.GetMap("map");
        IReadOnlyDictionary<string, EntryChange>? entries = null;
        m1.Observe(e => entries = e.Keys);
        m1.Set("a", 1);
        AssertKeys(entries, ("a", EntryChangeAction.Add, null, 1));
        m1.Set("a", 2);
        AssertKeys(entries, ("a", EntryChangeAction.Update, 1, 2));
        d1.Transact(_ =>
        {
            m1.Set("a", 3);
            m1.Set("a", 4);
        });
        AssertKeys(entries, ("a", EntryChangeAction.Update, 2, 4));
        m1.Delete("a");
        AssertKeys(entries, ("a", EntryChangeAction.Delete, 4, null));
        d1.Transact(_ =>
        {
            m1.Set("b", 1);
            m1.Set("b", 2);
        });
        AssertKeys(entries, ("b", EntryChangeAction.Add, null, 2));
        d1.Transact(_ =>
        {
            m1.Set("c", 1);
            m1.Delete("c");
        });
        AssertKeys(entries);
        var d2 = Doc(2);
        var m2 = d2.GetMap("map");
        entries = null;
        m2.Observe(e => entries = e.Keys);
        d2.ApplyUpdate(d1.EncodeStateAsUpdate(d2.EncodeStateVector()));
        AssertKeys(entries, ("b", EntryChangeAction.Add, null, 2));
    }

    internal static readonly Action<Doc, Random>[] MapTransactions =
    {
        (doc, rng) =>
        {
            var map = doc.GetMap("map");
            var key = rng.Choice(new[] { "one", "two" });
            map.Set(key, rng.RandomString());
        },
        (doc, rng) =>
        {
            var map = doc.GetMap("map");
            var key = rng.Choice(new[] { "one", "two", "three" });
            if (rng.NextDouble() <= 0.33)
            {
                map.Set(key, new YArray(new object?[] { 1, 2, 3, 4 }));
            }
            else if (rng.NextDouble() <= 0.33)
            {
                map.Set(key, new Text("deeptext"));
            }
            else
            {
                map.Set(key, new YMap(new Dictionary<string, object?> { ["deepkey"] = "deepvalue" }));
            }
        },
        (doc, rng) =>
        {
            var map = doc.GetMap("map");
            map.Delete(rng.Choice(new[] { "one", "two" }));
        },
    };

    // yrs: fuzzy_test_6
    [TestMethod]
    public void FuzzyTest6() => TestConnector.RunScenario(6, MapTransactions, 5, 6);

    [TestMethod]
    public void FuzzyTest300() => TestConnector.RunScenario(300, MapTransactions, 5, 300);

    // yrs: observe_deep
    [TestMethod]
    public void ObserveDeep()
    {
        var doc   = Doc(1);
        var map   = doc.GetMap("map");
        var paths = new List<List<string>>();
        var calls = 0;
        map.ObserveDeep(events =>
        {
            paths.Add(events.Select(e => string.Join("/", e.Path)).ToList());
            calls++;
        });
        var nested = map.Set("map", new YMap());
        nested.Set("array", new YArray());
        var nested2 = (YArray)nested.Get("array")!;
        nested2.Insert(0, "content");
        var nestedText = nested.Set("text", new Text("text"));
        nestedText.Insert(nestedText.Length, "!");
        Assert.AreEqual(5, calls);
        CollectionAssert.AreEqual(new[] { "" }, paths[0]);
        CollectionAssert.AreEqual(new[] { "map" }, paths[1]);
        CollectionAssert.AreEqual(new[] { "map/array" }, paths[2]);
        CollectionAssert.AreEqual(new[] { "map" }, paths[3]);
        CollectionAssert.AreEqual(new[] { "map/text" }, paths[4]);
    }

    // yrs: get_or_init
    [TestMethod]
    public void GetOrInit()
    {
        var doc = Doc(1);
        var map = doc.GetMap("map");
        doc.Transact(_ =>
        {
            var m = map.GetOrInit<YMap>("nested");
            m.Set("key", 1);
            m = map.GetOrInit<YMap>("nested");
            JsonEqual(1, m.Get("key"));
            var a = map.GetOrInit<YArray>("nested");
            a.Insert(0, 1);
            a = map.GetOrInit<YArray>("nested");
            JsonEqual(1, a.Get(0));
            var t = map.GetOrInit<Text>("nested");
            t.Insert(0, "a");
            t = map.GetOrInit<Text>("nested");
            Assert.AreEqual("a", t.ToString());
            var f = map.GetOrInit<XmlFragment>("nested");
            f.Insert(0, new XmlText("b"));
            f = map.GetOrInit<XmlFragment>("nested");
            Assert.AreEqual("b", f.ToString());
            var x = map.GetOrInit<XmlText>("nested");
            x.Insert(0, "c");
            x = map.GetOrInit<XmlText>("nested");
            Assert.AreEqual("c", x.ToString());
        });
    }

    // yrs: try_update
    [TestMethod]
    public void TryUpdate()
    {
        var doc = new Doc();
        var map = doc.GetMap("map");
        Assert.IsTrue(map.TryUpdate("key", 1), "new entry");
        JsonEqual(1, map.Get("key"));
        Assert.IsFalse(map.TryUpdate("key", 1), "unchanged entry shouldn't trigger update");
        JsonEqual(1, map.Get("key"));
        Assert.IsTrue(map.TryUpdate("key", 2), "entry should change");
        JsonEqual(2, map.Get("key"));
        map.Delete("key");
        Assert.IsTrue(map.TryUpdate("key", 2), "removed entry should trigger update");
        JsonEqual(2, map.Get("key"));
    }

    // yrs: get_as (serde deserialization) - ported as a JSON round trip of nested prelim types
    [TestMethod]
    public void GetAs()
    {
        var doc = new Doc();
        var map = doc.GetMap("map");
        map.Set("orders", new YArray(new object?[]
        {
            new YMap(new Dictionary<string, object?>
            {
                ["shipment_address"] = "123 Main St",
                ["items"] = new YMap(new Dictionary<string, object?>
                {
                    ["item1"] = new YMap(new Dictionary<string, object?> { ["name"] = "item1", ["price"] = 1.99, ["quantity"] = 2 }),
                    ["item2"] = new YMap(new Dictionary<string, object?> { ["name"] = "item2", ["price"] = 2.99, ["quantity"] = 1 }),
                }),
            }),
        }));
        var expected = List(Map(
            ("shipment_address", "123 Main St"),
            ("items", Map(
                ("item1", Map(("name", "item1"), ("price", 1.99), ("quantity", 2))),
                ("item2", Map(("name", "item2"), ("price", 2.99), ("quantity", 1)))))));
        JsonEqual(expected, ((YArray)map.Get("orders")!).ToJson());
        var doc2 = new Doc();
        doc2.ApplyUpdate(doc.EncodeStateAsUpdate());
        JsonEqual(expected, ((YArray)doc2.GetMap("map").Get("orders")!).ToJson());
    }

    // yrs: multi_threading
    [TestMethod]
    public void MultiThreading()
    {
        var doc  = Doc(1);
        var gate = new object();
        var threads = new[] { 1, 2 }.Select(v => new System.Threading.Thread(() =>
        {
            var rnd = new Random(v);
            for (var k = 0; k < 10; k++)
            {
                System.Threading.Thread.Sleep(rnd.Next(1, 5));
                lock (gate)
                {
                    doc.GetMap("test").Set("key", v);
                }
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        var value = doc.GetMap("test").Get("key");
        Assert.IsTrue(Equals(value, 1) || Equals(value, 2));
    }

    // yrs: test_delete_not_applied_map
    [TestMethod]
    public void DeleteNotAppliedMap()
    {
        var docA  = new Doc();
        var rootA = docA.GetMap("root");
        var docB  = new Doc();
        var rootB = docB.GetMap("root");
        rootA.Set("sub", new YMap());
        docB.ApplyUpdate(docA.EncodeStateAsUpdate());
        var svA = docA.EncodeStateVector();
        var svB = docB.EncodeStateVector();
        ((YMap)rootB.Get("sub")!).Set("x", 1L);
        docA.ApplyUpdate(docB.EncodeStateAsUpdate(svA));
        svA = docA.EncodeStateVector();
        var svBSaved = svB;
        docB.Transact(_ =>
        {
            rootB.Set("key", "value");
            rootB.Delete("sub");
        });
        docA.ApplyUpdate(docB.EncodeStateAsUpdate(svA));
        CollectionAssert.AreEqual(new[] { "key" }, rootA.Keys.ToArray());
        CollectionAssert.AreEqual(new[] { "key" }, rootB.Keys.ToArray());
        rootA.Delete("key");
        docB.ApplyUpdate(docA.EncodeStateAsUpdate(svBSaved));
        CollectionAssert.AreEqual(rootA.Keys.ToArray(), rootB.Keys.ToArray());
    }

    // yrs: key_event_over_a_value_integrated_after_a_clock_gap.
    // yrs integrates blocks behind a gap of their own client; Yjs keeps them pending until the gap is filled.
    // The port asserts the Yjs behaviour: the value stays pending, and the key event is correct once it arrives.
    [TestMethod]
    public void KeyEventOverAValueIntegratedAfterAClockGap()
    {
        var a  = Doc(20);
        var b  = Doc(10);
        var r  = Doc(30);
        var ma = a.GetMap("map");
        var mb = b.GetMap("map");
        var mr = r.GetMap("map");
        ma.Set("k", "a");
        var first = a.EncodeStateAsUpdate();
        b.ApplyUpdate(first);
        r.ApplyUpdate(first);
        ma.Set("filler", "f");
        ma.Set("k", "b");
        ma.Delete("k");
        var afterGap = a.EncodeStateAsUpdate(Y.EncodeStateVector(new Dictionary<ulong, uint> { [20] = 2 }));
        r.ApplyUpdate(afterGap);
        // the delete set applies at once (20:0 is known), the new value stays pending until 20:1 arrives
        Assert.IsNull(mr.Get("k"));
        Assert.IsTrue(r.HasPendingUpdates);
        mb.Set("k", "c");
        IReadOnlyDictionary<string, EntryChange>? keys = null;
        mr.Observe(e => keys = e.Keys);
        r.ApplyUpdate(b.EncodeStateAsUpdate(Y.EncodeStateVector(new Dictionary<ulong, uint> { [20] = 1 })));
        Assert.AreEqual("c", mr.Get("k"));
        Assert.AreEqual(EntryChangeAction.Add, keys!["k"].Action);
        r.ApplyUpdate(a.EncodeStateAsUpdate(r.EncodeStateVector()));
        Assert.IsNull(mr.Get("k"));
        Assert.AreEqual(EntryChangeAction.Delete, keys!["k"].Action);
        Assert.AreEqual("c", keys!["k"].OldValue);
        Assert.IsFalse(r.HasPendingUpdates);
        Exchange(a, b, r);
        Assert.IsNull(ma.Get("k"));
        Assert.IsNull(mb.Get("k"));
    }

    // yrs: nested_map_integrated_after_a_clock_gap_reports_its_changes (adapted to Yjs semantics, see above)
    [TestMethod]
    public void NestedMapIntegratedAfterAClockGapReportsItsChanges()
    {
        var a  = Doc(1);
        var r  = Doc(2);
        var ma = a.GetMap("map");
        var mr = r.GetMap("map");
        ma.Set("filler", 1);
        var nestedA    = ma.Set("nested", new YMap());
        var withNested = a.EncodeStateAsUpdate(Y.EncodeStateVector(new Dictionary<ulong, uint> { [1] = 1 }));
        r.ApplyUpdate(withNested);
        Assert.IsNull(mr.Get("nested"), "pending until 1:0 arrives");
        CollectionAssert.AreEqual(new[] { new KeyValuePair<ulong, uint>(1, 0) }, r.PendingMissing.ToArray());
        r.ApplyUpdate(a.EncodeStateAsUpdate());
        var nestedR = (YMap)mr.Get("nested")!;
        var events  = 0;
        nestedR.Observe(_ => events++);
        nestedA.Set("y", 2);
        r.ApplyUpdate(a.EncodeStateAsUpdate(r.EncodeStateVector()));
        JsonEqual(2, nestedR.Get("y"));
        Assert.AreEqual(1, events);
    }
}
