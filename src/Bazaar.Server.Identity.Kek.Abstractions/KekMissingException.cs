namespace Bazaar.Server.Identity.Kek;

/// <summary>
/// Thrown when the host is asked to open an existing KEK and there is none (doc 63 section 5, step 1). The host never
/// creates a KEK at start: a missing or empty key file usually means the boot root is not mounted, and a fresh key
/// would make every wrapped key unreadable. Only setup and the restore wizard create one.
/// </summary>
public sealed class KekMissingException(string message) : Exception(message);
