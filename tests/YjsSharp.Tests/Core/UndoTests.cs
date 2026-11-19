using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YjsSharp;
using YjsSharp.Structs;
using static YjsSharp.Tests.Core.T;

namespace YjsSharp.Tests.Core;

/// <summary>Port of the unit tests in yrs/src/undo.rs.</summary>
[TestClass]
public class UndoTests
{
    /// <summary>
    /// An undo manager whose clock never advances: every change is merged into the last stack item until
    /// <see cref="UndoManager.StopCapturing"/> (yrs: reset) is called, exactly like a fast test run in yrs / Yjs.
    /// </summary>
    private static UndoManager Mgr(AbstractType scope, long captureTimeout = 500, params object?[] trackedOrigins)
    {
        var options = new UndoManagerOptions { CaptureTimeout = captureTimeout, Clock = () => 1 };
        if (trackedOrigins.Length > 0)
        {
            options.TrackedOrigins = new HashSet<object?>(trackedOrigins);
        }
        return new UndoManager(scope, options);
    }

    private static UndoManager MgrNoCapture(AbstractType scope, params object?[] trackedOrigins)
    {
        var clock   = 0L;
        var options = new UndoManagerOptions { CaptureTimeout = 0, Clock = () => ++clock };
        if (trackedOrigins.Length > 0)
        {
            options.TrackedOrigins = new HashSet<object?>(trackedOrigins);
        }
        return new UndoManager(scope, options);
    }

    // yrs: selective_clear_preserves_other_stack_and_reports_actual_changes
    [TestMethod]
    public void SelectiveClearPreservesOtherStackAndReportsActualChanges()
    {
        foreach (var (clearUndo, clearRedo) in new[] { (true, false), (false, true), (true, true) })
        {
            var doc  = new Doc();
            var text = doc.GetText("text");
            var undo = Mgr(text);
            text.Insert(0, "a");
            undo.StopCapturing();
            text.Insert(1, "b");
            Assert.IsNotNull(undo.Undo());
            Assert.IsTrue(undo.CanUndo() && undo.CanRedo());
            var events = new List<(bool, bool)>();
            undo.StackCleared += (_, e) => events.Add((e.UndoStackCleared, e.RedoStackCleared));
            for (var i = 0; i < 2; i++)
            {
                undo.Clear(clearUndo, clearRedo);
                Assert.AreEqual(!clearUndo, undo.CanUndo());
                Assert.AreEqual(!clearRedo, undo.CanRedo());
                Assert.AreEqual("a", text.ToString());
                CollectionAssert.AreEqual(new[] { (clearUndo, clearRedo) }, events);
            }
            if (!clearUndo)
            {
                Assert.IsNotNull(undo.Undo());
                Assert.AreEqual("", text.ToString());
            }
            else if (!clearRedo)
            {
                Assert.IsNotNull(undo.Redo());
                Assert.AreEqual("ab", text.ToString());
            }
        }
    }

    // yrs: undo_text
    [TestMethod]
    public void UndoText()
    {
        var d1   = Doc(1);
        var txt1 = d1.GetText("test");
        var mgr  = Mgr(txt1);
        var d2   = Doc(2);
        var txt2 = d2.GetText("test");
        // items that are added & deleted in the same transaction won't be undone
        txt1.Insert(0, "test");
        txt1.Delete(0, 4);
        mgr.Undo();
        Assert.AreEqual("", txt1.ToString());
        // follow redone items
        txt1.Insert(0, "a");
        mgr.StopCapturing();
        txt1.Delete(0, 1);
        mgr.StopCapturing();
        mgr.Undo();
        Assert.AreEqual("a", txt1.ToString());
        mgr.Undo();
        Assert.AreEqual("", txt1.ToString());
        txt1.Insert(0, "abc");
        txt2.Insert(0, "xyz");
        Exchange(d1, d2);
        mgr.Undo();
        Assert.AreEqual("xyz", txt1.ToString());
        mgr.Redo();
        Assert.AreEqual("abcxyz", txt1.ToString());
        Exchange(d1, d2);
        txt2.Delete(0, 1);
        Exchange(d1, d2);
        mgr.Undo();
        Assert.AreEqual("xyz", txt1.ToString());
        mgr.Redo();
        Assert.AreEqual("bcxyz", txt1.ToString());
        // test marks
        var attrs = Attrs(("bold", true));
        txt1.Format(1, 3, attrs);
        DeltaEqual(new[] { Ins("b"), Ins("cxy", attrs), Ins("z") }, txt1.ToDelta());
        mgr.Undo();
        DeltaEqual(new[] { Ins("bcxyz") }, txt1.ToDelta());
        mgr.Redo();
        DeltaEqual(new[] { Ins("b"), Ins("cxy", attrs), Ins("z") }, txt1.ToDelta());
    }

