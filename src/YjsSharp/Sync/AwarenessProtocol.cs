using System;
using System.Collections.Generic;
using System.Linq;
using YjsSharp.Encoding;

namespace YjsSharp.Sync;

/// <summary>
/// Encoding and application of awareness updates (<c>y-protocols/awareness</c>), byte-compatible with
/// y-protocols 1.0.x: <c>[count : varUint, (clientId : varUint, clock : varUint, state : varString JSON)*]</c>
/// where a state of <c>null</c> marks the client as removed.
/// </summary>
public static class AwarenessProtocol
{
    /// <summary>
    /// Encodes the states of <paramref name="clients"/> (a removed client is written as <c>null</c>).
    /// Every client must be known to the awareness (present in <see cref="Awareness.Meta"/>).
    /// </summary>
    /// <exception cref="ArgumentException">A client is unknown to the awareness.</exception>
    public static byte[] EncodeAwarenessUpdate(Awareness awareness, IEnumerable<ulong> clients)
    {
        ArgumentNullException.ThrowIfNull(awareness);
        ArgumentNullException.ThrowIfNull(clients);
        var list    = clients as IReadOnlyCollection<ulong> ?? clients.ToList();
        var encoder = new Encoder();
        lock (awareness.SyncRoot)
        {
            encoder.WriteVarUInt((ulong)list.Count);
            foreach (var clientId in list)
            {
                if (!awareness.MetaUnsafe.TryGetValue(clientId, out var meta))
                {
                    throw new ArgumentException($"Client {clientId} is not known to this awareness instance", nameof(clients));
                }
                encoder.WriteVarUInt(clientId);
                encoder.WriteVarUInt(meta.Clock);
                encoder.WriteVarString(awareness.StatesUnsafe.TryGetValue(clientId, out var state) ? state : "null");
            }
        }
        return encoder.ToArray();
    }

    /// <summary>Encodes the states of every client that currently has one (what a server sends a new connection).</summary>
    public static byte[] EncodeAwarenessUpdate(Awareness awareness)
    {
        ArgumentNullException.ThrowIfNull(awareness);
        return EncodeAwarenessUpdate(awareness, awareness.States.Keys);
    }

    /// <summary>
    /// Applies an awareness update. A state replaces the known one when its clock is newer; a <c>null</c>
    /// state with the same clock removes it. A remote attempt to remove the local state is refused by bumping
    /// the local clock, so the next broadcast tells everyone this client is still there. Raises
    /// <see cref="Awareness.Change"/> and <see cref="Awareness.Update"/> with <paramref name="origin"/>.
    /// </summary>
    /// <exception cref="FormatException">The update or one of its JSON states is malformed.</exception>
    public static void ApplyAwarenessUpdate(Awareness awareness, byte[] update, object? origin)
    {
        ArgumentNullException.ThrowIfNull(awareness);
        ArgumentNullException.ThrowIfNull(update);
        var decoder = new Decoder(update);
        var len     = decoder.ReadVarUInt();
        // Each entry takes at least three bytes; refuse an absurd count instead of trusting it.
        if (len > (ulong)decoder.Remaining)
        {
            throw new FormatException("Awareness update declares more entries than it contains");
        }
        var entries = new List<(ulong Client, ulong Clock, string? State)>((int)len);
        for (ulong i = 0; i < len; i++)
        {
            var clientId = decoder.ReadVarUInt();
            var clock    = decoder.ReadVarUInt();
            var state    = Awareness.Normalize(decoder.ReadVarString());
            entries.Add((clientId, clock, state == "null" ? null : state));
        }

        var added           = new List<ulong>();
        var updated         = new List<ulong>();
        var filteredUpdated = new List<ulong>();
        var removed         = new List<ulong>();
        lock (awareness.SyncRoot)
        {
            var timestamp = awareness.Now();
            var states    = awareness.StatesUnsafe;
            var metas     = awareness.MetaUnsafe;
            foreach (var (clientId, entryClock, state) in entries)
            {
                var clock     = entryClock;
                var hasMeta   = metas.TryGetValue(clientId, out var clientMeta);
                var prevState = states.TryGetValue(clientId, out var p) ? p : null;
                var currClock = hasMeta ? clientMeta.Clock : 0;
                if (currClock < clock || (currClock == clock && state == null && states.ContainsKey(clientId)))
                {
                    if (state == null)
                    {
                        // never let a remote client remove this local state
                        if (clientId == awareness.ClientId && states.ContainsKey(clientId))
                        {
                            // remote client removed the local state. Do not remove it. Broadcast a message
                            // indicating that this client still exists by increasing the clock
                            clock++;
                        }
                        else
                        {
                            states.Remove(clientId);
                        }
                    }
                    else
                    {
                        states[clientId] = state;
                    }
                    metas[clientId] = new AwarenessMeta(clock, timestamp);
                    if (!hasMeta && state != null)
                    {
                        added.Add(clientId);
                    }
                    else if (hasMeta && state == null)
                    {
                        removed.Add(clientId);
                    }
                    else if (state != null)
                    {
                        if (!Awareness.JsonEquals(state, prevState))
                        {
                            filteredUpdated.Add(clientId);
                        }
                        updated.Add(clientId);
                    }
                }
            }
        }
        var change = added.Count > 0 || filteredUpdated.Count > 0 || removed.Count > 0
            ? new AwarenessChangeEventArgs(added, filteredUpdated, removed, origin)
            : null;
        var updateArgs = added.Count > 0 || updated.Count > 0 || removed.Count > 0
            ? new AwarenessChangeEventArgs(added, updated, removed, origin)
            : null;
        awareness.Raise(change, updateArgs);
    }

