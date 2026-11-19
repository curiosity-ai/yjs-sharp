using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using YjsSharp.Encoding;

namespace YjsSharp.Sync;

/// <summary>
/// A shared, non-persistent state per client (cursors, user names, presence), the C# counterpart of
/// <c>y-protocols/awareness</c>' <c>Awareness</c> class.
/// </summary>
/// <remarks>
/// <para>
/// Each client is identified by its document's client id and owns one JSON state. A client overrides its
/// state by propagating it with an increasing <c>clock</c>; a received state is applied when the known clock
/// of that client is older. A client that thinks a peer went offline may propagate
/// <c>{ clock: currentClock, state: null }</c> for it, which removes the state wherever the clock still matches.
/// </para>
/// <para>
/// States must be renewed every 30 seconds (<see cref="OutdatedTimeout"/>): the local state is re-sent after
/// half of that, and remote states that were not updated for the whole timeout are removed with the origin
/// <c>"timeout"</c>. <see cref="CheckOutdated"/> runs that check; by default a timer of the configured
/// <see cref="TimeProvider"/> calls it every 3 seconds, like the JavaScript implementation does.
/// </para>
/// <para>
/// States are stored and returned as JSON text, normalised the way <c>JSON.stringify(JSON.parse(text))</c>
/// would, so the encoded updates are byte-identical to those of y-protocols.
/// </para>
/// <para>All members are thread-safe. Events are raised outside the internal lock.</para>
/// </remarks>
public sealed class Awareness : IDisposable
{
    /// <summary>The origin of changes made through the local state methods.</summary>
    public const string LocalOrigin = "local";

    /// <summary>The origin of removals made by <see cref="CheckOutdated"/>.</summary>
    public const string TimeoutOrigin = "timeout";

    /// <summary>How long a state stays valid without being renewed (y-protocols' <c>outdatedTimeout</c>).</summary>
    public static readonly TimeSpan DefaultOutdatedTimeout = TimeSpan.FromMilliseconds(30000);

    private readonly object                              _sync   = new object();
    private readonly Dictionary<ulong, string>           _states = new Dictionary<ulong, string>();
    private readonly Dictionary<ulong, AwarenessMeta>    _meta   = new Dictionary<ulong, AwarenessMeta>();
    private readonly TimeProvider                        _time;
    private readonly ITimer?                             _timer;
    private readonly Doc                                 _doc;
    private          bool                                _disposed;

    /// <summary>Creates an awareness for <paramref name="doc"/> with the system clock and the automatic outdated check.</summary>
    public Awareness(Doc doc) : this(doc, null)
    {
    }

    /// <summary>Creates an awareness for <paramref name="doc"/>.</summary>
    /// <param name="doc">The document whose client id identifies the local client.</param>
    /// <param name="options">Clock, timeout and timer settings; null for the defaults.</param>
    public Awareness(Doc doc, AwarenessOptions? options)
    {
        ArgumentNullException.ThrowIfNull(doc);
        options ??= new AwarenessOptions();
        _doc            = doc;
        _time           = options.TimeProvider ?? TimeProvider.System;
        OutdatedTimeout = options.OutdatedTimeout;
        ClientId        = doc.ClientId;
        _doc.Destroyed += OnDocDestroyed;
        SetLocalState("{}");
        var interval = options.CheckInterval ?? TimeSpan.FromMilliseconds(Math.Floor(OutdatedTimeout.TotalMilliseconds / 10));
        if (interval > TimeSpan.Zero && interval != Timeout.InfiniteTimeSpan)
        {
            _timer = _time.CreateTimer(_ => CheckOutdated(), null, interval, interval);
        }
    }

    /// <summary>The document this awareness belongs to.</summary>
    public Doc Doc => _doc;

    /// <summary>The local client id (the document's client id).</summary>
    public ulong ClientId { get; }

    /// <summary>How long a state stays valid without being renewed.</summary>
    public TimeSpan OutdatedTimeout { get; }

    /// <summary>A snapshot of the JSON state of every client that currently has one.</summary>
    public IReadOnlyDictionary<ulong, string> States
    {
        get
        {
            lock (_sync)
            {
                return new ReadOnlyDictionary<ulong, string>(new Dictionary<ulong, string>(_states));
            }
        }
    }

    /// <summary>A snapshot of the clock and last-update time of every client this awareness has heard of.</summary>
    public IReadOnlyDictionary<ulong, AwarenessMeta> Meta
    {
        get
        {
            lock (_sync)
            {
                return new ReadOnlyDictionary<ulong, AwarenessMeta>(new Dictionary<ulong, AwarenessMeta>(_meta));
            }
        }
    }