    // yrs: double_undo
    [TestMethod]
    public void DoubleUndo()
    {
        var doc = Doc(1);
        var txt = doc.GetText("test");
        txt.Insert(0, "1221");
        var mgr = Mgr(txt);
        txt.Insert(2, "3");
        txt.Insert(3, "3");
        mgr.Undo();
        mgr.Undo();
        txt.Insert(2, "3");
        Assert.AreEqual("12321", txt.ToString());
    }

    // yrs: undo_map
    [TestMethod]
    public void UndoMap()
    {
        var d1   = Doc(1);
        var map1 = d1.GetMap("test");
        var d2   = Doc(2);
        var map2 = d2.GetMap("test");
        map1.Set("a", 0);
        var mgr = Mgr(map1);
        map1.Set("a", 1);
        mgr.Undo();
        JsonEqual(0, map1.Get("a"));
        mgr.Redo();
        JsonEqual(1, map1.Get("a"));
        // testing sub-types and if it can restore a whole type
        var subType = map1.Set("a", new YMap());
        subType.Set("x", 42);
        JsonEqual(Map(("a", Map(("x", 42)))), map1.ToJson());
        mgr.Undo();
        JsonEqual(1, map1.Get("a"));
        mgr.Redo();
        JsonEqual(Map(("a", Map(("x", 42)))), map1.ToJson());
        Exchange(d1, d2);
        // if content is overwritten by another user, undo operations should be skipped
        map2.Set("a", 44);
        Exchange(d1, d2);
        Exchange(d1, d2);
        mgr.Undo();
        JsonEqual(44, map1.Get("a"));
        mgr.Redo();
        JsonEqual(44, map1.Get("a"));
        // test setting value multiple times
        map1.Set("b", "initial");
        mgr.StopCapturing();
        map1.Set("b", "val1");
        map1.Set("b", "val2");
        mgr.StopCapturing();
        mgr.Undo();
        Assert.AreEqual("initial", map1.Get("b"));
    }

    // yrs: undo_array
    [TestMethod]
    public void UndoArray()
    {
        var d1     = Doc(1);
        var array1 = d1.GetArray("test");
        var d2     = Doc(2);
        var array2 = d2.GetArray("test");
        var mgr    = Mgr(array1);
        array1.Insert(0, 1, 2, 3);
        array2.Insert(0, 4, 5, 6);
        Exchange(d1, d2);
        JsonEqual(List(1, 2, 3, 4, 5, 6), array1.ToJson());
        mgr.Undo();
        JsonEqual(List(4, 5, 6), array1.ToJson());
        mgr.Redo();
        JsonEqual(List(1, 2, 3, 4, 5, 6), array1.ToJson());
        Exchange(d1, d2);
        array2.Delete(0, 1);
        Exchange(d1, d2);
        mgr.Undo();
        JsonEqual(List(4, 5, 6), array1.ToJson());
        mgr.Redo();
        JsonEqual(List(2, 3, 4, 5, 6), array1.ToJson());
        array1.Delete(0, 5);
        // test nested structure
        var map = new YMap();
        array1.Insert(0, map);
        JsonEqual(List(Map()), array1.ToJson());
        mgr.StopCapturing();
        map.Set("a", 1);
        JsonEqual(List(Map(("a", 1))), array1.ToJson());
        mgr.Undo();
        JsonEqual(List(Map()), array1.ToJson());
        mgr.Undo();
        JsonEqual(List(2, 3, 4, 5, 6), array1.ToJson());
        mgr.Redo();
        JsonEqual(List(Map()), array1.ToJson());
        mgr.Redo();
        JsonEqual(List(Map(("a", 1))), array1.ToJson());
        Exchange(d1, d2);
        var map2 = (YMap)array2.Get(0)!;
        map2.Set("b", 2);
        Exchange(d1, d2);
        JsonEqual(List(Map(("a", 1), ("b", 2))), array1.ToJson());
        mgr.Undo();
        JsonEqual(List(Map(("b", 2))), array1.ToJson());
        mgr.Undo();
        JsonEqual(List(2, 3, 4, 5, 6), array1.ToJson());
        mgr.Redo();
        JsonEqual(List(Map(("b", 2))), array1.ToJson());
        mgr.Redo();
        JsonEqual(List(Map(("a", 1), ("b", 2))), array1.ToJson());
    }

