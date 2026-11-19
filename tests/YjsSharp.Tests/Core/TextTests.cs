using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YjsSharp;
using YjsSharp.Structs;
using static YjsSharp.Tests.Core.T;

namespace YjsSharp.Tests.Core;

/// <summary>Port of the unit tests in yrs/src/types/text.rs (test names kept in the method names).</summary>
[TestClass]
public class TextTests
{
    // yrs: insert_empty_string
    [TestMethod]
    public void InsertEmptyString()
    {
        var doc = new Doc();
        var txt = doc.GetText("test");
        doc.Transact(_ =>
        {
            Assert.AreEqual("", txt.ToString());
            txt.Insert(txt.Length, "");
            Assert.AreEqual("", txt.ToString());
            txt.Insert(txt.Length, "abc");
            txt.Insert(txt.Length, "");
            Assert.AreEqual("abc", txt.ToString());
        });
    }

    // yrs: append_single_character_blocks
    [TestMethod]
    public void AppendSingleCharacterBlocks()
    {
        var txt = new Doc().GetText("test");
        txt.Insert(0, "a");
        txt.Insert(1, "b");
        txt.Insert(2, "c");
        Assert.AreEqual("abc", txt.ToString());
    }

    // yrs: append_mutli_character_blocks
    [TestMethod]
    public void AppendMultiCharacterBlocks()
    {
        var txt = new Doc().GetText("test");
        txt.Insert(0, "hello");
        txt.Insert(5, " ");
        txt.Insert(6, "world");
        Assert.AreEqual("hello world", txt.ToString());
    }

    // yrs: prepend_single_character_blocks
    [TestMethod]
    public void PrependSingleCharacterBlocks()
    {
        var txt = new Doc().GetText("test");
        txt.Insert(0, "a");
        txt.Insert(0, "b");
        txt.Insert(0, "c");
        Assert.AreEqual("cba", txt.ToString());
    }

    // yrs: prepend_mutli_character_blocks
    [TestMethod]
    public void PrependMultiCharacterBlocks()
    {
        var txt = new Doc().GetText("test");
        txt.Insert(0, "hello");
        txt.Insert(0, " ");
        txt.Insert(0, "world");
        Assert.AreEqual("world hello", txt.ToString());
    }

    // yrs: insert_after_block
    [TestMethod]
    public void InsertAfterBlock()
    {
        var txt = new Doc().GetText("test");
        txt.Insert(0, "hello");
        txt.Insert(5, " ");
        txt.Insert(6, "world");
        txt.Insert(6, "beautiful ");
        Assert.AreEqual("hello beautiful world", txt.ToString());
    }

    // yrs: insert_inside_of_block
    [TestMethod]
    public void InsertInsideOfBlock()
    {
        var txt = new Doc().GetText("test");
        txt.Insert(0, "it was expected");
        txt.Insert(6, " not");
        Assert.AreEqual("it was not expected", txt.ToString());
    }

    // yrs: insert_concurrent_root
    [TestMethod]
    public void InsertConcurrentRoot()
    {
        var d1   = Doc(1);
        var txt1 = d1.GetText("test");
        txt1.Insert(0, "hello ");
        var d2   = Doc(2);
        var txt2 = d2.GetText("test");
        txt2.Insert(0, "world");
        var u1 = d1.EncodeStateAsUpdate(d2.EncodeStateVector());
        var u2 = d2.EncodeStateAsUpdate(d1.EncodeStateVector());
        d1.ApplyUpdate(u2);
        d2.ApplyUpdate(u1);
        Assert.AreEqual(txt1.ToString(), txt2.ToString());
        Assert.AreEqual("hello world", txt1.ToString());
    }

    // yrs: insert_concurrent_in_the_middle
    [TestMethod]
    public void InsertConcurrentInTheMiddle()
    {
        var d1   = Doc(1);
        var txt1 = d1.GetText("test");
        txt1.Insert(0, "I expect that");
        Assert.AreEqual("I expect that", txt1.ToString());
        var d2   = Doc(2);
        var txt2 = d2.GetText("test");
        d2.ApplyUpdate(d1.EncodeStateAsUpdate(d2.EncodeStateVector()));
        Assert.AreEqual("I expect that", txt2.ToString());
        txt2.Insert(1, " have");
        txt2.Insert(13, "ed");
        Assert.AreEqual("I have expected that", txt2.ToString());
        txt1.Insert(1, " didn't");
        Assert.AreEqual("I didn't expect that", txt1.ToString());
        var u1 = d1.EncodeStateAsUpdate(d2.EncodeStateVector());
        var u2 = d2.EncodeStateAsUpdate(d1.EncodeStateVector());
        d1.ApplyUpdate(u2);
        d2.ApplyUpdate(u1);
        Assert.AreEqual(txt1.ToString(), txt2.ToString());
        Assert.AreEqual("I didn't have expected that", txt1.ToString());
    }

