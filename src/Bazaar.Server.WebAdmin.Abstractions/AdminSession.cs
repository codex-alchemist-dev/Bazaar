using Bazaar.Roles.Abstractions;

namespace Bazaar.Server.WebAdmin;

/// <summary>A signed-in panel user: who, what kind of principal, and the device credential the session rides on.</summary>
public sealed record AdminSession(Guid PrincipalId, PrincipalKind Kind, Guid DeviceId);
