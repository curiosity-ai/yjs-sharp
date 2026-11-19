# YjsSharp

A pure C# port of [Yjs](https://github.com/yjs/yjs) / [y-crdt](https://github.com/y-crdt/y-crdt) (yrs) for .NET:
the shared CRDT types (`Text`, `YArray`, `YMap`, `XmlFragment` / `XmlElement` / `XmlText`), the binary update
formats v1 and v2, state vectors, snapshots, relative positions and the undo manager, plus the
[y-protocols](https://github.com/yjs/y-protocols) sync and awareness protocols and the y-websocket message framing.

It speaks the Yjs wire format byte for byte, so a .NET process can be a full peer of Yjs clients in the browser:
hold the document on the server, persist it, edit it, and relay updates and presence between clients.

- No native dependencies, no JavaScript engine: plain managed code targeting .NET 8 and .NET 10.
- Wire-compatible with **Yjs 13.6.x** and **y-protocols 1.0.x** (see [Compatibility](#compatibility)).
- MIT licensed.

## Install

```bash
dotnet add package YjsSharp
```

## Quick start

```csharp
using YjsSharp;

var doc  = new Doc();
var text = doc.GetText("article");
text.Insert(0, "Hello world");
text.Format(0, 5, new Dictionary<string, object?> { ["bold"] = true });

var map = doc.GetMap("settings");
map.Set("theme", "dark");

// Send every change to the other peers.
doc.Update += (_, e) => Broadcast(e.Update);

// Apply what they send.
var other = new Doc();
other.ApplyUpdate(doc.EncodeStateAsUpdate());
Console.WriteLine(other.GetText("article").ToString());   // Hello world

// Only what the other side is missing:
var missing = doc.EncodeStateAsUpdate(other.EncodeStateVector());

// Group several changes into one transaction (and one update event).
doc.Transact(tx =>
{
    text.Insert(text.Length, "!");
    map.Delete("theme");
}, origin: "my-origin");
```

The static class `Y` mirrors Yjs' top-level functions: `Y.MergeUpdates`, `Y.DiffUpdate`,
`Y.EncodeStateVectorFromUpdate`, `Y.ConvertUpdateFormatV1ToV2` / `V2ToV1`, `Y.Snapshot`, `Y.CreateDocFromSnapshot`, and
the v2 variants of each. Updates in format v2 go through `EncodeStateAsUpdateV2` / `ApplyUpdateV2` / the `UpdateV2` event.

Values stored in shared types are the Yjs "Any" values - `null`, `bool`, `double` / `long`, `string`, `byte[]`,
`IList<object?>` and `IDictionary<string, object?>` - plus nested shared types and subdocuments (`Doc`).
See [docs/API.md](docs/API.md) for the public surface.

## Sync and awareness protocols

`YjsSharp.Sync` implements y-protocols' `sync.js` and `awareness.js` and is byte-compatible with them:

| Type | Purpose |
|---|---|
| `SyncProtocol` | Sync step 1 (state vector), sync step 2 (missing update), update messages: `WriteSyncStep1`, `ReadSyncMessage`, ... |
| `Awareness` | Per-client JSON presence state with clocks, the 30 s outdated timeout and `Change` / `Update` events |
| `AwarenessProtocol` | `EncodeAwarenessUpdate`, `ApplyAwarenessUpdate`, `RemoveAwarenessStates`, `ModifyAwarenessUpdate` |
| `Messages` | The y-websocket framing: 0 = sync, 1 = awareness, 2 = auth, 3 = query awareness, and `Handle` for one frame |
| `ProtocolMessage`, `SyncProtocolHandler` | yrs' message model and overridable protocol handler, for transports that batch messages |

Awareness states are JSON text. They are normalised the way `JSON.stringify(JSON.parse(text))` would, so encoded
awareness updates match those of a JavaScript peer exactly. The outdated check runs on a timer of the
`TimeProvider` given in `AwarenessOptions` (the system clock by default); set `CheckInterval = TimeSpan.Zero` and call
`CheckOutdated()` yourself to drive it from your own loop or from a test clock.

### A y-websocket compatible relay

A room holds one document and one awareness. Each connection is anything that can send a binary frame - an
ASP.NET Core `WebSocket`, a SignalR stream, a TCP socket:

```csharp
using YjsSharp;
using YjsSharp.Sync;

public sealed class Room
{
    private readonly Doc                        _doc       = new Doc();
    private readonly Awareness                  _awareness;
    private readonly HashSet<IConnection>       _conns     = new HashSet<IConnection>();
    private readonly Dictionary<IConnection, HashSet<ulong>> _clientsOf = new();

    public Room()
    {
        _awareness = new Awareness(_doc);
        _awareness.SetLocalState(null);            // the server itself has no presence

        // Every change to the document - from any connection - goes to everybody else.
        _doc.Update += (_, e) => Broadcast(Messages.EncodeUpdate(e.Update), except: e.Origin as IConnection);

        // Every awareness change goes to everybody, and we remember which clients a connection speaks for.
        _awareness.Update += (_, e) =>
        {
            if (e.Origin is IConnection conn)
            {
                var ids = _clientsOf[conn];
                foreach (var id in e.Added) ids.Add(id);
                foreach (var id in e.Removed) ids.Remove(id);
            }
            Broadcast(Messages.EncodeAwarenessUpdate(_awareness, e.All), except: null);
        };
    }

    public void OnOpen(IConnection conn)
    {
        _conns.Add(conn);
        _clientsOf[conn] = new HashSet<ulong>();
        conn.Send(Messages.EncodeSyncStep1(_doc));             // ask for what the client has
        var states = _awareness.States;
        if (states.Count > 0)
        {
            conn.Send(Messages.EncodeAwarenessUpdate(_awareness, states.Keys));
        }
    }

    public void OnMessage(IConnection conn, byte[] frame)
    {
        // Sync step 1 is answered with step 2; step 2, updates and awareness are applied with the
        // connection as origin (so the Update handlers above know where they came from).
        var reply = Messages.Handle(frame, _awareness, origin: conn);
        if (reply != null)
        {
            conn.Send(reply);
        }
    }

    public void OnClose(IConnection conn)
    {
        _conns.Remove(conn);
        AwarenessProtocol.RemoveAwarenessStates(_awareness, _clientsOf[conn], origin: null);
        _clientsOf.Remove(conn);
    }

    private void Broadcast(byte[] frame, IConnection? except)
    {
        foreach (var c in _conns)
        {
            if (c != except) c.Send(frame);
        }
    }
}

public interface IConnection
{
    void Send(byte[] frame);
}
```

That is the protocol `y-websocket`'s `WebsocketProvider` expects: a browser client connected to this room syncs,
receives everyone's edits and cursors, and gets the presence of a closed tab removed. A `Doc` is not thread-safe, so
handle one room's messages on one thread at a time (a lock or a channel per room). To persist the room, store
`Y.MergeUpdates` of the received updates, or `_doc.EncodeStateAsUpdate()`, and `ApplyUpdate` it when the room is
created again. For a read-only connection, override `SyncProtocolHandler.HandleUpdate` / `HandleSyncStep2`, or
check the frame type with `Messages.ReadMessageType` before handling it.

## Compatibility

- **Yjs 13.6.x**: updates (v1 and v2), state vectors, delete sets, snapshots and relative positions are read and
  written in the Yjs binary formats. The test suite replays fixtures recorded with **Yjs 13.6.33** for text (with
  formatting, embeds, surrogate pairs and emoji), arrays, maps, nested types, XML, subdocuments, snapshots, relative
  positions, concurrent and out-of-order edits from several peers, garbage-collected and `GC = false` documents, and
  large documents, and checks that YjsSharp produces byte-identical encodings, merges, diffs and format conversions for
  the same operations.
- **y-protocols 1.0.7**: sync handshakes, update messages, awareness updates and the y-websocket framing are checked
  against recorded byte streams.
- A live round-trip test applies YjsSharp's updates in real Yjs and runs a seeded three-peer fuzz that alternates
  JavaScript and C# peers; it runs when node is available (`tests/fixtures-generator`), and is skipped otherwise.

Client ids are 53-bit safe like in Yjs. Text indices are UTF-16 code units, as in JavaScript.

## Building and testing

```bash
dotnet build YjsSharp.slnx
dotnet run --project tests/YjsSharp.Tests                       # all tests (Microsoft.Testing.Platform)
dotnet run --project tests/YjsSharp.Tests -- --filter Awareness  # a subset
```

The compatibility fixtures under `tests/YjsSharp.Tests/Compat/Fixtures` are committed. To regenerate them, or to
enable the live round-trip tests, install the pinned Yjs packages:

```bash
cd tests/fixtures-generator && npm ci && node generate.mjs
```

## Credits

YjsSharp is a port of the work of others:

- [Yjs](https://github.com/yjs/yjs), [y-protocols](https://github.com/yjs/y-protocols) and
  [lib0](https://github.com/dmonad/lib0) by Kevin Jahns and contributors (MIT).
- [y-crdt / yrs](https://github.com/y-crdt/y-crdt), the Rust port by Bartosz Sypytkowski and contributors (MIT),
  whose v0.28 test suite and sync protocol implementation were ported here.

Maintained by [Curiosity GmbH](https://curiosity.ai). Released under the MIT license.
