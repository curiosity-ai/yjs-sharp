using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YjsSharp;
using YjsSharp.Encoding;
using static YjsSharp.Tests.Core.T;

namespace YjsSharp.Tests.Core;

/// <summary>
/// Port of the unit tests in yrs/src/encoding/mod.rs, any.rs and state_vector.rs, plus byte-level checks of the lib0
/// Any encoding and JSON.stringify against values produced by lib0 0.2.x / Node.
/// </summary>
[TestClass]
public class EncodingTests
{
    private static string Hex(object? value)
    {
        var e = new Encoder();
        e.WriteAny(value);
        return Convert.ToHexString(e.ToArray()).ToLowerInvariant();
    }

    [TestMethod]
    public void AnyEncodingMatchesLib0()
    {
        Assert.AreEqual("7d00", Hex(0));
        Assert.AreEqual("7d40", Hex(-0.0));
        Assert.AreEqual("7d01", Hex(1L));
        Assert.AreEqual("7d01", Hex(1.0));
        Assert.AreEqual("7d45", Hex(-5));
        Assert.AreEqual("7dbfffffff0f", Hex(2147483647));
        Assert.AreEqual("7dffffffff0f", Hex(-2147483647));
        Assert.AreEqual("7c4f000000", Hex(2147483648L));
        Assert.AreEqual("7ccf000000", Hex(-2147483648));
        Assert.AreEqual("7c4f800000", Hex(4294967296L));
        Assert.AreEqual("7c3fc00000", Hex(1.5));
        Assert.AreEqual("7b3fb999999999999a", Hex(0.1));
        Assert.AreEqual("7b7ff8000000000000", Hex(double.NaN));
        Assert.AreEqual("7c7f800000", Hex(double.PositiveInfinity));
        Assert.AreEqual("7cff800000", Hex(double.NegativeInfinity));
        Assert.AreEqual("7b7e37e43c8800759c", Hex(1e300));
        Assert.AreEqual("7b433fffffffffffff", Hex(9007199254740991L));
        Assert.AreEqual("770a68c3a96c6c6ff09f9880", Hex("héllo😀"));
        Assert.AreEqual("7704efbfbd78", Hex("\ud800x"));
        Assert.AreEqual("78", Hex(true));
        Assert.AreEqual("79", Hex(false));
        Assert.AreEqual("7e", Hex(null));
        Assert.AreEqual("7f", Hex(Undefined.Value));
        Assert.AreEqual("75037d017701617e", Hex(List(1, "a", null)));
        Assert.AreEqual("760301317d0201627d010161750178", Hex(Map(("b", 1), ("1", 2), ("a", List(true)))));
        Assert.AreEqual("7403010203", Hex(new byte[] { 1, 2, 3 }));
        Assert.AreEqual("7a000000000000007b", Hex(new BigInteger(123)));
        Assert.AreEqual("7afffffffffffffffb", Hex(new BigInteger(-5)));
        Assert.AreEqual("760401327d020231307d0101787d030230317d04", Hex(Map(("10", 1), ("2", 2), ("x", 3), ("01", 4))));
    }

    [TestMethod]
    public void AnyDecodingKeepsTheWireTypes()
    {
        foreach (var hex in new[] { "7d00", "7d40", "7c3fc00000", "7b3fb999999999999a", "7a000000000000007b", "760401327d020231307d0101787d030230317d04", "7f", "75037d017701617e" })
        {
            var bytes   = Convert.FromHexString(hex);
            var decoded = new Decoder(bytes).ReadAny();
            Assert.AreEqual(hex, Hex(decoded), "re-encoding must reproduce the bytes");
        }
        Assert.IsInstanceOfType(new Decoder(Convert.FromHexString("7d01")).ReadAny(), typeof(long));
        Assert.IsInstanceOfType(new Decoder(Convert.FromHexString("7a000000000000007b")).ReadAny(), typeof(BigInteger));
    }

    [TestMethod]
    public void JsonStringifyMatchesJavaScript()
    {
        var value = Map(
            ("a", 1e21), ("b", 1e-7), ("c", 123.456), ("d", -0.0), ("e", "\u0001\"\\\u2028\ud800"),
            ("f", List(Undefined.Value, double.NaN)), ("g", 0.000001), ("h", 1.5e-10), ("i", 9007199254740994.0),
            ("j", 100), ("k", 5e-324), ("l", 1.7976931348623157e308));
        Assert.AreEqual(
            "{\"a\":1e+21,\"b\":1e-7,\"c\":123.456,\"d\":0,\"e\":\"\\u0001\\\"\\\\\u2028\\ud800\",\"f\":[null,null],\"g\":0.000001,\"h\":1.5e-10,\"i\":9007199254740994,\"j\":100,\"k\":5e-324,\"l\":1.7976931348623157e+308}",
            JsJson.Stringify(value));
        Assert.AreEqual("{\"b\":true}", JsJson.Stringify(Map(("a", Undefined.Value), ("b", true))));
        Assert.IsNull(JsJson.Stringify(Undefined.Value));
        JsonEqual(Map(("a", List(1, 2.5, "x", null, true))), JsJson.Parse("{ \"a\" : [1, 2.5, \"x\", null, true] }"));
    }

