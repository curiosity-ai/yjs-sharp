using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YjsSharp;
using YjsSharp.Encoding;
using static YjsSharp.Tests.Core.T;

namespace YjsSharp.Tests.Core;

/// <summary>Port of the unit tests in yrs/src/sticky_index.rs (yrs StickyIndex = Yjs RelativePosition).</summary>
[TestClass]
public class StickyIndexTests
{
    private const int After  = 0;
    private const int Before = -1;

    private static void CheckStickyIndexes(Doc doc, Text text)
    {
        var len = text.Length;
        for (var i = 0; i < len; i++)
        {
            foreach (var assoc in new[] { After, Before })
            {
                var relPos  = RelativePosition.FromTypeIndex(text, i, assoc);
                var encoded = relPos.Encode();
                var decoded = RelativePosition.Decode(encoded);
                var absPos  = decoded.ToAbsolute(doc);
                Assert.IsNotNull(absPos, $"offset not found for index {i} of {decoded}");
                Assert.AreEqual(i, absPos!.Index);
                Assert.AreEqual(assoc, absPos.Assoc);
                Assert.AreSame(text, absPos.Type);
            }
        }
    }

    // yrs: sticky_index_case_1
    [TestMethod]
    public void StickyIndexCase1()
    {
        var doc = Doc(1);
        var txt = doc.GetText("test");
        doc.Transact(_ =>
        {
            txt.Insert(0, "1");
            txt.Insert(0, "abc");
            txt.Insert(0, "z");
            txt.Insert(0, "y");
            txt.Insert(0, "x");
        });
        CheckStickyIndexes(doc, txt);
    }

    // yrs: sticky_index_case_2
    [TestMethod]
    public void StickyIndexCase2()
    {
        var doc = Doc(1);
        var txt = doc.GetText("test");
        txt.Insert(0, "abc");
        CheckStickyIndexes(doc, txt);
    }

    // yrs: sticky_index_case_3
    [TestMethod]
    public void StickyIndexCase3()
    {
        var doc = Doc(1);
        var txt = doc.GetText("test");
        doc.Transact(_ =>
        {
            txt.Insert(0, "abc");
            txt.Insert(0, "1");
            txt.Insert(0, "xyz");
        });
        CheckStickyIndexes(doc, txt);
    }

    // yrs: sticky_index_case_4
    [TestMethod]
    public void StickyIndexCase4()
    {
        var doc = Doc(1);
        var txt = doc.GetText("test");
        txt.Insert(0, "1");
        CheckStickyIndexes(doc, txt);
    }

    // yrs: sticky_index_case_5
    [TestMethod]
    public void StickyIndexCase5()
    {
        var doc = Doc(1);
        var txt = doc.GetText("test");
        doc.Transact(_ =>
        {
            txt.Insert(0, "2");
            txt.Insert(0, "1");
        });
        CheckStickyIndexes(doc, txt);
    }

    // yrs: sticky_index_case_6
    [TestMethod]
    public void StickyIndexCase6()
    {
        var doc = Doc(1);
        CheckStickyIndexes(doc, doc.GetText("test"));
    }

    // yrs: sticky_index_association_difference
    [TestMethod]
    public void StickyIndexAssociationDifference()
    {
        var doc = Doc(1);
        var txt = doc.GetText("test");
        txt.Insert(0, "2");
        txt.Insert(0, "1");
        var rposRight = RelativePosition.FromTypeIndex(txt, 1, After);
        var rposLeft  = RelativePosition.FromTypeIndex(txt, 1, Before);
        txt.Insert(1, "x");
        Assert.AreEqual(2, rposRight.ToAbsolute(doc)!.Index);
        Assert.AreEqual(1, rposLeft.ToAbsolute(doc)!.Index);
    }