    // yrs: append_concurrent
    [TestMethod]
    public void AppendConcurrent()
    {
        var d1   = Doc(1);
        var txt1 = d1.GetText("test");
        txt1.Insert(0, "aaa");
        var d2   = Doc(2);
        var txt2 = d2.GetText("test");
        d2.ApplyUpdate(d1.EncodeStateAsUpdate(d2.EncodeStateVector()));
        Assert.AreEqual("aaa", txt2.ToString());
        txt2.Insert(3, "bbb");
        txt2.Insert(6, "bbb");
        Assert.AreEqual("aaabbbbbb", txt2.ToString());
        txt1.Insert(3, "aaa");
        Assert.AreEqual("aaaaaa", txt1.ToString());
        var u1 = d1.EncodeStateAsUpdate(d2.EncodeStateVector());
        var u2 = d2.EncodeStateAsUpdate(d1.EncodeStateVector());
        d1.ApplyUpdate(u2);
        d2.ApplyUpdate(u1);
        Assert.AreEqual("aaaaaabbbbbb", txt1.ToString());
        Assert.AreEqual(txt1.ToString(), txt2.ToString());
    }

    // yrs: delete_single_block_start
    [TestMethod]
    public void DeleteSingleBlockStart()
    {
        var txt = new Doc().GetText("test");
        txt.Insert(0, "bbb");
        txt.Insert(0, "aaa");
        txt.Delete(0, 3);
        Assert.AreEqual(3, txt.Length);
        Assert.AreEqual("bbb", txt.ToString());
    }

    // yrs: delete_single_block_end
    [TestMethod]
    public void DeleteSingleBlockEnd()
    {
        var txt = new Doc().GetText("test");
        txt.Insert(0, "bbb");
        txt.Insert(0, "aaa");
        txt.Delete(3, 3);
        Assert.AreEqual("aaa", txt.ToString());
    }

    // yrs: delete_multiple_whole_blocks
    [TestMethod]
    public void DeleteMultipleWholeBlocks()
    {
        var txt = new Doc().GetText("test");
        txt.Insert(0, "a");
        txt.Insert(1, "b");
        txt.Insert(2, "c");
        txt.Delete(1, 1);
        Assert.AreEqual("ac", txt.ToString());
        txt.Delete(1, 1);
        Assert.AreEqual("a", txt.ToString());
        txt.Delete(0, 1);
        Assert.AreEqual("", txt.ToString());
    }

    // yrs: delete_slice_of_block
    [TestMethod]
    public void DeleteSliceOfBlock()
    {
        var txt = new Doc().GetText("test");
        txt.Insert(0, "abc");
        txt.Delete(1, 1);
        Assert.AreEqual("ac", txt.ToString());
    }

    // yrs: delete_multiple_blocks_with_slicing
    [TestMethod]
    public void DeleteMultipleBlocksWithSlicing()
    {
        var txt = new Doc().GetText("test");
        txt.Insert(0, "hello ");
        txt.Insert(6, "beautiful");
        txt.Insert(15, " world");
        txt.Delete(5, 11);
        Assert.AreEqual("helloworld", txt.ToString());
    }

    // yrs: insert_after_delete
    [TestMethod]
    public void InsertAfterDelete()
    {
        var txt = new Doc().GetText("test");
        txt.Insert(0, "hello ");
        txt.Delete(0, 5);
        txt.Insert(1, "world");
        Assert.AreEqual(" world", txt.ToString());
    }

    // yrs: concurrent_insert_delete
    [TestMethod]
    public void ConcurrentInsertDelete()
    {
        var d1   = Doc(1);
        var txt1 = d1.GetText("test");
        txt1.Insert(0, "hello world");
        var u1   = d1.EncodeStateAsUpdate();
        var d2   = Doc(2);
        var txt2 = d2.GetText("test");
        d2.ApplyUpdate(u1);
        Assert.AreEqual("hello world", txt2.ToString());
        txt1.Insert(5, " beautiful");
        txt1.Insert(21, "!");
        txt1.Delete(0, 5);
        Assert.AreEqual(" beautiful world!", txt1.ToString());
        txt2.Delete(5, 5);
        txt2.Delete(0, 1);
        txt2.Insert(0, "H");
        Assert.AreEqual("Hellod", txt2.ToString());
        var a = d1.EncodeStateAsUpdate(d2.EncodeStateVector());
        var b = d2.EncodeStateAsUpdate(d1.EncodeStateVector());
        d1.ApplyUpdate(b);
        d2.ApplyUpdate(a);
        Assert.AreEqual(txt1.ToString(), txt2.ToString());
        Assert.AreEqual("H beautifuld!", txt1.ToString());
    }

