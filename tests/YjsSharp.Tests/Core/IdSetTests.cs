using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YjsSharp;
using YjsSharp.Structs;
using static YjsSharp.Tests.Core.T;

namespace YjsSharp.Tests.Core;

/// <summary>
/// Port of the unit tests in yrs/src/id_set.rs and the set-valued tests of yrs/src/ids.rs (IdRanges), against
/// <see cref="DeleteSet"/>. The attributed id maps of ids.rs / id_map.rs belong to yrs' attribution feature, which
/// has no Yjs 13.6 counterpart, and are not ported.
/// </summary>
[TestClass]
public class IdSetTests
{
    private const ulong C = 1;

    private static DeleteSet R(params (uint Start, uint End)[] ranges) => Set((C, ranges));

    private static DeleteSet Set(params (ulong Client, (uint Start, uint End)[] Ranges)[] entries)
    {
        var ds = new DeleteSet();
        foreach (var (client, ranges) in entries)
        {
            foreach (var (start, end) in ranges)
            {
                ds.Insert(client, start, end - start);
            }
        }
        return ds;
    }

    private static void AreEqual(DeleteSet expected, DeleteSet actual)
    {
        Assert.IsTrue(DeleteSet.Equal(expected, actual), $"expected {expected} but was {actual}");
    }

    // yrs: decode_rejects_range_length_amplification
    [TestMethod]
    public void DecodeRejectsRangeLengthAmplification()
    {
        var adversarial = new byte[] { 0x01, 0x01, 0xFF, 0xFF, 0xFF, 0x7A };
        Assert.ThrowsExactly<YjsSharp.Encoding.EndOfDataException>(() => DeleteSet.Decode(adversarial));
    }

    // yrs: id_range_merge_continous
    [TestMethod]
    public void IdRangeMergeContinuous()
    {
        var a = R((0, 5));
        a.Merge(R((2, 4)));
        AreEqual(R((0, 5)), a);
        a = R((0, 5));
        a.Merge(R((4, 9)));
        AreEqual(R((0, 9)), a);
        a = R((0, 5));
        a.Merge(R((5, 9)));
        AreEqual(R((0, 9)), a);
        a = R((0, 4));
        a.Merge(R((6, 9)));
        AreEqual(R((0, 4), (6, 9)), a);
    }

    // yrs: id_range_merge_canonical
    [TestMethod]
    public void IdRangeMergeCanonical()
    {
        var a = R((0, 3));
        a.Merge(new DeleteSet());
        AreEqual(R((0, 3)), a);
        a = new DeleteSet();
        a.Merge(R((1, 5)));
        AreEqual(R((1, 5)), a);
        a = R((5, 7));
        a.Merge(R((1, 3)));
        AreEqual(R((1, 3), (5, 7)), a);
        a = R((0, 2), (4, 6), (10, 12));
        a.Merge(R((1, 5), (11, 15)));
        AreEqual(R((0, 6), (10, 15)), a);
        a = R((0, 3), (10, 12));
        a.Merge(R((3, 5), (12, 14)));
        AreEqual(R((0, 5), (10, 14)), a);
        a = R((0, 2), (6, 8));
        a.Merge(R((3, 5), (9, 11)));
        AreEqual(R((0, 2), (3, 5), (6, 8), (9, 11)), a);
    }

    // yrs: id_range_compact, id_range_push, id_range_push_adjacency_merge, ids: insert_adjacent_coalesces
    [TestMethod]
    public void IdRangeCompactAndPush()
    {
        AreEqual(R((0, 5), (6, 7)), R((0, 3), (3, 5), (6, 7)));
        var range = new DeleteSet();
        range.Insert(C, 0, 4);
        AreEqual(R((0, 4)), range);
        range.Insert(C, 4, 2);
        AreEqual(R((0, 6)), range);
        range.Insert(C, 7, 2);
        AreEqual(R((0, 6), (7, 9)), range);
    }

    // yrs: id_range_contains, ids: contains_clock_basic
    [TestMethod]
    public void IdRangeContains()
    {
        Assert.IsFalse(R((1, 3)).IsDeleted(new ID(C, 0)));
        Assert.IsTrue(R((1, 3)).IsDeleted(new ID(C, 1)));
        Assert.IsTrue(R((1, 3)).IsDeleted(new ID(C, 2)));
        Assert.IsFalse(R((1, 3)).IsDeleted(new ID(C, 3)));
        var r = R((1, 3), (4, 5));
        Assert.IsFalse(r.IsDeleted(new ID(C, 0)));
        Assert.IsTrue(r.IsDeleted(new ID(C, 1)));
        Assert.IsTrue(r.IsDeleted(new ID(C, 2)));
        Assert.IsFalse(r.IsDeleted(new ID(C, 3)));
        Assert.IsTrue(r.IsDeleted(new ID(C, 4)));
        Assert.IsFalse(r.IsDeleted(new ID(C, 5)));
        Assert.IsFalse(r.IsDeleted(new ID(C, 6)));
        Assert.IsFalse(r.IsDeleted(new ID(2, 1)));
    }

