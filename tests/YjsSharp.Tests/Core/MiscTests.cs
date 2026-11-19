using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YjsSharp;
using YjsSharp.Encoding;
using static YjsSharp.Tests.Core.T;

namespace YjsSharp.Tests.Core;

/// <summary>
/// Port of the unit tests in yrs/src/alt.rs, observer.rs, block.rs, transact.rs and updates/decoder.rs.
/// atomic.rs tests yrs' AtomicRef, which has no counterpart in this port.
/// </summary>
[TestClass]
public class MiscTests
{
    // yrs: alt.rs merge_updates_compatibility_v1
    [TestMethod]
    public void MergeUpdatesCompatibilityV1()
    {
        var a = new byte[] { 1, 1, 220, 240, 237, 172, 15, 0, 4, 1, 4, 116, 101, 115, 116, 3, 97, 98, 99, 0 };
        var b = new byte[] { 1, 1, 201, 139, 250, 201, 1, 0, 4, 1, 4, 116, 101, 115, 116, 2, 100, 101, 0 };
        var expected = new byte[]
        {
            2, 1, 220, 240, 237, 172, 15, 0, 4, 1, 4, 116, 101, 115, 116, 3, 97, 98, 99, 1, 201,
            139, 250, 201, 1, 0, 4, 1, 4, 116, 101, 115, 116, 2, 100, 101, 0,
        };
        CollectionAssert.AreEqual(expected, Y.MergeUpdates(new[] { a, b }));
    }

    // yrs: alt.rs merge_updates_compatibility_v1_2
    [TestMethod]
    public void MergeUpdatesCompatibilityV1Part2()
    {
        var a        = new byte[] { 1, 1, 129, 231, 135, 164, 7, 0, 4, 1, 4, 49, 50, 51, 52, 1, 97, 0 };
        var b        = new byte[] { 1, 1, 129, 231, 135, 164, 7, 1, 68, 129, 231, 135, 164, 7, 0, 1, 98, 0 };
        var expected = new byte[] { 1, 2, 129, 231, 135, 164, 7, 0, 4, 1, 4, 49, 50, 51, 52, 1, 97, 68, 129, 231, 135, 164, 7, 0, 1, 98, 0 };
        CollectionAssert.AreEqual(expected, Y.MergeUpdates(new[] { a, b }));
    }

    // yrs: alt.rs encode_state_vector_compatibility_v1
    [TestMethod]
    public void EncodeStateVectorCompatibilityV1()
    {
        var update = new byte[]
        {
            2, 1, 220, 240, 237, 172, 15, 0, 4, 1, 4, 116, 101, 115, 116, 3, 97, 98, 99, 1, 201,
            139, 250, 201, 1, 0, 4, 1, 4, 116, 101, 115, 116, 2, 100, 101, 0,
        };
        CollectionAssert.AreEqual(new byte[] { 2, 220, 240, 237, 172, 15, 3, 201, 139, 250, 201, 1, 2 }, Y.EncodeStateVectorFromUpdate(update));
    }

    // yrs: alt.rs diff_updates_compatibility_v1
    [TestMethod]
    public void DiffUpdatesCompatibilityV1()
    {
        var stateVector = new byte[] { 1, 148, 189, 145, 162, 9, 3 };
        var update = new byte[] { 1, 2, 148, 189, 145, 162, 9, 0, 4, 1, 4, 116, 101, 115, 116, 3, 97, 98, 99, 68, 148, 189, 145, 162, 9, 0, 2, 100, 101, 0 };
        var expected = new byte[] { 1, 1, 148, 189, 145, 162, 9, 3, 68, 148, 189, 145, 162, 9, 0, 2, 100, 101, 0 };
        CollectionAssert.AreEqual(expected, Y.DiffUpdate(update, stateVector));
        CollectionAssert.AreEqual(Y.ConvertUpdateFormatV1ToV2(expected), Y.DiffUpdateV2(Y.ConvertUpdateFormatV1ToV2(update), stateVector));
    }

