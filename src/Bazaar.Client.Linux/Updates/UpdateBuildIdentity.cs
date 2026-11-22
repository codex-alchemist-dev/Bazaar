namespace Bazaar.Client.Linux.Updates;

/// <summary>
/// The company identity this build carries (doc 37 section 4a): the server address and its key-hash pin. Both null for a
/// build made from source. The head passes <c>CompanyBuildIdentity</c> values in, so the reader stays pure.
/// </summary>
/// <param name="ServerAddress">The company server's base address, or null.</param>
/// <param name="KeyHashPin">The server key-hash pin, or null.</param>
public sealed record UpdateBuildIdentity(Uri? ServerAddress, string? KeyHashPin);
