namespace Bazaar.Server.TimeMachine;

/// <summary>
/// One part of a replay frame that comes from another module. A missing or failing module yields <see cref="ReplayPart.Unavailable{T}"/>
/// instead of failing the whole frame, so replay degrades with the modules that are installed.
/// </summary>
/// <typeparam name="T">The part's value type.</typeparam>
public sealed record ReplayPart<T>
{
    internal ReplayPart(bool isAvailable, T? value, string? reason)
    {
        IsAvailable = isAvailable;
        Value = value;
        Reason = reason;
    }

    /// <summary>True when <see cref="Value"/> holds the part.</summary>
    public bool IsAvailable { get; }

    /// <summary>The part, or default when unavailable.</summary>
    public T? Value { get; }

    /// <summary>Why the part is unavailable, or null when available.</summary>
    public string? Reason { get; }
}

/// <summary>Factories for <see cref="ReplayPart{T}"/>.</summary>
public static class ReplayPart
{
    /// <summary>A part that was rebuilt.</summary>
    public static ReplayPart<T> Available<T>(T value) => new(true, value, null);

    /// <summary>A part that could not be rebuilt.</summary>
    public static ReplayPart<T> Unavailable<T>(string reason) => new(false, default, reason);
}
