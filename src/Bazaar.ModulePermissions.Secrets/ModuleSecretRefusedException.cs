namespace Bazaar.ModulePermissions.Secrets;

/// <summary>
/// Thrown when a module's secret read is refused for a reason the permission gate does not express: the name cannot be a module
/// secret, or the store could not give the value. The message never carries the name or a value.
/// </summary>
public sealed class ModuleSecretRefusedException(string reason) : UnauthorizedAccessException(reason);
