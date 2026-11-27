using Cdax.Core;

namespace Bazaar.Client.Tui.Host;

/// <summary>
/// The manifest of the TUI program (doc 58 work package 4, "each with its own manifest"). It states what the program provides and
/// what it needs from the server's modules, in the same shape every other component uses. The program is a separate process from the
/// server and adds nothing to it.
/// </summary>
public static class TuiHostManifest
{
    public static ComponentManifest Definition { get; } = new(
        Id: "tui-host",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["roles", "audit", "ops-ports"],
        Provides: ["feature:server-tui"],
        SoftDependsOn: ["web-admin", "setup"]);
}
