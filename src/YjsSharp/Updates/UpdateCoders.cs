using System;
using System.Collections.Generic;
using YjsSharp.Encoding;

namespace YjsSharp.Updates;

internal interface IDSEncoder
{
    Encoder RestEncoder { get; set; }

    void ResetDsCurVal();

    void WriteDsClock(uint clock);

    void WriteDsLen(uint len);

    byte[] ToArray();
}

internal interface IUpdateEncoder : IDSEncoder
{
    void WriteLeftID(ID id);

    void WriteRightID(ID id);

    void WriteClient(ulong client);

    void WriteInfo(byte info);

    void WriteString(string s);

    void WriteParentInfo(bool isYKey);

    void WriteTypeRef(uint info);

    void WriteLen(uint len);

    void WriteAny(object? any);

    void WriteBuf(byte[] buf);

    void WriteJson(object? embed);

    void WriteKey(string key);
}

internal interface IDSDecoder
{
    Decoder RestDecoder { get; }

    void ResetDsCurVal();

    uint ReadDsClock();

    uint ReadDsLen();
}

internal interface IUpdateDecoder : IDSDecoder
{
    ID ReadLeftID();

    ID ReadRightID();

    ulong ReadClient();

    byte ReadInfo();

    string ReadString();

    bool ReadParentInfo();

    uint ReadTypeRef();

    uint ReadLen();

    object? ReadAny();

    byte[] ReadBuf();

    object? ReadJson();

    string ReadKey();
}

internal class DSEncoderV1 : IDSEncoder
{
    public Encoder RestEncoder { get; set; } = new Encoder();

    public void ResetDsCurVal()
    {
    }

    public void WriteDsClock(uint clock) => RestEncoder.WriteVarUInt(clock);

    public void WriteDsLen(uint len) => RestEncoder.WriteVarUInt(len);

    public virtual byte[] ToArray() => RestEncoder.ToArray();
}

internal sealed class UpdateEncoderV1 : DSEncoderV1, IUpdateEncoder
{
    public void WriteLeftID(ID id)
    {
        RestEncoder.WriteVarUInt(id.Client);
        RestEncoder.WriteVarUInt(id.Clock);
    }

    public void WriteRightID(ID id)
    {
        RestEncoder.WriteVarUInt(id.Client);
        RestEncoder.WriteVarUInt(id.Clock);
    }

    public void WriteClient(ulong client) => RestEncoder.WriteVarUInt(client);

    public void WriteInfo(byte info) => RestEncoder.WriteUInt8(info);

    public void WriteString(string s) => RestEncoder.WriteVarString(s);

    public void WriteParentInfo(bool isYKey) => RestEncoder.WriteVarUInt(isYKey ? 1UL : 0UL);

    public void WriteTypeRef(uint info) => RestEncoder.WriteVarUInt(info);

    public void WriteLen(uint len) => RestEncoder.WriteVarUInt(len);

    public void WriteAny(object? any) => RestEncoder.WriteAny(any);

    public void WriteBuf(byte[] buf) => RestEncoder.WriteVarUInt8Array(buf);

    public void WriteJson(object? embed)
    {
        var json = JsJson.Stringify(embed);
        if (json is null)
        {
            throw new InvalidOperationException("Cannot encode undefined as JSON");
        }
        RestEncoder.WriteVarString(json);
    }

    public void WriteKey(string key) => RestEncoder.WriteVarString(key);
}

internal class DSEncoderV2 : IDSEncoder
{
    private uint _dsCurrVal;

    public Encoder RestEncoder { get; set; } = new Encoder();

    public void ResetDsCurVal() => _dsCurrVal = 0;

    public void WriteDsClock(uint clock)
    {
        var diff = clock - _dsCurrVal;
        _dsCurrVal = clock;
        RestEncoder.WriteVarUInt(diff);
    }

    public void WriteDsLen(uint len)
    {
        if (len == 0)
        {
            throw new InvalidOperationException("Delete set ranges must not be empty");
        }
        RestEncoder.WriteVarUInt(len - 1);
        _dsCurrVal += len;
    }

    public virtual byte[] ToArray() => RestEncoder.ToArray();
}

