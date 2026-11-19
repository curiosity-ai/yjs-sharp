using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YjsSharp;
using static YjsSharp.Tests.Core.T;

namespace YjsSharp.Tests.Core;

/// <summary>Port of the unit tests in yrs/src/types/xml.rs.</summary>
[TestClass]
public class XmlTests
{
    // yrs: insert_attribute
    [TestMethod]
    public void InsertAttribute()
    {
        var d1   = Doc(1);
        var f    = d1.GetXmlFragment("xml");
        var xml1 = new XmlElement("div");
        d1.Transact(_ =>
        {
            f.Push(xml1);
            xml1.SetAttribute("height", 10.ToString());
            Assert.AreEqual("10", xml1.GetAttribute("height"));
        });
        var d2   = Doc(1);
        var f2   = d2.GetXmlFragment("xml");
        var xml2 = new XmlElement("div");
        f2.Push(xml2);
        d2.ApplyUpdate(d1.EncodeStateAsUpdate());
        Assert.AreEqual("10", xml2.GetAttribute("height"));
    }

    // yrs: tree_walker
    [TestMethod]
    public void TreeWalker()
    {
        var doc  = Doc(1);
        var root = doc.GetXmlFragment("xml");
        var p1   = new XmlElement("p");
        var p2   = new XmlElement("p");
        doc.Transact(_ =>
        {
            root.Push(p1);
            p1.Push(new XmlText(""));
            p1.Push(new XmlText(""));
            root.Push(p2);
            root.Push(new XmlElement("img"));
        });
        var all = root.CreateTreeWalker().OfType<XmlElement>().Where(e => e.NodeName == "p").ToList();
        Assert.AreEqual(2, all.Count, "query selector should found two paragraphs");
        Assert.AreSame(p1, all[0], "query selector found 1st paragraph");
        Assert.AreSame(p2, all[1], "query selector found 2nd paragraph");
        CollectionAssert.AreEqual(new[] { p1, p2 }, root.QuerySelectorAll("P").ToArray());
        Assert.AreSame(p1, root.QuerySelector("p"));
    }

    // yrs: text_attributes
    [TestMethod]
    public void TextAttributes()
    {
        var doc = Doc(1);
        var f   = doc.GetXmlFragment("test");
        var txt = new XmlText("");
        f.Push(txt);
        txt.SetAttribute("test", 42.ToString());
        Assert.AreEqual("42", txt.GetAttribute("test"));
        JsonEqual(Map(("test", "42")), txt.GetAttributes());
    }

    // yrs: text_attributes_any
    [TestMethod]
    public void TextAttributesAny()
    {
        var doc = Doc(1);
        var f   = doc.GetXmlFragment("test");
        var txt = new XmlText("");
        f.Push(txt);
        txt.SetAttribute("test", 42);
        txt.SetAttribute("test_true", true);
        txt.SetAttribute("test_null", null);
        JsonEqual(42, txt.GetAttribute("test"));
        Assert.AreEqual(true, txt.GetAttribute("test_true"));
        Assert.IsNull(txt.GetAttribute("test_null"));
        CollectionAssert.AreEquivalent(new[] { "test", "test_true", "test_null" }, txt.GetAttributes().Keys.ToArray());
    }

    // yrs: siblings
    [TestMethod]
    public void Siblings()
    {
        var doc    = Doc(1);
        var root   = doc.GetXmlFragment("root");
        var first  = new XmlText("hello");
        var second = new XmlElement("p");
        root.Push(first);
        root.Push(second);
        Assert.AreSame(second, first.NextSibling, "first.next_sibling should point to second");
        Assert.AreSame(first, second.PrevSibling, "second.prev_sibling should point to first");
        Assert.AreSame(root, first.Parent, "first.parent should point to root");
        Assert.IsNull(root.Parent, "root parent should not exist");
        Assert.AreSame(first, root.FirstChild, "root.first_child should point to first");
    }

    // yrs: serialization
    [TestMethod]
    public void Serialization()
    {
        var d1 = Doc(1);
        var r1 = d1.GetXmlFragment("root");
        d1.Transact(_ =>
        {
            r1.Push(new XmlText("hello"));
            r1.Push(new XmlElement("p"));
        });
        const string expected = "hello<p></p>";
        Assert.AreEqual(expected, r1.ToString());
        var d2 = Doc(2);
        var r2 = d2.GetXmlFragment("root");
        d2.ApplyUpdate(d1.EncodeStateAsUpdate());
        Assert.AreEqual(expected, r2.ToString());
    }