    // yrs: undo_xml
    [TestMethod]
    public void UndoXml()
    {
        var d1   = Doc(1);
        var frag = d1.GetXmlFragment("xml");
        var xml1 = new XmlElement("undefined");
        frag.Insert(0, xml1);
        var mgr   = Mgr(xml1);
        var child = new XmlElement("p");
        xml1.Insert(0, child);
        var textChild = new XmlText("content");
        child.Insert(0, textChild);
        Assert.AreEqual("<undefined><p>content</p></undefined>", xml1.ToString());
        mgr.StopCapturing();
        textChild.Format(3, 4, Attrs(("bold", Map())));
        Assert.AreEqual("<undefined><p>con<bold>tent</bold></p></undefined>", xml1.ToString());
        mgr.Undo();
        Assert.AreEqual("<undefined><p>content</p></undefined>", xml1.ToString());
        mgr.Redo();
        Assert.AreEqual("<undefined><p>con<bold>tent</bold></p></undefined>", xml1.ToString());
        xml1.Delete(0, 1);
        Assert.AreEqual("<undefined></undefined>", xml1.ToString());
        mgr.Undo();
        Assert.AreEqual("<undefined><p>con<bold>tent</bold></p></undefined>", xml1.ToString());
    }

    // yrs: undo_events
    [TestMethod]
    public void UndoEvents()
    {
        var doc     = Doc(1);
        var txt     = doc.GetText("test");
        var mgr     = Mgr(txt);
        var result  = 0;
        var counter = 1;
        mgr.StackItemAdded += (_, e) =>
        {
            Assert.IsTrue(e.Transaction.HasChangedParentType(txt));
            e.StackItem.Meta["test"] = counter++;
        };
        mgr.StackItemPopped += (_, e) =>
        {
            Assert.IsTrue(e.Transaction.HasChangedParentType(txt));
            if (e.StackItem.Meta.TryGetValue("test", out var v))
            {
                result = (int)v!;
            }
        };
        txt.Insert(0, "abc");
        mgr.Undo();
        Assert.AreEqual(1, result);
        mgr.Redo();
        Assert.AreEqual(2, result);
    }

    // yrs: undo_stack_serialization
    [TestMethod]
    public void UndoStackSerialization()
    {
        byte[][] undoStack;
        byte[][] redoStack;
        byte[]   state;
        {
            var d1  = new Doc(new DocOptions { ClientId = 1, Guid = "A" });
            var txt = d1.GetText("test");
            var m1  = Mgr(txt);
            txt.Insert(0, "c");
            m1.StopCapturing();
            txt.Insert(0, "b");
            m1.StopCapturing();
            txt.Insert(0, "a");
            m1.Undo();
            Assert.AreEqual("bc", txt.ToString());
            m1.Redo();
            Assert.AreEqual("abc", txt.ToString());
            undoStack = m1.UndoStack.Select(s => s.Encode()).ToArray();
            redoStack = m1.RedoStack.Select(s => s.Encode()).ToArray();
            state     = d1.EncodeStateAsUpdate();
        }
        var d2   = new Doc(new DocOptions { ClientId = 2, Guid = "A" });
        var txt2 = d2.GetText("test");
        d2.ApplyUpdate(state);
        var m2 = new UndoManager(txt2, new UndoManagerOptions
        {
            Clock         = () => 1,
            InitUndoStack = undoStack.Select(StackItem.Decode).ToList(),
            InitRedoStack = redoStack.Select(StackItem.Decode).ToList(),
        });
        m2.Undo();
        Assert.AreEqual("bc", txt2.ToString());
        m2.Redo();
        Assert.AreEqual("abc", txt2.ToString());
    }

