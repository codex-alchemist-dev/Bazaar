namespace Bazaar.ModulePermissions.Fs;

/// <summary>
/// Thrown when a module's file access is refused for a reason the permission gate does not express: a path that is not
/// confined to a root, a link, a limit, an I/O failure. The message is a fixed sentence. It never repeats the path the module
/// gave (it may hold control characters) and never carries a host path, so the module learns nothing about the host layout.
/// </summary>
public sealed class ModuleFsRefusedException(string reason) : UnauthorizedAccessException(reason);
