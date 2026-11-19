using System;
using System.Collections.Generic;
using YjsSharp.Encoding;

namespace YjsSharp.Sync;

/// <summary>
/// One message of the y-sync protocol as y-websocket frames it (a port of yrs' <c>sync::Message</c>):
/// <c>[messageType : varUint, ...]</c> where the type is one of the <see cref="Messages"/> constants.
/// Several messages may be stored one after another in a single payload; <see cref="ReadAll"/> reads them.
/// </summary>
public abstract class ProtocolMessage : IEquatable<ProtocolMessage>
{
    private protected ProtocolMessage()
    {
    }

    /// <summary>The y-websocket message type (<see cref="Messages.MessageSync"/>, <see cref="Messages.MessageAwareness"/>, ...).</summary>
    public abstract int MessageType { get; }

    /// <summary>Writes the message, including its type, into <paramref name="encoder"/>.</summary>
    public abstract void Encode(Encoder encoder);

    /// <summary>Encodes the message into a new byte array.</summary>
    public byte[] ToArray()
    {
        var encoder = new Encoder();
        Encode(encoder);
        return encoder.ToArray();
    }

    /// <summary>Reads one message.</summary>
    /// <exception cref="FormatException">The message is malformed.</exception>
    public static ProtocolMessage Decode(Decoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        var tag = decoder.ReadVarUInt();
        switch (tag)
        {
            case Messages.MessageSync:
            {
                var syncType = decoder.ReadVarUInt();
                var payload  = decoder.ReadVarUInt8Array();
                switch (syncType)
                {
                    case SyncProtocol.MessageYjsSyncStep1: return new SyncStep1Message(payload);
                    case SyncProtocol.MessageYjsSyncStep2: return new SyncStep2Message(payload);
                    case SyncProtocol.MessageYjsUpdate: return new SyncUpdateMessage(payload);
                    default: throw new FormatException($"Unknown sync message type {syncType}");
                }
            }
            case Messages.MessageAwareness: return new AwarenessMessage(decoder.ReadVarUInt8Array());
            case Messages.MessageAuth:
                return decoder.ReadVarUInt() == Messages.PermissionDenied
                    ? new AuthMessage(decoder.ReadVarString())
                    : new AuthMessage(null);
            case Messages.MessageQueryAwareness: return new QueryAwarenessMessage();
            default:
                if (tag > int.MaxValue)
                {
                    throw new FormatException($"Invalid message type {tag}");
                }
                return new CustomMessage((int)tag, decoder.ReadVarUInt8Array());
        }
    }

    /// <summary>Decodes a single message from a byte array.</summary>
    public static ProtocolMessage Decode(byte[] data) => Decode(new Decoder(data));

    /// <summary>Reads every message stored one after another in <paramref name="data"/>.</summary>
    public static IEnumerable<ProtocolMessage> ReadAll(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var decoder = new Decoder(data);
        while (decoder.HasContent)
        {
            yield return Decode(decoder);
        }
    }

    /// <inheritdoc />
    public abstract bool Equals(ProtocolMessage? other);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ProtocolMessage m && Equals(m);

    /// <inheritdoc />
    public override int GetHashCode() => MessageType;

    private protected static bool SameBytes(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
}

/// <summary>Sync step 1: the sender's encoded state vector.</summary>
public sealed class SyncStep1Message : ProtocolMessage
{
    /// <summary>Creates the message.</summary>
    public SyncStep1Message(byte[] stateVector) => StateVector = stateVector ?? throw new ArgumentNullException(nameof(stateVector));

    /// <summary>The encoded state vector.</summary>
    public byte[] StateVector { get; }

    /// <inheritdoc />
    public override int MessageType => Messages.MessageSync;

    /// <inheritdoc />
    public override void Encode(Encoder encoder)
    {
        encoder.WriteVarUInt(Messages.MessageSync);
        encoder.WriteVarUInt(SyncProtocol.MessageYjsSyncStep1);
        encoder.WriteVarUInt8Array(StateVector);
    }

    /// <inheritdoc />
    public override bool Equals(ProtocolMessage? other) => other is SyncStep1Message m && SameBytes(StateVector, m.StateVector);

    /// <inheritdoc />
    public override int GetHashCode() => base.GetHashCode();
}

/// <summary>Sync step 2: the v1 update a peer is missing.</summary>
public sealed class SyncStep2Message : ProtocolMessage
{
    /// <summary>Creates the message.</summary>
    public SyncStep2Message(byte[] update) => Update = update ?? throw new ArgumentNullException(nameof(update));

    /// <summary>The v1 update.</summary>
    public byte[] Update { get; }

    /// <inheritdoc />
    public override int MessageType => Messages.MessageSync;

    /// <inheritdoc />
    public override void Encode(Encoder encoder)
    {
        encoder.WriteVarUInt(Messages.MessageSync);
        encoder.WriteVarUInt(SyncProtocol.MessageYjsSyncStep2);
        encoder.WriteVarUInt8Array(Update);
    }

