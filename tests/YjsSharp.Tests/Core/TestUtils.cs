using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YjsSharp;

namespace YjsSharp.Tests.Core;

/// <summary>Helpers shared by the ported yrs unit tests (yrs: test_utils.rs).</summary>
internal static class T
{
    public const string ExchangeUpdatesOrigin = "exchange_updates";

    public static Doc Doc(ulong clientId, bool gc = true) => new Doc(new DocOptions { ClientId = clientId, GC = gc });

    public static Doc DocNoGc(ulong clientId) => new Doc(new DocOptions { ClientId = clientId, GC = false });

    /// <summary>yrs: exchange_updates.</summary>
    public static void Exchange(params Doc[] docs)
    {
        for (var i = 0; i < docs.Length; i++)
        {
            for (var j = 0; j < docs.Length; j++)
            {
                if (i != j)
                {
                    var a      = docs[i];
                    var b      = docs[j];
                    var update = a.EncodeStateAsUpdate(b.EncodeStateVector());
                    b.ApplyUpdate(update, ExchangeUpdatesOrigin);
                }
            }
        }
    }

    public static Dictionary<string, object?> Attrs(params (string Key, object? Value)[] entries)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in entries)
        {
            d[k] = v;
        }
        return d;
    }

    public static Dictionary<string, object?> Map(params (string Key, object? Value)[] entries) => Attrs(entries);

    public static List<object?> List(params object?[] items) => new List<object?>(items);

    public static Delta Ins(object? value, Dictionary<string, object?>? attrs = null) => Delta.Inserted(value, attrs);

    public static Delta Ret(int n, Dictionary<string, object?>? attrs = null) => Delta.Retained(n, attrs);

    public static Delta Del(int n) => Delta.Deleted(n);

    public static void DeltaEqual(IReadOnlyList<Delta>? expected, IReadOnlyList<Delta>? actual)
    {
        Assert.IsNotNull(actual, "delta is null");
        Assert.IsNotNull(expected);
        var e = string.Join(", ", expected.Select(d => d.ToString()));
        var a = string.Join(", ", actual.Select(d => d.ToString()));
        Assert.AreEqual(expected.Count, actual.Count, $"expected [{e}] but was [{a}]");
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.IsTrue(expected[i].Equals(actual[i]), $"delta mismatch at {i}: expected [{e}] but was [{a}]");
        }
    }

    public static void JsonEqual(object? expected, object? actual)
    {
        if (!YjsSharp.Encoding.AnyValues.DeepEquals(expected, actual))
        {
            Assert.Fail($"expected {Show(expected)} but was {Show(actual)}");
        }
    }

    public static string Show(object? v)
    {
        try
        {
            return YjsSharp.Encoding.JsJson.Stringify(v) ?? "undefined";
        }
        catch (Exception)
        {
            return v?.ToString() ?? "null";
        }
    }
}
