using YjsSharp.Updates;

namespace YjsSharp.Structs;

/// <summary>Base of every block in the struct store: <see cref="Item"/>, <see cref="GC"/> and <see cref="Skip"/>.</summary>
public abstract class AbstractStruct
{
    internal ID   _id;
    internal uint _length;

    internal AbstractStruct(ID id, uint length)
    {
        _id     = id;
        _length = length;
    }

    /// <summary>The id of the first element of the block.</summary>
    public ID Id => _id;

    /// <summary>The number of clocks the block spans.</summary>
    public uint Length => _length;

    /// <summary>Whether the block is deleted.</summary>
    public abstract bool Deleted { get; }

    internal virtual bool MergeWith(AbstractStruct right) => false;

    /// <summary>Whether <see cref="MergeWith"/> would succeed (without changing anything).</summary>
    internal virtual bool CanMergeWith(AbstractStruct right) => false;

    internal abstract void Write(IUpdateEncoder encoder, uint offset);

    internal abstract void Integrate(Transaction transaction, uint offset);

    internal virtual ulong? GetMissing(Transaction transaction, StructStore store) => null;
}

/// <summary>A garbage collected range: deleted content whose structure is no longer needed.</summary>
public sealed class GC : AbstractStruct
{
    internal const byte StructGCRefNumber = 0;

    internal GC(ID id, uint length) : base(id, length)
    {
    }

    /// <inheritdoc />
    public override bool Deleted => true;

    internal override bool CanMergeWith(AbstractStruct right) => right is GC;

    internal override bool MergeWith(AbstractStruct right)
    {
        if (right is not GC)
        {
            return false;
        }
        _length += right._length;
        return true;
    }

    internal override void Integrate(Transaction transaction, uint offset)
    {
        if (offset > 0)
        {
            _id     =  new ID(_id.Client, _id.Clock + offset);
            _length -= offset;
        }
        transaction.Doc.Store.AddStruct(this);
    }

    internal override void Write(IUpdateEncoder encoder, uint offset)
    {
        encoder.WriteInfo(StructGCRefNumber);
        encoder.WriteLen(_length - offset);
    }
}

/// <summary>A placeholder for a range that is missing from an update (only appears in merged updates).</summary>
public sealed class Skip : AbstractStruct
{
    internal const byte StructSkipRefNumber = 10;

    internal Skip(ID id, uint length) : base(id, length)
    {
    }

    /// <inheritdoc />
    public override bool Deleted => true;

    internal override bool CanMergeWith(AbstractStruct right) => right is Skip;

    internal override bool MergeWith(AbstractStruct right)
    {
        if (right is not Skip)
        {
            return false;
        }
        _length += right._length;
        return true;
    }

    internal override void Integrate(Transaction transaction, uint offset)
    {
        throw new System.InvalidOperationException("Skip structs cannot be integrated");
    }

    internal override void Write(IUpdateEncoder encoder, uint offset)
    {
        encoder.WriteInfo(StructSkipRefNumber);
        encoder.RestEncoder.WriteVarUInt(_length - offset);
    }
}