    // yrs: observer
    [TestMethod]
    public void Observer()
    {
        var                  doc   = Doc(1);
        var                  txt   = doc.GetText("text");
        IReadOnlyList<Delta>? delta = null;
        var sub = txt.Observe(e => delta = e.Delta);
        txt.Insert(0, "abcd");
        DeltaEqual(new[] { Ins("abcd") }, delta);
        txt.Delete(1, 2);
        DeltaEqual(new[] { Ret(1), Del(2) }, delta);
        var attrs = Attrs(("bold", true));
        txt.Insert(1, "e", attrs);
        DeltaEqual(new[] { Ret(1), Ins("e", attrs) }, delta);
        var nullAttrs = Attrs(("bold", null));
        txt.Format(1, 1, nullAttrs);
        // yrs reports [retain 1, retain 1 {bold: null}]; Yjs (the reference for this port) reports retain 2
        DeltaEqual(new[] { Ret(1), Ret(2, nullAttrs) }, delta);
        delta = null;
        sub.Dispose();
        txt.Insert(1, "fgh");
        Assert.IsNull(delta);
    }

    // yrs: insert_and_remove_event_changes
    [TestMethod]
    public void InsertAndRemoveEventChanges()
    {
        var                   d1    = Doc(1);
        var                   txt   = d1.GetText("text");
        IReadOnlyList<Delta>? delta = null;
        txt.Observe(e => delta = e.Delta);
        txt.Insert(0, "abcd");
        DeltaEqual(new[] { Ins("abcd") }, delta);
        txt.Delete(1, 2);
        DeltaEqual(new[] { Ret(1), Del(2) }, delta);
        txt.Insert(1, "ef");
        DeltaEqual(new[] { Ret(1), Ins("ef") }, delta);
        var d2   = Doc(2);
        var txt2 = d2.GetText("text");
        txt2.Observe(e => delta = e.Delta);
        d2.ApplyUpdate(d1.EncodeStateAsUpdate(d2.EncodeStateVector()));
        DeltaEqual(new[] { Ins("aefd") }, delta);
    }

    internal static readonly Action<Doc, Random>[] TextTransactions =
    {
        (doc, rng) =>
        {
            var ytext = doc.GetText("text");
            var pos   = rng.Between(0, ytext.Length);
            ytext.Insert(pos, rng.RandomString());
        },
        (doc, rng) =>
        {
            var ytext = doc.GetText("text");
            var len   = ytext.Length;
            if (len > 0)
            {
                var pos      = rng.Between(0, len - 1);
                var toDelete = rng.Between(2, len - pos);
                ytext.Delete(pos, toDelete);
            }
        },
    };

    // yrs: fuzzy_test_3
    [TestMethod]
    public void FuzzyTest3()
    {
        TestConnector.RunScenario(1, TextTransactions, 5, 3);
    }

    [TestMethod]
    public void FuzzyTest300()
    {
        TestConnector.RunScenario(7, TextTransactions, 5, 300);
    }

    // yrs: basic_format
    [TestMethod]
    public void BasicFormat()
    {
        var                   d1     = Doc(1);
        var                   txt1   = d1.GetText("text");
        IReadOnlyList<Delta>? delta1 = null;
        txt1.Observe(e => delta1 = e.Delta);
        var                   d2     = Doc(2);
        var                   txt2   = d2.GetText("text");
        IReadOnlyList<Delta>? delta2 = null;
        txt2.Observe(e => delta2 = e.Delta);
        var a = Attrs(("bold", true));

        byte[]? last = null;
        d1.Update += (_, e) => last = e.Update;

        byte[]? Step(Action op)
        {
            last = null;
            d1.Transact(_ => op());
            return last;
        }

        // step 1
        var update = Step(() => txt1.Insert(0, "abc", a))!;
        var expected = new[] { Ins("abc", a) };
        Assert.AreEqual("abc", txt1.ToString());
        DeltaEqual(new[] { Ins("abc", a) }, txt1.ToDelta());
        DeltaEqual(expected, delta1);
        d2.ApplyUpdate(update);
        Assert.AreEqual("abc", txt2.ToString());
        DeltaEqual(expected, delta2);

        // step 2
        update   = Step(() => txt1.Delete(0, 1))!;
        expected = new[] { Del(1) };
        Assert.AreEqual("bc", txt1.ToString());
        DeltaEqual(new[] { Ins("bc", a) }, txt1.ToDelta());
        DeltaEqual(expected, delta1);
        d2.ApplyUpdate(update);
        Assert.AreEqual("bc", txt2.ToString());
        DeltaEqual(expected, delta2);

        // step 3
        update   = Step(() => txt1.Delete(1, 1))!;
        expected = new[] { Ret(1), Del(1) };
        Assert.AreEqual("b", txt1.ToString());
        DeltaEqual(new[] { Ins("b", a) }, txt1.ToDelta());
        DeltaEqual(expected, delta1);
        d2.ApplyUpdate(update);
        Assert.AreEqual("b", txt2.ToString());
        DeltaEqual(expected, delta2);

        // step 4
        update   = Step(() => txt1.Insert(0, "z", a))!;
        expected = new[] { Ins("z", a) };
        Assert.AreEqual("zb", txt1.ToString());
        DeltaEqual(new[] { Ins("zb", a) }, txt1.ToDelta());
        DeltaEqual(expected, delta1);
        d2.ApplyUpdate(update);
        Assert.AreEqual("zb", txt2.ToString());
        DeltaEqual(expected, delta2);

        // step 5
        update   = Step(() => txt1.Insert(0, "y"))!;
        expected = new[] { Ins("y") };
        Assert.AreEqual("yzb", txt1.ToString());
        DeltaEqual(new[] { Ins("y"), Ins("zb", a) }, txt1.ToDelta());
        DeltaEqual(expected, delta1);
        d2.ApplyUpdate(update);
        Assert.AreEqual("yzb", txt2.ToString());
        DeltaEqual(expected, delta2);

        // step 6
        var b = Attrs(("bold", null));
        update   = Step(() => txt1.Format(0, 2, b))!;
        expected = new[] { Ret(1), Ret(1, b) };
        Assert.AreEqual("yzb", txt1.ToString());
        DeltaEqual(new[] { Ins("yz"), Ins("b", a) }, txt1.ToDelta());
        DeltaEqual(expected, delta1);
        d2.ApplyUpdate(update);
        Assert.AreEqual("yzb", txt2.ToString());
        DeltaEqual(expected, delta2);
    }

