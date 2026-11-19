using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YjsSharp.Sync;

namespace YjsSharp.Tests.Sync;

/// <summary>
/// Ports of y-protocols' <c>awareness.test.js</c> and yrs' <c>sync/awareness.rs</c> tests (the yrs ones adapted to
/// the y-protocols semantics this implementation follows: the local state starts as <c>{}</c> with clock 0).
/// </summary>
[TestClass]
public sealed class AwarenessTests
{
    private static Doc NewDoc(ulong clientId) => new Doc(new DocOptions { ClientId = clientId });

    private static AwarenessOptions NoTimer() => new AwarenessOptions { CheckInterval = TimeSpan.Zero };

    [TestMethod]
    public void Awareness_YProtocolsTest()
    {
        using var aw1 = new Awareness(NewDoc(0), NoTimer());
        using var aw2 = new Awareness(NewDoc(1), NoTimer());
        aw1.Update += (_, e) =>
        {
            var enc = AwarenessProtocol.EncodeAwarenessUpdate(aw1, e.All);
            AwarenessProtocol.ApplyAwarenessUpdate(aw2, enc, "custom");
        };
        AwarenessChangeEventArgs? lastChangeLocal = null;
        aw1.Change += (_, e) => lastChangeLocal = e;
        AwarenessChangeEventArgs? lastChange = null;
        aw2.Change += (_, e) => lastChange = e;

        aw1.SetLocalState("{\"x\":3}");
        Assert.AreEqual("{\"x\":3}", aw2.States[0]);
        Assert.AreEqual(1UL, aw2.Meta[0].Clock);
        CollectionAssert.AreEqual(new ulong[] { 0 }, lastChange!.Added.ToArray());
        Assert.AreEqual("custom", lastChange.Origin);
        // the local client is already marked as available when the awareness is created, so it is updated
        AssertEvent(lastChangeLocal, added: new ulong[0], updated: new ulong[] { 0 }, removed: new ulong[0]);
        Assert.AreEqual(Awareness.LocalOrigin, lastChangeLocal!.Origin);

        lastChange      = null;
        lastChangeLocal = null;
        aw1.SetLocalState("{\"x\":4}");
        Assert.AreEqual("{\"x\":4}", aw2.States[0]);
        AssertEvent(lastChangeLocal, added: new ulong[0], updated: new ulong[] { 0 }, removed: new ulong[0]);
        AssertEvent(lastChange, added: new ulong[0], updated: new ulong[] { 0 }, removed: new ulong[0]);

        lastChange      = null;
        lastChangeLocal = null;
        aw1.SetLocalState("{\"x\":4}");
        Assert.IsNull(lastChange);
        Assert.IsNull(lastChangeLocal);
        Assert.AreEqual(3UL, aw2.Meta[0].Clock);

        aw1.SetLocalState(null);
        Assert.AreEqual(1, lastChange!.Removed.Count);
        Assert.IsFalse(aw1.States.ContainsKey(0));
        AssertEvent(lastChangeLocal, added: new ulong[0], updated: new ulong[0], removed: new ulong[] { 0 });
        Assert.IsFalse(aw2.States.ContainsKey(0));
    }

