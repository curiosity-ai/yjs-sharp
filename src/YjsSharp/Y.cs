using System.Collections.Generic;
using YjsSharp.Updates;

namespace YjsSharp;

/// <summary>Static helpers mirroring Yjs' top-level functions.</summary>
public static class Y
{
    private static IReadOnlyList<byte[]> ToList(IEnumerable<byte[]> updates) => updates as IReadOnlyList<byte[]> ?? new List<byte[]>(updates);

    /// <summary>Merges several updates (v1) into one, without a document.</summary>
    public static byte[] MergeUpdates(IEnumerable<byte[]> updates) => UpdateUtils.MergeUpdates(ToList(updates));

    /// <summary>Merges several updates (v2) into one, without a document.</summary>
    public static byte[] MergeUpdatesV2(IEnumerable<byte[]> updates) => UpdateUtils.MergeUpdatesV2(ToList(updates));

    /// <summary>Computes the state vector an update (v1) would produce, without a document.</summary>
    public static byte[] EncodeStateVectorFromUpdate(byte[] update) => UpdateUtils.EncodeStateVectorFromUpdate(update, false);

    /// <summary>Computes the state vector an update (v2) would produce, without a document.</summary>
    public static byte[] EncodeStateVectorFromUpdateV2(byte[] update) => UpdateUtils.EncodeStateVectorFromUpdate(update, true);

    /// <summary>Removes from an update (v1) everything the state vector already contains.</summary>
    public static byte[] DiffUpdate(byte[] update, byte[] stateVector) => UpdateUtils.DiffUpdate(update, stateVector);

    /// <summary>Removes from an update (v2) everything the state vector already contains.</summary>
    public static byte[] DiffUpdateV2(byte[] update, byte[] stateVector) => UpdateUtils.DiffUpdateV2(update, stateVector);

    /// <summary>Converts an update from format v1 to v2.</summary>
    public static byte[] ConvertUpdateFormatV1ToV2(byte[] update) => UpdateUtils.ConvertUpdateFormatV1ToV2(update);

    /// <summary>Converts an update from format v2 to v1.</summary>
    public static byte[] ConvertUpdateFormatV2ToV1(byte[] update) => UpdateUtils.ConvertUpdateFormatV2ToV1(update);

    /// <summary>The first and last clock per client covered by an update (v1).</summary>
    public static (IReadOnlyDictionary<ulong, uint> From, IReadOnlyDictionary<ulong, uint> To) ParseUpdateMeta(byte[] update)
    {
        var (from, to) = UpdateUtils.ParseUpdateMeta(update, false);
        return (from, to);
    }

    /// <summary>The first and last clock per client covered by an update (v2).</summary>
    public static (IReadOnlyDictionary<ulong, uint> From, IReadOnlyDictionary<ulong, uint> To) ParseUpdateMetaV2(byte[] update)
    {
        var (from, to) = UpdateUtils.ParseUpdateMeta(update, true);
        return (from, to);
    }

    /// <summary>Takes a snapshot of a document.</summary>
    public static Snapshot Snapshot(Doc doc) => YjsSharp.Snapshot.Create(doc);

    /// <summary>An empty snapshot.</summary>
    public static Snapshot EmptySnapshot => YjsSharp.Snapshot.Empty;

    /// <summary>Encodes a snapshot (v1).</summary>
    public static byte[] EncodeSnapshot(Snapshot s) => s.Encode();

    /// <summary>Decodes a snapshot (v1).</summary>
    public static Snapshot DecodeSnapshot(byte[] b) => YjsSharp.Snapshot.Decode(b);

    /// <summary>Encodes a snapshot (v2).</summary>
    public static byte[] EncodeSnapshotV2(Snapshot s) => s.EncodeV2();

    /// <summary>Decodes a snapshot (v2).</summary>
    public static Snapshot DecodeSnapshotV2(byte[] b) => YjsSharp.Snapshot.DecodeV2(b);

    /// <summary>Restores a snapshot into a new document. The origin document must have GC disabled.</summary>
    public static Doc CreateDocFromSnapshot(Doc originDoc, Snapshot snapshot, Doc? newDoc = null) => YjsSharp.Snapshot.CreateDocFromSnapshot(originDoc, snapshot, newDoc);

    /// <summary>Whether two snapshots are equal.</summary>
    public static bool EqualSnapshots(Snapshot a, Snapshot b) => a.Equals(b);

    /// <summary>Whether the snapshot contains everything in the (v1) update.</summary>
    public static bool SnapshotContainsUpdate(Snapshot snapshot, byte[] update) => snapshot.ContainsUpdate(update);

    /// <summary>Whether the snapshot contains everything in the (v2) update.</summary>
    public static bool SnapshotContainsUpdateV2(Snapshot snapshot, byte[] update) => snapshot.ContainsUpdateV2(update);

    /// <summary>Decodes a state vector.</summary>
    public static IReadOnlyDictionary<ulong, uint> DecodeStateVector(byte[] sv) => UpdateCodec.DecodeStateVector(sv);

    /// <summary>Encodes a state vector.</summary>
    public static byte[] EncodeStateVector(IReadOnlyDictionary<ulong, uint> sv) => UpdateCodec.EncodeStateVector(sv);

    /// <summary>Encodes the state vector of a document.</summary>
    public static byte[] EncodeStateVector(Doc doc) => doc.EncodeStateVector();

    /// <summary>Encodes the state of a document as an update (v1).</summary>
    public static byte[] EncodeStateAsUpdate(Doc doc, byte[]? encodedTargetStateVector = null) => doc.EncodeStateAsUpdate(encodedTargetStateVector);

    /// <summary>Encodes the state of a document as an update (v2).</summary>
    public static byte[] EncodeStateAsUpdateV2(Doc doc, byte[]? encodedTargetStateVector = null) => doc.EncodeStateAsUpdateV2(encodedTargetStateVector);

    /// <summary>Applies an update (v1) to a document.</summary>
    public static void ApplyUpdate(Doc doc, byte[] update, object? origin = null) => doc.ApplyUpdate(update, origin);

    /// <summary>Applies an update (v2) to a document.</summary>
    public static void ApplyUpdateV2(Doc doc, byte[] update, object? origin = null) => doc.ApplyUpdateV2(update, origin);

    /// <summary>Creates a relative position from an index in a type.</summary>
    public static RelativePosition CreateRelativePositionFromTypeIndex(AbstractType type, int index, int assoc = 0) => RelativePosition.FromTypeIndex(type, index, assoc);

    /// <summary>Resolves a relative position in a document.</summary>
    public static AbsolutePosition? CreateAbsolutePositionFromRelativePosition(RelativePosition rpos, Doc doc, bool followUndoneDeletions = true) => rpos.ToAbsolute(doc, followUndoneDeletions);

    /// <summary>Encodes a relative position.</summary>
    public static byte[] EncodeRelativePosition(RelativePosition rpos) => rpos.Encode();

    /// <summary>Decodes a relative position.</summary>
    public static RelativePosition DecodeRelativePosition(byte[] data) => RelativePosition.Decode(data);

    /// <summary>Whether two relative positions are equal.</summary>
    public static bool CompareRelativePositions(RelativePosition? a, RelativePosition? b) => ReferenceEquals(a, b) || (a != null && a.Equals(b));

    /// <summary>Whether <paramref name="parent"/> is an ancestor of <paramref name="child"/>.</summary>
    public static bool IsParentOf(AbstractType parent, Structs.Item? child) => UndoManager.IsParentOf(parent, child);
}