    // yrs: embed_with_attributes
    [TestMethod]
    public void EmbedWithAttributes()
    {
        var                   d1     = Doc(1);
        var                   txt1   = d1.GetText("text");
        IReadOnlyList<Delta>? delta1 = null;
        txt1.Observe(e => delta1 = e.Delta);
        var a1    = Attrs(("bold", true));
        var a2    = Attrs(("width", 100L));
        var embed = Map(("image", "imageSrc.png"));
        d1.Transact(_ =>
        {
            txt1.Insert(0, "ab", a1);
            txt1.InsertEmbed(1, embed, a2);
        });
        DeltaEqual(new[] { Ins("a", a1), Ins(embed, a2), Ins("b", a1) }, delta1);
        var expected = new[] { Ins("a", a1), Ins(embed, a2), Ins("b", a1) };
        DeltaEqual(expected, txt1.ToDelta());
        var updateV1 = d1.EncodeStateAsUpdate();
        var updateV2 = d1.EncodeStateAsUpdateV2();

        var d2 = new Doc();
        d2.ApplyUpdate(updateV1);
        DeltaEqual(expected, d2.GetText("text").ToDelta());

        var d3 = new Doc();
        d3.ApplyUpdateV2(updateV2);
        DeltaEqual(expected, d3.GetText("text").ToDelta());
    }

    // yrs: issue_101
    [TestMethod]
    public void Issue101()
    {
        var                   d1    = Doc(1);
        var                   txt1  = d1.GetText("text");
        IReadOnlyList<Delta>? delta = null;
        var                   attrs = Attrs(("bold", true));
        txt1.Insert(0, "abcd");
        txt1.Observe(e => delta = e.Delta);
        txt1.Format(1, 2, attrs);
        DeltaEqual(new[] { Ret(1), Ret(2, attrs) }, delta);
    }

    // yrs: yrs_delete
    [TestMethod]
    public void YrsDelete()
    {
        var doc   = new Doc();
        var text1 = string.Concat(Enumerable.Repeat("\n\t\tLorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. ", 20)) + "\n\t\t";
        var text2 = "test";
        var text  = doc.GetText("content");
        text.Insert(0, text1);
        text.Insert(100, text2);
        var count = text1.Length + text2.Length;
        var fired = false;
        text.Observe(e =>
        {
            DeltaEqual(new[] { Del(count) }, new[] { e.Delta[0] });
            fired = true;
        });
        text.Delete(0, count);
        Assert.IsTrue(fired);
        Assert.AreEqual("", text.ToString());
    }

    // yrs: text_diff_adjacent
    [TestMethod]
    public void TextDiffAdjacent()
    {
        var doc    = Doc(1);
        var txt    = doc.GetText("text");
        var attrs1 = Attrs(("a", "a"));
        var attrs2 = Attrs(("a", "a"), ("b", "b"));
        doc.Transact(_ =>
        {
            txt.Insert(0, "abc", attrs1);
            txt.Insert(3, "def", attrs2);
        });
        DeltaEqual(new[] { Ins("abc", attrs1), Ins("def", attrs2) }, txt.ToDelta());
    }

    // yrs: text_remove_4_byte_range
    [TestMethod]
    public void TextRemove4ByteRange()
    {
        var d1  = new Doc();
        var txt = d1.GetText("test");
        txt.Insert(0, "😭😊");
        var d2 = new Doc();
        Exchange(d1, d2);
        txt.Delete(0, "😭".Length);
        Assert.AreEqual("😊", txt.ToString());
        Exchange(d1, d2);
        Assert.AreEqual("😊", d2.GetText("test").ToString());
    }

