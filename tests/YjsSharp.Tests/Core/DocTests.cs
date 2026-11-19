using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YjsSharp;
using YjsSharp.Structs;
using static YjsSharp.Tests.Core.T;

namespace YjsSharp.Tests.Core;

/// <summary>Port of the unit tests in yrs/src/doc.rs.</summary>
[TestClass]
public class DocTests
{
    private static readonly byte[] BasicUpdateV1 =
    {
        1, 3, 227, 214, 245, 198, 5, 0, 4, 1, 4, 116, 121, 112, 101, 1, 48, 68, 227, 214, 245,
        198, 5, 0, 1, 49, 68, 227, 214, 245, 198, 5, 1, 1, 50, 0,
    };

    // yrs: apply_update_basic_v1
    [TestMethod]
    public void ApplyUpdateBasicV1()
    {
        var doc = new Doc();
        var txt = doc.GetText("type");
        doc.ApplyUpdate(BasicUpdateV1);
        Assert.AreEqual("210", txt.ToString());
    }

    // yrs: apply_update_basic_v2
    [TestMethod]
    public void ApplyUpdateBasicV2()
    {
        var update = new byte[]
        {
            0, 0, 6, 195, 187, 207, 162, 7, 1, 0, 2, 0, 2, 3, 4, 0, 68, 11, 7, 116, 121, 112, 101,
            48, 49, 50, 4, 65, 1, 1, 1, 0, 0, 1, 3, 0, 0,
        };
        var doc = new Doc();
        var txt = doc.GetText("type");
        doc.ApplyUpdateV2(update);
        Assert.AreEqual("210", txt.ToString());
    }

    // yrs: encode_basic
    [TestMethod]
    public void EncodeBasic()
    {
        var doc = Doc(1490905955);
        var txt = doc.GetText("type");
        doc.Transact(_ =>
        {
            txt.Insert(0, "0");
            txt.Insert(0, "1");
            txt.Insert(0, "2");
        });
        CollectionAssert.AreEqual(BasicUpdateV1, doc.EncodeStateAsUpdate());
    }

    // yrs: integrate
    [TestMethod]
    public void Integrate()
    {
        var d1  = new Doc();
        var txt = d1.GetText("test");
        txt.Insert(0, "hello");
        txt.Insert(5, " ");
        txt.Insert(6, "world");
        Assert.AreEqual("hello world", txt.ToString());
        var d2 = new Doc();
        d2.ApplyUpdate(d1.EncodeStateAsUpdate(d2.EncodeStateVector()));
        Assert.IsFalse(d2.HasPendingUpdates);
        Assert.AreEqual("hello world", d2.GetText("test").ToString());
    }

    // yrs: on_update
    [TestMethod]
    public void OnUpdate()
    {
        var counter = 0u;
        var doc     = new Doc();
        var doc2    = new Doc();
        EventHandler<UpdateEventArgs> handler = (_, e) =>
        {
            foreach (var block in Y.DecodeUpdate(e.Update).Structs)
            {
                counter += block.Length;
            }
        };
        doc2.Update += handler;
        var txt = doc.GetText("test");
        txt.Insert(0, "abc");
        doc2.ApplyUpdate(doc.EncodeStateAsUpdate(doc2.EncodeStateVector()));
        Assert.AreEqual(3u, counter);
        doc2.Update -= handler;
        txt.Insert(3, "de");
        doc2.ApplyUpdate(doc.EncodeStateAsUpdate(doc2.EncodeStateVector()));
        Assert.AreEqual(3u, counter);
    }

    // yrs: pending_update_integration (feature "small-client" in yrs; the update data uses small client ids)
    [TestMethod]
    public void PendingUpdateIntegration()
    {
        var doc = new Doc();
        var txt = doc.GetText("source");
        var updates = new[]
        {
            new byte[] { 1, 2, 242, 196, 218, 129, 3, 0, 40, 1, 5, 115, 116, 97, 116, 101, 5, 100, 105, 114, 116, 121, 1, 121, 40, 1, 7, 99, 111, 110, 116, 101, 120, 116, 4, 112, 97, 116, 104, 1, 119, 13, 117, 110, 116, 105, 116, 108, 101, 100, 52, 46, 116, 120, 116, 0 },
            new byte[] { 1, 1, 242, 196, 218, 129, 3, 2, 40, 1, 7, 99, 111, 110, 116, 101, 120, 116, 13, 108, 97, 115, 116, 95, 109, 111, 100, 105, 102, 105, 101, 100, 1, 119, 27, 50, 48, 50, 50, 45, 48, 52, 45, 49, 51, 84, 49, 48, 58, 49, 48, 58, 53, 55, 46, 48, 55, 51, 54, 50, 51, 90, 0 },
            new byte[] { 1, 2, 242, 196, 218, 129, 3, 3, 4, 1, 6, 115, 111, 117, 114, 99, 101, 1, 97, 168, 242, 196, 218, 129, 3, 0, 1, 120, 0 },
            new byte[] { 1, 1, 242, 196, 218, 129, 3, 4, 168, 242, 196, 218, 129, 3, 0, 1, 120, 1, 242, 196, 218, 129, 3, 1, 0, 1 },
            new byte[] { 1, 1, 152, 182, 129, 244, 193, 193, 227, 4, 0, 168, 242, 196, 218, 129, 3, 4, 1, 121, 1, 242, 196, 218, 129, 3, 2, 0, 1, 4, 1 },
            new byte[] { 1, 2, 242, 196, 218, 129, 3, 5, 132, 242, 196, 218, 129, 3, 3, 1, 98, 168, 152, 190, 167, 244, 1, 0, 1, 120, 0 },
            new byte[] { 1, 1, 242, 196, 218, 129, 3, 6, 168, 152, 190, 167, 244, 1, 0, 1, 120, 1, 152, 190, 167, 244, 1, 1, 0, 1 },
            new byte[] { 1, 1, 242, 196, 218, 129, 3, 7, 132, 242, 196, 218, 129, 3, 5, 1, 99, 0 },
            new byte[] { 1, 1, 242, 196, 218, 129, 3, 8, 132, 242, 196, 218, 129, 3, 7, 1, 100, 0 },
        };
        foreach (var u in updates)
        {
            doc.ApplyUpdate(u);
        }
        // yrs expects "abcd" only with its "small-client" feature, which truncates the 64-bit client id of the
        // fifth update to 32 bits. Yjs (like this port) keeps the full id, so "c" and "d" wait for a missing update.
        Assert.AreEqual("ab", txt.ToString());
        Assert.IsTrue(doc.HasPendingUpdates);
    }