    // yrs: serialization_compatibility
    [TestMethod]
    public void SerializationCompatibility()
    {
        var d1 = Doc(1);
        var r1 = d1.GetXmlFragment("root");
        d1.Transact(_ =>
        {
            r1.Push(new XmlText("hello"));
            r1.Push(new XmlElement("p"));
        });
        var expected = new byte[] { 1, 3, 1, 0, 7, 1, 4, 114, 111, 111, 116, 6, 4, 0, 1, 0, 5, 104, 101, 108, 108, 111, 135, 1, 0, 3, 1, 112, 0 };
        CollectionAssert.AreEqual(expected, d1.EncodeStateAsUpdate());
    }

    // yrs: event_observers
    [TestMethod]
    public void EventObservers()
    {
        var d1  = Doc(1);
        var f   = d1.GetXmlFragment("xml");
        var xml = new XmlElement("test");
        f.Insert(0, xml);
        var d2 = Doc(2);
        var f2 = d2.GetXmlFragment("xml");
        Exchange(d1, d2);
        var                                        xml2       = (XmlElement)f2.Get(0)!;
        IReadOnlyDictionary<string, EntryChange>? attributes = null;
        IReadOnlyList<Delta>?                      nodes      = null;
        xml.Observe(e =>
        {
            attributes = e.Keys;
            nodes      = e.Delta;
        });
        d1.Transact(_ =>
        {
            xml.SetAttribute("key1", "value1");
            xml.SetAttribute("key2", "value2");
        });
        Assert.AreEqual(0, nodes!.Count);
        Assert.AreEqual(2, attributes!.Count);
        Assert.AreEqual(EntryChangeAction.Add, attributes["key1"].Action);
        Assert.AreEqual("value1", attributes["key1"].NewValue);
        Assert.AreEqual(EntryChangeAction.Add, attributes["key2"].Action);
        Assert.AreEqual("value2", attributes["key2"].NewValue);

        d1.Transact(_ =>
        {
            xml.SetAttribute("key1", "value11");
            xml.RemoveAttribute("key2");
        });
        Assert.AreEqual(0, nodes!.Count);
        Assert.AreEqual(2, attributes!.Count);
        Assert.AreEqual(EntryChangeAction.Update, attributes["key1"].Action);
        Assert.AreEqual("value1", attributes["key1"].OldValue);
        Assert.AreEqual("value11", attributes["key1"].NewValue);
        Assert.AreEqual(EntryChangeAction.Delete, attributes["key2"].Action);
        Assert.AreEqual("value2", attributes["key2"].OldValue);

        var nestedTxt = new XmlText("");
        var nestedXml = new XmlElement("div");
        d1.Transact(_ =>
        {
            xml.Insert(0, nestedTxt);
            xml.Insert(1, nestedXml);
        });
        Assert.AreEqual(1, nodes!.Count);
        CollectionAssert.AreEqual(new object[] { nestedTxt, nestedXml }, ((List<object?>)nodes[0].Insert!).ToArray());
        Assert.AreEqual(0, attributes!.Count);

        var nestedXml2 = new XmlElement("p");
        d1.Transact(_ =>
        {
            xml.Delete(1, 1);
            xml.Insert(1, nestedXml2);
        });
        Assert.AreEqual(3, nodes!.Count);
        Assert.AreEqual(1, nodes[0].Retain);
        CollectionAssert.AreEqual(new object[] { nestedXml2 }, ((List<object?>)nodes[1].Insert!).ToArray());
        Assert.AreEqual(1, nodes[2].Delete);
        Assert.AreEqual(0, attributes!.Count);

        attributes = null;
        nodes      = null;
        xml2.Observe(e =>
        {
            attributes = e.Keys;
            nodes      = e.Delta;
        });
        d2.ApplyUpdate(d1.EncodeStateAsUpdate(d2.EncodeStateVector()));
        Assert.AreEqual(1, nodes!.Count);
        var inserted = (List<object?>)nodes[0].Insert!;
        Assert.AreEqual(2, inserted.Count);
        Assert.IsInstanceOfType(inserted[0], typeof(XmlText));
        Assert.AreEqual("p", ((XmlElement)inserted[1]!).NodeName);
        Assert.AreEqual(1, attributes!.Count);
        Assert.AreEqual(EntryChangeAction.Add, attributes["key1"].Action);
        Assert.AreEqual("value11", attributes["key1"].NewValue);
    }

    // yrs: xml_to_string
    [TestMethod]
    public void XmlToString()
    {
        var doc  = new Doc();
        var f    = doc.GetXmlFragment("test");
        var div  = new XmlElement("div");
        var text = new XmlText("hello world");
        doc.Transact(_ =>
        {
            f.Push(div);
            div.SetAttribute("class", "t-button");
            div.Push(text);
            text.Format(6, 5, Attrs(("a", Map(("href", "http://domain.org")))));
        });
        Assert.AreEqual("<div class=\"t-button\">hello <a href=\"http://domain.org\">world</a></div>", f.ToString());
    }