    // yrs: text_remove_3_byte_range
    [TestMethod]
    public void TextRemove3ByteRange()
    {
        var d1  = new Doc();
        var txt = d1.GetText("test");
        txt.Insert(0, "⏰⏳");
        var d2 = new Doc();
        Exchange(d1, d2);
        txt.Delete(0, "⏰".Length);
        Assert.AreEqual("⏳", txt.ToString());
        Exchange(d1, d2);
        Assert.AreEqual("⏳", d2.GetText("test").ToString());
    }

    // yrs: delete_4_byte_character_from_middle
    [TestMethod]
    public void Delete4ByteCharacterFromMiddle()
    {
        var txt = new Doc().GetText("test");
        txt.Insert(0, "😊😭");
        txt.Delete("😊".Length, "😭".Length);
        Assert.AreEqual("😊", txt.ToString());
    }

    // yrs: delete_3_byte_character_from_middle_1
    [TestMethod]
    public void Delete3ByteCharacterFromMiddle1()
    {
        var txt = new Doc().GetText("test");
        txt.Insert(0, "⏰⏳");
        txt.Delete("⏰".Length, "⏳".Length);
        Assert.AreEqual("⏰", txt.ToString());
    }

    // yrs: delete_3_byte_character_from_middle_2
    [TestMethod]
    public void Delete3ByteCharacterFromMiddle2()
    {
        var doc = new Doc();
        var txt = doc.GetText("test");
        doc.Transact(_ =>
        {
            txt.Insert(0, "👯🙇‍♀️🙇‍♀️⏰👩‍❤️‍💋‍👨");
            txt.Format("👯".Length, "🙇‍♀️🙇‍♀️".Length, new Dictionary<string, object?>());
            txt.Delete("👯🙇‍♀️🙇‍♀️".Length, "⏰".Length);
            Assert.AreEqual("👯🙇‍♀️🙇‍♀️👩‍❤️‍💋‍👨", txt.ToString());
        });
    }

    // yrs: delete_3_byte_character_from_middle_after_insert_and_format
    [TestMethod]
    public void Delete3ByteCharacterFromMiddleAfterInsertAndFormat()
    {
        var doc = new Doc();
        var txt = doc.GetText("test");
        doc.Transact(_ =>
        {
            txt.Insert(0, "🙇‍♀️🙇‍♀️⏰👩‍❤️‍💋‍👨");
            txt.Insert(0, "👯");
            txt.Format("👯".Length, "🙇‍♀️🙇‍♀️".Length, new Dictionary<string, object?>());
            txt.Delete("👯🙇‍♀️🙇‍♀️".Length, "⏰".Length);
            Assert.AreEqual("👯🙇‍♀️🙇‍♀️👩‍❤️‍💋‍👨", txt.ToString());
        });
    }

    // yrs: delete_multi_byte_character_from_middle_after_insert_and_format
    [TestMethod]
    public void DeleteMultiByteCharacterFromMiddleAfterInsertAndFormat()
    {
        var doc = Doc(1);
        var txt = doc.GetText("test");
        doc.Transact(_ =>
        {
            txt.Insert(0, "❤️❤️🙇‍♀️🙇‍♀️⏰👩‍❤️‍💋‍👨👩‍❤️‍💋‍👨");
            txt.Insert(0, "👯");
            txt.Format("👯".Length, "❤️❤️🙇‍♀️🙇‍♀️⏰".Length, new Dictionary<string, object?>());
            txt.Insert("👯❤️❤️🙇‍♀️🙇‍♀️⏰".Length, "⏰");
            txt.Format("👯❤️❤️🙇‍♀️🙇‍♀️⏰⏰".Length, "👩‍❤️‍💋‍👨".Length, new Dictionary<string, object?>());
            txt.Delete("👯❤️❤️🙇‍♀️🙇‍♀️⏰⏰👩‍❤️‍💋‍👩".Length, "👩‍❤️‍💋‍👨".Length);
            Assert.AreEqual("👯❤️❤️🙇‍♀️🙇‍♀️⏰⏰👩‍❤️‍💋‍👨", txt.ToString());
        });
    }

    // yrs: insert_string_with_no_attribute
    [TestMethod]
    public void InsertStringWithNoAttribute()
    {
        var doc   = new Doc();
        var txt   = doc.GetText("test");
        var attrs = Attrs(("a", "a"));
        txt.Insert(0, "ac", attrs);
        txt.Insert(1, "b", new Dictionary<string, object?>());
        DeltaEqual(new[] { Ins("a", attrs), Ins("b"), Ins("c", attrs) }, txt.ToDelta());
    }

