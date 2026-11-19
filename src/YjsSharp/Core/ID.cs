using System;
using YjsSharp.Encoding;

namespace YjsSharp;

/// <summary>A unique identifier of an inserted element: the client that created it and its logical clock.</summary>
public readonly struct ID : IEquatable<ID>
{
    /// <summary>The client id.</summary>
    public readonly ulong Client;

    /// <summary>The clock (per-client sequence number of the element).</summary>
    public readonly uint Clock;

    /// <summary>Creates an id.</summary>
    public ID(ulong client, uint clock)
    {
        Client = client;
        Clock  = clock;
    }

    /// <inheritdoc />
    public bool Equals(ID other) => Client == other.Client && Clock == other.Clock;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ID other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Client, Clock);

    /// <summary>Equality.</summary>
    public static bool operator ==(ID a, ID b) => a.Equals(b);

    /// <summary>Inequality.</summary>
    public static bool operator !=(ID a, ID b) => !a.Equals(b);

    /// <inheritdoc />
    public override string ToString() => $"<{Client}#{Clock}>";

    internal static bool Equal(ID? a, ID? b)
    {
        if (a.HasValue != b.HasValue)
        {
            return false;
        }
        return !a.HasValue || a.Value.Equals(b!.Value);
    }

    /// <summary>Writes the id as two var-uints.</summary>
    public void Write(Encoder encoder)
    {
        encoder.WriteVarUInt(Client);
        encoder.WriteVarUInt(Clock);
    }

    /// <summary>Reads an id written by <see cref="Write"/>.</summary>
    public static ID Read(Decoder decoder)
    {
        var client = decoder.ReadVarUInt();
        return new ID(client, decoder.ReadVarUInt32());
    }
}
