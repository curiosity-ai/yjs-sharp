using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YjsSharp;
using static YjsSharp.Tests.Core.T;

namespace YjsSharp.Tests.Core;

/// <summary>Port of the unit tests in yrs/src/types/array.rs.</summary>
[TestClass]
public class ArrayTests
{
    // yrs: push_back
    [TestMethod]
    public void PushBack()
    {
        var doc = Doc(1);
        var a   = doc.GetArray("array");
        doc.Transact(_ =>
        {
            a.Push("a");
            a.Push("b");
            a.Push("c");
        });
        JsonEqual(List("a", "b", "c"), a.ToArray());
    }

    // yrs: push_front
    [TestMethod]
    public void PushFront()
    {
        var doc = Doc(1);
        var a   = doc.GetArray("array");
        a.Unshift("c");
        a.Unshift("b");
        a.Unshift("a");
        JsonEqual(List("a", "b", "c"), a.ToArray());
    }

    // yrs: insert
    [TestMethod]
    public void Insert()
    {
        var doc = Doc(1);
        var a   = doc.GetArray("array");
        a.Insert(0, "a");
        a.Insert(1, "c");
        a.Insert(1, "b");
        JsonEqual(List("a", "b", "c"), a.ToArray());
    }

    // yrs: basic
    [TestMethod]
    public void Basic()
    {
        var d1 = Doc(1);
        var d2 = Doc(2);
        d1.GetArray("array").Insert(0, "Hi");
        d2.ApplyUpdate(d1.EncodeStateAsUpdate());
        JsonEqual(List("Hi"), d2.GetArray("array").ToArray());
    }

    // yrs: len
    [TestMethod]
    public void Len()
    {
        var d = Doc(1);
        var a = d.GetArray("array");
        d.Transact(_ =>
        {
            a.Push(0);
            a.Push(1);
            a.Push(2);
            a.Push(3);
            a.Delete(0, 1);
            a.Insert(0, 0);
            Assert.AreEqual(4, a.Length);
        });
        d.Transact(_ =>
        {
            a.Delete(1, 1);
            Assert.AreEqual(3, a.Length);
            a.Insert(1, 1);
            Assert.AreEqual(4, a.Length);
            a.Delete(2, 1);
            Assert.AreEqual(3, a.Length);
            a.Insert(2, 2);
            Assert.AreEqual(4, a.Length);
        });
        Assert.AreEqual(4, a.Length);
        a.Delete(1, 1);
        Assert.AreEqual(3, a.Length);
        a.Insert(1, 1);
        Assert.AreEqual(4, a.Length);
    }

    // yrs: remove_insert
    [TestMethod]
    public void RemoveInsert()
    {
        var d1 = Doc(1);
        var a1 = d1.GetArray("array");
        a1.Insert(0, "A");
        a1.Delete(1, 0);
        JsonEqual(List("A"), a1.ToArray());
    }

    // yrs: insert_3_elements_try_re_get
    [TestMethod]
    public void Insert3ElementsTryReGet()
    {
        var d1 = Doc(1);
        var d2 = Doc(2);
        var a1 = d1.GetArray("array");
        d1.Transact(_ =>
        {
            a1.Push(1);
            a1.Push(true);
            a1.Push(false);
            JsonEqual(List(1.0, true, false), a1.ToArray());
        });
        Exchange(d1, d2);
        JsonEqual(List(1.0, true, false), d2.GetArray("array").ToArray());
    }

    // yrs: concurrent_insert_with_3_conflicts
    [TestMethod]
    public void ConcurrentInsertWith3Conflicts()
    {
        var d1 = Doc(1);
        var a  = d1.GetArray("array");
        a.Insert(0, 0);
        var d2 = Doc(2);
        a.Insert(0, 1);
        var d3 = Doc(3);
        a.Insert(0, 2);
        Exchange(d1, d2, d3);
        JsonEqual(d1.GetArray("array").ToArray(), d2.GetArray("array").ToArray());
        JsonEqual(d2.GetArray("array").ToArray(), d3.GetArray("array").ToArray());
    }

