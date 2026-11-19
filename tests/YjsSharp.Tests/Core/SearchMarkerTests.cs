using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YjsSharp;
using static YjsSharp.Tests.Core.T;

namespace YjsSharp.Tests.Core;

/// <summary>
/// Randomized checks of index lookups (search markers, block splitting and merging) against a plain model, with
/// edits spread over many transactions, grouped in transactions, and interleaved with remote updates.
/// </summary>
[TestClass]
public class SearchMarkerTests
{
    [TestMethod]
    public void TextMatchesModel()
    {
        var rng = new Random(11);
        for (var round = 0; round < 20; round++)
        {
            var doc    = Doc(1);
            var remote = Doc(2);
            var txt    = doc.GetText("t");
            var model  = new System.Text.StringBuilder();
            for (var step = 0; step < 300; step++)
            {
                var batch = rng.Next(4) == 0 ? rng.Next(2, 30) : 1;
                doc.Transact(_ =>
                {
                    for (var b = 0; b < batch; b++)
                    {
                        if (model.Length > 0 && rng.Next(3) == 0)
                        {
                            var pos = rng.Next(model.Length);
                            var len = rng.Next(1, Math.Min(5, model.Length - pos) + 1);
                            txt.Delete(pos, len);
                            model.Remove(pos, len);
                        }
                        else
                        {
                            var pos = rng.Next(model.Length + 1);
                            var s   = rng.RandomString();
                            txt.Insert(pos, s);
                            model.Insert(pos, s);
                        }
                    }
                });
                Assert.AreEqual(model.Length, txt.Length);
                if (step % 37 == 0)
                {
                    Assert.AreEqual(model.ToString(), txt.ToString());
                    remote.GetText("t").Insert(0, "r");
                    Exchange(doc, remote);
                    Assert.AreEqual(model.Length + 1, txt.Length);
                    Assert.AreEqual(remote.GetText("t").ToString(), txt.ToString());
                    model.Clear().Append(txt.ToString());
                }
            }
            Assert.AreEqual(model.ToString(), txt.ToString());
        }
    }

    [TestMethod]
    public void ArrayMatchesModel()
    {
        var rng = new Random(12);
        for (var round = 0; round < 20; round++)
        {
            var doc   = Doc(1);
            var arr   = doc.GetArray("a");
            var model = new List<object?>();
            var next  = 0L;
            for (var step = 0; step < 300; step++)
            {
                var batch = rng.Next(4) == 0 ? rng.Next(2, 30) : 1;
                doc.Transact(_ =>
                {
                    for (var b = 0; b < batch; b++)
                    {
                        var op = rng.Next(5);
                        if (model.Count > 0 && op == 0)
                        {
                            var pos = rng.Next(model.Count);
                            var len = rng.Next(1, Math.Min(4, model.Count - pos) + 1);
                            arr.Delete(pos, len);
                            model.RemoveRange(pos, len);
                        }
                        else if (model.Count > 0 && op == 1)
                        {
                            var pos = rng.Next(model.Count);
                            JsonEqual(model[pos], arr.Get(pos));
                        }
                        else if (op == 2)
                        {
                            var items = Enumerable.Range(0, rng.Next(1, 4)).Select(_ => (object?)next++).ToList();
                            arr.Push(items);
                            model.AddRange(items);
                        }
                        else
                        {
                            var pos   = rng.Next(model.Count + 1);
                            var items = Enumerable.Range(0, rng.Next(1, 4)).Select(_ => (object?)next++).ToList();
                            arr.Insert(pos, items);
                            model.InsertRange(pos, items);
                        }
                    }
                });
                Assert.AreEqual(model.Count, arr.Length);
            }
            JsonEqual(model, arr.ToArray());
            var copy = new Doc();
            copy.ApplyUpdate(doc.EncodeStateAsUpdate());
            JsonEqual(model, copy.GetArray("a").ToArray());
        }
    }
}