    /// <summary>Raised when a state was added, removed or changed its content. Updates that only renew a state are not reported.</summary>
    public event EventHandler<AwarenessChangeEventArgs>? Change;

    /// <summary>Raised for every applied state, including renewals with unchanged content. This is the event to broadcast from.</summary>
    public event EventHandler<AwarenessChangeEventArgs>? Update;

    /// <summary>Returns the JSON state of a client, or null when it has none.</summary>
    public string? GetState(ulong clientId)
    {
        lock (_sync)
        {
            return _states.TryGetValue(clientId, out var s) ? s : null;
        }
    }

    /// <summary>Returns the local JSON state, or null when it was cleared.</summary>
    public string? GetLocalState() => GetState(ClientId);

    /// <summary>
    /// Sets the local state to <paramref name="json"/> (null marks the local client as offline) and raises
    /// <see cref="Update"/> (and <see cref="Change"/> when the content changed) with the origin <c>"local"</c>.
    /// </summary>
    /// <exception cref="FormatException"><paramref name="json"/> is not valid JSON.</exception>
    public void SetLocalState(string? json)
    {
        var state = json == null ? null : Normalize(json);
        if (state == "null")
        {
            state = null;
        }
        AwarenessChangeEventArgs? change;
        AwarenessChangeEventArgs  update;
        lock (_sync)
        {
            ThrowIfDisposed();
            var clock     = _meta.TryGetValue(ClientId, out var currentMeta) ? currentMeta.Clock + 1 : 0;
            var prevState = _states.TryGetValue(ClientId, out var p) ? p : null;
            if (state == null)
            {
                _states.Remove(ClientId);
            }
            else
            {
                _states[ClientId] = state;
            }
            _meta[ClientId] = new AwarenessMeta(clock, Now());
            var added           = new List<ulong>();
            var updated         = new List<ulong>();
            var filteredUpdated = new List<ulong>();
            var removed         = new List<ulong>();
            if (state == null)
            {
                removed.Add(ClientId);
            }
            else if (prevState == null)
            {
                added.Add(ClientId);
            }
            else
            {
                updated.Add(ClientId);
                if (!JsonEquals(prevState, state))
                {
                    filteredUpdated.Add(ClientId);
                }
            }
            change = added.Count > 0 || filteredUpdated.Count > 0 || removed.Count > 0
                ? new AwarenessChangeEventArgs(added, filteredUpdated, removed, LocalOrigin)
                : null;
            update = new AwarenessChangeEventArgs(added, updated, removed, LocalOrigin);
        }
        if (change != null)
        {
            Change?.Invoke(this, change);
        }
        Update?.Invoke(this, update);
    }

    /// <summary>
    /// Sets one top-level field of the local state (a no-op while the local state is null). The value is an
    /// Any value: null, bool, a number, a string, a list or a dictionary.
    /// </summary>
    public void SetLocalStateField(string field, object? value)
    {
        ArgumentNullException.ThrowIfNull(field);
        var current = GetLocalState();
        if (current == null)
        {
            return;
        }
        var parsed = JsJson.Parse(current) as IDictionary<string, object?>;
        var state  = new Dictionary<string, object?>();
        if (parsed != null)
        {
            foreach (var kv in AnyValues.JsKeyOrder(parsed))
            {
                state[kv.Key] = kv.Value;
            }
        }
        state[field] = value is System.Text.Json.JsonElement element ? AnyValues.FromJsonElement(element) : value;
        SetLocalState(JsJson.Stringify(state));
    }

