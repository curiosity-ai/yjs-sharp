using Microsoft.VisualStudio.TestTools.UnitTesting;
using YjsSharp;

namespace YjsSharp.Tests.Core;

[TestClass]
public class SmokeTests
{
    [TestMethod]
    public void TextSync()
    {
        var d1 = T.Doc(1);
        var d2 = T.Doc(2);
        d1.GetText("t").Insert(0, "hello ");
        d2.GetText("t").Insert(0, "world");
        T.Exchange(d1, d2);
        Assert.AreEqual(d1.GetText("t").ToString(), d2.GetText("t").ToString());
        Assert.AreEqual("hello world", d1.GetText("t").ToString());
        var v2 = d1.EncodeStateAsUpdateV2();
        var d3 = T.Doc(3);
        d3.ApplyUpdateV2(v2);
        Assert.AreEqual("hello world", d3.GetText("t").ToString());
        System.Console.WriteLine(System.Convert.ToHexString(d1.EncodeStateAsUpdate()));
        System.Console.WriteLine(System.Convert.ToHexString(v2));
    }
}