    // yrs: sticky_index_is_yjs_compatible (the JSON shape of the y-prosemirror / tiptap awareness cursor)
    [TestMethod]
    public void StickyIndexIsYjsCompatible()
    {
        var json   = "{\"type\":{\"client\":3731284436,\"clock\":1},\"tname\":null,\"item\":{\"client\":3731284436,\"clock\":20},\"assoc\":-1}";
        var parsed = (IDictionary<string, object?>)JsJson.Parse(json)!;
        var anchor = RelativePosition.FromJson(parsed);
        Assert.AreEqual(new RelativePosition(new ID(3731284436, 1), null, new ID(3731284436, 20), -1), anchor);
        var back = anchor.ToJson();
        JsonEqual(Map(("client", 3731284436L), ("clock", 20L)), back["item"]);
        JsonEqual(-1, back["assoc"]);
        Assert.AreEqual(
            "{\"type\":{\"client\":3731284436,\"clock\":1},\"item\":{\"client\":3731284436,\"clock\":20},\"assoc\":-1}",
            JsJson.Stringify(back));
    }

    // yrs: sticky_index_nested_type_scope_resolves_to_type_end_when_right_associated
    [TestMethod]
    public void StickyIndexNestedTypeScopeResolvesToTypeEndWhenRightAssociated()
    {
        var doc       = Doc(1);
        var fragment  = doc.GetXmlFragment("prosemirror");
        var paragraph = new XmlElement("paragraph");
        var text      = new XmlText("hello");
        fragment.Insert(0, paragraph);
        paragraph.Insert(0, text);
        var end = RelativePosition.FromType(text, After);
        Assert.AreEqual(5, end.ToAbsolute(doc)!.Index);
        var start = RelativePosition.FromType(text, Before);
        Assert.AreEqual(0, start.ToAbsolute(doc)!.Index);
        var decoded = RelativePosition.Decode(end.Encode());
        Assert.AreEqual(5, decoded.ToAbsolute(doc)!.Index);
        var json = RelativePosition.FromJson((IDictionary<string, object?>)JsJson.Parse(JsJson.Stringify(end.ToJson())!)!);
        Assert.AreEqual(5, json.ToAbsolute(doc)!.Index);
    }

    // yrs: sticky_index_round_trip_with_byte_offsets (ported to UTF-16 offsets; at the end of the text a
    // right-associated Yjs position is type scoped and resolves to the length, where yrs returns none)
    [TestMethod]
    public void StickyIndexRoundTripWithOffsets()
    {
        var doc  = new Doc();
        var text = doc.GetText("text");
        text.Insert(0, "déjà vu");
        text.Insert(7, "!");
        var s = text.ToString();
        int? Roundtrip(int i, int assoc) => RelativePosition.FromTypeIndex(text, i, assoc).ToAbsolute(doc)?.Index;
        for (var index = 0; index < s.Length; index++)
        {
            foreach (var assoc in new[] { After, Before })
            {
                Assert.AreEqual(index, Roundtrip(index, assoc), $"sticky index {assoc}");
            }
        }
        Assert.AreEqual(s.Length, Roundtrip(s.Length, Before));
        Assert.AreEqual(s.Length, Roundtrip(s.Length, After));
    }

    [TestMethod]
    public void EncodingMatchesYjs()
    {
        var doc = Doc(1);
        var txt = doc.GetText("t");
        txt.Insert(0, "abc");
        CollectionAssert.AreEqual(new byte[] { 0, 1, 1, 0 }, RelativePosition.FromTypeIndex(txt, 1).Encode());
        CollectionAssert.AreEqual(new byte[] { 1, 1, (byte)'t', 0 }, RelativePosition.FromTypeIndex(txt, 3).Encode());
        CollectionAssert.AreEqual(new byte[] { 0, 1, 0, 0x41 }, RelativePosition.FromTypeIndex(txt, 1, -1).Encode());
        var legacy = RelativePosition.Decode(new byte[] { 0, 1, 1 });
        Assert.AreEqual(0, legacy.Assoc);
    }
}