    // yrs: encoding/mod.rs encoding_prop (property test with a fixed seed)
    [TestMethod]
    public void EncodingProp()
    {
        var rng = new Random(1234);
        for (var iter = 0; iter < 2000; iter++)
        {
            var e    = new Encoder();
            var kind = rng.Next(12);
            var u8   = (byte)rng.Next(256);
            var u16  = (ushort)rng.Next(65536);
            var u32  = (uint)rng.NextInt64(0, 1L << 32);
            var u53  = (ulong)rng.NextInt64(0, 1L << 53);
            var i53  = rng.NextInt64(-(1L << 53), 1L << 53);
            var buf  = new byte[rng.Next(0, 40)];
            rng.NextBytes(buf);
            var str  = RandomString(rng);
            var f32  = (float)(rng.NextDouble() * 1000 - 500);
            var f64  = rng.NextDouble() * 1e10 - 5e9;
            switch (kind)
            {
                case 0: e.WriteUInt8(u8); break;
                case 1: e.WriteUInt16(u16); break;
                case 2: e.WriteUInt32(u32); break;
                case 3: e.WriteUInt32BigEndian(u32); break;
                case 4: e.WriteVarUInt(u53); break;
                case 5: e.WriteVarInt(i53); break;
                case 6: e.WriteUInt8Array(buf); break;
                case 7: e.WriteVarUInt8Array(buf); break;
                case 8: e.WriteVarString(str); break;
                case 9: e.WriteFloat32(f32); break;
                case 10: e.WriteFloat64(f64); break;
                default: e.WriteBigInt64(i53); break;
            }
            var d = new Decoder(e.ToArray());
            switch (kind)
            {
                case 0: Assert.AreEqual(u8, d.ReadUInt8()); break;
                case 1: Assert.AreEqual(u16, d.ReadUInt16()); break;
                case 2: Assert.AreEqual(u32, d.ReadUInt32()); break;
                case 3: Assert.AreEqual(u32, d.ReadUInt32BigEndian()); break;
                case 4: Assert.AreEqual(u53, d.ReadVarUInt()); break;
                case 5: Assert.AreEqual(i53, d.ReadVarInt()); break;
                case 6: CollectionAssert.AreEqual(buf, d.ReadUInt8Array(buf.Length)); break;
                case 7: CollectionAssert.AreEqual(buf, d.ReadVarUInt8Array()); break;
                case 8: Assert.AreEqual(str, d.ReadVarString()); break;
                case 9: Assert.AreEqual(f32, d.ReadFloat32()); break;
                case 10: Assert.AreEqual(f64, d.ReadFloat64()); break;
                default: Assert.AreEqual(i53, d.ReadBigInt64()); break;
            }
            Assert.IsFalse(d.HasContent);
        }
    }

    private static string RandomString(Random rng)
    {
        var len = rng.Next(0, 20);
        var sb  = new System.Text.StringBuilder();
        for (var i = 0; i < len; i++)
        {
            switch (rng.Next(4))
            {
                case 0: sb.Append((char)rng.Next('a', 'z' + 1)); break;
                case 1: sb.Append((char)rng.Next(0x80, 0x800)); break;
                case 2: sb.Append((char)rng.Next(0x800, 0xD7FF)); break;
                default: sb.Append(char.ConvertFromUtf32(rng.Next(0x10000, 0x10FFFF))); break;
            }
        }
        return sb.ToString();
    }

    private static object? RandomAny(Random rng, int depth)
    {
        switch (rng.Next(depth > 3 ? 7 : 9))
        {
            case 0: return null;
            case 1: return Undefined.Value;
            case 2: return rng.Next(2) == 1;
            case 3: return rng.NextDouble() * 1e6;
            case 4: return (long)rng.Next(-100000, 100000);
            case 5: return RandomString(rng);
            case 6:
                {
                    var b = new byte[rng.Next(0, 10)];
                    rng.NextBytes(b);
                    return b;
                }
            case 7:
                {
                    var list = new List<object?>();
                    for (var i = rng.Next(0, 6); i > 0; i--)
                    {
                        list.Add(RandomAny(rng, depth + 1));
                    }
                    return list;
                }
            default:
                {
                    var dict = new Dictionary<string, object?>();
                    for (var i = rng.Next(0, 6); i > 0; i--)
                    {
                        dict["k" + rng.Next(100)] = RandomAny(rng, depth + 1);
                    }
                    return dict;
                }
        }
    }

