using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YjsSharp;
using YjsSharp.Structs;
using YjsSharp.Updates;
using static YjsSharp.Tests.Core.T;

namespace YjsSharp.Tests.Core;

/// <summary>Port of the unit tests in yrs/src/update.rs.</summary>
[TestClass]
public class UpdateTests
{
    // yrs: update_decode
    [TestMethod]
    public void UpdateDecode()
    {
        var update = new byte[] { 1, 1, 176, 249, 159, 198, 7, 0, 40, 1, 0, 4, 107, 101, 121, 66, 1, 119, 6, 118, 97, 108, 117, 101, 66, 0 };
        var (structs, ds) = Y.DecodeUpdate(update);
        Assert.AreEqual(1, structs.Count);
        var item = (Item)structs[0];
        Assert.AreEqual(new ID(2026372272, 0), item.Id);
        Assert.IsNull(item.OriginId);
        Assert.IsNull(item.RightOriginId);
        Assert.AreEqual("", item.ParentKey);
        Assert.AreEqual("keyB", item.Key);
        CollectionAssert.AreEqual(new object[] { "valueB" }, item.Content.GetContent().ToArray());
        Assert.IsTrue(ds.IsEmpty);
    }

    // yrs: update_merge
    [TestMethod]
    public void UpdateMerge()
    {
        var     d1   = Doc(1);
        var     txt1 = d1.GetText("test");
        var     d2   = Doc(2);
        var     txt2 = d2.GetText("test");
        byte[]? b1   = null;
        byte[]? b2   = null;
        d1.Update += (_, e) => b1 = e.Update;
        d2.Update += (_, e) => b2 = e.Update;
        d1.Transact(_ =>
        {
            txt1.Insert(0, "aaa");
            txt1.Insert(0, "aaa");
        });
        d2.Transact(_ =>
        {
            txt2.Insert(0, "bbb");
            txt2.Insert(2, "bbb");
        });
        var binary1 = b1!;
        var binary2 = b2!;
        d1.ApplyUpdate(binary2);
        d2.ApplyUpdate(binary1);
        var u12 = Y.MergeUpdates(new[] { binary1, binary2 });
        var d3  = Doc(3);
        d3.ApplyUpdate(u12);
        Assert.AreEqual(txt1.ToString(), txt2.ToString());
        Assert.AreEqual(txt2.ToString(), d3.GetText("test").ToString());
    }

    // yrs: test_duplicate_updates
    [TestMethod]
    public void DuplicateUpdates()
    {
        var doc = Doc(1);
        doc.GetText("test").Insert(0, "aaa");
        var binary = doc.EncodeStateAsUpdate();
        CollectionAssert.AreEqual(binary, Y.MergeUpdates(new[] { binary, binary }));
        var v2 = Y.ConvertUpdateFormatV1ToV2(binary);
        CollectionAssert.AreEqual(v2, Y.MergeUpdatesV2(new[] { v2, v2 }));
    }

    // yrs: test_multiple_clients_in_one_update
    [TestMethod]
    public void MultipleClientsInOneUpdate()
    {
        var a = Doc(1);
        a.GetText("test").Insert(0, "aaa");
        var b = Doc(2);
        b.GetText("test").Insert(0, "bbb");
        var binary1 = a.EncodeStateAsUpdate();
        var binary2 = b.EncodeStateAsUpdate();
        var u12     = Y.MergeUpdates(new[] { binary1, binary2 });
        var u12Copy = Y.MergeUpdates(new[] { binary1, binary2 });
        Assert.AreEqual(2, Y.DecodeUpdate(u12).Structs.Select(s => s.Id.Client).Distinct().Count());
        CollectionAssert.AreEqual(u12Copy, Y.MergeUpdates(new[] { u12 }));
    }

