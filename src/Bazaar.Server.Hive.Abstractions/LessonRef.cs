namespace Bazaar.Server.Hive.Abstractions;

/// <summary>A lesson in one project's memory that someone wants to copy into Hive Mind.</summary>
/// <param name="OriginProject">Project slug the lesson came from.</param>
/// <param name="OriginNote">Vault-relative path of the source note.</param>
/// <param name="Kind">The Hive kind the lesson would be copied as.</param>
public sealed record LessonRef(string OriginProject, string OriginNote, HiveKind Kind);