    // yrs: concurrent_insert_remove_with_3_conflicts
    [TestMethod]
    public void ConcurrentInsertRemoveWith3Conflicts()
    {
        var d1 = Doc(1);
        d1.GetArray("array").Insert(0, "x", "y", "z");
        var d2 = Doc(2);
        var d3 = Doc(3);
        Exchange(d1, d2, d3);
        d1.GetArray("array").Insert(1, 0);
        d2.Transact(_ =>
        {
            d2.GetArray("array").Delete(0, 1);
            d2.GetArray("array").Delete(1, 1);
        });
        d3.GetArray("array").Insert(1, 2);
        Exchange(d1, d2, d3);
        JsonEqual(d1.GetArray("array").ToArray(), d2.GetArray("array").ToArray());
        JsonEqual(d2.GetArray("array").ToArray(), d3.GetArray("array").ToArray());
        JsonEqual(List(0, 2, "y"), d1.GetArray("array").ToArray());
    }

    // yrs: insertions_in_late_sync
    [TestMethod]
    public void InsertionsInLateSync()
    {
        var d1 = Doc(1);
        d1.GetArray("array").Push("x", "y");
        var d2 = Doc(2);
        var d3 = Doc(3);
        Exchange(d1, d2, d3);
        d1.GetArray("array").Insert(1, "user0");
        d2.GetArray("array").Insert(1, "user1");
        d3.GetArray("array").Insert(1, "user2");
        Exchange(d1, d2, d3);
        JsonEqual(d1.GetArray("array").ToArray(), d2.GetArray("array").ToArray());
        JsonEqual(d2.GetArray("array").ToArray(), d3.GetArray("array").ToArray());
    }

    // yrs: removals_in_late_sync
    [TestMethod]
    public void RemovalsInLateSync()
    {
        var d1 = Doc(1);
        d1.GetArray("array").Push("x", "y");
        var d2 = Doc(2);
        Exchange(d1, d2);
        d2.GetArray("array").Delete(1, 1);
        d1.GetArray("array").Delete(0, 2);
        Exchange(d1, d2);
        JsonEqual(d1.GetArray("array").ToArray(), d2.GetArray("array").ToArray());
        Assert.AreEqual(0, d1.GetArray("array").Length);
    }

    // yrs: insert_then_merge_delete_on_sync
    [TestMethod]
    public void InsertThenMergeDeleteOnSync()
    {
        var d1 = Doc(1);
        d1.GetArray("array").Push("x", "y", "z");
        var d2 = Doc(2);
        Exchange(d1, d2);
        d2.GetArray("array").Delete(0, 3);
        Exchange(d1, d2);
        JsonEqual(d1.GetArray("array").ToArray(), d2.GetArray("array").ToArray());
    }

    // yrs: iter_array_containing_types
    [TestMethod]
    public void IterArrayContainingTypes()
    {
        var d = Doc(1);
        var a = d.GetArray("arr");
        d.Transact(_ =>
        {
            for (var i = 0; i < 10; i++)
            {
                a.Push(new YMap(new Dictionary<string, object?> { ["value"] = i }));
            }
        });
        var idx = 0;
        foreach (var value in a)
        {
            Assert.IsInstanceOfType(value, typeof(YMap), $"Value of array at index {idx} was no YMap");
            JsonEqual(Map(("value", (double)idx)), ((YMap)value!).ToJson());
            idx++;
        }
    }

    // yrs: insert_and_remove_events
    [TestMethod]
    public void InsertAndRemoveEvents()
    {
        var d        = Doc(1);
        var array    = d.GetArray("array");
        var happened = false;
        array.Observe(_ => happened = true);
        array.Insert(0, 0, 1, 2);
        Assert.IsTrue(happened, "insert of [0,1,2] should trigger event");
        happened = false;
        array.Delete(0, 1);
        Assert.IsTrue(happened, "removal of [0] should trigger event");
        happened = false;
        array.Delete(0, 2);
        Assert.IsTrue(happened, "removal of [1,2] should trigger event");
    }