    // yrs: test_v2_encoding_of_fragmented_delete_set
    [TestMethod]
    public void V2EncodingOfFragmentedDeleteSet()
    {
        var before = new byte[]
        {
            0, 1, 0, 11, 129, 215, 239, 201, 16, 198, 237, 152, 220, 8, 4, 4, 0, 4, 1, 1, 0, 11,
            40, 3, 39, 0, 4, 0, 7, 0, 40, 3, 8, 163, 1, 142, 1, 110, 111, 116, 101, 46, 103, 117,
            105, 100, 110, 111, 116, 101, 71, 117, 105, 100, 110, 111, 116, 101, 46, 111, 119, 110,
            101, 114, 111, 119, 110, 101, 114, 110, 111, 116, 101, 46, 116, 121, 112, 101, 110,
            111, 116, 101, 84, 121, 112, 101, 110, 111, 116, 101, 46, 99, 114, 101, 97, 116, 101,
            84, 105, 109, 101, 99, 114, 101, 97, 116, 101, 84, 105, 109, 101, 110, 111, 116, 101,
            46, 116, 105, 116, 108, 101, 116, 105, 116, 108, 101, 49, 112, 114, 111, 115, 101, 109,
            105, 114, 114, 111, 114, 108, 105, 110, 107, 110, 111, 116, 101, 103, 117, 105, 100,
            115, 108, 111, 116, 71, 117, 105, 100, 116, 121, 112, 101, 108, 105, 110, 107, 84, 121,
            112, 101, 99, 104, 105, 108, 100, 114, 101, 110, 98, 9, 8, 10, 5, 9, 8, 15, 74, 0, 5,
            1, 11, 8, 4, 8, 4, 72, 0, 1, 9, 1, 4, 0, 0, 1, 0, 0, 3, 1, 2, 2, 3, 2, 65, 8, 2, 4, 0,
            119, 22, 97, 99, 70, 120, 85, 89, 68, 76, 82, 104, 101, 114, 107, 74, 97, 66, 101, 99,
            115, 99, 51, 103, 125, 136, 57, 125, 0, 119, 13, 49, 54, 56, 53, 53, 51, 48, 50, 56,
            54, 54, 53, 54, 9, 0, 119, 22, 66, 67, 100, 81, 112, 112, 119, 69, 84, 48, 105, 82, 86,
            66, 81, 45, 56, 69, 87, 50, 87, 103, 119, 22, 106, 114, 69, 109, 73, 77, 112, 86, 84,
            101, 45, 99, 114, 78, 50, 86, 76, 51, 99, 97, 72, 81, 119, 8, 108, 105, 110, 107, 110,
            111, 116, 101, 119, 1, 49, 118, 2, 4, 103, 117, 105, 100, 119, 22, 66, 67, 100, 81,
            112, 112, 119, 69, 84, 48, 105, 82, 86, 66, 81, 45, 56, 69, 87, 50, 87, 103, 8, 115,
            108, 111, 116, 71, 117, 105, 100, 119, 22, 106, 114, 69, 109, 73, 77, 112, 86, 84, 101,
            45, 99, 114, 78, 50, 86, 76, 51, 99, 97, 72, 81, 119, 22, 66, 67, 100, 81, 112, 112,
            119, 69, 84, 48, 105, 82, 86, 66, 81, 45, 56, 69, 87, 50, 87, 103, 0,
        };
        var update = new byte[] { 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 198, 182, 140, 174, 4, 1, 2, 0, 0, 5 };
        var doc         = DocNoGc(1);
        var prosemirror = doc.GetXmlFragment("prosemirror");
        doc.ApplyUpdateV2(before);
        Assert.AreEqual("linknote", ((XmlElement)prosemirror.Get(0)!).NodeName);
        byte[]? binary = null;
        doc.UpdateV2 += (_, e) => binary = e.Update;
        doc.ApplyUpdateV2(update);
        Assert.IsNotNull(binary);
        Y.DecodeUpdateV2(binary!);
        Assert.AreEqual(0, prosemirror.Length);
    }