    // yrs: ypy_issue_32
    [TestMethod]
    public void YpyIssue32()
    {
        var d1      = Doc(1971027812);
        var source1 = d1.GetText("source");
        source1.Insert(0, "a");
        var updates = new[]
        {
            new byte[] { 1, 2, 201, 210, 153, 56, 0, 40, 1, 5, 115, 116, 97, 116, 101, 5, 100, 105, 114, 116, 121, 1, 121, 40, 1, 7, 99, 111, 110, 116, 101, 120, 116, 4, 112, 97, 116, 104, 1, 119, 13, 117, 110, 116, 105, 116, 108, 101, 100, 52, 46, 116, 120, 116, 0 },
            new byte[] { 1, 1, 201, 210, 153, 56, 2, 168, 201, 210, 153, 56, 0, 1, 120, 1, 201, 210, 153, 56, 1, 0, 1 },
            new byte[] { 1, 1, 201, 210, 153, 56, 3, 40, 1, 7, 99, 111, 110, 116, 101, 120, 116, 13, 108, 97, 115, 116, 95, 109, 111, 100, 105, 102, 105, 101, 100, 1, 119, 27, 50, 48, 50, 50, 45, 48, 52, 45, 49, 54, 84, 49, 52, 58, 48, 51, 58, 53, 51, 46, 57, 51, 48, 52, 54, 56, 90, 0 },
            new byte[] { 1, 1, 201, 210, 153, 56, 4, 168, 201, 210, 153, 56, 2, 1, 121, 1, 201, 210, 153, 56, 1, 2, 1 },
        };
        foreach (var u in updates)
        {
            d1.ApplyUpdate(u);
        }
        Assert.AreEqual("a", source1.ToString());
        var d2 = new Doc();
        var source2 = d2.GetText("source");
        d2.ApplyUpdate(d1.EncodeStateAsUpdate(d2.EncodeStateVector()));
        Assert.AreEqual("a", source2.ToString());
        d1.ApplyUpdate(new byte[] { 1, 2, 201, 210, 153, 56, 5, 132, 228, 254, 237, 171, 7, 0, 1, 98, 168, 201, 210, 153, 56, 4, 1, 120, 0 });
        Assert.AreEqual("ab", source1.ToString());
        var d3 = new Doc();
        var source3 = d3.GetText("source");
        d3.ApplyUpdate(d1.EncodeStateAsUpdate(d3.EncodeStateVector()));
        Assert.AreEqual("ab", source3.ToString());
    }

    // yrs: observe_transaction_cleanup
    [TestMethod]
    public void ObserveTransactionCleanup()
    {
        var doc  = new Doc();
        var text = doc.GetText("test");
        IReadOnlyDictionary<ulong, uint>? beforeState = null;
        IReadOnlyDictionary<ulong, uint>? afterState  = null;
        DeleteSet?                        deleteSet   = null;
        EventHandler<TransactionEventArgs> handler = (_, e) =>
        {
            beforeState = e.Transaction.BeforeState;
            afterState  = e.Transaction.AfterState;
            deleteSet   = e.Transaction.DeleteSet;
        };
        doc.AfterTransactionCleanup += handler;
        Transaction? txn = null;
        doc.Transact(t =>
        {
            txn = t;
            text.Insert(0, "abc");
            text.Delete(1, 2);
        });
        Assert.AreSame(txn!.BeforeState, beforeState);
        Assert.AreSame(txn.AfterState, afterState);
        Assert.AreSame(txn.DeleteSet, deleteSet);
        Assert.AreEqual(3u, afterState![doc.ClientId]);
        Assert.IsTrue(deleteSet!.IsDeleted(new ID(doc.ClientId, 1)));
        doc.AfterTransactionCleanup -= handler;
        afterState = null;
        text.Insert(0, "should not update");
        Assert.IsNull(afterState);
    }

    // yrs: partially_duplicated_update
    [TestMethod]
    public void PartiallyDuplicatedUpdate()
    {
        var d1   = Doc(1);
        var txt1 = d1.GetText("text");
        txt1.Insert(0, "hello");
        var u  = d1.EncodeStateAsUpdate();
        var d2 = Doc(2);
        var txt2 = d2.GetText("text");
        d2.ApplyUpdate(u);
        txt1.Insert(5, "world");
        d2.ApplyUpdate(d1.EncodeStateAsUpdate());
        Assert.AreEqual(txt1.ToString(), txt2.ToString());
    }

    // yrs: incremental_observe_update
    [TestMethod]
    public void IncrementalObserveUpdate()
    {
        const string input = "hello";
        var d1   = Doc(1);
        var txt1 = d1.GetText("text");
        var acc  = new System.Text.StringBuilder();
        EventHandler<UpdateEventArgs> h1 = (_, e) =>
        {
            foreach (var block in Y.DecodeUpdate(e.Update).Structs)
            {
                if (block is Item item)
                {
                    acc.Append(((ContentString)item.Content).Value);
                }
            }
        };
        d1.Update += h1;
        foreach (var c in input)
        {
            txt1.Insert(txt1.Length, c.ToString());
        }
        Assert.AreEqual(input, acc.ToString());
        d1.Update -= h1;
        var ranges = new List<(uint, uint)>();
        d1.Update += (_, e) =>
        {
            foreach (var r in Y.DecodeUpdate(e.Update).DeleteSet.Get(1))
            {
                ranges.Add((r.Clock, r.End));
            }
        };
        for (var i = 0; i < input.Length; i++)
        {
            txt1.Delete(0, 1);
        }
        CollectionAssert.AreEqual(new[] { (0u, 1u), (1u, 2u), (2u, 3u), (3u, 4u), (4u, 5u) }, ranges);
    }