    // yrs: insert_empty_string_with_attributes
    [TestMethod]
    public void InsertEmptyStringWithAttributes()
    {
        var doc   = new Doc();
        var txt   = doc.GetText("test");
        var attrs = Attrs(("a", "a"));
        txt.Insert(0, "abc");
        txt.Insert(1, "");
        txt.Insert(1, "", attrs);
        Assert.AreEqual("abc", txt.ToString());
        var bin  = doc.EncodeStateAsUpdate();
        var doc2 = new Doc();
        doc2.ApplyUpdate(bin);
        Assert.AreEqual("abc", doc2.GetText("test").ToString());
    }

    private static Dictionary<string, object?> YChange(string kind, ulong client, uint clock)
    {
        return new Dictionary<string, object?> { ["type"] = kind, ["user"] = (long)client, ["clock"] = (long)clock };
    }

    private static IDictionary<string, object?> Identity(string kind, ID id) => YChange(kind, id.Client, id.Clock);

    // yrs: snapshots
    [TestMethod]
    public void Snapshots()
    {
        var doc  = Doc(1);
        var text = doc.GetText("text");
        text.Insert(0, "hello");
        var prev = Y.Snapshot(doc);
        text.Insert(5, " world");
        var next = Y.Snapshot(doc);
        var diff = text.ToDelta(next, prev, Identity);
        DeltaEqual(new[] { Ins("hello"), Ins(" world", Attrs(("ychange", YChange(YChangeKind.Added, 1, 5)))) }, diff);
    }

    // yrs: diff_with_embedded_items
    [TestMethod]
    public void DiffWithEmbeddedItems()
    {
        var doc    = new Doc();
        var text   = doc.GetText("article");
        var bold   = Attrs(("b", true));
        var italic = Attrs(("i", true));
        var array  = new YArray();
        var image  = new byte[] { 0, 0, 0, 0 };
        doc.Transact(_ =>
        {
            text.Insert(0, "hello world", italic);
            text.Format(6, 5, bold);
            text.InsertEmbed(5, image);
            text.InsertEmbed(5, array);
        });
        var italicAndBold = Attrs(("i", true), ("b", true));
        var chunks        = text.ToDelta();
        // yrs lets embeds inherit the surrounding formatting; Yjs' insertEmbed without attributes inserts them unformatted
        DeltaEqual(new[] { Ins("hello", italic), Ins(array), Ins(image), Ins(" ", italic), Ins("world", italicAndBold) }, chunks);
        Assert.AreSame(array, chunks[1].Insert);
    }

    // yrs: multi_threading. A Doc is single-writer: serialized access from several threads works.
    [TestMethod]
    public void MultiThreading()
    {
        var doc  = Doc(1);
        var gate = new object();
        var t1 = new System.Threading.Thread(() =>
        {
            var rnd = new Random(1);
            for (var i = 0; i < 10; i++)
            {
                System.Threading.Thread.Sleep(rnd.Next(1, 5));
                lock (gate)
                {
                    var txt = doc.GetText("test");
                    txt.Insert(txt.Length, "a");
                }
            }
        });
        var t2 = new System.Threading.Thread(() =>
        {
            var rnd = new Random(2);
            for (var i = 0; i < 10; i++)
            {
                System.Threading.Thread.Sleep(rnd.Next(1, 5));
                lock (gate)
                {
                    var txt = doc.GetText("test");
                    txt.Insert(txt.Length, "b");
                }
            }
        });
        t1.Start();
        t2.Start();
        t1.Join();
        t2.Join();
        Assert.AreEqual(20, doc.GetText("test").Length);
    }

    [TestMethod]
    public void ConcurrentTransactionsFromTwoThreadsFailLoudly()
    {
        var doc     = Doc(1);
        var txt     = doc.GetText("test");
        var entered = new System.Threading.ManualResetEventSlim();
        var release = new System.Threading.ManualResetEventSlim();
        var t = new System.Threading.Thread(() =>
        {
            doc.Transact(_ =>
            {
                txt.Insert(0, "a");
                entered.Set();
                release.Wait();
            });
        });
        t.Start();
        entered.Wait();
        Assert.ThrowsException<ConcurrentDocumentAccessException>(() => txt.Insert(0, "b"));
        release.Set();
        t.Join();
        txt.Insert(0, "b");
        Assert.AreEqual("ba", txt.ToString());
    }

    // yrs: multiline_format
    [TestMethod]
    public void MultilineFormat()
    {
        var doc  = Doc(1);
        var txt  = doc.GetText("text");
        var bold = Attrs(("bold", true));
        doc.Transact(_ =>
        {
            txt.Insert(0, "Test\nMulti-line\nFormatting");
            txt.ApplyDelta(new[] { Ret(4, bold), Ret(1), Ret(10, bold), Ret(1), Ret(10, bold) });
        });
        DeltaEqual(new[] { Ins("Test", bold), Ins("\n"), Ins("Multi-line", bold), Ins("\n"), Ins("Formatting", bold) }, txt.ToDelta());
    }