    // yrs: insert_and_remove_event_changes
    [TestMethod]
    public void InsertAndRemoveEventChanges()
    {
        var                   d1      = Doc(1);
        var                   array   = d1.GetArray("array");
        HashSet<ID>?          added   = null;
        HashSet<ID>?          removed = null;
        IReadOnlyList<Delta>? delta   = null;
        array.Observe(e =>
        {
            added   = e.Changes.Added.Select(i => i.Id).ToHashSet();
            removed = e.Changes.Deleted.Select(i => i.Id).ToHashSet();
            delta   = e.Delta;
        });
        d1.Transact(_ =>
        {
            array.Push(4);
            array.Push("dtrn");
        });
        CollectionAssert.AreEquivalent(new[] { new ID(1, 0), new ID(1, 1) }, added!.ToArray());
        Assert.AreEqual(0, removed!.Count);
        DeltaEqual(new[] { Ins(List(4, "dtrn")) }, delta);

        array.Delete(0, 1);
        Assert.AreEqual(0, added!.Count);
        CollectionAssert.AreEquivalent(new[] { new ID(1, 0) }, removed!.ToArray());
        DeltaEqual(new[] { Del(1) }, delta);

        array.Insert(1, 0.5);
        CollectionAssert.AreEquivalent(new[] { new ID(1, 2) }, added!.ToArray());
        Assert.AreEqual(0, removed!.Count);
        DeltaEqual(new[] { Ret(1), Ins(List(0.5)) }, delta);

        var d2     = Doc(2);
        var array2 = d2.GetArray("array");
        array2.Observe(e =>
        {
            added   = e.Changes.Added.Select(i => i.Id).ToHashSet();
            removed = e.Changes.Deleted.Select(i => i.Id).ToHashSet();
            delta   = e.Delta;
        });
        d2.ApplyUpdate(d1.EncodeStateAsUpdate(d2.EncodeStateVector()));
        CollectionAssert.AreEquivalent(new[] { new ID(1, 1) }, added!.ToArray());
        Assert.AreEqual(0, removed!.Count);
        DeltaEqual(new[] { Ins(List("dtrn", 0.5)) }, delta);
    }

    // yrs: target_on_local_and_remote
    [TestMethod]
    public void TargetOnLocalAndRemote()
    {
        var           d1 = Doc(1);
        var           d2 = Doc(2);
        var           a1 = d1.GetArray("array");
        var           a2 = d2.GetArray("array");
        AbstractType? c1 = null;
        AbstractType? c2 = null;
        a1.Observe(e => c1 = e.Target);
        a2.Observe(e => c2 = e.Target);
        a1.Insert(0, 1, 2);
        Exchange(d1, d2);
        Assert.AreSame(a1, c1);
        Assert.AreSame(a2, c2);
    }

    private static long _uniqueNumber;

    internal static readonly Action<Doc, Random>[] ArrayTransactions =
    {
        (doc, rng) =>
        {
            var yarray       = doc.GetArray("array");
            var uniqueNumber = _uniqueNumber++;
            var len          = rng.Between(1, 4);
            var content      = Enumerable.Repeat((object?)uniqueNumber, len).ToList();
            var pos          = rng.Between(0, yarray.Length);
            var expected     = (List<object?>)yarray.ToJson()!;
            yarray.Insert(pos, content);
            expected.InsertRange(pos, content);
            JsonEqual(expected, yarray.ToJson());
        },
        (doc, rng) =>
        {
            var yarray = doc.GetArray("array");
            var pos    = rng.Between(0, yarray.Length);
            var array2 = new YArray(new object?[] { 1, 2, 3, 4 });
            yarray.Insert(pos, array2);
            JsonEqual(List(1, 2, 3, 4), array2.ToJson());
        },
        (doc, rng) =>
        {
            var yarray = doc.GetArray("array");
            var pos    = rng.Between(0, yarray.Length);
            var map    = new YMap();
            doc.Transact(_ =>
            {
                yarray.Insert(pos, map);
                map.Set("someprop", 42);
                map.Set("someprop", 43);
                map.Set("someprop", 44);
            });
        },
        (doc, rng) =>
        {
            var yarray = doc.GetArray("array");
            var len    = yarray.Length;
            if (len > 0)
            {
                var pos    = rng.Between(0, len - 1);
                var delLen = rng.Between(1, Math.Min(2, len - pos));
                if (rng.NextBool())
                {
                    if (yarray.Get(pos) is YArray array2 && array2.Length > 0)
                    {
                        var p2 = rng.Between(0, array2.Length - 1);
                        var d2 = rng.Between(0, Math.Min(2, array2.Length - p2));
                        array2.Delete(p2, d2);
                    }
                }
                else
                {
                    var oldContent = (List<object?>)yarray.ToJson()!;
                    yarray.Delete(pos, delLen);
                    oldContent.RemoveRange(pos, delLen);
                    JsonEqual(oldContent, yarray.ToJson());
                }
            }
        },
    };

