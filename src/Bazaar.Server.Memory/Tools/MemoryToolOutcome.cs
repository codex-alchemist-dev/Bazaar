namespace Bazaar.Server.Memory.Tools;

/// <summary>
/// The result of a memory tool call: the wire DTO on success, or a <see cref="MemoryToolError"/>. A conflict on
/// update is not an error; it is a successful call whose DTO says <c>Conflict</c>.
/// </summary>
/// <typeparam name="T">The DTO type.</typeparam>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000", Justification = "Factories on the result type are the intended API.")]
public sealed record MemoryToolOutcome<T>
where T : class
{
    private MemoryToolOutcome(T? value, MemoryToolError? error)
    {
        Value = value;
        Error = error;
    }

    /// <summary>The DTO when <see cref="IsSuccess"/> is true.</summary>
    public T? Value { get; }

    /// <summary>The refusal when <see cref="IsSuccess"/> is false.</summary>
    public MemoryToolError? Error { get; }

    /// <summary>True when the call succeeded.</summary>
    public bool IsSuccess => Error is null;

    /// <summary>A successful call.</summary>
    public static MemoryToolOutcome<T> Ok(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new MemoryToolOutcome<T>(value, null);
    }

    /// <summary>A refused call.</summary>
    public static MemoryToolOutcome<T> Fail(MemoryToolErrorCode code, string message) =>
        new(null, new MemoryToolError(code, message));
}
