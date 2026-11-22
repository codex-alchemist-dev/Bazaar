namespace Bazaar.Client.ColdArchive;

/// <summary>An archived unit. <see cref="Kind"/> is <c>gc</c> or <c>project</c>.</summary>
public sealed record ArchiveUnitInfo(string Kind, string Id);

/// <summary>The answer to an archive request.</summary>
public sealed record ArchiveOutcome(string Kind, string Id, int Records, int Segments, string RootHash);

/// <summary>The answer to a verify request. <see cref="Problems"/> is empty when <see cref="Ok"/>.</summary>
public sealed record ArchiveVerifyOutcome(bool Ok, IReadOnlyList<string> Problems, string? RootHash);

/// <summary>The restore report: how many records are still live and identical, and which ones differ.</summary>
public sealed record ArchiveRestoreOutcome(int Restored, int AlreadyPresent, IReadOnlyList<string> Conflicts);

/// <summary>One search hit in archived text.</summary>
public sealed record ArchiveHit(string Kind, string Unit, string RecordId, DateTimeOffset At, string Snippet);

/// <summary>A refusal from the server, with its code (<c>forbidden</c>, <c>already_archived</c>, <c>cannot_reinsert</c>, ...).</summary>
public sealed class ArchiveRequestException(int status, string code) : Exception($"Archive request refused: {code} ({status}).")
{
    public int Status { get; } = status;

    public string Code { get; } = code;
}

/// <summary>The owner-only archive routes (doc 25 "Archive cold storage"). A head hands an implementation to the pane through the pane module context.</summary>
public interface IColdArchiveClient
{
    Task<IReadOnlyList<ArchiveUnitInfo>> ListAsync(CancellationToken ct);

    Task<ArchiveOutcome> ArchiveAsync(string kind, string id, CancellationToken ct);

    Task<ArchiveVerifyOutcome> VerifyAsync(string kind, string id, CancellationToken ct);

    Task<ArchiveRestoreOutcome> RestoreAsync(string kind, string id, CancellationToken ct);

    Task<IReadOnlyList<ArchiveHit>> SearchAsync(string query, CancellationToken ct);
}