    // yrs: observer.rs subscribe_and_unsubscribe, subscribers_predicate, unsubscribe, unsubscribe2
    [TestMethod]
    public void SubscribeAndUnsubscribe()
    {
        var list = new EventHandlerList<int>();
        Assert.AreEqual(0, list.Count);
        var s1   = 0;
        var s2   = 0;
        var sub1 = list.Add((v, _) => s1 = v);
        var sub2 = list.Add((v, _) => s2 = v * 2);
        Assert.AreEqual(2, list.Count);
        list.Call(1, null!);
        Assert.AreEqual(1, s1);
        Assert.AreEqual(2, s2);
        list.Call(2, null!);
        Assert.AreEqual(2, s1);
        Assert.AreEqual(4, s2);
        sub1.Dispose();
        sub2.Dispose();
        sub2.Dispose();
        Assert.AreEqual(0, list.Count);
        list.Call(3, null!);
        Assert.AreEqual(2, s1);
        Assert.AreEqual(4, s2);
        var subs = Enumerable.Range(0, 100).Select(_ => list.Add((_, _) => { })).ToList();
        Assert.AreEqual(100, list.Count);
        subs.ForEach(s => s.Dispose());
        Assert.AreEqual(0, list.Count);
    }

    // yrs: observer.rs unsubscribe_during_trigger. Yjs calls every handler registered when the event started (it
    // iterates a snapshot of the listener array), so a handler removed during dispatch still runs once.
    [TestMethod]
    public void UnsubscribeDuringTrigger()
    {
        var calls = 0;
        var doc   = new Doc();
        var txt   = doc.GetText("t");
        IDisposable? a = null;
        IDisposable? b = null;
        a = txt.Observe(_ =>
        {
            calls++;
            b!.Dispose();
        });
        b = txt.Observe(_ =>
        {
            calls++;
            a!.Dispose();
        });
        txt.Insert(0, "x");
        Assert.AreEqual(2, calls);
        txt.Insert(0, "y");
        Assert.AreEqual(2, calls);
    }

    // yrs: observer.rs subscribe_during_trigger_replaces_callback (a handler added during dispatch runs from the next event)
    [TestMethod]
    public void SubscribeDuringTrigger()
    {
        var log = new List<string>();
        var doc = new Doc();
        var txt = doc.GetText("t");
        IDisposable? first = null;
        first = txt.Observe(_ =>
        {
            log.Add("a");
            first!.Dispose();
            txt.Observe(_ => log.Add("b"));
        });
        txt.Insert(0, "x");
        CollectionAssert.AreEqual(new[] { "a" }, log);
        txt.Insert(0, "y");
        CollectionAssert.AreEqual(new[] { "a", "b" }, log);
    }

    [TestMethod]
    public void ObserverExceptionsDoNotStopOtherObserversOrCleanup()
    {
        var doc   = new Doc();
        var txt   = doc.GetText("t");
        var other = false;
        var thrown = false;
        txt.Observe(_ =>
        {
            if (!thrown)
            {
                thrown = true;
                throw new InvalidOperationException("boom");
            }
        });
        txt.Observe(_ => other = true);
        byte[]? update = null;
        doc.Update += (_, e) => update = e.Update;
        Assert.ThrowsExactly<InvalidOperationException>(() => txt.Insert(0, "x"));
        Assert.IsTrue(other);
        Assert.IsNotNull(update);
        Assert.IsNull(doc.CurrentTransaction);
        txt.Insert(1, "y");
        Assert.AreEqual("xy", txt.ToString());
    }

    // yrs: block.rs splittable_string_len / push_str / split_str (UTF-16 offsets; a split inside a surrogate pair
    // replaces both halves with U+FFFD, like Yjs)
    [TestMethod]
    public void SplittableString()
    {
        const string s = "Zażółć gęślą jaźń😀";
        Assert.AreEqual(34, System.Text.Encoding.UTF8.GetByteCount(s + " 女"));
        Assert.AreEqual(21, (s + " 女").Length);
        var doc = new Doc();
        var txt = doc.GetText("t");
        txt.Insert(0, s);
        txt.Insert(txt.Length, "ありがとうございます");
        Assert.AreEqual(s + "ありがとうございます", txt.ToString());
        Assert.AreEqual(29, txt.Length);
        txt.Insert(19, "|");
        Assert.AreEqual("Zażółć gęślą jaźń😀|ありがとうございます", txt.ToString());
        var doc2 = new Doc();
        var t2   = doc2.GetText("t");
        t2.Insert(0, s + "ありがとうございます");
        t2.Insert(18, "|");
        Assert.AreEqual("Zażółć gęślą jaźń�|�ありがとうございます", t2.ToString());
    }