    // yrs: merge_pending_updates
    [TestMethod]
    public void MergePendingUpdates()
    {
        var d0      = Doc(0);
        var updates = new List<byte[]>();
        d0.Update += (_, e) => updates.Add(e.Update);
        var txt = d0.GetText("textBlock");
        foreach (var c in new[] { "r", "o", "n", "e", "n" })
        {
            txt.ApplyDelta(new[] { Ins(c) });
        }
        var d1 = Doc(1);
        d1.ApplyUpdate(updates[0]);
        var u1 = d1.EncodeStateAsUpdate();
        var d2 = Doc(2);
        d2.ApplyUpdate(u1);
        d2.ApplyUpdate(updates[1]);
        var u2 = d2.EncodeStateAsUpdate();
        var d3 = Doc(3);
        d3.ApplyUpdate(u2);
        d3.ApplyUpdate(updates[3]);
        var u3 = d3.EncodeStateAsUpdate();
        var d4 = Doc(4);
        d4.ApplyUpdate(u3);
        d4.ApplyUpdate(updates[2]);
        var u4 = d4.EncodeStateAsUpdate();
        var d5 = Doc(5);
        d5.ApplyUpdate(u4);
        d5.ApplyUpdate(updates[4]);
        Assert.AreEqual("nenor", d5.GetText("textBlock").ToString());
    }

    // yrs: apply_update_filling_partial_skip
    [TestMethod]
    public void ApplyUpdateFillingPartialSkip()
    {
        var updates = new[]
        {
            new byte[] { 1, 1, 182, 144, 197, 137, 4, 0, 4, 1, 1, 116, 1, 109, 0 },
            new byte[] { 1, 1, 152, 176, 234, 156, 14, 3, 132, 152, 176, 234, 156, 14, 0, 1, 99, 0 },
            new byte[] { 0, 1, 152, 176, 234, 156, 14, 1, 2, 1 },
            new byte[] { 1, 1, 152, 176, 234, 156, 14, 0, 4, 1, 1, 116, 1, 112, 0 },
            new byte[] { 1, 1, 152, 176, 234, 156, 14, 1, 68, 152, 176, 234, 156, 14, 0, 1, 100, 0 },
            new byte[] { 1, 1, 152, 176, 234, 156, 14, 2, 196, 152, 176, 234, 156, 14, 1, 152, 176, 234, 156, 14, 0, 1, 110, 0 },
            new byte[] { 1, 1, 182, 144, 197, 137, 4, 1, 132, 182, 144, 197, 137, 4, 0, 1, 100, 0 },
        };
        var doc = new Doc();
        doc.Transact(_ =>
        {
            foreach (var u in updates)
            {
                doc.ApplyUpdate(u);
            }
        });
        Assert.AreEqual("mddpc", doc.GetText("t").ToString());
    }

    // yrs: apply_update_filling_middle_of_skip
    [TestMethod]
    public void ApplyUpdateFillingMiddleOfSkip()
    {
        var d = Doc(100);
        d.Transact(_ =>
        {
            d.GetText("t").Insert(0, "P");
            d.GetText("t").Insert(1, "Q");
        });
        var dState  = d.EncodeStateAsUpdate();
        var c       = Doc(1);
        c.ApplyUpdate(dState);
        var updates = new List<byte[]>();
        c.Update += (_, e) => updates.Add(e.Update);
        var txt = c.GetText("t");
        txt.Insert(1, "a");
        txt.Insert(2, "b");
        txt.Insert(1, "c");
        txt.Insert(5, "d");
        var msgs = new List<byte[]> { dState };
        msgs.AddRange(updates);
        string Apply(int[] order)
        {
            var doc = new Doc();
            doc.Transact(_ =>
            {
                foreach (var i in order)
                {
                    doc.ApplyUpdate(msgs[i]);
                }
            });
            return doc.GetText("t").ToString();
        }
        var causal = Apply(new[] { 0, 1, 2, 3, 4 });
        Assert.AreEqual("PcabQd", causal);
        Assert.AreEqual(causal, Apply(new[] { 0, 1, 4, 3, 2 }), "filling the middle of a gap dropped a block");
    }