    // yrs: fuzzy_test_6
    [TestMethod]
    public void FuzzyTest6() => TestConnector.RunScenario(6, ArrayTransactions, 5, 6);

    // yrs: fuzzy_test_300
    [TestMethod]
    public void FuzzyTest300() => TestConnector.RunScenario(300, ArrayTransactions, 5, 300);

    // yrs: get_at_removed_index
    [TestMethod]
    public void GetAtRemovedIndex()
    {
        var d1 = Doc(1);
        var a1 = d1.GetArray("array");
        a1.Insert(0, "A");
        a1.Delete(0, 1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => a1.Get(0));
        Assert.AreEqual(0, a1.Length);
    }

    // yrs: observe_deep_event_order
    [TestMethod]
    public void ObserveDeepEventOrder()
    {
        var doc   = Doc(1);
        var array = doc.GetArray("array");
        var paths = new List<List<string>>();
        array.ObserveDeep(events => paths.Add(events.Select(e => string.Join("/", e.Path)).ToList()));
        array.Insert(0, new YMap());
        doc.Transact(_ =>
        {
            var map = (YMap)array.Get(0)!;
            map.Set("a", "a");
            array.Insert(0, 0);
        });
        Assert.AreEqual(2, paths.Count);
        CollectionAssert.AreEqual(new[] { "" }, paths[0]);
        CollectionAssert.AreEqual(new[] { "", "1" }, paths[1]);
    }

    // yrs: multi_threading
    [TestMethod]
    public void MultiThreading()
    {
        var doc  = Doc(1);
        var gate = new object();
        var threads = new[] { "a", "b" }.Select((v, i) => new System.Threading.Thread(() =>
        {
            var rnd = new Random(i);
            for (var k = 0; k < 10; k++)
            {
                System.Threading.Thread.Sleep(rnd.Next(1, 5));
                lock (gate)
                {
                    doc.GetArray("test").Push(v);
                }
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.AreEqual(20, doc.GetArray("test").Length);
    }

    // yrs: insert_empty_range
    [TestMethod]
    public void InsertEmptyRange()
    {
        var doc   = Doc(1);
        var array = doc.GetArray("array");
        doc.Transact(_ =>
        {
            array.Insert(0, 1);
            array.Insert(1, new List<object?>());
            array.Push(2);
        });
        JsonEqual(List(1, 2), array.ToArray());
        var data = doc.EncodeStateAsUpdate();
        var doc2 = Doc(2);
        doc2.ApplyUpdate(data);
        JsonEqual(List(1, 2), doc2.GetArray("array").ToArray());
    }

    [TestMethod]
    public void PrelimArrayContentAndSlice()
    {
        var doc = Doc(1);
        var a   = doc.GetArray("a");
        a.Push(1, 2, 3, 4, 5);
        JsonEqual(List(2, 3), a.Slice(1, 3));
        JsonEqual(List(4, 5), a.Slice(-2));
        var nested = new YArray(new object?[] { "x" });
        nested.Push("y");
        Assert.AreEqual(2, nested.Length);
        a.Push(nested);
        JsonEqual(List("x", "y"), ((YArray)a.Get(5)!).ToArray());
        var clone = (YArray)nested.Clone();
        Assert.IsNull(clone.Doc);
        JsonEqual(List("x", "y"), clone.ToArray());
    }
}