    [TestMethod]
    public void Awareness_YrsExchange()
    {
        using var local  = new Awareness(NewDoc(1), NoTimer());
        using var remote = new Awareness(NewDoc(2), NoTimer());
        AwarenessChangeEventArgs? update       = null;
        AwarenessChangeEventArgs? changeLocal  = null;
        AwarenessChangeEventArgs? changeRemote = null;
        local.Update  += (_, e) => update = e;
        local.Change  += (_, e) => changeLocal = e;
        remote.Change += (_, e) => changeRemote = e;

        void Exchange()
        {
            var e = update;
            update = null;
            if (e != null)
            {
                AwarenessProtocol.ApplyAwarenessUpdate(remote, AwarenessProtocol.EncodeAwarenessUpdate(local, e.All), null);
            }
        }

        local.SetLocalState("{\"x\":3}");
        Exchange();
        Assert.AreEqual("{\"x\":3}", remote.GetState(1));
        Assert.AreEqual(1UL, remote.Meta[1].Clock);
        CollectionAssert.AreEqual(new ulong[] { 1 }, changeRemote!.Added.ToArray());

        local.SetLocalState("{\"x\":4}");
        Exchange();
        Assert.AreEqual("{\"x\":4}", remote.GetState(1));
        AssertEvent(changeRemote, new ulong[0], new ulong[] { 1 }, new ulong[0]);
        AssertEvent(changeLocal, new ulong[0], new ulong[] { 1 }, new ulong[0]);

        changeLocal  = null;
        changeRemote = null;
        local.SetLocalState("{\"x\":4}");
        Exchange();
        Assert.AreEqual("{\"x\":4}", remote.GetState(1));
        Assert.AreEqual(3UL, remote.Meta[1].Clock);
        Assert.IsNull(changeLocal);
        Assert.IsNull(changeRemote);

        local.SetLocalState(null);
        Exchange();
        Assert.AreEqual(1, changeRemote!.Removed.Count);
        Assert.IsNull(local.GetLocalState());
        AssertEvent(changeLocal, new ulong[0], new ulong[0], new ulong[] { 1 });
        Assert.AreEqual(4UL, remote.Meta[1].Clock);
    }

    [TestMethod]
    public void Awareness_SetLocalStateField()
    {
        using var aw = new Awareness(NewDoc(5), NoTimer());
        aw.SetLocalState("{\"user\":{\"name\":\"A\"}}");
        aw.SetLocalStateField("cursor", new Dictionary<string, object?> { ["anchor"] = 1L, ["head"] = 2.5 });
        Assert.AreEqual("{\"user\":{\"name\":\"A\"},\"cursor\":{\"anchor\":1,\"head\":2.5}}", aw.GetLocalState());
        aw.SetLocalStateField("user", "B");
        Assert.AreEqual("{\"user\":\"B\",\"cursor\":{\"anchor\":1,\"head\":2.5}}", aw.GetLocalState());
        aw.SetLocalState(null);
        aw.SetLocalStateField("x", 1L);
        Assert.IsNull(aw.GetLocalState());
    }

    [TestMethod]
    public void Awareness_NormalizesJsonLikeJavaScript()
    {
        using var aw = new Awareness(NewDoc(5), NoTimer());
        aw.SetLocalState("{ \"n\" : 1.0, \"e\": 1E3, \"u\": \"\\u00e9\" }");
        Assert.AreEqual("{\"n\":1,\"e\":1000,\"u\":\"é\"}", aw.GetLocalState());
        Assert.ThrowsException<FormatException>(() => aw.SetLocalState("{not json"));
    }

    [TestMethod]
    public void Awareness_InitialStateIsEmptyObjectWithClockZero()
    {
        using var aw = new Awareness(NewDoc(9), NoTimer());
        Assert.AreEqual(9UL, aw.ClientId);
        Assert.AreEqual("{}", aw.GetLocalState());
        Assert.AreEqual(0UL, aw.Meta[9].Clock);
        Assert.AreEqual(1, aw.States.Count);
    }

    [TestMethod]
    public void Awareness_RemoteCannotRemoveLocalState()
    {
        using var aw = new Awareness(NewDoc(1), NoTimer());
        aw.SetLocalState("{\"a\":1}");
        var clock = aw.Meta[1].Clock;
        var removal = Write((1, clock, "null"));
        AwarenessProtocol.ApplyAwarenessUpdate(aw, removal, "remote");
        Assert.AreEqual("{\"a\":1}", aw.GetLocalState());
        Assert.AreEqual(clock + 1, aw.Meta[1].Clock);
    }

