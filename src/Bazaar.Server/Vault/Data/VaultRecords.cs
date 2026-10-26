namespace Bazaar.Server.Vault.Data;

/// <summary>
/// One row of <c>vault_docs</c>. <see cref="DocId"/> is the stable document id (doc 18: it survives renames);
/// <see cref="FrontmatterJson"/> is the parsed front matter as opaque JSON.
/// </summary>
public sealed record VaultDocRecord(
    Guid DocId,
    Guid VaultId,
    string Path,
    string Title,
    string Type,
    string? Project,
    string Area,
    string? Owner,
    string FrontmatterJson,
    string? HeadCommit,
    DateTimeOffset UpdatedAt);

/// <summary>One row of <c>vault_blocks</c>: a block at <see cref="Ordinal"/> within its document.</summary>
public sealed record VaultBlockRecord(
    int Ordinal,
    string? BlockId,
    IReadOnlyList<string> HeadingPath,
    int StartLine,
    int EndLine,
    byte[] TextHash,
    string Body);

/// <summary>
/// One row of <c>vault_links</c>. <see cref="TargetDoc"/> is null while the link is unresolved; <see cref="TargetRaw"/>
/// keeps the text as written so it can resolve later.
/// </summary>
public sealed record VaultLinkRecord(
    Guid SrcDoc,
    int SrcOrdinal,
    int LinkOrd,
    string Kind,
    string TargetRaw,
    Guid? TargetDoc,
    string? TargetAnchor,
    string? Alias);

/// <summary>One row of <c>vault_commit</c>. <c>is_merge</c> is derived from <see cref="Parents"/>, not supplied.</summary>
public sealed record VaultCommitRecord(
    Guid VaultId,
    string Sha,
    IReadOnlyList<string> Parents,
    string Author,
    DateTimeOffset CommittedAt,
    IReadOnlyList<string> Refs,
    IReadOnlyList<string> TouchedPaths,
    Guid? GcId,
    Guid? TaskId);

/// <summary>An embedding for one block under one profile. The vector length must match the profile's dimensions.</summary>
public sealed record VaultBlockEmbedding(int Ordinal, byte[] TextHash, float[] Vector);
