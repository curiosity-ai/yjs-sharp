using System;
using YjsSharp.Encoding;

namespace YjsSharp.Sync;

/// <summary>
/// The Yjs document synchronisation protocol (<c>y-protocols/sync</c>), byte-compatible with y-protocols 1.0.x.
/// </summary>
/// <remarks>
/// <para>
/// Core Yjs defines two message types. <b>SyncStep1</b> carries the state vector of the sender; the receiver
/// answers it with <b>SyncStep2</b>, which carries every struct the sender is missing plus the complete delete
/// set. After that, <b>Update</b> messages carry incremental document updates.
/// </para>
/// <para>
/// In a client-server model the client initiates with SyncStep1. The server answers with SyncStep2 immediately
/// followed by its own SyncStep1, and the client answers that with SyncStep2. A message does not carry the
/// room name; that is up to the transport (see <see cref="Messages"/> for the y-websocket framing).
/// </para>
/// <para>Construction of a message: <c>[messageType : varUint, payload : varUint8Array]</c>.</para>
/// </remarks>
public static class SyncProtocol
{
    /// <summary>Message type of a sync step 1 (the sender's state vector).</summary>
    public const int MessageYjsSyncStep1 = 0;

    /// <summary>Message type of a sync step 2 (the update the receiver of a step 1 is missing).</summary>
    public const int MessageYjsSyncStep2 = 1;

    /// <summary>Message type of an incremental document update.</summary>
    public const int MessageYjsUpdate = 2;

    /// <summary>Writes a sync step 1 message carrying the state vector of <paramref name="doc"/>.</summary>
    public static void WriteSyncStep1(Encoder encoder, Doc doc)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(doc);
        encoder.WriteVarUInt(MessageYjsSyncStep1);
        encoder.WriteVarUInt8Array(doc.EncodeStateVector());
    }

    /// <summary>
    /// Writes a sync step 2 message carrying everything in <paramref name="doc"/> that a peer with
    /// <paramref name="encodedStateVector"/> is missing (the whole document when it is null).
    /// </summary>
    public static void WriteSyncStep2(Encoder encoder, Doc doc, byte[]? encodedStateVector)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(doc);
        encoder.WriteVarUInt(MessageYjsSyncStep2);
        encoder.WriteVarUInt8Array(doc.EncodeStateAsUpdate(encodedStateVector));
    }

    /// <summary>Writes an update message carrying a v1 document update.</summary>
    public static void WriteUpdate(Encoder encoder, byte[] update)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(update);
        encoder.WriteVarUInt(MessageYjsUpdate);
        encoder.WriteVarUInt8Array(update);
    }

    /// <summary>
    /// Reads the payload of a sync step 1 message (the type has already been read) and writes the
    /// sync step 2 answer into <paramref name="encoder"/>.
    /// </summary>
    public static void ReadSyncStep1(Decoder decoder, Encoder encoder, Doc doc)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        WriteSyncStep2(encoder, doc, decoder.ReadVarUInt8Array());
    }

    /// <summary>
    /// Reads the payload of a sync step 2 message (the type has already been read) and applies it to
    /// <paramref name="doc"/> in a transaction with the given <paramref name="origin"/>.
    /// </summary>
    public static void ReadSyncStep2(Decoder decoder, Doc doc, object? origin)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(doc);
        doc.ApplyUpdate(decoder.ReadVarUInt8Array(), origin);
    }

    /// <summary>Reads the payload of an update message and applies it, exactly like <see cref="ReadSyncStep2"/>.</summary>
    public static void ReadUpdate(Decoder decoder, Doc doc, object? origin) => ReadSyncStep2(decoder, doc, origin);

    /// <summary>
    /// Reads one sync message, applies it to <paramref name="doc"/> and writes the answer, if any, into
    /// <paramref name="encoder"/> (only a sync step 1 is answered). Returns the message type that was read.
    /// </summary>
    /// <exception cref="InvalidOperationException">The message type is not a sync message type.</exception>
    public static int ReadSyncMessage(Decoder decoder, Encoder encoder, Doc doc, object? origin)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        var messageType = decoder.ReadVarUInt();
        switch (messageType)
        {
            case MessageYjsSyncStep1: ReadSyncStep1(decoder, encoder, doc); break;
            case MessageYjsSyncStep2: ReadSyncStep2(decoder, doc, origin); break;
            case MessageYjsUpdate: ReadUpdate(decoder, doc, origin); break;
            default: throw new InvalidOperationException($"Unknown sync message type {messageType}");
        }
        return (int)messageType;
    }
}