    // yrs: encoding/mod.rs encoding_any_prop
    [TestMethod]
    public void EncodingAnyProp()
    {
        var rng = new Random(99);
        for (var i = 0; i < 1000; i++)
        {
            var any = RandomAny(rng, 0);
            var e   = new Encoder();
            e.WriteAny(any);
            var copy = new Decoder(e.ToArray()).ReadAny();
            JsonEqual(any, copy);
            var e2 = new Encoder();
            e2.WriteAny(copy);
            CollectionAssert.AreEqual(e.ToArray(), e2.ToArray());
        }
    }

    // yrs: any.rs decode_map_rejects_length_amplification / decode_array_rejects_length_amplification / string
    [TestMethod]
    public void AnyDecodeRejectsLengthAmplification()
    {
        foreach (var tag in new byte[] { 118, 117, 119, 116 })
        {
            var adversarial = new byte[] { tag, 0xFF, 0xFF, 0xFF, 0x7A };
            Assert.ThrowsExactly<EndOfDataException>(() => new Decoder(adversarial).ReadAny());
        }
    }

    // yrs: state_vector.rs decode_rejects_length_amplification
    [TestMethod]
    public void StateVectorDecodeRejectsLengthAmplification()
    {
        Assert.ThrowsExactly<EndOfDataException>(() => Y.DecodeStateVector(new byte[] { 255, 255, 255, 122 }));
    }

    private static Dictionary<ulong, uint> Sv(params (ulong Client, uint Clock)[] entries) => entries.ToDictionary(e => e.Client, e => e.Clock);

    // yrs: state_vector.rs ordering
    [TestMethod]
    public void StateVectorOrdering()
    {
        static Dictionary<ulong, uint> S(uint a, uint b, uint c) => Sv((1, a), (2, b), (3, c));
        Assert.AreEqual(0, Y.CompareStateVectors(S(1, 2, 3), S(1, 2, 3)));
        Assert.AreEqual(-1, Y.CompareStateVectors(S(1, 2, 2), S(1, 2, 3)));
        Assert.AreEqual(1, Y.CompareStateVectors(S(2, 2, 3), S(1, 2, 3)));
        Assert.IsNull(Y.CompareStateVectors(S(3, 2, 1), S(1, 2, 3)));
    }

    // yrs: state_vector.rs ordering_missing_fields
    [TestMethod]
    public void StateVectorOrderingMissingFields()
    {
        Assert.IsNull(Y.CompareStateVectors(Sv((1, 1), (2, 2)), Sv((2, 1), (3, 2))));
        Assert.IsNull(Y.CompareStateVectors(Sv((1, 1), (2, 2)), Sv((1, 1), (2, 1), (3, 2))));
        Assert.AreEqual(1, Y.CompareStateVectors(Sv((1, 1), (2, 2), (3, 3)), Sv((2, 2), (3, 3))));
        Assert.AreEqual(-1, Y.CompareStateVectors(Sv((2, 2), (3, 2)), Sv((1, 1), (2, 2), (3, 2))));
        Assert.AreEqual(0, Y.CompareStateVectors(Sv(), Sv()));
    }

    // yrs: state_vector.rs ordering_one_of
    [TestMethod]
    public void StateVectorOrderingOneOf()
    {
        var doc = Doc(1);
        doc.GetText("text").Insert(0, "a");
        Assert.AreEqual(1, Y.CompareStateVectors(doc.StateVector, Sv()));
    }

    [TestMethod]
    public void RleEncodersRoundTrip()
    {
        var rng = new Random(5);
        for (var iter = 0; iter < 200; iter++)
        {
            var values = Enumerable.Range(0, rng.Next(0, 50)).Select(_ => (ulong)rng.Next(0, 4)).ToList();
            var uintRle = new UIntOptRleEncoder();
            var intDiff = new IntDiffOptRleEncoder();
            var bytes   = new RleByteEncoder();
            foreach (var v in values)
            {
                uintRle.Write(v);
                intDiff.Write((long)v - 2);
                bytes.Write((byte)v);
            }
            var u = new UIntOptRleDecoder(new Decoder(uintRle.ToArray()));
            var o = new Encoder();
            intDiff.FlushInto(o);
            var i = new IntDiffOptRleDecoder(new Decoder(o.ToArray()).ReadVarUInt8ArrayAsDecoder());
            var b = new RleByteDecoder(new Decoder(bytes.Encoder.ToArray()));
            foreach (var v in values)
            {
                Assert.AreEqual(v, u.Read());
                Assert.AreEqual((long)v - 2, i.Read());
                Assert.AreEqual((byte)v, b.Read());
            }
        }
    }
}
