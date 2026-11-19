using System;
using System.Collections.Generic;
using YjsSharp.Encoding;

namespace YjsSharp.Sync;

/// <summary>
/// A message-level implementation of the y-sync protocol over an <see cref="Awareness"/> and its document
/// (a port of yrs' <c>sync::Protocol</c> / <c>DefaultProtocol</c>). Every step is virtual, so a server can
/// change one (for example refuse updates of a read-only connection) and keep the rest.
/// </summary>
public class SyncProtocolHandler
{
    /// <summary>A shared instance with the default behaviour.</summary>
    public static readonly SyncProtocolHandler Default = new SyncProtocolHandler();

    /// <summary>
    /// The messages to send when a connection has been accepted: a sync step 1 followed by the awareness
    /// states, encoded one after another.
    /// </summary>
    public virtual byte[] Start(Awareness awareness)
    {
        ArgumentNullException.ThrowIfNull(awareness);
        var encoder = new Encoder();
        new SyncStep1Message(awareness.Doc.EncodeStateVector()).Encode(encoder);
        new AwarenessMessage(AwarenessProtocol.EncodeAwarenessUpdate(awareness)).Encode(encoder);
        return encoder.ToArray();
    }

    /// <summary>Handles every message in <paramref name="data"/> and returns the replies to send back.</summary>
    public IReadOnlyList<ProtocolMessage> Handle(Awareness awareness, byte[] data, object? origin = null)
    {
        ArgumentNullException.ThrowIfNull(awareness);
        var replies = new List<ProtocolMessage>();
        foreach (var message in ProtocolMessage.ReadAll(data))
        {
            var reply = HandleMessage(awareness, message, origin);
            if (reply != null)
            {
                replies.Add(reply);
            }
        }
        return replies;
    }

    /// <summary>Handles one message and returns the reply, if any.</summary>
    public virtual ProtocolMessage? HandleMessage(Awareness awareness, ProtocolMessage message, object? origin = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        switch (message)
        {
            case SyncStep1Message m: return HandleSyncStep1(awareness, m.StateVector);
            case SyncStep2Message m: return HandleSyncStep2(awareness, m.Update, origin);
            case SyncUpdateMessage m: return HandleUpdate(awareness, m.Update, origin);
            case AuthMessage m: return HandleAuth(awareness, m.DenyReason);
            case QueryAwarenessMessage: return HandleAwarenessQuery(awareness);
            case AwarenessMessage m: return HandleAwarenessUpdate(awareness, m.Update, origin);
            case CustomMessage m: return HandleCustom(awareness, m.Tag, m.Data);
            default: throw new NotSupportedException($"Unsupported message {message.GetType().Name}");
        }
    }

    /// <summary>Answers a peer's state vector with the update it is missing.</summary>
    public virtual ProtocolMessage? HandleSyncStep1(Awareness awareness, byte[] stateVector)
        => new SyncStep2Message(awareness.Doc.EncodeStateAsUpdate(stateVector));

    /// <summary>Applies the answer to a sync step 1 this side sent.</summary>
    public virtual ProtocolMessage? HandleSyncStep2(Awareness awareness, byte[] update, object? origin)
    {
        awareness.Doc.ApplyUpdate(update, origin);
        return null;
    }

    /// <summary>Applies an incremental update; by default exactly like <see cref="HandleSyncStep2"/>.</summary>
    public virtual ProtocolMessage? HandleUpdate(Awareness awareness, byte[] update, object? origin)
        => HandleSyncStep2(awareness, update, origin);

    /// <summary>Throws <see cref="PermissionDeniedException"/> when access was denied.</summary>
    public virtual ProtocolMessage? HandleAuth(Awareness awareness, string? denyReason)
    {
        if (denyReason != null)
        {
            throw new PermissionDeniedException(denyReason);
        }
        return null;
    }

    /// <summary>Answers an awareness query with every known state.</summary>
    public virtual ProtocolMessage? HandleAwarenessQuery(Awareness awareness)
        => new AwarenessMessage(AwarenessProtocol.EncodeAwarenessUpdate(awareness));

    /// <summary>Applies an awareness update.</summary>
    public virtual ProtocolMessage? HandleAwarenessUpdate(Awareness awareness, byte[] update, object? origin)
    {
        AwarenessProtocol.ApplyAwarenessUpdate(awareness, update, origin);
        return null;
    }

    /// <summary>Handles an application-defined message; throws <see cref="NotSupportedException"/> by default.</summary>
    public virtual ProtocolMessage? HandleCustom(Awareness awareness, int tag, byte[] data)
        => throw new NotSupportedException($"Unsupported message type {tag}");
}
