namespace Bazaar.Server.Backup.Manifest;

/// <summary>One file in a backup or export bundle: its normalised relative path, byte length and lower-case SHA-256 hex digest.</summary>
public sealed record BackupManifestEntry(string Path, long Length, string Sha256);