    // yrs: undo_until_change_performed
    [TestMethod]
    public void UndoUntilChangePerformed()
    {
        var d1    = Doc(1);
        var arr1  = d1.GetArray("array");
        var d2    = Doc(2);
        var arr2  = d2.GetArray("array");
        var map1a = new YMap(new Dictionary<string, object?> { ["hello"] = "world" });
        arr1.Push(map1a);
        var map1b = new YMap(new Dictionary<string, object?> { ["key"] = "value" });
        arr1.Push(map1b);
        Exchange(d1, d2);
        var mgr1 = Mgr(arr1, 500, d1.ClientId);
        var mgr2 = Mgr(arr2, 500, d2.ClientId);
        d1.Transact(_ => map1b.Set("key", "value modified"), d1.ClientId);
        Exchange(d1, d2);
        mgr1.StopCapturing();
        d1.Transact(_ => map1a.Set("hello", "world modified"), d1.ClientId);
        Exchange(d1, d2);
        d2.Transact(_ => arr2.Delete(0, 1), d2.ClientId);
        Exchange(d1, d2);
        mgr2.Undo();
        Exchange(d1, d2);
        mgr1.Undo();
        Exchange(d1, d2);
        Assert.AreEqual("value", map1b.Get("key"));
    }

    // yrs: nested_undo (https://github.com/yjs/yjs/issues/317)
    [TestMethod]
    public void NestedUndo()
    {
        var doc    = DocNoGc(1);
        var design = doc.GetMap("map");
        var mgr    = MgrNoCapture(design);
        design.Set("text", new YMap(new Dictionary<string, object?>
        {
            ["blocks"] = new YMap(new Dictionary<string, object?> { ["text"] = "Type something" }),
        }));
        var text = (YMap)design.Get("text")!;
        text.Set("blocks", new YMap(new Dictionary<string, object?> { ["text"] = "Something" }));
        text.Set("blocks", new YMap(new Dictionary<string, object?> { ["text"] = "Something else" }));
        JsonEqual(Map(("text", Map(("blocks", Map(("text", "Something else")))))), design.ToJson());
        mgr.Undo();
        JsonEqual(Map(("text", Map(("blocks", Map(("text", "Something")))))), design.ToJson());
        mgr.Undo();
        JsonEqual(Map(("text", Map(("blocks", Map(("text", "Type something")))))), design.ToJson());
        mgr.Undo();
        JsonEqual(Map(), design.ToJson());
        mgr.Redo();
        JsonEqual(Map(("text", Map(("blocks", Map(("text", "Type something")))))), design.ToJson());
        mgr.Redo();
        JsonEqual(Map(("text", Map(("blocks", Map(("text", "Something")))))), design.ToJson());
        mgr.Redo();
        JsonEqual(Map(("text", Map(("blocks", Map(("text", "Something else")))))), design.ToJson());
    }

    // yrs: consecutive_redo_bug (https://github.com/yjs/yjs/issues/355)
    [TestMethod]
    public void ConsecutiveRedoBug()
    {
        var doc  = Doc(1);
        var root = doc.GetMap("root");
        var mgr  = Mgr(root);
        root.Set("a", new YMap(new Dictionary<string, object?> { ["x"] = 0, ["y"] = 0 }));
        var point = (YMap)root.Get("a")!;
        mgr.StopCapturing();
        point.Set("x", 100);
        point.Set("y", 100);
        mgr.StopCapturing();
        point.Set("x", 200);
        point.Set("y", 200);
        mgr.StopCapturing();
        point.Set("x", 300);
        point.Set("y", 300);
        mgr.StopCapturing();
        JsonEqual(Map(("x", 300), ("y", 300)), point.ToJson());
        mgr.Undo();
        JsonEqual(Map(("x", 200), ("y", 200)), point.ToJson());
        mgr.Undo();
        JsonEqual(Map(("x", 100), ("y", 100)), point.ToJson());
        mgr.Undo();
        JsonEqual(Map(("x", 0), ("y", 0)), point.ToJson());
        mgr.Undo();
        Assert.IsFalse(root.Has("a"));
        mgr.Redo();
        point = (YMap)root.Get("a")!;
        JsonEqual(Map(("x", 0), ("y", 0)), point.ToJson());
        mgr.Redo();
        JsonEqual(Map(("x", 100), ("y", 100)), point.ToJson());
        mgr.Redo();
        JsonEqual(Map(("x", 200), ("y", 200)), point.ToJson());
        mgr.Redo();
        JsonEqual(Map(("x", 300), ("y", 300)), point.ToJson());
    }

