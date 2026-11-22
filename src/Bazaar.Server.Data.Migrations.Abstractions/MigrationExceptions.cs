namespace Bazaar.Server.Data.Migrations;

/// <summary>Base for refusals by the migration runner; the database is left at its last good version.</summary>
public class MigrationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>An applied file's content no longer matches the checksum recorded when it ran.</summary>
public sealed class MigrationChecksumException(string message) : MigrationException(message);

/// <summary>The database and the binary disagree about which migrations exist (older, newer or out of order).</summary>
public sealed class SchemaVersionException(string message) : MigrationException(message);