    // yrs: transact.rs multi_thread_transact_mut (callers serialize access to the single-writer document)
    [TestMethod]
    public void MultiThreadTransact()
    {
        var doc  = new Doc();
        var txt  = doc.GetText("text");
        var gate = new object();
        const int n = 3;
        var threads = Enumerable.Range(0, n).Select(i => new System.Threading.Thread(() =>
        {
            System.Threading.Thread.Sleep(new Random(i).Next(0, 5) * 10);
            lock (gate)
            {
                doc.Transact(_ => txt.Insert(0, "a"));
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.AreEqual(new string('a', n), txt.ToString());
    }

    // yrs: updates/decoder.rs decode_v2_unterminated_varint_returns_error
    [TestMethod]
    public void DecodeV2UnterminatedVarintReturnsError()
    {
        foreach (var bytes in new[] { new byte[] { 0x80, 0xFF }, new byte[] { 0x00, 0xFF }, new byte[] { 0x00, 0x80 }, new byte[] { 0x00, 0xFF, 0xFF, 0xFF } })
        {
            Assert.ThrowsExactly<EndOfDataException>(() => new Doc().ApplyUpdateV2(bytes));
        }
    }

    private static void AssertRejected(Action a)
    {
        try
        {
            a();
        }
        catch (FormatException)
        {
            return;
        }
        Assert.Fail("malformed input accepted");
    }

    // yrs: updates/decoder.rs decode_v2_long_varint_returns_error
    [TestMethod]
    public void DecodeV2LongVarintReturnsError()
    {
        for (var len = 1; len < 40; len++)
        {
            var buf = new List<byte> { 0x00 };
            buf.AddRange(Enumerable.Repeat((byte)0xFF, len));
            AssertRejected(() => new Doc().ApplyUpdateV2(buf.ToArray()));
            buf.Add(0x01);
            AssertRejected(() => new Doc().ApplyUpdateV2(buf.ToArray()));
        }
    }

    // yrs: updates/decoder.rs decoder_v2_read_usize_bounds / decoder_v2_read_buf_length_overflow
    [TestMethod]
    public void DecoderReadVarUIntBounds()
    {
        static ulong Read(params byte[] b) => new Decoder(b).ReadVarUInt();
        Assert.ThrowsExactly<EndOfDataException>(() => Read());
        Assert.ThrowsExactly<EndOfDataException>(() => Read(0xFF));
        Assert.ThrowsExactly<EndOfDataException>(() => Read(0xFF, 0xFF));
        Assert.AreEqual(0ul, Read(0x00));
        Assert.AreEqual(127ul, Read(0x7F));
        Assert.AreEqual(128ul, Read(0x80, 0x01));
        Assert.AreEqual(ulong.MaxValue, Read(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01));
        AssertRejected(() => Read(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01));
        var buf = Enumerable.Repeat((byte)0xFF, 9).Concat(new byte[] { 0x01, 0x00 }).ToArray();
        AssertRejected(() => new Decoder(buf).ReadVarUInt8Array());
    }

    // yrs: updates/decoder.rs decode_v2_malformed_input_never_panics
    [TestMethod]
    public void MalformedInputNeverCrashes()
    {
        static void Try(byte[] bytes)
        {
            foreach (var v2 in new[] { false, true })
            {
                try
                {
                    var doc = new Doc();
                    if (v2)
                    {
                        doc.ApplyUpdateV2(bytes);
                    }
                    else
                    {
                        doc.ApplyUpdate(bytes);
                    }
                }
                catch (FormatException)
                {
                }
                try
                {
                    if (v2)
                    {
                        Y.MergeUpdatesV2(new[] { bytes, bytes });
                    }
                    else
                    {
                        Y.EncodeStateVectorFromUpdate(bytes);
                    }
                }
                catch (FormatException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                catch (NotSupportedException)
                {
                }
                catch (InvalidCastException)
                {
                }
            }
        }
        for (var a = 0; a <= 255; a++)
        {
            Try(new[] { (byte)a });
            for (var b = 0; b <= 255; b += 3)
            {
                Try(new[] { (byte)a, (byte)b });
            }
        }
        var state = 0x2545F4914F6CDD1DUL;
        ulong Next()
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            return state;
        }
        for (var i = 0; i < 5000; i++)
        {
            var len = (int)(Next() % 24);
            var buf = new byte[len];
            for (var k = 0; k < len; k++)
            {
                buf[k] = (Next() % 4) switch
                {
                    0 => 0xFF,
                    1 => (byte)(0x80 | ((byte)Next() & 0x7F)),
                    _ => (byte)Next(),
                };
            }
            Try(buf);
        }
    }
}