    [TestMethod]
    public void Awareness_RemoveAwarenessStates()
    {
        using var aw = new Awareness(NewDoc(1), NoTimer());
        AwarenessProtocol.ApplyAwarenessUpdate(aw, Write((2, 1, "{\"b\":2}"), (3, 4, "{\"c\":3}")), "remote");
        AwarenessChangeEventArgs? update = null;
        aw.Update += (_, e) => update = e;
        AwarenessProtocol.RemoveAwarenessStates(aw, new ulong[] { 2, 1, 42 }, "custom");
        AssertEvent(update, new ulong[0], new ulong[0], new ulong[] { 2, 1 });
        Assert.AreEqual("custom", update!.Origin);
        CollectionAssert.AreEquivalent(new ulong[] { 3 }, aw.States.Keys.ToArray());
        Assert.AreEqual(1UL, aw.Meta[1].Clock);
        Assert.AreEqual(1UL, aw.Meta[2].Clock);

        update = null;
        AwarenessProtocol.RemoveAwarenessStates(aw, new ulong[] { 2 }, "custom");
        Assert.IsNull(update);
    }

    [TestMethod]
    public void Awareness_OutdatedStatesAreRemovedByTheTimer()
    {
        var time = new ManualTimeProvider();
        using var aw = new Awareness(NewDoc(1), new AwarenessOptions { TimeProvider = time });
        var removedByTimeout = new List<ulong>();
        var renewals         = 0;
        aw.Update += (_, e) =>
        {
            if (Equals(e.Origin, Awareness.TimeoutOrigin))
            {
                removedByTimeout.AddRange(e.Removed);
            }
            else if (Equals(e.Origin, Awareness.LocalOrigin))
            {
                renewals++;
            }
        };
        AwarenessProtocol.ApplyAwarenessUpdate(aw, Write((2, 1, "{\"b\":2}")), "remote");
        time.Advance(TimeSpan.FromSeconds(10));
        AwarenessProtocol.ApplyAwarenessUpdate(aw, Write((3, 1, "{\"c\":3}")), "remote");
        Assert.AreEqual(0, renewals);

        // 15s after it was set, the local state is renewed (clock 0 -> 1) at the next 3s tick
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.AreEqual(1, renewals);
        Assert.AreEqual(1UL, aw.Meta[1].Clock);

        // client 2 is outdated 30s after its update, at the first tick at or after that
        time.Advance(TimeSpan.FromSeconds(13));
        CollectionAssert.AreEqual(Array.Empty<ulong>(), removedByTimeout);
        time.Advance(TimeSpan.FromSeconds(2));
        CollectionAssert.AreEqual(new ulong[] { 2 }, removedByTimeout);
        Assert.IsNull(aw.GetState(2));
        Assert.AreEqual("{\"c\":3}", aw.GetState(3));

        // client 3 was updated at 10s: still there at the 39s tick, gone at the 42s one
        time.Advance(TimeSpan.FromSeconds(8));
        CollectionAssert.AreEqual(new ulong[] { 2 }, removedByTimeout);
        time.Advance(TimeSpan.FromSeconds(4));
        CollectionAssert.AreEqual(new ulong[] { 2, 3 }, removedByTimeout);
        Assert.AreEqual("{}", aw.GetLocalState());
    }

    [TestMethod]
    public void Awareness_CheckOutdatedCanBeDrivenManually()
    {
        var time = new ManualTimeProvider();
        using var aw = new Awareness(NewDoc(1), new AwarenessOptions { TimeProvider = time, CheckInterval = TimeSpan.Zero, OutdatedTimeout = TimeSpan.FromSeconds(10) });
        Assert.AreEqual(0, time.ActiveTimers);
        AwarenessProtocol.ApplyAwarenessUpdate(aw, Write((2, 1, "{\"b\":2}")), "remote");
        time.Advance(TimeSpan.FromSeconds(9));
        aw.CheckOutdated();
        Assert.AreEqual("{\"b\":2}", aw.GetState(2));
        Assert.AreEqual(1UL, aw.Meta[1].Clock); // renewed after half the timeout
        time.Advance(TimeSpan.FromSeconds(1));
        aw.CheckOutdated();
        Assert.IsNull(aw.GetState(2));
        Assert.AreEqual(1UL, aw.Meta[2].Clock);
    }

