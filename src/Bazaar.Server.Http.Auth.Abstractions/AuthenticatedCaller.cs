namespace Bazaar.Server.Http.Auth;

/// <summary>
/// The device and human behind a verified access token (doc 32 section 5). The token is a bearer credential scoped to one device and its
/// principal; routes read it with <see cref="HttpContextAuthenticatedCallerExtensions.GetAuthenticatedCaller"/>.
/// </summary>
public sealed record AuthenticatedCaller(Guid DeviceId, Guid HumanId);