    /// <inheritdoc />
    public override bool Equals(ProtocolMessage? other) => other is SyncStep2Message m && SameBytes(Update, m.Update);

    /// <inheritdoc />
    public override int GetHashCode() => base.GetHashCode();
}

/// <summary>An incremental v1 document update.</summary>
public sealed class SyncUpdateMessage : ProtocolMessage
{
    /// <summary>Creates the message.</summary>
    public SyncUpdateMessage(byte[] update) => Update = update ?? throw new ArgumentNullException(nameof(update));

    /// <summary>The v1 update.</summary>
    public byte[] Update { get; }

    /// <inheritdoc />
    public override int MessageType => Messages.MessageSync;

    /// <inheritdoc />
    public override void Encode(Encoder encoder)
    {
        encoder.WriteVarUInt(Messages.MessageSync);
        encoder.WriteVarUInt(SyncProtocol.MessageYjsUpdate);
        encoder.WriteVarUInt8Array(Update);
    }

    /// <inheritdoc />
    public override bool Equals(ProtocolMessage? other) => other is SyncUpdateMessage m && SameBytes(Update, m.Update);

    /// <inheritdoc />
    public override int GetHashCode() => base.GetHashCode();
}

/// <summary>An awareness update (see <see cref="AwarenessProtocol"/>).</summary>
public sealed class AwarenessMessage : ProtocolMessage
{
    /// <summary>Creates the message.</summary>
    public AwarenessMessage(byte[] update) => Update = update ?? throw new ArgumentNullException(nameof(update));

    /// <summary>The encoded awareness update.</summary>
    public byte[] Update { get; }

    /// <inheritdoc />
    public override int MessageType => Messages.MessageAwareness;

    /// <inheritdoc />
    public override void Encode(Encoder encoder)
    {
        encoder.WriteVarUInt(Messages.MessageAwareness);
        encoder.WriteVarUInt8Array(Update);
    }

    /// <inheritdoc />
    public override bool Equals(ProtocolMessage? other) => other is AwarenessMessage m && SameBytes(Update, m.Update);

    /// <inheritdoc />
    public override int GetHashCode() => base.GetHashCode();
}

/// <summary>An authentication message: permission denied with a reason, or granted.</summary>
public sealed class AuthMessage : ProtocolMessage
{
    /// <summary>Creates the message; a null <paramref name="denyReason"/> means permission granted.</summary>
    public AuthMessage(string? denyReason) => DenyReason = denyReason;

    /// <summary>Why permission was denied, or null when it was granted.</summary>
    public string? DenyReason { get; }

    /// <inheritdoc />
    public override int MessageType => Messages.MessageAuth;

    /// <inheritdoc />
    public override void Encode(Encoder encoder)
    {
        encoder.WriteVarUInt(Messages.MessageAuth);
        if (DenyReason != null)
        {
            encoder.WriteVarUInt(Messages.PermissionDenied);
            encoder.WriteVarString(DenyReason);
        }
        else
        {
            encoder.WriteVarUInt(Messages.PermissionGranted);
        }
    }

    /// <inheritdoc />
    public override bool Equals(ProtocolMessage? other) => other is AuthMessage m && m.DenyReason == DenyReason;

    /// <inheritdoc />
    public override int GetHashCode() => base.GetHashCode();
}

/// <summary>A request for the receiver's awareness states.</summary>
public sealed class QueryAwarenessMessage : ProtocolMessage
{
    /// <inheritdoc />
    public override int MessageType => Messages.MessageQueryAwareness;

    /// <inheritdoc />
    public override void Encode(Encoder encoder) => encoder.WriteVarUInt(Messages.MessageQueryAwareness);

    /// <inheritdoc />
    public override bool Equals(ProtocolMessage? other) => other is QueryAwarenessMessage;

    /// <inheritdoc />
    public override int GetHashCode() => base.GetHashCode();
}

/// <summary>An application-defined message: any other type, followed by a length-prefixed payload.</summary>
public sealed class CustomMessage : ProtocolMessage
{
    /// <summary>Creates the message.</summary>
    public CustomMessage(int tag, byte[] data)
    {
        if (tag <= Messages.MessageQueryAwareness)
        {
            throw new ArgumentOutOfRangeException(nameof(tag), "Tags 0 to 3 are reserved by the protocol");
        }
        Tag  = tag;
        Data = data ?? throw new ArgumentNullException(nameof(data));
    }

    /// <summary>The message type.</summary>
    public int Tag { get; }

    /// <summary>The payload.</summary>
    public byte[] Data { get; }

    /// <inheritdoc />
    public override int MessageType => Tag;

    /// <inheritdoc />
    public override void Encode(Encoder encoder)
    {
        encoder.WriteVarUInt((ulong)Tag);
        encoder.WriteVarUInt8Array(Data);
    }

    /// <inheritdoc />
    public override bool Equals(ProtocolMessage? other) => other is CustomMessage m && m.Tag == Tag && SameBytes(Data, m.Data);

    /// <inheritdoc />
    public override int GetHashCode() => base.GetHashCode();
}
