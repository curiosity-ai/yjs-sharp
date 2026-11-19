using System;
using System.Collections.Generic;
using YjsSharp.Encoding;

namespace YjsSharp.Sync;

/// <summary>
/// The y-websocket message framing, so a server (or client) can speak the protocol of
/// <c>y-websocket</c> / <c>@y/websocket-server</c>: every frame starts with a var-uint message type,
/// <list type="bullet">
/// <item><description><see cref="MessageSync"/> (0) followed by a <see cref="SyncProtocol"/> message,</description></item>
/// <item><description><see cref="MessageAwareness"/> (1) followed by a length-prefixed awareness update,</description></item>
/// <item><description><see cref="MessageAuth"/> (2) followed by <see cref="PermissionDenied"/> and a reason (y-protocols/auth),</description></item>
/// <item><description><see cref="MessageQueryAwareness"/> (3) with no payload: "send me your awareness states".</description></item>
/// </list>
/// </summary>
/// <remarks>
/// A minimal relay: on connect send <see cref="EncodeSyncStep1"/> (and, when there are any, the awareness
/// states); for every frame received call <see cref="Handle(byte[], Awareness, object?)"/> and send the reply
/// back to the sender when it is not null; forward every <see cref="Doc.Update"/> as
/// <see cref="EncodeUpdate"/> and every <see cref="Awareness.Update"/> as <see cref="EncodeAwarenessUpdate(Awareness, IEnumerable{ulong})"/>
/// to all connections.
/// </remarks>
public static class Messages
{
    /// <summary>A document sync message (<see cref="SyncProtocol"/>).</summary>
    public const int MessageSync = 0;

    /// <summary>An awareness update.</summary>
    public const int MessageAwareness = 1;

    /// <summary>An authentication message (y-protocols/auth).</summary>
    public const int MessageAuth = 2;

    /// <summary>A request for the awareness states of the receiver.</summary>
    public const int MessageQueryAwareness = 3;

    /// <summary>The auth sub-type meaning "permission denied", followed by a reason string.</summary>
    public const int PermissionDenied = 0;

    /// <summary>The auth sub-type meaning "permission granted" (yrs; y-protocols itself only sends denials).</summary>
    public const int PermissionGranted = 1;

    /// <summary>Frames a sync step 1 for <paramref name="doc"/>: what a peer sends right after connecting.</summary>
    public static byte[] EncodeSyncStep1(Doc doc)
    {
        var encoder = new Encoder();
        encoder.WriteVarUInt(MessageSync);
        SyncProtocol.WriteSyncStep1(encoder, doc);
        return encoder.ToArray();
    }

    /// <summary>Frames a sync step 2 for <paramref name="doc"/> against a peer's state vector (the whole document when null).</summary>
    public static byte[] EncodeSyncStep2(Doc doc, byte[]? encodedStateVector = null)
    {
        var encoder = new Encoder();
        encoder.WriteVarUInt(MessageSync);
        SyncProtocol.WriteSyncStep2(encoder, doc, encodedStateVector);
        return encoder.ToArray();
    }

    /// <summary>Frames a v1 document update (what is broadcast for every <see cref="Doc.Update"/>).</summary>
    public static byte[] EncodeUpdate(byte[] update)
    {
        var encoder = new Encoder();
        encoder.WriteVarUInt(MessageSync);
        SyncProtocol.WriteUpdate(encoder, update);
        return encoder.ToArray();
    }

    /// <summary>Frames an already encoded awareness update.</summary>
    public static byte[] EncodeAwarenessUpdate(byte[] awarenessUpdate)
    {
        ArgumentNullException.ThrowIfNull(awarenessUpdate);
        var encoder = new Encoder();
        encoder.WriteVarUInt(MessageAwareness);
        encoder.WriteVarUInt8Array(awarenessUpdate);
        return encoder.ToArray();
    }

    /// <summary>Frames the awareness states of <paramref name="clients"/>.</summary>
    public static byte[] EncodeAwarenessUpdate(Awareness awareness, IEnumerable<ulong> clients)
        => EncodeAwarenessUpdate(AwarenessProtocol.EncodeAwarenessUpdate(awareness, clients));

