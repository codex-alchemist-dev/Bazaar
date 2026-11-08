namespace Bazaar.Server.Souls;

/// <summary>
/// An append was based on a head that is no longer current (doc 07: "a stale edit is rejected"). Nothing was written.
/// <see cref="ActualVersion"/> is the head when the service read it under the lock, or null when a concurrent insert
/// was caught by the primary key instead.
/// </summary>
public sealed class SoulVersionConflictException(Guid soulId, int expectedVersion, int? actualVersion)
    : Exception(actualVersion is { } actual
        ? $"Soul {soulId:D} is at version {actual}, not the expected {expectedVersion}."
        : $"Soul {soulId:D} changed concurrently; expected version {expectedVersion} is no longer the head.")
{
    public Guid SoulId { get; } = soulId;

    public int ExpectedVersion { get; } = expectedVersion;

    public int? ActualVersion { get; } = actualVersion;
}