    // yrs: ycrdt_issue_174
    [TestMethod]
    public void YcrdtIssue174()
    {
        var doc = new Doc();
        var bin = new byte[]
        {
            0, 0, 11, 176, 133, 128, 149, 31, 205, 190, 199, 196, 21, 7, 3, 0, 3, 5, 0, 17, 168, 1,
            8, 0, 40, 0, 8, 0, 40, 0, 8, 0, 40, 0, 33, 1, 39, 110, 91, 49, 49, 49, 114, 111, 111,
            116, 105, 51, 50, 114, 111, 111, 116, 115, 116, 114, 105, 110, 103, 114, 111, 111, 116,
            97, 95, 108, 105, 115, 116, 114, 111, 111, 116, 97, 95, 109, 97, 112, 114, 111, 111,
            116, 105, 51, 50, 95, 108, 105, 115, 116, 114, 111, 111, 116, 105, 51, 50, 95, 109, 97,
            112, 114, 111, 111, 116, 115, 116, 114, 105, 110, 103, 95, 108, 105, 115, 116, 114,
            111, 111, 116, 115, 116, 114, 105, 110, 103, 95, 109, 97, 112, 65, 1, 4, 3, 4, 6, 4, 6,
            4, 5, 4, 8, 4, 7, 4, 11, 4, 10, 3, 0, 5, 1, 6, 0, 1, 0, 1, 0, 1, 2, 65, 8, 2, 8, 0,
            125, 2, 119, 5, 119, 111, 114, 108, 100, 118, 2, 1, 98, 119, 1, 97, 1, 97, 125, 1, 118,
            2, 1, 98, 119, 1, 98, 1, 97, 125, 2, 125, 1, 125, 2, 119, 1, 97, 119, 1, 98, 8, 0, 1,
            141, 223, 163, 226, 10, 1, 0, 1,
        };
        doc.ApplyUpdateV2(bin);
        var root     = doc.GetMap("root");
        var expected = YjsSharp.Encoding.JsJson.Parse(@"{
              ""string"": ""world"",
              ""a_list"": [{""b"": ""a"", ""a"": 1}],
              ""i32_map"": {""1"": 2},
              ""a_map"": { ""1"": {""a"": 2, ""b"": ""b""} },
              ""string_list"": [""a""],
              ""i32"": 2,
              ""string_map"": {""1"": ""b""},
              ""i32_list"": [1]
            }");
        JsonEqual(expected, root.ToJson());
    }

    // yrs: snapshots_splitting_text
    [TestMethod]
    public void SnapshotsSplittingText()
    {
        var d1   = DocNoGc(1);
        var txt1 = d1.GetText("text");
        txt1.Insert(0, "hello");
        var snapshot = Y.Snapshot(d1);
        txt1.Insert(5, "_world");
        var update = d1.EncodeStateFromSnapshot(snapshot);
        var d2     = Doc(2);
        var txt2   = d2.GetText("text");
        d2.ApplyUpdate(update);
        Assert.AreEqual("hello", txt2.ToString());
    }

    // yrs: snapshot_splitting_text_after_first_char
    [TestMethod]
    public void SnapshotSplittingTextAfterFirstChar()
    {
        var d1   = DocNoGc(1);
        var txt1 = d1.GetText("text");
        txt1.Insert(0, "h");
        var snapshot = Y.Snapshot(d1);
        txt1.Insert(1, "llo");
        var update = d1.EncodeStateFromSnapshot(snapshot);
        var d2     = Doc(2);
        var txt2   = d2.GetText("text");
        d2.ApplyUpdate(update);
        Assert.AreEqual("h", txt2.ToString());
    }

    // yrs: snapshot_non_splitting_text
    [TestMethod]
    public void SnapshotNonSplittingText()
    {
        var doc = new Doc(new DocOptions { GC = false });
        var txt = doc.GetText("name");
        txt.Insert(0, "Lucas");
        var snapshot  = Y.Snapshot(doc);
        var stateDiff = doc.EncodeStateFromSnapshot(snapshot);
        var remoteDoc = new Doc(new DocOptions { GC = false });
        var remoteTxt = remoteDoc.GetText("name");
        remoteDoc.ApplyUpdate(stateDiff);
        Assert.AreEqual("Lucas", remoteTxt.ToString());
    }

