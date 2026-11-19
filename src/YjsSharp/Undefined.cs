namespace YjsSharp;

/// <summary>
/// The JavaScript <c>undefined</c> value as it can appear in Yjs content (lib0 Any type 127, or
/// <c>"undefined"</c> in JSON content). Use <see cref="Value"/>.
/// </summary>
public sealed class Undefined
{
    /// <summary>The single instance.</summary>
    public static readonly Undefined Value = new Undefined();

    private Undefined()
    {
    }

    /// <inheritdoc />
    public override string ToString() => "undefined";
}
