# YjsSharp public API (contract)

This file is the contract other code is written against (Curiosity's Mosaik server, the sync and
awareness protocol layer). Names here are fixed; anything not listed is free to design.
Namespace for everything: `YjsSharp` (protocol types in `YjsSharp.Sync`).

Wire compatibility with Yjs 13.6.x (JS) is the first requirement: an update produced by `Y.encodeStateAsUpdate`
must apply here and vice versa, byte-compatible for v1 and v2.

```csharp
public sealed class DocOptions
{
    public ulong?  ClientId      { get; set; }   // random 32-bit-ish (JS safe) when null, like Yjs
    public string? Guid          { get; set; }
    public bool    GC            { get; set; } = true;   // false keeps deleted content (needed for snapshots)
    public bool    SkipGC        => !GC;
    public string? CollectionId  { get; set; }
    public bool    AutoLoad      { get; set; }
    public bool    ShouldLoad    { get; set; } = true;
    public object? Meta          { get; set; }
}

public sealed class Doc : IDisposable
{
    public Doc();
    public Doc(DocOptions options);
    public ulong   ClientId { get; }
    public string  Guid     { get; }

    public Text        GetText(string name);
    public YArray      GetArray(string name);
    public YMap        GetMap(string name);
    public XmlFragment GetXmlFragment(string name);

    // Implicit transactions: every mutating method on a shared type opens one when none is active,
    // exactly like Yjs. Transact groups several changes into one transaction / one update event.
    public void Transact(Action<Transaction> action, object? origin = null);
    public T    Transact<T>(Func<Transaction, T> action, object? origin = null);

    public byte[] EncodeStateVector();
    public byte[] EncodeStateAsUpdate(byte[]? encodedTargetStateVector = null);     // v1
    public byte[] EncodeStateAsUpdateV2(byte[]? encodedTargetStateVector = null);
    public void   ApplyUpdate(byte[] update, object? origin = null);                 // v1
    public void   ApplyUpdateV2(byte[] update, object? origin = null);

    public event EventHandler<UpdateEventArgs>?            Update;        // v1 bytes of each transaction
    public event EventHandler<UpdateEventArgs>?            UpdateV2;
    public event EventHandler<TransactionEventArgs>?       AfterTransaction;
    public event EventHandler<SubdocsEventArgs>?           Subdocs;
    public event EventHandler?                             Destroyed;
}

public sealed class UpdateEventArgs : EventArgs
{
    public byte[]       Update      { get; }
    public object?      Origin      { get; }
    public Transaction  Transaction { get; }
}

// Static helpers mirroring Yjs' top-level functions
public static class Y
{
    public static byte[]  MergeUpdates(IEnumerable<byte[]> updates);
    public static byte[]  MergeUpdatesV2(IEnumerable<byte[]> updates);
    public static byte[]  EncodeStateVectorFromUpdate(byte[] update);
    public static byte[]  EncodeStateVectorFromUpdateV2(byte[] update);
    public static byte[]  DiffUpdate(byte[] update, byte[] stateVector);
    public static byte[]  DiffUpdateV2(byte[] update, byte[] stateVector);
    public static byte[]  ConvertUpdateFormatV1ToV2(byte[] update);
    public static byte[]  ConvertUpdateFormatV2ToV1(byte[] update);
    public static Snapshot Snapshot(Doc doc);
    public static byte[]  EncodeSnapshot(Snapshot s);   public static Snapshot DecodeSnapshot(byte[] b);
    public static byte[]  EncodeSnapshotV2(Snapshot s); public static Snapshot DecodeSnapshotV2(byte[] b);
    public static Doc     CreateDocFromSnapshot(Doc originDoc, Snapshot snapshot, Doc? newDoc = null); // origin needs GC=false
    public static bool    EqualSnapshots(Snapshot a, Snapshot b);
    public static IReadOnlyDictionary<ulong, uint> DecodeStateVector(byte[] sv);
    public static byte[]  EncodeStateVector(IReadOnlyDictionary<ulong, uint> sv);
}

public sealed class Text   // Y.Text
{
    public int    Length { get; }
    public void   Insert(int index, string text, IDictionary<string, object?>? attributes = null);
    public void   Delete(int index, int length);
    public void   Format(int index, int length, IDictionary<string, object?> attributes);
    public void   ApplyDelta(IEnumerable<Delta> delta);
    public IReadOnlyList<Delta> ToDelta(Snapshot? snapshot = null, Snapshot? prevSnapshot = null);
    public override string ToString();
    public IDisposable Observe(Action<TextEvent> handler);
    public IDisposable ObserveDeep(Action<IReadOnlyList<YEvent>> handler);
    // + InsertEmbed, RemoveAttribute, SetAttribute, GetAttribute(s), Clone
}
// YArray (Insert/Push/Unshift/Delete/Get/Slice/ToArray/Length/Observe/ToJson),
// YMap (Set/Get/Has/Delete/Clear/Keys/Values/Entries/Count/Observe/ToJson),
// XmlFragment / XmlElement / XmlText / XmlHook with Yjs semantics.
// Values: null, bool, double/long, string, byte[], IList<object?>, IDictionary<string, object?> (the Yjs "Any"),
// plus nested shared types and subdocuments (Doc).

public sealed class UndoManager : IDisposable { /* Yjs UndoManager: Undo/Redo/StopCapturing/Clear, CaptureTimeout,
                                                   TrackedOrigins, events StackItemAdded/Popped/Updated */ }

public sealed class RelativePosition { /* Yjs relative positions + encode/decode, CreateRelativePositionFromTypeIndex,
                                          CreateAbsolutePositionFromRelativePosition */ }
```

