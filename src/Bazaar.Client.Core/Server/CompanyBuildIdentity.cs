namespace Bazaar.Client.Core.Server;

/// <summary>
/// The company server this client build was made for (doc 37 section 4a). A company build replaces this file with a generated
/// version that carries the server's address and key-hash pin. This source file is the unset identity, so a release head built
/// from source has no server and fails closed with a clear message (<see cref="ClientServer.ConnectToCompanyServer"/>).
/// </summary>
internal static class CompanyBuildIdentity
{
    /// <summary>The server's base address, or null when this build carries no identity.</summary>
    public static Uri? Address => null;

    /// <summary>The SHA-256 pin of the server's key, or null when this build carries no identity.</summary>
    public static string? KeyHashPin => null;
}