    // yrs: undo_xml_bug (https://github.com/yjs/yjs/issues/304)
    [TestMethod]
    public void UndoXmlBug()
    {
        const string origin = "origin";
        var          doc    = Doc(1);
        var          f      = doc.GetXmlFragment("t");
        var          mgr    = MgrNoCapture(f, origin);
        doc.Transact(_ =>
        {
            var e = new XmlElement("test-node");
            f.Insert(0, e);
            e.SetAttribute("a", "100");
            e.SetAttribute("b", "0");
        }, origin);
        doc.Transact(_ => ((XmlElement)f.Get(0)!).SetAttribute("a", "200"), origin);
        doc.Transact(_ =>
        {
            var e = (XmlElement)f.Get(0)!;
            e.SetAttribute("a", "180");
            e.SetAttribute("b", "50");
        }, origin);
        mgr.Undo();
        mgr.Undo();
        mgr.Undo();
        mgr.Redo();
        mgr.Redo();
        mgr.Redo();
        Assert.AreEqual("<test-node a=\"180\" b=\"50\"></test-node>", f.ToString());
    }

    // yrs: undo_block_bug (https://github.com/yjs/yjs/issues/343)
    [TestMethod]
    public void UndoBlockBug()
    {
        var doc    = DocNoGc(1);
        var design = doc.GetMap("map");
        var mgr    = MgrNoCapture(design);
        var text   = design.Set("text", new YMap(new Dictionary<string, object?> { ["blocks"] = new YMap(new Dictionary<string, object?> { ["text"] = "1" }) }));
        text.Set("blocks", new YMap(new Dictionary<string, object?> { ["text"] = "1" }));
        text.Set("blocks", new YMap(new Dictionary<string, object?> { ["text"] = "3" }));
        text.Set("blocks", new YMap(new Dictionary<string, object?> { ["text"] = "4" }));
        mgr.Undo();
        mgr.Undo();
        mgr.Undo();
        mgr.Undo();
        mgr.Redo();
        mgr.Redo();
        mgr.Redo();
        mgr.Redo();
        JsonEqual(Map(("text", Map(("blocks", Map(("text", "4")))))), design.ToJson());
    }

    // yrs: undo_delete_text_format (https://github.com/yjs/yjs/issues/392)
    [TestMethod]
    public void UndoDeleteTextFormat()
    {
        static void Send(Doc src, Doc dst) => dst.ApplyUpdate(src.EncodeStateAsUpdate());
        var doc1 = Doc(1);
        var txt  = doc1.GetText("test");
        txt.Insert(0, "Attack ships on fire off the shoulder of Orion.");
        var doc2 = Doc(2);
        var txt2 = doc2.GetText("test");
        Send(doc1, doc2);
        var mgr   = Mgr(txt);
        var attrs = Attrs(("bold", true));
        txt.Format(13, 7, attrs);
        mgr.StopCapturing();
        Send(doc1, doc2);
        txt.Format(16, 4, Attrs(("bold", null)));
        DeltaEqual(new[] { Ins("Attack ships "), Ins("on ", attrs), Ins("fire off the shoulder of Orion.") }, txt.ToDelta());
        mgr.StopCapturing();
        Send(doc1, doc2);
        mgr.Undo();
        Send(doc1, doc2);
        var expected = new[] { Ins("Attack ships "), Ins("on fire", attrs), Ins(" off the shoulder of Orion.") };
        DeltaEqual(expected, txt.ToDelta());
        DeltaEqual(expected, txt2.ToDelta());
    }

    // yrs: special_deletion_case (https://github.com/yjs/yjs/issues/447)
    [TestMethod]
    public void SpecialDeletionCase()
    {
        const string origin = "undoable";
        var          doc    = Doc(1);
        var          f      = doc.GetXmlFragment("test");
        var          mgr    = Mgr(f, 500, origin);
        doc.Transact(_ =>
        {
            var e = new XmlElement("test");
            f.Insert(0, e);
            e.SetAttribute("a", "1");
            e.SetAttribute("b", "2");
        });
        Assert.AreEqual("<test a=\"1\" b=\"2\"></test>", f.ToString());
        doc.Transact(_ =>
        {
            var e = (XmlElement)f.Get(0)!;
            e.SetAttribute("b", "3");
            f.Delete(0, 1);
        }, origin);
        Assert.AreEqual("", f.ToString());
        mgr.Undo();
        Assert.AreEqual("<test a=\"1\" b=\"2\"></test>", f.ToString());
    }