    // yrs: delta_with_embeds
    [TestMethod]
    public void DeltaWithEmbeds()
    {
        var doc       = Doc(1);
        var txt       = doc.GetText("text");
        var linebreak = Map(("linebreak", "s"));
        txt.ApplyDelta(new[] { Ins(linebreak) });
        DeltaEqual(new[] { Ins(linebreak) }, txt.ToDelta());
    }

    // yrs: delta_with_shared_ref
    [TestMethod]
    public void DeltaWithSharedRef()
    {
        var d1   = Doc(1);
        var txt1 = d1.GetText("text");
        byte[]? update = null;
        d1.Update += (_, e) => update = e.Update;
        txt1.ApplyDelta(new[] { Ins(new YMap(new Dictionary<string, object?> { ["key"] = "val" })) });
        var delta = txt1.ToDelta();
        var d     = (YMap)delta[0].Insert!;
        Assert.AreEqual("val", d.Get("key"));
        var triggered = false;
        var d2        = Doc(2);
        var txt2      = d2.GetText("text");
        txt2.Observe(e =>
        {
            var m = (YMap)e.Delta[0].Insert!;
            Assert.AreEqual("val", m.Get("key"));
            triggered = true;
        });
        d2.ApplyUpdate(update!);
        Assert.IsTrue(triggered, "fired event");
        var delta2 = txt2.ToDelta();
        Assert.AreEqual(1, delta2.Count);
        Assert.AreEqual("val", ((YMap)delta2[0].Insert!).Get("key"));
    }

    // yrs: delta_snapshots
    [TestMethod]
    public void DeltaSnapshots()
    {
        var doc = DocNoGc(1);
        var txt = doc.GetText("text");
        txt.ApplyDelta(new[] { Ins("abcd") });
        var snapshot1 = Y.Snapshot(doc);
        txt.ApplyDelta(new[] { Ret(1), Ins("x"), Del(1) });
        var snapshot2 = Y.Snapshot(doc);
        txt.ApplyDelta(new[] { Ret(2), Del(1), Ins("x"), Del(1) });
        DeltaEqual(new[] { Ins("abcd") }, txt.ToDelta(snapshot1));
        DeltaEqual(new[] { Ins("axcd") }, txt.ToDelta(snapshot2));
        var diff = txt.ToDelta(snapshot2, snapshot1, Identity);
        DeltaEqual(new[]
        {
            Ins("a"),
            Ins("x", Attrs(("ychange", YChange(YChangeKind.Added, 1, 4)))),
            Ins("b", Attrs(("ychange", YChange(YChangeKind.Removed, 1, 1)))),
            Ins("cd"),
        }, diff);
    }

    // yrs: snapshot_delete_after
    [TestMethod]
    public void SnapshotDeleteAfter()
    {
        var doc = DocNoGc(1);
        var txt = doc.GetText("text");
        txt.ApplyDelta(new[] { Ins("abcd") });
        var snapshot1 = Y.Snapshot(doc);
        txt.ApplyDelta(new[] { Ret(4), Ins("e") });
        DeltaEqual(new[] { Ins("abcd") }, txt.ToDelta(snapshot1));
    }

    // yrs: empty_delta_chunks
    [TestMethod]
    public void EmptyDeltaChunks()
    {
        var doc = Doc(1);
        var txt = doc.GetText("text");
        txt.ApplyDelta(new[] { Ins("a"), Ins("", Attrs(("bold", true))), Ins("b") });
        Assert.AreEqual("ab", txt.ToString());
        var bin  = doc.EncodeStateAsUpdate();
        var doc2 = Doc(2);
        doc2.ApplyUpdate(bin);
        Assert.AreEqual("ab", doc2.GetText("text").ToString());
    }

    private static (Doc Local, Snapshot Snapshot) PartiallySynced(int total, int synced)
    {
        var remote = DocNoGc(1);
        var rtxt   = remote.GetText("text");
        var local  = Doc(2);
        byte[]? update = null;
        remote.Update += (_, e) => update = e.Update;
        for (var i = 0; i < total; i++)
        {
            rtxt.Insert(0, "a");
            if (i < synced)
            {
                local.ApplyUpdate(update!);
            }
        }
        return (local, Y.Snapshot(remote));
    }

    private static void AssertPartialHistory(int total, int synced)
    {
        var (local, snapshot) = PartiallySynced(total, synced);
        var txt  = local.GetText("text");
        var diff = txt.ToDelta(snapshot);
        var text = string.Concat(diff.Select(d => (string)d.Insert!));
        Assert.AreEqual(new string('a', synced), text);
    }

    // yrs: diff_range_with_snapshot_ahead_single_block
    [TestMethod]
    public void DiffRangeWithSnapshotAheadSingleBlock() => AssertPartialHistory(6, 1);