    // yrs: id_range_push_out_of_order, id_range_push_front, ids: insert_non_overlapping
    [TestMethod]
    public void IdRangePushOutOfOrder()
    {
        AreEqual(R((1, 7)), R((5, 7), (1, 3), (3, 5)));
        AreEqual(R((1, 3), (5, 7)), R((5, 7), (1, 3)));
        AreEqual(R((0, 3), (5, 8), (10, 12)), R((5, 8), (0, 3), (10, 12)));
    }

    // yrs: id_range_push_skips_empty, ids: insert_empty_range_noop
    [TestMethod]
    public void IdRangePushSkipsEmpty()
    {
        var range = new DeleteSet();
        range.Insert(C, 5, 0);
        Assert.IsTrue(range.IsEmpty);
        range.Insert(C, 0, 3);
        range.Insert(C, 7, 0);
        AreEqual(R((0, 3)), range);
    }

    // yrs: id_range_push_chain_absorb, ids: insert_spanning_multiple_coalesces, insert_overlapping_coalesces
    [TestMethod]
    public void IdRangePushChainAbsorb()
    {
        AreEqual(R((0, 10)), R((0, 2), (4, 6), (8, 10), (1, 9)));
        AreEqual(R((0, 12)), R((0, 3), (5, 8), (10, 12), (2, 11)));
        AreEqual(R((0, 8)), R((0, 5), (3, 8)));
    }

    // yrs: id_range_push_subset_of_last, ids: insert_contained_coalesces
    [TestMethod]
    public void IdRangePushSubsetOfLast()
    {
        AreEqual(R((0, 10)), R((0, 10), (5, 7)));
        AreEqual(R((0, 10)), R((0, 10), (3, 10)));
        AreEqual(R((0, 10)), R((0, 10), (0, 10)));
    }

    private static DeleteSet Removed(DeleteSet r, uint start, uint end)
    {
        r.Remove(C, start, end - start);
        return r;
    }

    // yrs: id_range_remove, ids: remove_middle_splits / remove_prefix / remove_suffix / remove_entire / remove_beyond
    [TestMethod]
    public void IdRangeRemove()
    {
        AreEqual(R((0, 10)), Removed(R((0, 10)), 5, 5));
        AreEqual(R((0, 5)), Removed(R((0, 5)), 10, 15));
        AreEqual(R((0, 5), (10, 15)), Removed(R((0, 5), (10, 15)), 5, 10));
        AreEqual(R((0, 3), (7, 10)), Removed(R((0, 10)), 3, 7));
        AreEqual(R((3, 10)), Removed(R((0, 10)), 0, 3));
        AreEqual(R((0, 7)), Removed(R((0, 10)), 7, 10));
        AreEqual(new DeleteSet(), Removed(R((0, 10)), 0, 10));
        AreEqual(R((0, 2), (13, 15)), Removed(R((0, 3), (6, 10), (12, 15)), 2, 13));
        AreEqual(new DeleteSet(), Removed(R((0, 3), (6, 10), (12, 15)), 0, 15));
        AreEqual(R((0, 5), (10, 12)), Removed(R((0, 5), (10, 15)), 12, 100));
        AreEqual(R((7, 10)), Removed(R((5, 10)), 0, 7));
    }

    // yrs: id_set_remove
    [TestMethod]
    public void IdSetRemove()
    {
        var set = Set((1, new[] { (0u, 10u) }));
        set.Remove(2, 0, 5);
        AreEqual(Set((1, new[] { (0u, 10u) })), set);
        set = Set((1, new[] { (0u, 10u) }));
        set.Remove(1, 3, 4);
        AreEqual(Set((1, new[] { (0u, 3u), (7u, 10u) })), set);
        set = Set((1, new[] { (0u, 10u) }), (2, new[] { (0u, 5u) }));
        set.Remove(1, 0, 10);
        AreEqual(Set((2, new[] { (0u, 5u) })), set);
        set = Set((1, new[] { (0u, 10u) }));
        set.Remove(1, 5, 0);
        AreEqual(Set((1, new[] { (0u, 10u) })), set);
    }