    // yrs: apply_update_pending_delete_set_not_lost
    [TestMethod]
    public void ApplyUpdatePendingDeleteSetNotLost()
    {
        static string ApplyUpdates(params byte[][] updates)
        {
            var doc = new Doc(new DocOptions { GC = false });
            doc.Transact(_ =>
            {
                foreach (var u in updates)
                {
                    doc.ApplyUpdate(u);
                }
            });
            return doc.GetText("t").ToString();
        }
        var a = new byte[] { 1, 1, 174, 156, 239, 251, 3, 0, 4, 1, 1, 116, 1, 124, 0 };
        var b = new byte[] { 1, 1, 174, 156, 239, 251, 3, 1, 68, 174, 156, 239, 251, 3, 0, 1, 71, 0 };
        var d = new byte[] { 0, 1, 174, 156, 239, 251, 3, 1, 1, 1 };
        var e = new byte[] { 0, 1, 174, 156, 239, 251, 3, 1, 0, 1 };
        Assert.AreEqual("", ApplyUpdates(a, b, d, e));
        Assert.AreEqual("", ApplyUpdates(b, d, e, a));
        Assert.AreEqual("", ApplyUpdates(d, b, e, a));
    }

    // yrs: issue_763. yrs integrates the blocks behind a skip; Yjs keeps them pending until clocks 0..69 arrive.
    [TestMethod]
    public void Issue763()
    {
        var ahead  = new byte[] { 1, 1, 1, 102, 1, 1, 2, (byte)'f', 1, 1, 1, 1, 1, 1, 63 };
        var around = new byte[] { 1, 1, 1, 70, 1, 1, 2, (byte)'f', 1, 0xc4, 1, 1, 1, 0 };
        var doc    = Doc(1);
        doc.Transact(_ =>
        {
            doc.ApplyUpdate(ahead);
            doc.ApplyUpdate(around);
        });
        Assert.IsTrue(doc.HasPendingUpdates);
        CollectionAssert.AreEqual(new[] { new KeyValuePair<ulong, uint>(1, 69) }, doc.PendingMissing.ToArray());
        Assert.AreEqual(0, doc.StateVector.Count);
        // the pending content is still part of the encoded state, so it is not lost
        var copy = new Doc();
        copy.ApplyUpdate(doc.EncodeStateAsUpdate());
        Assert.IsTrue(copy.HasPendingUpdates);
    }

    private static byte[] GcUpdate(params (uint Start, uint Len)[] ranges)
    {
        var blocks = new List<(byte Info, uint Len)>();
        var clock  = ranges[0].Start;
        foreach (var (start, len) in ranges)
        {
            if (start > clock)
            {
                blocks.Add((10, start - clock));
            }
            blocks.Add((0, len));
            clock = start + len;
        }
        var enc = new YjsSharp.Encoding.Encoder();
        enc.WriteVarUInt(1);
        enc.WriteVarUInt((ulong)blocks.Count);
        enc.WriteVarUInt(1);
        enc.WriteVarUInt(ranges[0].Start);
        foreach (var (info, len) in blocks)
        {
            enc.WriteUInt8(info);
            enc.WriteVarUInt(len);
        }
        enc.WriteVarUInt(0);
        return enc.ToArray();
    }

    // yrs: apply_update_around_known_content (ported to Yjs semantics: content that does not start at the
    // receiver's clock is pending; once the gap is filled everything is integrated)
    [TestMethod]
    public void ApplyUpdateAroundKnownContent()
    {
        var doc = Doc(2);
        doc.ApplyUpdate(GcUpdate((102, 1)));
        doc.ApplyUpdate(GcUpdate((70, 196)));
        Assert.IsTrue(doc.HasPendingUpdates);
        doc.ApplyUpdate(GcUpdate((0, 70)));
        Assert.IsFalse(doc.HasPendingUpdates);
        Assert.AreEqual(266u, doc.StateVector[1]);
        var doc2 = Doc(2);
        doc2.ApplyUpdate(GcUpdate((80, 5), (100, 5)));
        doc2.ApplyUpdate(GcUpdate((0, 80)));
        doc2.ApplyUpdate(GcUpdate((70, 20), (95, 30)));
        // clocks 90..94 were never sent, so everything from 95 on waits for them
        Assert.IsTrue(doc2.HasPendingUpdates);
        Assert.AreEqual(90u, doc2.StateVector[1]);
        doc2.ApplyUpdate(GcUpdate((90, 5)));
        Assert.IsFalse(doc2.HasPendingUpdates);
        Assert.AreEqual(125u, doc2.StateVector[1]);
    }