    // yrs: yrb_issue_45 (a long series of overlapping updates must apply without error)
    [TestMethod]
    public void YrbIssue45()
    {
        var c = new byte[] { 197, 134, 244, 186, 10 };
        byte[] B(params object[] parts)
        {
            var list = new List<byte>();
            foreach (var p in parts)
            {
                if (p is byte[] arr)
                {
                    list.AddRange(arr);
                }
                else
                {
                    list.Add(Convert.ToByte(p));
                }
            }
            return list.ToArray();
        }
        var diffs = new List<byte[]>
        {
            B(1, 3, c, 0, 7, 1, 7, 100, 101, 102, 97, 117, 108, 116, 3, 9, 112, 97, 114, 97, 103, 114, 97, 112, 104, 7, 0, c, 0, 6, 4, 0, c, 1, 1, 115, 0),
            B(1, 1, c, 3, 132, c, 2, 3, 227, 129, 149, 1, c, 1, 2, 1),
            B(1, 4, c, 0, 7, 1, 7, 100, 101, 102, 97, 117, 108, 116, 3, 9, 112, 97, 114, 97, 103, 114, 97, 112, 104, 7, 0, c, 0, 6, 1, 0, c, 1, 1, 132, c, 2, 3, 227, 129, 149, 1, c, 1, 2, 1),
            B(1, 1, c, 4, 132, c, 3, 1, 120, 0),
            B(1, 1, c, 5, 132, c, 4, 3, 227, 129, 129, 1, c, 1, 4, 1),
            B(1, 1, c, 6, 132, c, 5, 1, 107, 0),
            B(1, 2, c, 4, 129, c, 3, 1, 132, c, 4, 3, 227, 129, 129, 1, c, 1, 4, 1),
            B(1, 1, c, 7, 132, c, 6, 3, 227, 129, 147, 1, c, 1, 6, 1),
            B(1, 2, c, 6, 129, c, 5, 1, 132, c, 6, 3, 227, 129, 147, 1, c, 1, 6, 1),
            B(1, 1, c, 8, 132, c, 7, 1, 114, 0),
            B(1, 1, c, 9, 132, c, 8, 3, 227, 130, 140, 1, c, 1, 8, 1),
            B(1, 1, c, 8, 132, c, 7, 1, 114, 0),
            B(1, 1, c, 10, 132, c, 9, 1, 107, 0),
            B(1, 1, c, 11, 132, c, 10, 3, 227, 129, 139, 1, c, 1, 10, 1),
            B(1, 1, c, 12, 132, c, 11, 1, 114, 0),
            B(1, 1, c, 13, 132, c, 12, 3, 227, 130, 137, 1, c, 1, 12, 1),
            B(1, 1, c, 9, 132, c, 8, 3, 227, 130, 140, 1, c, 1, 8, 1),
            B(1, 1, c, 10, 132, c, 9, 1, 107, 0),
            B(1, 1, c, 11, 132, c, 10, 3, 227, 129, 139, 1, c, 1, 10, 1),
            B(1, 1, c, 12, 132, c, 11, 1, 114, 0),
            B(1, 1, c, 14, 132, c, 13, 1, 98, 0),
            B(1, 1, c, 16, 132, c, 15, 1, 103, 0),
            B(1, 1, c, 15, 132, c, 14, 3, 227, 129, 176, 1, c, 1, 14, 1),
            B(1, 1, c, 17, 132, c, 16, 3, 227, 129, 144, 1, c, 1, 16, 1),
            B(1, 1, c, 17, 132, c, 16, 3, 227, 129, 144, 1, c, 1, 16, 1),
            B(1, 1, c, 18, 132, c, 17, 6, 227, 131, 144, 227, 130, 176, 1, c, 2, 15, 1, 17, 1),
            B(1, 1, c, 20, 132, c, 19, 1, 103, 0),
            B(1, 3, c, 13, 132, c, 12, 3, 227, 130, 137, 129, c, 13, 1, 132, c, 14, 4, 227, 129, 176, 103, 1, c, 2, 12, 1, 14, 1),
            B(1, 1, c, 21, 132, c, 20, 3, 227, 129, 140, 1, c, 1, 20, 1),
            B(1, 1, c, 23, 132, c, 22, 3, 227, 129, 170, 1, c, 1, 22, 1),
            B(1, 3, c, 18, 132, c, 17, 6, 227, 131, 144, 227, 130, 176, 129, c, 19, 1, 132, c, 20, 3, 227, 129, 140, 1, c, 3, 15, 1, 17, 1, 20, 1),
            B(1, 1, c, 24, 132, c, 23, 3, 227, 129, 132, 0),
            B(1, 1, c, 22, 132, c, 21, 1, 110, 0),
            B(1, 1, c, 26, 132, c, 25, 3, 227, 129, 139, 1, c, 1, 25, 1),
            B(1, 1, c, 25, 132, c, 24, 1, 107, 0),
            B(1, 4, c, 22, 129, c, 21, 1, 132, c, 22, 6, 227, 129, 170, 227, 129, 132, 129, c, 24, 1, 132, c, 25, 3, 227, 129, 139, 1, c, 2, 22, 1, 25, 1),
            B(1, 1, c, 27, 132, c, 26, 1, 100, 0),
            B(1, 1, c, 28, 132, c, 27, 3, 227, 129, 169, 1, c, 1, 27, 1),
            B(1, 2, c, 27, 129, c, 26, 1, 132, c, 27, 3, 227, 129, 169, 1, c, 1, 27, 1),
            B(1, 1, c, 29, 132, c, 28, 3, 227, 129, 134, 0),
            B(1, 1, c, 30, 132, c, 29, 1, 107, 0),
            B(1, 1, c, 29, 132, c, 28, 3, 227, 129, 134, 0),
            B(1, 1, c, 31, 132, c, 30, 3, 227, 129, 139, 1, c, 1, 30, 1),
            B(1, 1, c, 30, 132, c, 29, 1, 107, 0),
            B(1, 1, c, 31, 132, c, 30, 3, 227, 129, 139, 1, c, 1, 30, 1),
            B(1, 1, c, 32, 135, c, 0, 3, 9, 112, 97, 114, 97, 103, 114, 97, 112, 104, 0),
            B(1, 1, c, 32, 135, c, 0, 3, 9, 112, 97, 114, 97, 103, 114, 97, 112, 104, 0),
            B(1, 2, c, 33, 7, 0, c, 32, 6, 4, 0, c, 33, 1, 107, 0),
            B(1, 1, c, 35, 132, c, 34, 3, 227, 129, 139, 1, c, 1, 34, 1),
            B(1, 1, c, 36, 132, c, 35, 1, 107, 0),
        };
        var doc = new Doc();
        doc.Transact(_ =>
        {
            foreach (var diff in diffs)
            {
                doc.ApplyUpdate(diff);
            }
        });
    }

    // yrs: root_refs
    [TestMethod]
    public void RootRefs()
    {
        var doc = new Doc();
        doc.GetText("text");
        doc.GetArray("array");
        doc.GetMap("map");
        doc.GetXmlFragment("xml_elem");
        foreach (var kv in doc.RootTypes)
        {
            switch (kv.Key)
            {
                case "text":
                    Assert.IsInstanceOfType(kv.Value, typeof(Text));
                    break;
                case "array":
                    Assert.IsInstanceOfType(kv.Value, typeof(YArray));
                    break;
                case "map":
                    Assert.IsInstanceOfType(kv.Value, typeof(YMap));
                    break;
                case "xml_elem":
                    Assert.IsInstanceOfType(kv.Value, typeof(XmlFragment));
                    break;
                default:
                    Assert.Fail($"unrecognized root type: '{kv.Key}'");
                    break;
            }
        }
    }