    // yrs: undo_in_embed
    [TestMethod]
    public void UndoInEmbed()
    {
        var d1     = Doc(1);
        var txt1   = d1.GetText("test");
        var mgr    = Mgr(txt1);
        var d2     = Doc(2);
        var txt2   = d2.GetText("test");
        var nested = new Text("initial text");
        txt1.InsertEmbed(0, nested, Attrs(("bold", true)));
        Assert.AreEqual("initial text", nested.ToString());
        mgr.StopCapturing();
        nested.Delete(0, nested.Length);
        nested.Insert(0, "other text");
        Assert.AreEqual("other text", nested.ToString());
        mgr.Undo();
        Assert.AreEqual("initial text", nested.ToString());
        Exchange(d1, d2);
        var nested2 = (Text)txt2.ToDelta()[0].Insert!;
        Assert.AreEqual("initial text", nested2.ToString());
        mgr.Undo();
        Assert.AreEqual(0, nested.Length);
    }

    // yrs: github_issue_345
    [TestMethod]
    public void GithubIssue345()
    {
        var doc = new Doc();
        var map = doc.GetMap("r");
        var cid = doc.ClientId;
        var mgr = Mgr(map, 500, cid);
        var s1  = new YMap();
        doc.Transact(_ => map.Set("s1", s1), cid);
        mgr.StopCapturing();
        var a = new YArray();
        doc.Transact(_ =>
        {
            s1.Set("a", a);
            a.Insert(0, "a1");
        }, cid);
        mgr.StopCapturing();
        var b = new YArray();
        doc.Transact(_ =>
        {
            s1.Set("b", b);
            b.Insert(0, "b1");
        }, cid);
        mgr.StopCapturing();
        doc.Transact(_ => b.Insert(1, "b2"), cid);
        mgr.StopCapturing();
        doc.Transact(_ => a.Insert(1, "a3"), cid);
        mgr.StopCapturing();
        doc.Transact(_ => a.Insert(2, "a4"), cid);
        mgr.StopCapturing();
        doc.Transact(_ => a.Insert(3, "a5"), cid);
        mgr.StopCapturing();
        JsonEqual(Map(("s1", Map(("a", List("a1", "a3", "a4", "a5")), ("b", List("b1", "b2"))))), map.ToJson());
        mgr.Undo();
        mgr.Undo();
        mgr.Undo();
        mgr.Undo();
        JsonEqual(Map(("s1", Map(("a", List("a1")), ("b", List("b1"))))), map.ToJson());
        mgr.Redo();
        JsonEqual(Map(("s1", Map(("b", List("b1", "b2")), ("a", List("a1"))))), map.ToJson());
        mgr.Redo();
        JsonEqual(Map(("s1", Map(("a", List("a1", "a3")), ("b", List("b1", "b2"))))), map.ToJson());
        mgr.Redo();
        JsonEqual(Map(("s1", Map(("a", List("a1", "a3", "a4")), ("b", List("b1", "b2"))))), map.ToJson());
        mgr.Redo();
        JsonEqual(Map(("s1", Map(("a", List("a1", "a3", "a4", "a5")), ("b", List("b1", "b2"))))), map.ToJson());
    }

    // yrs: github_issue_345_part_2
    [TestMethod]
    public void GithubIssue345Part2()
    {
        var d   = new Doc();
        var r   = d.GetMap("r");
        var s1  = r.Set("s1", new YMap());
        var mgr = Mgr(r);
        d.Transact(_ =>
        {
            var b1 = s1.Set("b1", new YMap());
            b1.Set("f1", 11);
        });
        mgr.StopCapturing();
        s1.Delete("b1");
        mgr.StopCapturing();
        d.Transact(_ =>
        {
            var b1 = s1.Set("b1", new YMap());
            b1.Set("f1", 20);
        });
        mgr.StopCapturing();
        JsonEqual(Map(("s1", Map(("b1", Map(("f1", 20)))))), r.ToJson());
        mgr.Undo();
        JsonEqual(Map(("s1", Map())), r.ToJson());
        Assert.IsTrue(mgr.CanUndo(), "should be able to undo");
        mgr.Undo();
        JsonEqual(Map(("s1", Map(("b1", Map(("f1", 11)))))), r.ToJson());
        Assert.IsTrue(mgr.CanUndo(), "should be able to undo to the init state");
    }