    // yrs: update_state_vector_with_skips
    [TestMethod]
    public void UpdateStateVectorWithSkips()
    {
        var enc = new UpdateEncoderV1();
        enc.RestEncoder.WriteVarUInt(3);
        // client 3: item 0, skip 1, item 2
        enc.RestEncoder.WriteVarUInt(3);
        enc.WriteClient(3);
        enc.RestEncoder.WriteVarUInt(0);
        TestItem(3, 0).Write(enc, 0);
        new Skip(new ID(3, 1), 1).Write(enc, 0);
        TestItem(3, 2).Write(enc, 0);
        // client 2: item starting from 1
        enc.RestEncoder.WriteVarUInt(1);
        enc.WriteClient(2);
        enc.RestEncoder.WriteVarUInt(1);
        TestItem(2, 1).Write(enc, 0);
        // client 1: skip 0, item 1
        enc.RestEncoder.WriteVarUInt(2);
        enc.WriteClient(1);
        enc.RestEncoder.WriteVarUInt(0);
        new Skip(new ID(1, 0), 1).Write(enc, 0);
        TestItem(1, 1).Write(enc, 0);
        enc.RestEncoder.WriteVarUInt(0);
        var sv = Y.DecodeStateVector(Y.EncodeStateVectorFromUpdate(enc.ToArray()));
        CollectionAssert.AreEquivalent(new[] { new KeyValuePair<ulong, uint>(3, 1) }, sv.ToArray());
    }

    private static Item TestItem(ulong client, uint clock)
    {
        var item = new Item(new ID(client, clock), null, null, null, null, null, null, new ContentAny(new List<object?> { 0L }));
        item.ParentKey = "test";
        return item;
    }

    // yrs: test_extends - yrs-only API (Update::extends) with no Yjs counterpart; not ported.

    // yrs: empty_update_v1
    [TestMethod]
    public void EmptyUpdateV1()
    {
        CollectionAssert.AreEqual(new byte[] { 0, 0 }, new Doc().EncodeStateAsUpdate());
        CollectionAssert.AreEqual(new byte[] { 0, 0 }, Y.MergeUpdates(new[] { new byte[] { 0, 0 }, new byte[] { 0, 0 } }));
    }

    // yrs: empty_update_v2
    [TestMethod]
    public void EmptyUpdateV2()
    {
        var empty = new byte[] { 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        CollectionAssert.AreEqual(empty, new Doc().EncodeStateAsUpdateV2());
        CollectionAssert.AreEqual(empty, Y.MergeUpdatesV2(new[] { empty, empty }));
        CollectionAssert.AreEqual(empty, Y.ConvertUpdateFormatV1ToV2(new byte[] { 0, 0 }));
    }

    private static byte[] UpdateWithSkips(bool v2)
    {
        IUpdateEncoder enc = v2 ? new UpdateEncoderV2() : new UpdateEncoderV1();
        enc.RestEncoder.WriteVarUInt(1);
        enc.RestEncoder.WriteVarUInt(3);
        enc.WriteClient(1);
        enc.RestEncoder.WriteVarUInt(0);
        var hello = new Item(new ID(1, 0), null, null, null, null, null, null, new ContentString("hello"));
        hello.ParentKey = "test";
        hello.Write(enc, 0);
        new Skip(new ID(1, 5), 3).Write(enc, 0);
        new Item(new ID(1, 8), null, new ID(1, 7), null, null, null, null, new ContentString("world")).Write(enc, 0);
        enc.RestEncoder.WriteVarUInt(0);
        return enc.ToArray();
    }

    // yrs: update_v2_with_skips
    [TestMethod]
    public void UpdateV2WithSkips()
    {
        var v1 = UpdateWithSkips(false);
        var v2 = UpdateWithSkips(true);
        CollectionAssert.AreEqual(v2, Y.ConvertUpdateFormatV1ToV2(v1));
        CollectionAssert.AreEqual(v1, Y.ConvertUpdateFormatV2ToV1(v2));
        var structs = Y.DecodeUpdateV2(v2).Structs;
        Assert.AreEqual(3, structs.Count);
        Assert.IsInstanceOfType(structs[1], typeof(Skip));
    }

    // yrs: pending_update_check
    [TestMethod]
    public void PendingUpdateCheck()
    {
        var expected = UpdateWithSkips(false);
        var doc      = Doc(2);
        var txt      = doc.GetText("test");
        doc.ApplyUpdate(expected);
        Assert.AreEqual("hello", txt.ToString());
        Assert.IsTrue(doc.HasPendingUpdates);
        var state = doc.EncodeStateAsUpdate();
        // the pending update is included: applying the state elsewhere gives the same document
        var other = new Doc();
        other.ApplyUpdate(state);
        Assert.AreEqual("hello", other.GetText("test").ToString());
        Assert.IsTrue(other.HasPendingUpdates);
        var pending = doc.PrunePending();
        Assert.IsNotNull(pending);
        Assert.IsFalse(doc.HasPendingUpdates);
        var pruned = doc.EncodeStateAsUpdate();
        CollectionAssert.AreNotEqual(state, pruned);
        CollectionAssert.AreEqual(state, Y.MergeUpdates(new[] { pruned, pending! }));
    }

    // yrs: malformed_input::invalid_utf8_in_string_content. yrs rejects invalid UTF-8; Yjs (TextDecoder) replaces
    // it with U+FFFD, and so does this port.
    [TestMethod]
    public void InvalidUtf8InStringContentIsReplaced()
    {
        const string text = "hello world, this is a yrs document";
        var doc = new Doc();
        doc.GetText("t").Insert(0, text);
        var bytes  = doc.EncodeStateAsUpdate();
        var offset = IndexOf(bytes, System.Text.Encoding.UTF8.GetBytes(text));
        bytes[offset] = 0xff;
        var d2 = new Doc();
        d2.ApplyUpdate(bytes);
        Assert.AreEqual("�" + text.Substring(1), d2.GetText("t").ToString());
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }
        return -1;
    }