    // yrs: id_range_subset_of
    [TestMethod]
    public void IdRangeSubsetOf()
    {
        Assert.IsTrue(R((1, 2)).IsSubsetOf(R((1, 2))));
        Assert.IsTrue(R((1, 2)).IsSubsetOf(R((0, 2))));
        Assert.IsTrue(R((1, 2)).IsSubsetOf(R((1, 3))));
        Assert.IsTrue(R((1, 2), (3, 4)).IsSubsetOf(R((1, 4))));
        Assert.IsTrue(R((1, 2), (3, 4), (5, 6)).IsSubsetOf(R((1, 2), (3, 6))));
        Assert.IsTrue(R((3, 4), (5, 6)).IsSubsetOf(R((0, 1), (3, 6))));
        Assert.IsTrue(R((3, 4), (5, 6)).IsSubsetOf(R((3, 4), (5, 6))));
        Assert.IsFalse(R((1, 3)).IsSubsetOf(R((0, 2))));
        Assert.IsFalse(R((1, 3)).IsSubsetOf(R((1, 2))));
        Assert.IsFalse(R((1, 3)).IsSubsetOf(R((2, 3))));
        Assert.IsFalse(R((1, 3)).IsSubsetOf(R((2, 4))));
        Assert.IsFalse(R((1, 2), (3, 4)).IsSubsetOf(R((1, 3))));
        Assert.IsFalse(R((1, 2), (3, 6)).IsSubsetOf(R((1, 2), (3, 5))));
        Assert.IsFalse(R((1, 2), (3, 6)).IsSubsetOf(R((1, 2), (4, 7))));
    }

    private static DeleteSet Excluded(DeleteSet a, DeleteSet b)
    {
        a.Exclude(b);
        return a;
    }

    private static DeleteSet Intersected(DeleteSet a, DeleteSet b)
    {
        a.Intersect(b);
        return a;
    }

    // yrs: id_range_exclude, ids: exclude_partial
    [TestMethod]
    public void IdRangeExclude()
    {
        AreEqual(R((2, 4)), Excluded(R((0, 4)), R((0, 2))));
        AreEqual(R((0, 2)), Excluded(R((0, 4)), R((2, 4))));
        AreEqual(R((0, 1), (3, 4)), Excluded(R((0, 4)), R((1, 3))));
        AreEqual(R((0, 1), (3, 4), (5, 10)), Excluded(R((0, 10)), R((1, 3), (4, 5))));
        AreEqual(R((0, 3), (8, 10)), Excluded(R((0, 4), (5, 6), (7, 10)), R((3, 8))));
        AreEqual(R((0, 3), (9, 10)), Excluded(R((0, 4), (7, 10)), R((3, 5), (6, 9))));
        AreEqual(R((0, 2), (3, 4), (7, 9)), Excluded(R((0, 4), (7, 10)), R((2, 3), (5, 6), (9, 10))));
    }

    // yrs: id_range_intersect, ids: intersect_partial / intersect_no_overlap
    [TestMethod]
    public void IdRangeIntersect()
    {
        AreEqual(R((5, 10)), Intersected(R((0, 10)), R((5, 15))));
        AreEqual(R((3, 5), (10, 12)), Intersected(R((0, 5), (10, 15)), R((3, 12))));
        AreEqual(new DeleteSet(), Intersected(R((0, 5)), R((10, 15))));
        AreEqual(R((2, 4), (6, 7)), Intersected(R((1, 4), (6, 9)), R((2, 7))));
        AreEqual(R((2, 8)), Intersected(R((2, 8)), R((0, 10))));
        AreEqual(R((5, 10)), Intersected(R((5, 15)), R((0, 10))));
        AreEqual(R((3, 5), (10, 12), (22, 25)), Intersected(R((0, 5), (10, 15), (20, 25)), R((3, 12), (22, 30))));
        AreEqual(R((5, 10)), Intersected(R((5, 10)), R((5, 10))));
        AreEqual(R((4, 5)), Intersected(R((0, 5)), R((4, 10))));
        AreEqual(new DeleteSet(), Intersected(R((0, 5)), R((5, 10))));
    }

    // yrs: id_range_encode_decode, id_set_encode_decode
    [TestMethod]
    public void IdSetEncodeDecode()
    {
        foreach (var set in new[] { R((0, 4)), R((1, 4), (5, 8)) })
        {
            AreEqual(set, DeleteSet.Decode(set.Encode()));
            AreEqual(set, DeleteSet.DecodeV2(set.EncodeV2()));
        }
        var s = new DeleteSet();
        s.Insert(124, 0, 1);
        s.Insert(1337, 0, 12);
        s.Insert(124, 1, 3);
        AreEqual(s, DeleteSet.Decode(s.Encode()));
        AreEqual(s, DeleteSet.DecodeV2(s.EncodeV2()));
    }