    // yrs: issue_371
    [TestMethod]
    public void Issue371()
    {
        var doc  = Doc(1);
        var r    = doc.GetMap("r");
        var s1   = r.Set("s1", new YMap());
        var b1Arr = s1.Set("b1", new YArray());
        var el1  = new YMap();
        b1Arr.Insert(0, el1);
        el1.Set("f1", 8);
        el1.Set("f2", true);
        var mgr = Mgr(r);
        doc.Transact(_ =>
        {
            var el0 = new YMap();
            b1Arr.Insert(0, el0);
            el0.Set("f1", 8);
            el0.Set("f2", false);
            el1.Set("f1", 13);
            el1.Delete("f2");
        });
        mgr.StopCapturing();
        el1.Set("f2", false);
        mgr.StopCapturing();
        el1.Set("f2", true);
        mgr.StopCapturing();
        mgr.Undo();
        JsonEqual(Map(("s1", Map(("b1", List(Map(("f1", 8), ("f2", false)), Map(("f1", 13), ("f2", false))))))), r.ToJson());
        mgr.Undo();
        JsonEqual(Map(("s1", Map(("b1", List(Map(("f1", 8), ("f2", false)), Map(("f1", 13))))))), r.ToJson());
        mgr.Undo();
        JsonEqual(Map(("s1", Map(("b1", List(Map(("f1", 8), ("f2", true))))))), r.ToJson());
        Assert.IsNull(mgr.Undo(), "no more changes tracked by undo manager");
    }

    // yrs: issue_371_2
    [TestMethod]
    public void Issue371Part2()
    {
        var doc = Doc(1);
        var r   = doc.GetMap("r");
        var s1  = r.Set("s1", new YMap());
        s1.Set("f2", "AAA");
        s1.Set("f1", false);
        var mgr = Mgr(r);
        s1.Delete("f2");
        mgr.StopCapturing();
        s1.Set("f2", "C1");
        mgr.StopCapturing();
        s1.Set("f2", "C2");
        mgr.StopCapturing();
        mgr.Undo();
        JsonEqual(Map(("s1", Map(("f1", false), ("f2", "C1")))), r.ToJson());
        mgr.Undo();
        JsonEqual(Map(("s1", Map(("f1", false)))), r.ToJson());
        mgr.Undo();
        JsonEqual(Map(("s1", Map(("f1", false), ("f2", "AAA")))), r.ToJson());
        Assert.IsNull(mgr.Undo(), "no more changes tracked by undo manager");
    }

    // yrs: issue_380
    [TestMethod]
    public void Issue380()
    {
        var d          = Doc(1);
        var r          = d.GetMap("r");
        var s1         = r.Set("s1", new YMap());
        var b1Arr      = s1.Set("b1", new YArray());
        var b1El1      = new YMap();
        b1Arr.Insert(0, b1El1);
        var b2Arr      = b1El1.Set("b2", new YArray());
        var b2ArrNest  = new YArray();
        b2Arr.Insert(0, b2ArrNest);
        b2ArrNest.Insert(0, 232291652);
        b2ArrNest.Insert(1, -30);
        var mgr = Mgr(r);
        d.Transact(_ =>
        {
            b2ArrNest.Delete(1, 1);
            b2ArrNest.Insert(1, -5);
        });
        mgr.StopCapturing();
        d.Transact(_ =>
        {
            var b1El0 = new YMap();
            b1Arr.Insert(0, b1El0);
            var b20Arr = b1El0.Set("b2", new YArray());
            var b20ArrNest = new YArray();
            b20Arr.Insert(0, b20ArrNest);
            b20ArrNest.Insert(0, 232291652);
            b20ArrNest.Insert(1, -6);
            b1Arr.Delete(1, 1);
            var b1El1New = new YMap();
            b1Arr.Insert(1, b1El1New);
            b1El1New.Set("f2", "C1");
        });
        mgr.StopCapturing();
        JsonEqual(Map(("s1", Map(("b1", List(Map(("b2", List(List(232291652, -6)))), Map(("f2", "C1"))))))), r.ToJson());
        mgr.Undo();
        JsonEqual(Map(("s1", Map(("b1", List(Map(("b2", List(List(232291652, -5))))))))), r.ToJson());
        mgr.Undo();
        JsonEqual(Map(("s1", Map(("b1", List(Map(("b2", List(List(232291652, -30))))))))), r.ToJson());
    }

    // yrs: multi_doc_undo and multi_doc_after_destroy are not ported: a Yjs 13.6 UndoManager tracks a single
    // document (yrs' multi-document undo manager has no Yjs counterpart).