    /// <summary>
    /// Renews the local state when half of <see cref="OutdatedTimeout"/> has passed since it was last set, and
    /// removes remote states that were not updated for the whole timeout (origin <c>"timeout"</c>). Called
    /// periodically by the built-in timer; call it yourself when the timer is disabled.
    /// </summary>
    public void CheckOutdated()
    {
        string?     renew  = null;
        List<ulong> remove = new List<ulong>();
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            var now     = Now();
            var timeout = (long)OutdatedTimeout.TotalMilliseconds;
            if (_states.TryGetValue(ClientId, out var local) && timeout / 2 <= now - _meta[ClientId].LastUpdated)
            {
                renew = local;
            }
            foreach (var kv in _meta)
            {
                if (kv.Key != ClientId && timeout <= now - kv.Value.LastUpdated && _states.ContainsKey(kv.Key))
                {
                    remove.Add(kv.Key);
                }
            }
        }
        if (renew != null)
        {
            SetLocalState(renew);
        }
        if (remove.Count > 0)
        {
            AwarenessProtocol.RemoveAwarenessStates(this, remove, TimeoutOrigin);
        }
    }

    /// <summary>Stops the timer, clears the local state (announcing it as removed) and detaches from the document.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
        }
        _timer?.Dispose();
        _doc.Destroyed -= OnDocDestroyed;
        SetLocalState(null);
        lock (_sync)
        {
            _disposed = true;
        }
    }

    internal long Now() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    internal object SyncRoot => _sync;

    internal Dictionary<ulong, string> StatesUnsafe => _states;

    internal Dictionary<ulong, AwarenessMeta> MetaUnsafe => _meta;

    internal void Raise(AwarenessChangeEventArgs? change, AwarenessChangeEventArgs? update)
    {
        if (change != null)
        {
            Change?.Invoke(this, change);
        }
        if (update != null)
        {
            Update?.Invoke(this, update);
        }
    }

    /// <summary>Normalises JSON text the way <c>JSON.stringify(JSON.parse(json))</c> does.</summary>
    internal static string Normalize(string json) => JsJson.Stringify(JsJson.Parse(json)) ?? "null";

    /// <summary>Deep equality of two JSON texts (object key order does not matter), like lib0's <c>equalityDeep</c>.</summary>
    internal static bool JsonEquals(string? a, string? b)
    {
        if (a == null || b == null)
        {
            return a == b;
        }
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return true;
        }
        return AnyValues.DeepEquals(JsJson.Parse(a), JsJson.Parse(b));
    }

    private void OnDocDestroyed(object? sender, EventArgs e) => Dispose();

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(Awareness));
        }
    }
}

/// <summary>Settings of an <see cref="Awareness"/>.</summary>
public sealed class AwarenessOptions
{
    /// <summary>The clock used for <see cref="AwarenessMeta.LastUpdated"/> and the outdated check; the system clock when null.</summary>
    public TimeProvider? TimeProvider { get; set; }

    /// <summary>How long a state stays valid without being renewed. 30 seconds by default.</summary>
    public TimeSpan OutdatedTimeout { get; set; } = Awareness.DefaultOutdatedTimeout;

    /// <summary>
    /// How often the outdated check runs on a timer of <see cref="TimeProvider"/>. Null means a tenth of
    /// <see cref="OutdatedTimeout"/> (3 seconds, like y-protocols); <see cref="TimeSpan.Zero"/> or
    /// <see cref="Timeout.InfiniteTimeSpan"/> disables the timer, leaving <see cref="Awareness.CheckOutdated"/> to the caller.
    /// </summary>
    public TimeSpan? CheckInterval { get; set; }
}

/// <summary>The clock of a client's awareness state and when it was last updated.</summary>
/// <param name="Clock">The client's logical clock; a state is only replaced by one with a higher clock.</param>
/// <param name="LastUpdated">When this awareness last received a state of the client, in Unix milliseconds.</param>
public readonly record struct AwarenessMeta(ulong Clock, long LastUpdated);

/// <summary>Arguments of the <see cref="Awareness.Change"/> and <see cref="Awareness.Update"/> events.</summary>
public sealed class AwarenessChangeEventArgs : EventArgs
{
    /// <summary>Creates the event arguments.</summary>
    public AwarenessChangeEventArgs(IReadOnlyList<ulong> added, IReadOnlyList<ulong> updated, IReadOnlyList<ulong> removed, object? origin)
    {
        Added   = added;
        Updated = updated;
        Removed = removed;
        Origin  = origin;
    }

    /// <summary>Clients that had no state before.</summary>
    public IReadOnlyList<ulong> Added { get; }

    /// <summary>Clients whose state was updated (for <see cref="Awareness.Change"/>: whose content changed).</summary>
    public IReadOnlyList<ulong> Updated { get; }

    /// <summary>Clients whose state was removed.</summary>
    public IReadOnlyList<ulong> Removed { get; }

    /// <summary>The origin passed to the operation: <c>"local"</c>, <c>"timeout"</c>, or whatever was given to <see cref="AwarenessProtocol.ApplyAwarenessUpdate"/>.</summary>
    public object? Origin { get; }

    /// <summary>All the changed clients: added, then updated, then removed.</summary>
    public IReadOnlyList<ulong> All
    {
        get
        {
            var all = new List<ulong>(Added.Count + Updated.Count + Removed.Count);
            all.AddRange(Added);
            all.AddRange(Updated);
            all.AddRange(Removed);
            return all;
        }
    }
}