    // yrs: id_set_exclude
    [TestMethod]
    public void IdSetExclude()
    {
        AreEqual(
            Set((1, new[] { (0u, 10u) }), (2, new[] { (3u, 5u) })),
            Excluded(Set((1, new[] { (0u, 10u) }), (2, new[] { (0u, 5u) })), Set((2, new[] { (0u, 3u) }), (3, new[] { (0u, 100u) }))));
        AreEqual(Set((1, new[] { (0u, 3u), (7u, 10u) })), Excluded(Set((1, new[] { (0u, 10u) })), Set((1, new[] { (3u, 7u) }))));
        AreEqual(Set((2, new[] { (0u, 5u) })), Excluded(Set((1, new[] { (0u, 5u) }), (2, new[] { (0u, 5u) })), Set((1, new[] { (0u, 5u) }))));
        AreEqual(Set((1, new[] { (0u, 10u) })), Excluded(Set((1, new[] { (0u, 10u) })), new DeleteSet()));
    }

    // yrs: id_set_intersect
    [TestMethod]
    public void IdSetIntersect()
    {
        AreEqual(Set((1, new[] { (5u, 10u) })), Intersected(Set((1, new[] { (0u, 10u) }), (2, new[] { (0u, 5u) })), Set((1, new[] { (5u, 15u) }))));
        AreEqual(Set((1, new[] { (3u, 7u) })), Intersected(Set((1, new[] { (0u, 10u) })), Set((1, new[] { (3u, 7u) }), (2, new[] { (0u, 100u) }))));
        AreEqual(Set((2, new[] { (1u, 4u) })), Intersected(Set((1, new[] { (0u, 5u) }), (2, new[] { (0u, 5u) })), Set((1, new[] { (10u, 20u) }), (2, new[] { (1u, 4u) }))));
        Assert.IsTrue(Intersected(Set((1, new[] { (0u, 10u) })), new DeleteSet()).IsEmpty);
    }

    private static List<(bool Deleted, ID Id, uint Len, string Content)> DeletedBlocks(Doc doc, DeleteSet ds, int limit)
    {
        var blocks = new List<(bool, ID, uint, string)>();
        doc.Transact(t =>
        {
            StructStore.IterateDeletedStructs(t, ds, s =>
            {
                if (blocks.Count < limit && s is Item item && item.Content is ContentString str)
                {
                    blocks.Add((item.Deleted, item.Id, item.Length, str.Value));
                }
            });
        });
        return blocks;
    }

    // yrs: deleted_blocks
    [TestMethod]
    public void DeletedBlocksIteration()
    {
        var d1 = DocNoGc(1);
        var t1 = d1.GetText("test");
        var d2 = DocNoGc(2);
        var t2 = d2.GetText("test");
        t1.Insert(0, "aaaaa");
        t1.Insert(0, "bbb");
        Exchange(d1, d2);
        t2.Insert(4, "cccc");
        Exchange(d1, d2);
        t1.Delete(2, 2);
        t1.Delete(3, 1);
        t1.Delete(3, 1);
        t1.Delete(7, 1);
        var s      = Y.Snapshot(d1);
        var blocks = DeletedBlocks(d1, s.DeleteSet, 5);
        CollectionAssert.AreEquivalent(new[]
        {
            (true, new ID(1, 0), 1u, "a"),
            (true, new ID(1, 4), 1u, "a"),
            (true, new ID(1, 7), 1u, "b"),
            (true, new ID(2, 1), 2u, "cc"),
        }, blocks);
    }

    // yrs: deleted_blocks2
    [TestMethod]
    public void DeletedBlocks2()
    {
        var ds  = new DeleteSet();
        var doc = Doc(1);
        var txt = doc.GetText("test");
        txt.Insert(0, "testab");
        ds.Insert(1, 5, 1);
        var blocks = DeletedBlocks(doc, ds, 10);
        Assert.AreEqual(1, blocks.Count);
        Assert.AreEqual(new ID(1, 5), blocks[0].Id);
        Assert.AreEqual(1u, blocks[0].Len);
    }