    // yrs: gc_collects_kept_children_of_a_collected_type
    [TestMethod]
    public void GcCollectsKeptChildrenOfACollectedType()
    {
        var d1      = Doc(1);
        var d2      = Doc(2);
        var styles1 = d1.GetMap("styles");
        var styles2 = d2.GetMap("styles");
        styles1.Set("p", new YMap(new Dictionary<string, object?> { ["kind"] = "number" }));
        Exchange(d1, d2);
        var number = new ID(1, 1);
        var mgr    = Mgr(styles2);
        ((YMap)styles2.Get("p")!).Set("kind", "bullet");
        mgr.Undo();
        mgr.StopCapturing();
        styles2.Set("q", new YMap());
        styles1.Delete("p");
        Exchange(d1, d2);
        Assert.IsInstanceOfType(d2.Store.Find(number), typeof(YjsSharp.Structs.GC), "\"number\" went with its map, as in Yjs");
        d2.EncodeStateAsUpdate();
        while (mgr.Undo() != null)
        {
        }
        Exchange(d1, d2);
        JsonEqual(styles1.ToJson(), styles2.ToJson());
    }

    // yrs: undo_embedded_type_attribute
    [TestMethod]
    public void UndoEmbeddedTypeAttribute()
    {
        var d1     = Doc(1);
        var d2     = Doc(2);
        var text1  = d1.GetText("text");
        var text2  = d2.GetText("text");
        var button = new YMap(new Dictionary<string, object?> { ["type"] = "button", ["test"] = true });
        text1.InsertEmbed(0, button);
        Exchange(d1, d2);
        var mgr = Mgr(text1);
        mgr.StopCapturing();
        button.Set("type", "paragraph");
        text1.Delete(0, 1);
        mgr.Undo();
        var actual = (YMap)text1.ToDelta()[0].Insert!;
        JsonEqual(Map(("type", "button"), ("test", true)), actual.ToJson());
        Exchange(d1, d2);
        var actual2 = (YMap)text2.ToDelta()[0].Insert!;
        JsonEqual(Map(("type", "button"), ("test", true)), actual2.ToJson());
    }

    // yrs: sticky_index_inside_redone_text_keeps_its_offset
    [TestMethod]
    public void StickyIndexInsideRedoneTextKeepsItsOffset()
    {
        var doc = Doc(1);
        var txt = doc.GetText("test");
        var mgr = Mgr(txt);
        txt.Insert(0, "hello world");
        var pos = RelativePosition.FromTypeIndex(txt, 5);
        mgr.Undo();
        Assert.AreEqual("", txt.ToString());
        mgr.Redo();
        Assert.AreEqual("hello world", txt.ToString());
        var offset = pos.ToAbsolute(doc)!;
        Assert.AreEqual(5, offset.Index, "Yjs resolves the same position to 5");
    }

    [TestMethod]
    public void CaptureTimeoutMergesChanges()
    {
        var now = 1000L;
        var doc = Doc(1);
        var txt = doc.GetText("t");
        var mgr = new UndoManager(txt, new UndoManagerOptions { CaptureTimeout = 500, Clock = () => now });
        txt.Insert(0, "a");
        now += 100;
        txt.Insert(1, "b");
        now += 1000;
        txt.Insert(2, "c");
        Assert.AreEqual(2, mgr.UndoStack.Count);
        mgr.Undo();
        Assert.AreEqual("ab", txt.ToString());
        mgr.Undo();
        Assert.AreEqual("", txt.ToString());
        mgr.Redo();
        mgr.Redo();
        Assert.AreEqual("abc", txt.ToString());
    }

    [TestMethod]
    public void UntrackedOriginsAreIgnoredAndTypeOriginsAreTracked()
    {
        var doc = Doc(1);
        var txt = doc.GetText("t");
        var mgr = new UndoManager(txt, new UndoManagerOptions { Clock = () => 1, TrackedOrigins = new HashSet<object?> { typeof(Uri) } });
        doc.Transact(_ => txt.Insert(0, "x"), "not tracked");
        Assert.IsFalse(mgr.CanUndo());
        doc.Transact(_ => txt.Insert(0, "y"), new Uri("http://example.org"));
        Assert.IsTrue(mgr.CanUndo());
        mgr.Undo();
        Assert.AreEqual("x", txt.ToString());
        mgr.Dispose();
        txt.Insert(0, "z");
        Assert.IsFalse(mgr.CanRedo() && mgr.UndoStack.Count > 0);
    }
}