    [TestMethod]
    public void Awareness_DisposeClearsLocalStateAndStopsTimer()
    {
        var time = new ManualTimeProvider();
        var aw   = new Awareness(NewDoc(1), new AwarenessOptions { TimeProvider = time });
        Assert.AreEqual(1, time.ActiveTimers);
        AwarenessChangeEventArgs? update = null;
        aw.Update += (_, e) => update = e;
        aw.Dispose();
        Assert.AreEqual(0, time.ActiveTimers);
        AssertEvent(update, new ulong[0], new ulong[0], new ulong[] { 1 });
        Assert.IsNull(aw.GetLocalState());
        aw.Dispose();
        Assert.ThrowsException<ObjectDisposedException>(() => aw.SetLocalState("{}"));
    }

    [TestMethod]
    public void Awareness_DocDestroyDisposesAwareness()
    {
        var doc = NewDoc(1);
        var aw  = new Awareness(doc, NoTimer());
        doc.Dispose();
        Assert.IsNull(aw.GetLocalState());
    }

    [TestMethod]
    public void Awareness_ModifyAwarenessUpdate()
    {
        var update   = Write((7, 3, "{\"user\":{\"name\":\"Mallory\"}}"), (8, 1, "null"));
        var modified = AwarenessProtocol.ModifyAwarenessUpdate(update, s => s == "null" ? s : s.Replace("Mallory", "Server"));
        var entries  = AwarenessProtocol.DecodeAwarenessUpdate(modified);
        Assert.AreEqual(2, entries.Count);
        Assert.AreEqual(new AwarenessUpdateEntry(7, 3, "{\"user\":{\"name\":\"Server\"}}"), entries[0]);
        Assert.AreEqual(new AwarenessUpdateEntry(8, 1, "null"), entries[1]);
    }

    [TestMethod]
    public void Awareness_DecodeRejectsLengthAmplification()
    {
        // yrs regression: a huge declared client count must be rejected, not eagerly allocated
        var adversarial = new byte[] { 0xFF, 0xFF, 0xFF, 0x7A };
        Assert.ThrowsException<FormatException>(() => AwarenessProtocol.DecodeAwarenessUpdate(adversarial));
        using var aw = new Awareness(NewDoc(1), NoTimer());
        Assert.ThrowsException<FormatException>(() => AwarenessProtocol.ApplyAwarenessUpdate(aw, adversarial, null));
    }

    [TestMethod]
    public void Awareness_EncodeUnknownClientThrows()
    {
        using var aw = new Awareness(NewDoc(1), NoTimer());
        Assert.ThrowsException<ArgumentException>(() => AwarenessProtocol.EncodeAwarenessUpdate(aw, new ulong[] { 99 }));
    }

    internal static byte[] Write(params (ulong Client, ulong Clock, string Json)[] entries)
    {
        var encoder = new YjsSharp.Encoding.Encoder();
        encoder.WriteVarUInt((ulong)entries.Length);
        foreach (var (client, clock, json) in entries)
        {
            encoder.WriteVarUInt(client);
            encoder.WriteVarUInt(clock);
            encoder.WriteVarString(json);
        }
        return encoder.ToArray();
    }

    private static void AssertEvent(AwarenessChangeEventArgs? e, ulong[] added, ulong[] updated, ulong[] removed)
    {
        Assert.IsNotNull(e);
        CollectionAssert.AreEqual(added, e.Added.ToArray(), "added");
        CollectionAssert.AreEqual(updated, e.Updated.ToArray(), "updated");
        CollectionAssert.AreEqual(removed, e.Removed.ToArray(), "removed");
    }
}