    /// <summary>Frames a query for the receiver's awareness states.</summary>
    public static byte[] EncodeQueryAwareness()
    {
        var encoder = new Encoder();
        encoder.WriteVarUInt(MessageQueryAwareness);
        return encoder.ToArray();
    }

    /// <summary>Frames a permission-denied auth message.</summary>
    public static byte[] EncodePermissionDenied(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        var encoder = new Encoder();
        encoder.WriteVarUInt(MessageAuth);
        encoder.WriteVarUInt(PermissionDenied);
        encoder.WriteVarString(reason);
        return encoder.ToArray();
    }

    /// <summary>Reads the message type of a frame without consuming anything else.</summary>
    public static int ReadMessageType(byte[] message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return (int)new Decoder(message).ReadVarUInt();
    }

    /// <summary>
    /// Handles one y-websocket frame against <paramref name="awareness"/> and its document and returns the
    /// frame to send back to the sender, or null when there is nothing to answer:
    /// a sync step 1 is answered with a sync step 2, a step 2 or an update is applied with
    /// <paramref name="origin"/>, an awareness update is applied with <paramref name="origin"/>, an awareness
    /// query is answered with every known state, and a permission-denied auth message is passed to
    /// <paramref name="permissionDenied"/>.
    /// </summary>
    /// <exception cref="PermissionDeniedException">A permission-denied message arrived and no handler was given.</exception>
    /// <exception cref="FormatException">The frame is malformed or of an unknown type.</exception>
    public static byte[]? Handle(byte[] message, Awareness awareness, object? origin, Action<string>? permissionDenied = null)
    {
        ArgumentNullException.ThrowIfNull(awareness);
        return Handle(message, awareness.Doc, awareness, origin, permissionDenied);
    }

    /// <summary>
    /// Handles one y-websocket frame against <paramref name="doc"/> only (awareness frames are rejected),
    /// returning the reply frame or null. See <see cref="Handle(byte[], Awareness, object?, Action{string}?)"/>.
    /// </summary>
    public static byte[]? Handle(byte[] message, Doc doc, object? origin, Action<string>? permissionDenied = null)
        => Handle(message, doc, null, origin, permissionDenied);

    private static byte[]? Handle(byte[] message, Doc doc, Awareness? awareness, object? origin, Action<string>? permissionDenied)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(doc);
        var decoder     = new Decoder(message);
        var encoder     = new Encoder();
        var messageType = decoder.ReadVarUInt();
        switch (messageType)
        {
            case MessageSync:
                encoder.WriteVarUInt(MessageSync);
                SyncProtocol.ReadSyncMessage(decoder, encoder, doc, origin);
                return encoder.Length > 1 ? encoder.ToArray() : null;
            case MessageAwareness:
                AwarenessProtocol.ApplyAwarenessUpdate(RequireAwareness(awareness), decoder.ReadVarUInt8Array(), origin);
                return null;
            case MessageQueryAwareness:
                return EncodeAwarenessUpdate(AwarenessProtocol.EncodeAwarenessUpdate(RequireAwareness(awareness)));
            case MessageAuth:
                if (decoder.ReadVarUInt() == PermissionDenied)
                {
                    var reason = decoder.ReadVarString();
                    if (permissionDenied == null)
                    {
                        throw new PermissionDeniedException(reason);
                    }
                    permissionDenied(reason);
                }
                return null;
            default:
                throw new FormatException($"Unknown y-websocket message type {messageType}");
        }
    }

    private static Awareness RequireAwareness(Awareness? awareness)
        => awareness ?? throw new FormatException("Received an awareness message but no awareness instance was given");
}

/// <summary>Thrown when the other side denied access (a y-protocols/auth permission-denied message).</summary>
public sealed class PermissionDeniedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PermissionDeniedException(string reason) : base("Permission denied: " + reason) => Reason = reason;

    /// <summary>The reason given by the other side.</summary>
    public string Reason { get; }
}