    // yrs: integrate_block_with_parent_gc
    [TestMethod]
    public void IntegrateBlockWithParentGc()
    {
        var d1 = Doc(1);
        var d2 = Doc(2);
        var d3 = Doc(3);
        d1.GetArray("array").Push(new YArray(new object?[] { "A" }));
        Exchange(d1, d2, d3);
        byte[]? u2 = null;
        d2.Update += (_, e) => u2 = e.Update;
        d2.GetArray("array").Delete(0, 1);
        d1.ApplyUpdate(u2!);
        byte[]? u3 = null;
        d3.Update += (_, e) => u3 = e.Update;
        ((YArray)d3.GetArray("array").Get(0)!).Push("B");
        d1.ApplyUpdate(u3!);
        Exchange(d1, d2, d3);
        var r1 = d1.GetArray("array").ToJson();
        var r2 = d2.GetArray("array").ToJson();
        var r3 = d3.GetArray("array").ToJson();
        JsonEqual(r1, r2);
        JsonEqual(r2, r3);
    }

    private sealed class SubdocEvent
    {
        public List<string> Added   = new List<string>();
        public List<string> Removed = new List<string>();
        public List<string> Loaded  = new List<string>();

        public void AssertIs(string[] added, string[] removed, string[] loaded)
        {
            CollectionAssert.AreEqual(added, Added.OrderBy(x => x, StringComparer.Ordinal).ToArray(), "added");
            CollectionAssert.AreEqual(removed, Removed.ToArray(), "removed");
            CollectionAssert.AreEqual(loaded, Loaded.ToArray(), "loaded");
        }
    }

    private static Func<SubdocEvent?> ObserveSubdocs(Doc doc)
    {
        SubdocEvent? last = null;
        doc.Subdocs += (_, e) => last = new SubdocEvent
        {
            Added   = e.Added.Select(d => d.Guid).ToList(),
            Removed = e.Removed.Select(d => d.Guid).ToList(),
            Loaded  = e.Loaded.Select(d => d.Guid).ToList(),
        };
        return () =>
        {
            var r = last;
            last = null;
            return r;
        };
    }

    // yrs: subdoc
    [TestMethod]
    public void Subdoc()
    {
        var doc     = Doc(1);
        var take    = ObserveSubdocs(doc);
        var subdocs = doc.GetMap("mysubdocs");
        var docA    = new Doc(new DocOptions { Guid = "A" });
        doc.Transact(_ =>
        {
            subdocs.Set("a", docA);
            docA.Load();
        });
        take()!.AssertIs(new[] { "A" }, new string[0], new[] { "A" });
        ((Doc)subdocs.Get("a")!).Load();
        Assert.IsNull(take());
        ((Doc)subdocs.Get("a")!).Destroy();
        take()!.AssertIs(new[] { "A" }, new[] { "A" }, new string[0]);
        ((Doc)subdocs.Get("a")!).Load();
        take()!.AssertIs(new string[0], new string[0], new[] { "A" });
        var docB = new Doc(new DocOptions { Guid = "A", ShouldLoad = false });
        subdocs.Set("b", docB);
        take()!.AssertIs(new[] { "A" }, new string[0], new string[0]);
        ((Doc)subdocs.Get("b")!).Load();
        take()!.AssertIs(new string[0], new string[0], new[] { "A" });
        var docC = new Doc(new DocOptions { Guid = "C" });
        doc.Transact(_ =>
        {
            subdocs.Set("c", docC);
            docC.Load();
        });
        take()!.AssertIs(new[] { "C" }, new string[0], new[] { "C" });
        CollectionAssert.AreEquivalent(new[] { "A", "C" }, doc.GetSubdocGuids().ToArray());

        var data  = doc.EncodeStateAsUpdate();
        var doc2  = new Doc();
        var take2 = ObserveSubdocs(doc2);
        doc2.ApplyUpdate(data);
        take2()!.AssertIs(new[] { "A", "A", "C" }, new string[0], new string[0]);
        var subdocs2 = doc2.GetMap("mysubdocs");
        ((Doc)subdocs2.Get("a")!).Load();
        take2()!.AssertIs(new string[0], new string[0], new[] { "A" });
        CollectionAssert.AreEquivalent(new[] { "A", "C" }, doc2.GetSubdocGuids().ToArray());
        subdocs2.Delete("a");
        take2()!.AssertIs(new string[0], new[] { "A" }, new string[0]);
        CollectionAssert.AreEquivalent(new[] { "A", "C" }, doc2.GetSubdocGuids().ToArray());
    }

    // yrs: subdoc_load_edge_cases
    [TestMethod]
    public void SubdocLoadEdgeCases()
    {
        var doc     = Doc(1);
        var array   = doc.GetArray("test");
        var subdoc1 = new Doc();
        var uuid1   = subdoc1.Guid;
        var take    = ObserveSubdocs(doc);
        array.Insert(0, subdoc1);
        Assert.IsTrue(subdoc1.ShouldLoad);
        Assert.IsFalse(subdoc1.AutoLoad);
        take()!.AssertIs(new[] { uuid1 }, new string[0], new[] { uuid1 });
        subdoc1.Destroy();
        var subdoc2 = (Doc)array.Get(0)!;
        Assert.AreNotSame(subdoc1, subdoc2);
        var uuid2 = subdoc2.Guid;
        take()!.AssertIs(new[] { uuid2 }, new[] { uuid2 }, new string[0]);
        subdoc2.Load();
        take()!.AssertIs(new string[0], new string[0], new[] { uuid2 });
        var doc2  = Doc(2);
        var take2 = ObserveSubdocs(doc2);
        doc2.ApplyUpdate(doc.EncodeStateAsUpdate());
        var subdoc3 = (Doc)doc2.GetArray("test").Get(0)!;
        Assert.IsFalse(subdoc3.ShouldLoad);
        Assert.IsFalse(subdoc3.AutoLoad);
        var uuid3 = subdoc3.Guid;
        take2()!.AssertIs(new[] { uuid3 }, new string[0], new string[0]);
        subdoc3.Load();
        Assert.IsTrue(subdoc3.ShouldLoad);
        take2()!.AssertIs(new string[0], new string[0], new[] { uuid3 });
    }