internal sealed class UpdateEncoderV2 : DSEncoderV2, IUpdateEncoder
{
    private          uint                 _keyClock;
    private readonly IntDiffOptRleEncoder _keyClockEncoder   = new IntDiffOptRleEncoder();
    private readonly UIntOptRleEncoder    _clientEncoder     = new UIntOptRleEncoder();
    private readonly IntDiffOptRleEncoder _leftClockEncoder  = new IntDiffOptRleEncoder();
    private readonly IntDiffOptRleEncoder _rightClockEncoder = new IntDiffOptRleEncoder();
    private readonly RleByteEncoder       _infoEncoder       = new RleByteEncoder();
    private readonly StringEncoder        _stringEncoder     = new StringEncoder();
    private readonly RleByteEncoder       _parentInfoEncoder = new RleByteEncoder();
    private readonly UIntOptRleEncoder    _typeRefEncoder    = new UIntOptRleEncoder();
    private readonly UIntOptRleEncoder    _lenEncoder        = new UIntOptRleEncoder();

    public override byte[] ToArray()
    {
        var encoder = new Encoder(RestEncoder.Length + 64);
        encoder.WriteVarUInt(0);
        _keyClockEncoder.FlushInto(encoder);
        _clientEncoder.FlushInto(encoder);
        _leftClockEncoder.FlushInto(encoder);
        _rightClockEncoder.FlushInto(encoder);
        encoder.WriteVarUInt8Array(_infoEncoder.Encoder.WrittenSpan);
        _stringEncoder.FlushInto(encoder);
        encoder.WriteVarUInt8Array(_parentInfoEncoder.Encoder.WrittenSpan);
        _typeRefEncoder.FlushInto(encoder);
        _lenEncoder.FlushInto(encoder);
        encoder.WriteUInt8Array(RestEncoder.WrittenSpan);
        return encoder.ToArray();
    }

    public void WriteLeftID(ID id)
    {
        _clientEncoder.Write(id.Client);
        _leftClockEncoder.Write(id.Clock);
    }

    public void WriteRightID(ID id)
    {
        _clientEncoder.Write(id.Client);
        _rightClockEncoder.Write(id.Clock);
    }

    public void WriteClient(ulong client) => _clientEncoder.Write(client);

    public void WriteInfo(byte info) => _infoEncoder.Write(info);

    public void WriteString(string s) => _stringEncoder.Write(s);

    public void WriteParentInfo(bool isYKey) => _parentInfoEncoder.Write(isYKey ? (byte)1 : (byte)0);

    public void WriteTypeRef(uint info) => _typeRefEncoder.Write(info);

    public void WriteLen(uint len) => _lenEncoder.Write(len);

    public void WriteAny(object? any) => RestEncoder.WriteAny(any);

    public void WriteBuf(byte[] buf) => RestEncoder.WriteVarUInt8Array(buf);

    public void WriteJson(object? embed) => RestEncoder.WriteAny(embed);

    public void WriteKey(string key)
    {
        // Yjs never re-uses key clocks (its key cache is disabled), so every key is written in full.
        _keyClockEncoder.Write(_keyClock++);
        _stringEncoder.Write(key);
    }
}

internal class DSDecoderV1 : IDSDecoder
{
    public DSDecoderV1(Decoder decoder)
    {
        RestDecoder = decoder;
    }

    public Decoder RestDecoder { get; }

    public void ResetDsCurVal()
    {
    }

    public uint ReadDsClock() => RestDecoder.ReadVarUInt32();

    public uint ReadDsLen() => RestDecoder.ReadVarUInt32();
}

internal sealed class UpdateDecoderV1 : DSDecoderV1, IUpdateDecoder
{
    public UpdateDecoderV1(Decoder decoder) : base(decoder)
    {
    }

    public ID ReadLeftID() => new ID(RestDecoder.ReadVarUInt(), RestDecoder.ReadVarUInt32());

    public ID ReadRightID() => new ID(RestDecoder.ReadVarUInt(), RestDecoder.ReadVarUInt32());

    public ulong ReadClient() => RestDecoder.ReadVarUInt();

    public byte ReadInfo() => RestDecoder.ReadUInt8();

    public string ReadString() => RestDecoder.ReadVarString();

    public bool ReadParentInfo() => RestDecoder.ReadVarUInt() == 1;

    public uint ReadTypeRef() => RestDecoder.ReadVarUInt32();

    public uint ReadLen() => RestDecoder.ReadVarUInt32();

