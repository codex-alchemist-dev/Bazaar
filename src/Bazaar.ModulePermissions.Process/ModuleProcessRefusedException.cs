namespace Bazaar.ModulePermissions.Process;

/// <summary>
/// Thrown when a module's request to run a program is refused for a reason the permission gate does not express: a program
/// the host does not offer, an argument or input over a limit, too many programs at once, a program that could not be started.
/// The message is a fixed sentence. It never repeats what the module gave and never carries a host path.
/// </summary>
public sealed class ModuleProcessRefusedException(string reason) : UnauthorizedAccessException(reason);