```csharp
namespace YjsSharp.Sync;

// y-protocols/sync: message types 0 = SyncStep1, 1 = SyncStep2, 2 = Update
public static class SyncProtocol
{
    public const int MessageYjsSyncStep1 = 0, MessageYjsSyncStep2 = 1, MessageYjsUpdate = 2;
    public static void WriteSyncStep1(Encoder encoder, Doc doc);
    public static void WriteSyncStep2(Encoder encoder, Doc doc, byte[]? encodedStateVector);
    public static void WriteUpdate(Encoder encoder, byte[] update);
    public static void ReadSyncStep1(Decoder decoder, Encoder encoder, Doc doc);
    public static void ReadSyncStep2(Decoder decoder, Doc doc, object? origin);
    public static void ReadUpdate(Decoder decoder, Doc doc, object? origin);
    public static int  ReadSyncMessage(Decoder decoder, Encoder encoder, Doc doc, object? origin);
}

// y-protocols/awareness
public sealed class Awareness : IDisposable
{
    public Awareness(Doc doc);
    public ulong ClientId { get; }
    public IReadOnlyDictionary<ulong, string> States { get; }            // JSON state per client
    public string? GetLocalState();  public void SetLocalState(string? json);
    public void SetLocalStateField(string field, object? value);
    public event EventHandler<AwarenessChangeEventArgs>? Change;      // added/updated/removed client ids + origin
    public event EventHandler<AwarenessChangeEventArgs>? Update;
}
public static class AwarenessProtocol
{
    public static byte[] EncodeAwarenessUpdate(Awareness a, IEnumerable<ulong> clients);
    public static void   ApplyAwarenessUpdate(Awareness a, byte[] update, object? origin);
    public static void   RemoveAwarenessStates(Awareness a, IEnumerable<ulong> clients, object? origin);
    public static byte[] ModifyAwarenessUpdate(byte[] update, Func<string, string> modify);
}
```

lib0 encoding is public as `YjsSharp.Encoding.Encoder` / `Decoder` (var-uint, var-int, var-string, var-uint8-array,
any) because the protocol layer and applications frame messages with it.
