namespace Bazaar.Rubedo;

/// <summary>
/// One problem found while compiling or evaluating. <see cref="Code"/> is stable and is what the golden corpus asserts.
/// <see cref="Offset"/> is a character offset into the source, or -1 when the problem is not tied to a position.
/// </summary>
public sealed record RubedoDiagnostic(string Code, string Message, int Offset = -1);

/// <summary>Internal control flow for a problem. It never leaves the library: the public API returns diagnostics.</summary>
internal sealed class RubedoException(string code, string message, int offset = -1) : Exception(message)
{
    public string Code { get; } = code;

    public int Offset { get; } = offset;

    public RubedoDiagnostic ToDiagnostic() => new(Code, Message, Offset);
}