    /// <summary>
    /// Marks clients as inactive and removes their states. Removing the local client bumps its clock so the
    /// removal propagates. Raises <see cref="Awareness.Change"/> and <see cref="Awareness.Update"/> for the
    /// clients that had a state.
    /// </summary>
    public static void RemoveAwarenessStates(Awareness awareness, IEnumerable<ulong> clients, object? origin)
    {
        ArgumentNullException.ThrowIfNull(awareness);
        ArgumentNullException.ThrowIfNull(clients);
        var removed = new List<ulong>();
        lock (awareness.SyncRoot)
        {
            foreach (var clientId in clients)
            {
                if (awareness.StatesUnsafe.Remove(clientId))
                {
                    if (clientId == awareness.ClientId)
                    {
                        var curMeta = awareness.MetaUnsafe[clientId];
                        awareness.MetaUnsafe[clientId] = new AwarenessMeta(curMeta.Clock + 1, awareness.Now());
                    }
                    removed.Add(clientId);
                }
            }
        }
        if (removed.Count > 0)
        {
            var args = new AwarenessChangeEventArgs(Array.Empty<ulong>(), Array.Empty<ulong>(), removed, origin);
            awareness.Raise(args, args);
        }
    }

    /// <summary>
    /// Rewrites the states of an awareness update, e.g. so a server can make sure a client does not
    /// impersonate somebody else. <paramref name="modify"/> receives each state as JSON text (<c>"null"</c> for
    /// a removed client) and returns the replacement JSON text; clients and clocks are kept.
    /// </summary>
    public static byte[] ModifyAwarenessUpdate(byte[] update, Func<string, string> modify)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(modify);
        var decoder = new Decoder(update);
        var encoder = new Encoder();
        var len     = decoder.ReadVarUInt();
        encoder.WriteVarUInt(len);
        for (ulong i = 0; i < len; i++)
        {
            var clientId = decoder.ReadVarUInt();
            var clock    = decoder.ReadVarUInt();
            var state    = Awareness.Normalize(decoder.ReadVarString());
            var modified = modify(state);
            encoder.WriteVarUInt(clientId);
            encoder.WriteVarUInt(clock);
            encoder.WriteVarString(modified == null ? "null" : Awareness.Normalize(modified));
        }
        return encoder.ToArray();
    }

    /// <summary>Decodes an awareness update into its entries without applying it.</summary>
    public static IReadOnlyList<AwarenessUpdateEntry> DecodeAwarenessUpdate(byte[] update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var decoder = new Decoder(update);
        var len     = decoder.ReadVarUInt();
        if (len > (ulong)decoder.Remaining)
        {
            throw new FormatException("Awareness update declares more entries than it contains");
        }
        var entries = new List<AwarenessUpdateEntry>((int)len);
        for (ulong i = 0; i < len; i++)
        {
            var clientId = decoder.ReadVarUInt();
            var clock    = decoder.ReadVarUInt();
            entries.Add(new AwarenessUpdateEntry(clientId, clock, decoder.ReadVarString()));
        }
        return entries;
    }
}

/// <summary>One client entry of an awareness update.</summary>
/// <param name="ClientId">The client the state belongs to.</param>
/// <param name="Clock">The client's clock for this state.</param>
/// <param name="State">The state as JSON text; <c>"null"</c> when the client was removed.</param>
public readonly record struct AwarenessUpdateEntry(ulong ClientId, ulong Clock, string State);