    // yrs: xml_to_string_2
    [TestMethod]
    public void XmlToString2()
    {
        var doc = new Doc();
        var f   = doc.GetXmlFragment("article");
        var xml = new XmlText("");
        f.Insert(0, xml);
        doc.Transact(_ =>
        {
            xml.Insert(0, "hello ");
            xml.Insert(6, "world", Attrs(("i", true)));
            xml.Format(0, 5, Attrs(("b", true)));
            Assert.AreEqual("<b>hello</b> <i>world</i>", xml.ToString());
            xml.Format(6, 5, Attrs(("i", null)));
            Assert.AreEqual("<b>hello</b> world", xml.ToString());
        });
    }

    // yrs: format_attributes_decode_compatibility_v1
    [TestMethod]
    public void FormatAttributesDecodeCompatibilityV1()
    {
        var data = new byte[]
        {
            1, 6, 1, 0, 6, 1, 4, 116, 101, 115, 116, 1, 105, 4, 116, 114, 117, 101, 132, 1, 0, 6,
            104, 101, 108, 108, 111, 32, 132, 1, 6, 5, 119, 111, 114, 108, 100, 134, 1, 11, 1, 105,
            4, 110, 117, 108, 108, 198, 1, 6, 1, 7, 1, 98, 4, 116, 114, 117, 101, 134, 1, 12, 1,
            98, 4, 110, 117, 108, 108, 0,
        };
        var doc = new Doc();
        var txt = doc.GetXmlText("test");
        doc.ApplyUpdate(data);
        Assert.AreEqual("<i>hello </i><b><i>world</i></b>", txt.ToString());
        CollectionAssert.AreEqual(data, doc.EncodeStateAsUpdate());
    }

    // yrs: format_attributes_decode_compatibility_v2
    [TestMethod]
    public void FormatAttributesDecodeCompatibilityV2()
    {
        var data = new byte[]
        {
            0, 3, 0, 3, 1, 2, 65, 5, 5, 0, 12, 10, 74, 12, 1, 14, 9, 6, 0, 132, 1, 134, 0, 198, 0,
            134, 26, 19, 116, 101, 115, 116, 105, 104, 101, 108, 108, 111, 32, 119, 111, 114, 108,
            100, 105, 98, 98, 4, 1, 6, 5, 65, 1, 1, 1, 0, 0, 1, 6, 0, 120, 126, 120, 126, 0,
        };
        var doc = new Doc();
        var txt = doc.GetXmlText("test");
        doc.ApplyUpdateV2(data);
        Assert.AreEqual("<i>hello </i><b><i>world</i></b>", txt.ToString());
        CollectionAssert.AreEqual(data, doc.EncodeStateAsUpdateV2());
    }

    // yrs: issue_607
    [TestMethod]
    public void Issue607()
    {
        var doc = new Doc();
        var xml = doc.GetXmlFragment("doc");
        var u1 = new byte[]
        {
            2, 2, 2, 0, 0, 1, 7, 0, 1, 1, 3, 1, 97, 2, 1, 0, 1, 1, 3, 100, 111, 99, 1, 71, 1, 0, 3,
            1, 112, 2, 2, 1, 0, 1, 1, 1, 0, 1,
        };
        var u2 = new byte[] { 1, 1, 209, 229, 151, 135, 6, 0, 71, 2, 1, 3, 1, 98, 2, 2, 1, 0, 2, 1, 1, 0, 1 };
        doc.ApplyUpdate(u1);
        Assert.AreEqual("<p><a></a></p>", ((XmlElement)xml.Get(0)!).ToString());
        doc.ApplyUpdate(u2);
        Assert.AreEqual("<p><b></b></p>", ((XmlElement)xml.Get(0)!).ToString());
    }

    // yrs: yrs_issue_636
    [TestMethod]
    public void YrsIssue636()
    {
        var docA  = Doc(1);
        var fragA = docA.GetXmlFragment("frag");
        fragA.Insert(0, new XmlElement("first"));
        var update = docA.EncodeStateAsUpdate();
        var docB   = Doc(2);
        var fragB  = docB.GetXmlFragment("frag");
        docB.ApplyUpdate(update);
        fragB.Insert(0, new XmlElement("zero"));
        Assert.AreEqual("<zero></zero><first></first>", fragB.ToString());
    }

    [TestMethod]
    public void XmlHookRoundTrip()
    {
        var d1   = Doc(1);
        var f    = d1.GetXmlFragment("f");
        var hook = new XmlHook("custom");
        hook.Set("a", 1);
        f.Push(hook);
        var d2 = Doc(2);
        d2.ApplyUpdate(d1.EncodeStateAsUpdate());
        var hook2 = (XmlHook)d2.GetXmlFragment("f").Get(0)!;
        Assert.AreEqual("custom", hook2.HookName);
        JsonEqual(1, hook2.Get("a"));
        CollectionAssert.AreEqual(d1.EncodeStateAsUpdateV2(), d2.EncodeStateAsUpdateV2());
    }
}
