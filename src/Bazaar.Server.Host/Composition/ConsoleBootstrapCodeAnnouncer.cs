using Bazaar.Server.Setup.Http;

namespace Bazaar.Server.Host.Composition;

/// <summary>Prints the setup bootstrap code to the process's standard output, which the operator who started the server can see. It never goes through logging.</summary>
public sealed class ConsoleBootstrapCodeAnnouncer : Bazaar.Server.Setup.Host.IBootstrapCodeAnnouncer
{
    /// <inheritdoc />
    public void Announce(string code) =>
        Console.Out.WriteLine($"Bazaar setup bootstrap code (use once, from this machine, in the {SetupHttpConstants.BootstrapCodeHeader} header): {code}");
}
