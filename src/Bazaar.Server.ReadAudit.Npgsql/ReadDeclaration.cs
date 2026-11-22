namespace Bazaar.Server.ReadAudit.Npgsql;

/// <summary>
/// What a command reads, declared by the caller (doc 48 section 5.1). <see cref="ObjectType"/> is the table or object class.
/// When <see cref="PersonalIdColumn"/> names a column of the result, the object is personal: each row is recorded on its own
/// audit row by that id. Otherwise the rows are counted into one batched row per query shape.
/// </summary>
public sealed record ReadDeclaration(string ObjectType, string? PersonalIdColumn = null);