    // ids.rs: iter_blocks_* (the delete set slices the blocks it covers)
    [TestMethod]
    public void IterBlocksTrimsToTheRanges()
    {
        var doc = Doc(1);
        var txt = doc.GetText("t");
        txt.Insert(0, "abcdefghij");
        CollectionAssert.AreEqual(new[] { (new ID(1, 3), 4u) }, DeletedBlocks(doc, R((3, 7)), 10).Select(b => (b.Id, b.Len)).ToArray());
        CollectionAssert.AreEqual(new[] { (new ID(1, 2), 3u) }, DeletedBlocks(doc, R((2, 5)), 10).Select(b => (b.Id, b.Len)).ToArray());
        CollectionAssert.AreEqual(new[] { (new ID(1, 0), 2u), (new ID(1, 5), 3u) }, DeletedBlocks(doc, R((0, 2), (5, 8)), 10).Select(b => (b.Id, b.Len)).ToArray());
        CollectionAssert.AreEqual(new[] { (new ID(1, 0), 10u) }, DeletedBlocks(doc, R((0, 10)), 10).Select(b => (b.Id, b.Len)).OrderBy(x => x.Item1.Clock).ToArray()
            .Aggregate(new List<(ID, uint)>(), (acc, x) =>
            {
                if (acc.Count > 0 && acc[^1].Item1.Clock + acc[^1].Item2 == x.Item1.Clock)
                {
                    acc[^1] = (acc[^1].Item1, acc[^1].Item2 + x.Item2);
                }
                else
                {
                    acc.Add(x);
                }
                return acc;
            }).ToArray());
    }

    private static HashSet<(ulong, uint)> Model(DeleteSet ds)
    {
        var set = new HashSet<(ulong, uint)>();
        foreach (var kv in ds.Entries)
        {
            foreach (var r in kv.Value)
            {
                for (var c = r.Clock; c < r.End; c++)
                {
                    set.Add((kv.Key, c));
                }
            }
        }
        return set;
    }

    private static DeleteSet RandomSet(Random rng, int clients, uint clockRange)
    {
        var ds = new DeleteSet();
        var n  = rng.Next(0, 6);
        for (var i = 0; i < n; i++)
        {
            var start = (uint)rng.Next(0, (int)clockRange);
            var len   = (uint)rng.Next(1, 6);
            ds.Insert((ulong)rng.Next(0, clients), start, len);
        }
        return ds;
    }

    private static void AssertNormalized(DeleteSet ds)
    {
        foreach (var kv in ds.Entries)
        {
            Assert.IsTrue(kv.Value.Count > 0, "empty client entry");
            for (var i = 1; i < kv.Value.Count; i++)
            {
                Assert.IsTrue(kv.Value[i - 1].End < kv.Value[i].Clock, $"not normalized: {ds}");
            }
        }
    }

    // ids.rs: repeat_merging_mutliple_maps, repeat_random_diffing(_2), repeat_random_deletes, repeat_random_intersects
    [TestMethod]
    public void RandomSetAlgebraMatchesModel()
    {
        var rng = new Random(42);
        for (var iter = 0; iter < 500; iter++)
        {
            var a = RandomSet(rng, 3, 30);
            var b = RandomSet(rng, 3, 30);
            var ma = Model(a);
            var mb = Model(b);

            var union = a.Clone();
            union.Merge(b);
            AssertNormalized(union);
            Assert.IsTrue(ma.Union(mb).ToHashSet().SetEquals(Model(union)), "merge");

            var diff = a.Clone();
            diff.Exclude(b);
            AssertNormalized(diff);
            Assert.IsTrue(ma.Except(mb).ToHashSet().SetEquals(Model(diff)), "exclude");

            var inter = a.Clone();
            inter.Intersect(b);
            AssertNormalized(inter);
            Assert.IsTrue(ma.Intersect(mb).ToHashSet().SetEquals(Model(inter)), "intersect");

            Assert.AreEqual(ma.IsSubsetOf(mb), a.IsSubsetOf(b), "subset");

            var removed = a.Clone();
            var client  = (ulong)rng.Next(0, 3);
            var start   = (uint)rng.Next(0, 30);
            var len     = (uint)rng.Next(0, 10);
            removed.Remove(client, start, len);
            AssertNormalized(removed);
            Assert.IsTrue(ma.Where(x => !(x.Item1 == client && x.Item2 >= start && x.Item2 < start + len)).ToHashSet().SetEquals(Model(removed)), "remove");

            var merged = DeleteSet.Merge(new[] { a, b });
            Assert.IsTrue(DeleteSet.Equal(union, merged), "static merge");
            AreEqual(a, DeleteSet.Decode(a.Encode()));
            AreEqual(a, DeleteSet.DecodeV2(a.EncodeV2()));
        }
    }
}