    // yrs: subdoc_auto_load_edge_cases
    [TestMethod]
    public void SubdocAutoLoadEdgeCases()
    {
        var doc     = Doc(1);
        var array   = doc.GetArray("test");
        var subdoc1 = new Doc(new DocOptions { AutoLoad = true });
        var take    = ObserveSubdocs(doc);
        array.Insert(0, subdoc1);
        Assert.IsTrue(subdoc1.ShouldLoad);
        Assert.IsTrue(subdoc1.AutoLoad);
        var uuid1 = subdoc1.Guid;
        take()!.AssertIs(new[] { uuid1 }, new string[0], new[] { uuid1 });
        subdoc1.Destroy();
        var subdoc2 = (Doc)array.Get(0)!;
        Assert.AreNotSame(subdoc1, subdoc2);
        var uuid2 = subdoc2.Guid;
        take()!.AssertIs(new[] { uuid2 }, new[] { uuid2 }, new string[0]);
        subdoc2.Load();
        take()!.AssertIs(new string[0], new string[0], new[] { uuid2 });
        var doc2  = Doc(2);
        var take2 = ObserveSubdocs(doc2);
        doc2.ApplyUpdate(doc.EncodeStateAsUpdate());
        var subdoc3 = (Doc)doc2.GetArray("test").Get(0)!;
        Assert.IsTrue(subdoc3.ShouldLoad);
        Assert.IsTrue(subdoc3.AutoLoad);
        var uuid3 = subdoc3.Guid;
        take2()!.AssertIs(new[] { uuid3 }, new string[0], new[] { uuid3 });
    }

    // yrs: to_json
    [TestMethod]
    public void ToJson()
    {
        var doc   = new Doc();
        var text  = doc.GetText("text");
        var array = doc.GetArray("array");
        var map   = doc.GetMap("map");
        var xml   = doc.GetXmlFragment("xml-fragment");
        var subDoc = new Doc();
        doc.Transact(_ =>
        {
            var xmlElement = new XmlElement("xml-element");
            xml.Insert(0, xmlElement);
            var xmlText = new XmlText("");
            xml.Insert(0, xmlText);
            text.Insert(0, "hello");
            xmlText.Insert(0, "world");
            xml.Insert(0, new XmlElement("div"));
            xmlElement.Insert(0, new XmlElement("body"));
            array.Insert(0, 1, 2, 3);
            map.Set("key1", "value1");
            map.Set("sub-doc", subDoc);
        });
        subDoc.GetText("sub-text").Insert(0, "sample");
        var actual = doc.ToJson();
        Assert.AreEqual("hello", actual["text"]);
        JsonEqual(List(1, 2, 3), actual["array"]);
        var m = (IDictionary<string, object?>)actual["map"]!;
        Assert.AreEqual("value1", m["key1"]);
        Assert.AreSame(subDoc, m["sub-doc"]);
        Assert.AreEqual("<div></div>world<xml-element><body></body></xml-element>", actual["xml-fragment"]);
    }

    // yrs: apply_snapshot_updates
    [TestMethod]
    public void ApplySnapshotUpdates()
    {
        byte[] update;
        {
            var doc = DocNoGc(1);
            var txt = doc.GetText("test");
            byte[]? u = null;
            doc.Transact(_ =>
            {
                txt.Insert(0, "hello");
                var snap = Y.Snapshot(doc);
                txt.Insert(5, " world");
                u = doc.EncodeStateFromSnapshot(snap);
            });
            update = u!;
        }
        var doc2 = Doc(1);
        var txt2 = doc2.GetText("test");
        doc2.ApplyUpdate(update);
        Assert.AreEqual("hello", txt2.ToString());
    }

    // yrs: out_of_order_updates. yrs integrates u3 behind a skip; Yjs keeps it pending until u2 arrives.
    [TestMethod]
    public void OutOfOrderUpdates()
    {
        var updates = new List<byte[]>();
        var d1      = new Doc();
        d1.Update += (_, e) => updates.Add(e.Update);
        var map = d1.GetMap("map");
        map.Set("a", 1);
        map.Set("a", 1.1);
        map.Set("b", 2);
        JsonEqual(Map(("a", 1.1), ("b", 2)), map.ToJson());
        var d2   = new Doc();
        var map2 = d2.GetMap("map");
        d2.ApplyUpdate(updates[0]);
        JsonEqual(Map(("a", 1)), map2.ToJson());
        d2.ApplyUpdate(updates[2]);
        JsonEqual(Map(("a", 1)), map2.ToJson());
        Assert.IsTrue(d2.HasPendingUpdates);
        d2.ApplyUpdate(updates[1]);
        JsonEqual(Map(("a", 1.1), ("b", 2)), map2.ToJson());
        Assert.IsFalse(d2.HasPendingUpdates);
    }