    // yrs: diff_range_with_snapshot_ahead_many_blocks
    [TestMethod]
    public void DiffRangeWithSnapshotAheadManyBlocks() => AssertPartialHistory(20, 3);

    // yrs: apply_update_with_missing_predecessors
    [TestMethod]
    public void ApplyUpdateWithMissingPredecessors()
    {
        var remote  = Doc(1);
        var rtxt    = remote.GetText("text");
        var updates = new List<byte[]>();
        remote.Update += (_, e) => updates.Add(e.Update);
        for (var i = 0; i < 6; i++)
        {
            rtxt.Insert(0, "a");
        }
        var local = Doc(2);
        var ltxt  = local.GetText("text");
        for (var i = updates.Count - 1; i >= 0; i--)
        {
            local.ApplyUpdate(updates[i]);
        }
        Assert.AreEqual("aaaaaa", ltxt.ToString());
    }

    // yrs: utf16_split_surrogate_pair
    [TestMethod]
    public void Utf16SplitSurrogatePair()
    {
        var cases = new (string Expected, Action<Text> Op)[]
        {
            ("a�b", t => t.Delete(2, 1)),
            ("a�b", t => t.Delete(1, 1)),
            ("a�", t => t.Delete(2, 2)),
            ("a�Z�b", t => t.Insert(2, "Z")),
        };
        foreach (var (expected, op) in cases)
        {
            var d1 = Doc(1);
            var d2 = Doc(2);
            var t1 = d1.GetText("text");
            var t2 = d2.GetText("text");
            d1.Transact(_ =>
            {
                t1.Insert(0, "a😀b");
                op(t1);
                Assert.AreEqual(expected, t1.ToString());
                Assert.AreEqual(expected.Length, t1.Length);
            });
            Exchange(d1, d2);
            Assert.AreEqual(expected, t2.ToString());
        }
    }

    // yrs: utf16_remove_low_surrogate_at_end
    [TestMethod]
    public void Utf16RemoveLowSurrogateAtEnd()
    {
        var doc  = Doc(1);
        var text = doc.GetText("text");
        doc.Transact(_ =>
        {
            text.Insert(0, "a😀");
            text.Delete(2, 1);
            Assert.AreEqual("a�", text.ToString());
        });
    }

    private static void AssertItemLengthsMatchContent(TextBase text)
    {
        for (var item = GetStart(text); item != null; item = item.RightItem)
        {
            Assert.AreEqual(item.Length, item.Content.Length, $"item {item}: len differs from its content's UTF-16 length");
        }
    }

    private static Item? GetStart(AbstractType t) => t._start;

    // yrs: utf16_item_len_matches_content
    [TestMethod]
    public void Utf16ItemLenMatchesContent()
    {
        var unit = "aé😀あ";
        var d1   = Doc(1);
        var d2   = Doc(2);
        var t1   = d1.GetText("text");
        var t2   = d2.GetText("text");
        d1.Transact(_ =>
        {
            t1.Insert(0, string.Concat(Enumerable.Repeat(unit, 10)));
            t1.Insert(4, "x");
            t1.Insert(9, "x");
            t1.Insert(1, "😀");
            t1.Delete(20, 7);
            t1.Delete(12, 1);
        });
        AssertItemLengthsMatchContent(t1);
        for (var i = 0; i < 20; i++)
        {
            t1.Insert(t1.Length, "é😀");
        }
        AssertItemLengthsMatchContent(t1);
        var last = GetStart(t1)!;
        while (last.RightItem != null)
        {
            last = last.RightItem;
        }
        Assert.AreEqual(string.Concat(Enumerable.Repeat("é😀", 20)), ((ContentString)last.Content).Value, "squashed run");
        Assert.AreEqual(60u, last.Length);
        var expected = t1.ToString();
        Assert.AreEqual(expected.Length, t1.Length);
        Exchange(d1, d2);
        AssertItemLengthsMatchContent(t2);
        Assert.AreEqual(expected, t2.ToString());
        Assert.AreEqual(t1.Length, t2.Length);
    }

    // yrs: utf16_insert_after_split_surrogate_pair
    [TestMethod]
    public void Utf16InsertAfterSplitSurrogatePair()
    {
        var d1 = Doc(1);
        var d2 = Doc(2);
        var t1 = d1.GetText("text");
        var t2 = d2.GetText("text");
        d1.Transact(_ =>
        {
            t1.Insert(0, "😀a");
            t1.Insert(1, "X");
            t1.Insert(2, "Y");
            Assert.AreEqual("�XY�a", t1.ToString());
            t1.Delete(3, 1);
            Assert.AreEqual("�XYa", t1.ToString());
            Assert.AreEqual(4, t1.Length);
        });
        Exchange(d1, d2);
        Assert.AreEqual("�XYa", t2.ToString());
    }
}