    public object? ReadAny() => RestDecoder.ReadAny();

    public byte[] ReadBuf() => RestDecoder.ReadVarUInt8Array();

    public object? ReadJson() => JsJson.Parse(RestDecoder.ReadVarString());

    public string ReadKey() => RestDecoder.ReadVarString();
}

internal class DSDecoderV2 : IDSDecoder
{
    private uint _dsCurrVal;

    public DSDecoderV2(Decoder decoder)
    {
        RestDecoder = decoder;
    }

    public Decoder RestDecoder { get; protected set; }

    public void ResetDsCurVal() => _dsCurrVal = 0;

    public uint ReadDsClock()
    {
        _dsCurrVal += RestDecoder.ReadVarUInt32();
        return _dsCurrVal;
    }

    public uint ReadDsLen()
    {
        var diff = RestDecoder.ReadVarUInt32() + 1;
        _dsCurrVal += diff;
        return diff;
    }
}

internal sealed class UpdateDecoderV2 : DSDecoderV2, IUpdateDecoder
{
    private readonly List<string>         _keys = new List<string>();
    private readonly IntDiffOptRleDecoder _keyClockDecoder;
    private readonly UIntOptRleDecoder    _clientDecoder;
    private readonly IntDiffOptRleDecoder _leftClockDecoder;
    private readonly IntDiffOptRleDecoder _rightClockDecoder;
    private readonly RleByteDecoder       _infoDecoder;
    private readonly StringDecoder        _stringDecoder;
    private readonly RleByteDecoder       _parentInfoDecoder;
    private readonly UIntOptRleDecoder    _typeRefDecoder;
    private readonly UIntOptRleDecoder    _lenDecoder;

    public UpdateDecoderV2(Decoder decoder) : base(decoder)
    {
        decoder.ReadVarUInt(); // feature flag, currently unused
        _keyClockDecoder   = new IntDiffOptRleDecoder(decoder.ReadVarUInt8ArrayAsDecoder());
        _clientDecoder     = new UIntOptRleDecoder(decoder.ReadVarUInt8ArrayAsDecoder());
        _leftClockDecoder  = new IntDiffOptRleDecoder(decoder.ReadVarUInt8ArrayAsDecoder());
        _rightClockDecoder = new IntDiffOptRleDecoder(decoder.ReadVarUInt8ArrayAsDecoder());
        _infoDecoder       = new RleByteDecoder(decoder.ReadVarUInt8ArrayAsDecoder());
        _stringDecoder     = new StringDecoder(decoder.ReadVarUInt8ArrayAsDecoder());
        _parentInfoDecoder = new RleByteDecoder(decoder.ReadVarUInt8ArrayAsDecoder());
        _typeRefDecoder    = new UIntOptRleDecoder(decoder.ReadVarUInt8ArrayAsDecoder());
        _lenDecoder        = new UIntOptRleDecoder(decoder.ReadVarUInt8ArrayAsDecoder());
    }

    private static uint ToClock(long v)
    {
        if (v < 0 || v > uint.MaxValue)
        {
            throw new FormatException("Clock out of range");
        }
        return (uint)v;
    }

    public ID ReadLeftID() => new ID(_clientDecoder.Read(), ToClock(_leftClockDecoder.Read()));

    public ID ReadRightID() => new ID(_clientDecoder.Read(), ToClock(_rightClockDecoder.Read()));

    public ulong ReadClient() => _clientDecoder.Read();

    public byte ReadInfo() => _infoDecoder.Read();

    public string ReadString() => _stringDecoder.Read();

    public bool ReadParentInfo() => _parentInfoDecoder.Read() == 1;

    public uint ReadTypeRef() => (uint)_typeRefDecoder.Read();

    public uint ReadLen() => (uint)_lenDecoder.Read();

    public object? ReadAny() => RestDecoder.ReadAny();

    public byte[] ReadBuf() => RestDecoder.ReadVarUInt8Array();

    public object? ReadJson() => RestDecoder.ReadAny();

    public string ReadKey()
    {
        var keyClock = _keyClockDecoder.Read();
        if (keyClock >= 0 && keyClock < _keys.Count)
        {
            return _keys[(int)keyClock];
        }
        var key = _stringDecoder.Read();
        _keys.Add(key);
        return key;
    }
}