    // yrs: out_of_order_updates_replay_pending_when_skip_is_filled
    [TestMethod]
    public void OutOfOrderUpdatesReplayPendingWhenSkipIsFilled()
    {
        var d1      = Doc(1);
        var a       = d1.GetText("a");
        var b       = d1.GetText("b");
        var updates = new List<byte[]>();
        d1.Update += (_, e) => updates.Add(e.Update);
        a.Insert(0, "A");
        a.Insert(1, "B");
        b.Insert(0, "C");
        foreach (var order in new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } })
        {
            var d2 = Doc(2);
            var a2 = d2.GetText("a");
            var b2 = d2.GetText("b");
            d2.Transact(_ =>
            {
                foreach (var i in order)
                {
                    d2.ApplyUpdate(updates[i]);
                }
            });
            var o = string.Join(",", order);
            Assert.AreEqual("AB", a2.ToString(), $"order: {o}");
            Assert.AreEqual("C", b2.ToString(), $"order: {o}");
            Assert.IsFalse(d2.HasPendingUpdates, $"order: {o}");
        }
    }

    // yrs: failed_retry_keeps_pending_update_dependencies
    [TestMethod]
    public void FailedRetryKeepsPendingUpdateDependencies()
    {
        var d1 = Doc(1);
        var d2 = Doc(2);
        var d3 = Doc(3);
        byte[] InsertAfter(Doc doc, string after, string chunk)
        {
            var txt = doc.GetText("t");
            byte[]? u = null;
            EventHandler<UpdateEventArgs> h = (_, e) => u = e.Update;
            doc.Update += h;
            var idx   = txt.ToString().IndexOf(after, StringComparison.Ordinal);
            var index = after.Length == 0 || idx < 0 ? 0 : idx + 1;
            txt.Insert(index, chunk);
            doc.Update -= h;
            return u!;
        }
        void Apply(Doc doc, params byte[][] updates)
        {
            doc.Transact(_ =>
            {
                foreach (var u in updates)
                {
                    doc.ApplyUpdate(u);
                }
            });
        }
        var a = InsertAfter(d2, "", "a");
        Apply(d1, a);
        var b = InsertAfter(d1, "a", "b");
        var c = InsertAfter(d1, "b", "c");
        var x = InsertAfter(d3, "", "x");
        Apply(d2, x);
        var y = InsertAfter(d2, "x", "y");
        Apply(d3, a, b, c);
        var z = InsertAfter(d3, "c", "z");
        foreach (var order in new[] { new[] { a, b, y, z }, new[] { b, y, z, a } })
        {
            var d4  = Doc(4);
            var txt = d4.GetText("t");
            Apply(d4, order);
            Assert.AreEqual("ab", txt.ToString());
            Assert.IsTrue(d4.HasPendingUpdates);
        }
    }

    // yrs: encoding_buffer_overflow_errors - malformed updates must fail with an exception, not hang or crash
    [TestMethod]
    public void EncodingBufferOverflowErrors()
    {
        var v1 = new byte[]
        {
            0xe4, 0x9c, 0x10, 0x00, 0x05, 0xff, 0xff, 0x05, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x01, 0x00, 0x00, 0x00, 0xed, 0x00, 0x00, 0x00, 0x01, 0x01, 0x00, 0xfe, 0xb8, 0xc2,
            0xe9, 0xad, 0x87, 0xd9, 0x12, 0x00, 0x00, 0x01, 0x01, 0xff, 0xed, 0xf6,
        };
        var v2s = new[]
        {
            new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x01, 0x02, 0x00, 0x00, 0x16, 0x02, 0x00, 0x00, 0x01, 0xfd, 0xff, 0xff, 0xff, 0xff, 0x7f, 0x00, 0x00 },
            new byte[] { 0xe4, 0x95, 0x00, 0x00, 0x01, 0x18, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0xed, 0x01, 0xbe, 0x82, 0xe3, 0xc3, 0x1c, 0x01, 0x02, 0xe4, 0x95, 0x00, 0x00, 0x01, 0x18, 0x00, 0x00, 0x01, 0x18, 0x00, 0x00, 0x01, 0x00, 0x01, 0xed, 0x00 },
            new byte[] { 0x8f, 0x01, 0x80, 0x00, 0x00, 0x00, 0x01, 0xaa, 0x01, 0x00, 0x01, 0x02, 0x00, 0x00, 0x16, 0x02, 0x00, 0xe5, 0xc4, 0x43, 0x14, 0xe7, 0xa6, 0x8b, 0x93, 0xae, 0xb5, 0xfd, 0x5d, 0xe8, 0x26, 0x9a, 0x8a, 0x59, 0x00, 0x31, 0xd5, 0x0f, 0x12, 0x01, 0x30, 0x00, 0x00, 0x00 },
            new byte[] { 0x00, 0x01, 0x23, 0x00, 0x00, 0x00, 0x01, 0x02, 0x81, 0x00, 0x00, 0x10, 0x00, 0xc7, 0xdc, 0x00, 0xc4, 0x7a, 0x80, 0x00, 0x41, 0xab, 0xea, 0xd6, 0x00, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x84, 0x00, 0x00, 0x10, 0xff, 0xc7, 0xdc, 0xff, 0x00, 0x00, 0x00 },
        };
        static void AssertMalformed(Action a)
        {
            try
            {
                a();
            }
            catch (FormatException)
            {
                return;
            }
            Assert.Fail("a malformed update was accepted");
        }
        AssertMalformed(() => new Doc().ApplyUpdate(v1));
        foreach (var v2 in v2s)
        {
            AssertMalformed(() => new Doc().ApplyUpdateV2(v2));
        }
    }

    // yrs: observe_after_transaction
    [TestMethod]
    public void ObserveAfterTransaction()
    {
        var d1   = Doc(1);
        var txt1 = d1.GetText("text");
        (Dictionary<ulong, uint> Before, Dictionary<ulong, uint> After, DeleteSet Ds)? e = null;
        EventHandler<TransactionEventArgs> handler = (_, a) => e = (
            new Dictionary<ulong, uint>(a.Transaction.BeforeState),
            new Dictionary<ulong, uint>(a.Transaction.AfterState),
            a.Transaction.DeleteSet.Clone());
        d1.AfterTransaction += handler;
        txt1.Insert(0, "hello world");
        Assert.AreEqual(0, e!.Value.Before.Count);
        Assert.AreEqual(11u, e.Value.After[1]);
        Assert.IsTrue(e.Value.Ds.IsEmpty);
        txt1.Delete(2, 7);
        Assert.AreEqual(11u, e!.Value.Before[1]);
        Assert.AreEqual(11u, e.Value.After[1]);
        CollectionAssert.AreEqual(new[] { new DeleteItem(2, 7) }, e.Value.Ds.Get(1).ToArray());
        d1.AfterTransaction -= handler;
        e = null;
        txt1.Insert(4, " the door");
        Assert.IsNull(e);
    }

    private static Text InitTestData(YMap map, params string[] data)
    {
        var txt = map.Set("text", new Text());
        foreach (var ch in data)
        {
            txt.Insert(0, ch);
        }
        return txt;
    }

    // yrs: force_gc
    [TestMethod]
    public void ForceGc()
    {
        var doc = DocNoGc(1);
        var map = doc.GetMap("map");
        doc.Transact(_ =>
        {
            InitTestData(map, "c", "b", "a");
            map.Delete("text");
        });
        var i = 1u;
        foreach (var c in new[] { "c", "b", "a" })
        {
            var block = (Item)doc.Store.Find(new ID(1, i));
            Assert.IsTrue(block.Deleted, "`abc` should be marked as deleted");
            Assert.AreEqual(c, ((ContentString)block.Content).Value);
            i++;
        }
        doc.ForceGc();
        var gced = doc.Store.Find(new ID(1, 1));
        Assert.AreEqual(3u, gced.Length, "GCed blocks should be squashed");
        Assert.IsTrue(gced.Deleted, "`abc` should be deleted");
        Assert.IsInstanceOfType(gced, typeof(YjsSharp.Structs.GC));
    }

    // yrs: force_gc_with_delete_set
    [TestMethod]
    public void ForceGcWithDeleteSet()
    {
        var doc = DocNoGc(1);
        var m0  = doc.GetMap("map");
        Snapshot s1 = null!, s2 = null!;
        doc.Transact(_ =>
        {
            Assert.AreEqual("abc", InitTestData(m0, "c", "b", "a").ToString());
        });
        s1 = Y.Snapshot(doc);
        doc.Transact(_ =>
        {
            Assert.AreEqual("def", InitTestData(m0, "f", "e", "d").ToString());
        });
        s2 = Y.Snapshot(doc);
        doc.Transact(_ =>
        {
            Assert.AreEqual("ghi", InitTestData(m0, "i", "h", "g").ToString());
        });
        Doc Restore(Snapshot s)
        {
            var restored = new Doc();
            restored.ApplyUpdate(doc.EncodeStateFromSnapshot(s));
            return restored;
        }
        Assert.AreEqual("abc", ((Text)Restore(s1).GetMap("map").Get("text")!).ToString());
        var i = 1u;
        foreach (var c in new[] { "c", "b", "a" })
        {
            var block = (Item)doc.Store.Find(new ID(1, i));
            Assert.IsTrue(block.Deleted, "`abc` should be marked as deleted");
            Assert.AreEqual(c, ((ContentString)block.Content).Value);
            i++;
        }
        doc.ForceGc(s2.DeleteSet);
        var gced = doc.Store.Find(new ID(1, 1));
        Assert.IsInstanceOfType(gced, typeof(YjsSharp.Structs.GC), "block should be GCed & compressed");
        Assert.AreEqual(new ID(1, 1), gced.Id);
        Assert.AreEqual(3u, gced.Length);
        Assert.IsNull(Restore(s1).GetMap("map").Get("text"), "we restored snapshot s1, but it's content should be already GCed");
        Assert.AreEqual("def", ((Text)Restore(s2).GetMap("map").Get("text")!).ToString());
    }

    // yrs: uuid_generation
    [TestMethod]
    public void UuidGeneration()
    {
        var guid = new Doc().Guid;
        Assert.IsTrue(Guid.TryParse(guid, out var parsed));
        Assert.AreEqual(parsed.ToString(), guid);
    }

    // yrs: pending_delete_out_of_order
    [TestMethod]
    public void PendingDeleteOutOfOrder()
    {
        var doc  = new Doc();
        var doc2 = new Doc();
        var ups  = new List<byte[]>();
        doc2.UpdateV2 += (_, e) => ups.Add(e.Update);
        var text = doc2.GetText("example");
        text.Insert(0, "foo");
        text.Delete(0, 1);
        Assert.AreEqual("oo", text.ToString());
        doc.ApplyUpdateV2(ups[1]);
        Assert.IsTrue(doc.HasPendingUpdates, "Delete should be pending");
        doc.ApplyUpdateV2(ups[0]);
        Assert.AreEqual("oo", doc.GetText("example").ToString(), "Pending delete should have been applied");
        Assert.IsFalse(doc.HasPendingUpdates);
    }

    // yrs: unobserve_from_callback
    [TestMethod]
    public void UnobserveFromCallback()
    {
        var notify = 0;
        var doc    = new Doc();
        EventHandler<UpdateEventArgs>? handler = null;
        handler = (_, _) =>
        {
            notify++;
            doc.UpdateV2 -= handler;
        };
        doc.UpdateV2 += handler;
        var txt = doc.GetText("test");
        txt.Insert(0, "hello");
        Assert.AreEqual(1, notify);
        txt.Insert(5, " world");
        Assert.AreEqual(1, notify);
    }

    [TestMethod]
    public void ClientIdIsAJavaScriptSafe32BitNumber()
    {
        for (var i = 0; i < 100; i++)
        {
            Assert.IsTrue(new Doc().ClientId <= uint.MaxValue);
        }
    }

    [TestMethod]
    public void ClientIdChangesWhenARemoteUpdateUsesIt()
    {
        var d1 = Doc(7);
        var d2 = Doc(7);
        d1.GetText("t").Insert(0, "a");
        d2.ApplyUpdate(d1.EncodeStateAsUpdate());
        Assert.AreNotEqual(7ul, d2.ClientId);
    }

    [TestMethod]
    public void TransactWithOriginAndResult()
    {
        var     doc    = Doc(1);
        object? origin = null;
        doc.Update += (_, e) => origin = e.Origin;
        var len = doc.Transact(t =>
        {
            doc.GetText("t").Insert(t, 0, "abc");
            doc.GetText("t").Insert(3, "d");
            return doc.GetText("t").Length;
        }, "me");
        Assert.AreEqual(4, len);
        Assert.AreEqual("me", origin);
    }
}
