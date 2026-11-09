namespace Bazaar.Server.Gc;

/// <summary>The group chat does not exist.</summary>
public sealed class GcNotFoundException(Guid gcId) : Exception($"Group chat {gcId} does not exist.")
{
    public Guid GcId { get; } = gcId;
}

/// <summary>The group chat is archived, and archived GCs take no appends (docs/design/01, archive).</summary>
public sealed class GcArchivedException(Guid gcId) : Exception($"Group chat {gcId} is archived.")
{
    public Guid GcId { get; } = gcId;
}

/// <summary>The viewer may not read this group chat.</summary>
public sealed class GcAccessDeniedException(Guid gcId) : Exception($"Access to group chat {gcId} is denied.")
{
    public Guid GcId { get; } = gcId;
}