    // yrs: malformed_input::no_single_byte_corruption_aborts
    [TestMethod]
    public void NoSingleByteCorruptionAborts()
    {
        var doc = new Doc();
        doc.GetText("t").Insert(0, "hello world, this is a yrs document");
        foreach (var v2 in new[] { false, true })
        {
            var good = v2 ? doc.EncodeStateAsUpdateV2() : doc.EncodeStateAsUpdate();
            for (var i = 0; i < good.Length; i++)
            {
                foreach (var xor in new byte[] { 0x01, 0x7f, 0xff })
                {
                    var bad = (byte[])good.Clone();
                    bad[i] ^= xor;
                    try
                    {
                        var d = new Doc();
                        if (v2)
                        {
                            d.ApplyUpdateV2(bad);
                        }
                        else
                        {
                            d.ApplyUpdate(bad);
                        }
                        d.EncodeStateAsUpdate();
                    }
                    catch (FormatException)
                    {
                    }
                }
            }
        }
    }

    // yrs: malformed_input::empty_client_listing
    [TestMethod]
    public void EmptyClientListing()
    {
        var doc  = Doc(5);
        var text = doc.GetText("text");
        text.Insert(0, "a");
        var noBlocks = new byte[] { 1, 0, 5, 0, 0 };
        var zeroLen  = new byte[] { 1, 1, 5, 0, 0x04, 1, 4, (byte)'t', (byte)'e', (byte)'x', (byte)'t', 0, 0 };
        doc.ApplyUpdate(noBlocks);
        doc.ApplyUpdate(zeroLen);
        Assert.AreEqual("a", text.ToString());
    }

    // yrs: malformed_input::zero_length_gc_block_is_rejected
    [TestMethod]
    public void ZeroLengthGcBlockIsRejected()
    {
        Assert.ThrowsException<FormatException>(() => new Doc().ApplyUpdate(new byte[] { 1, 1, 96, 0, 0, 0, 0 }));
    }

    // yrs: malformed_input::zero_length_skip_block_is_rejected
    [TestMethod]
    public void ZeroLengthSkipBlockIsRejected()
    {
        Assert.ThrowsException<FormatException>(() => new Doc().ApplyUpdate(new byte[] { 1, 1, 96, 0, 10, 0, 0 }));
    }
}
