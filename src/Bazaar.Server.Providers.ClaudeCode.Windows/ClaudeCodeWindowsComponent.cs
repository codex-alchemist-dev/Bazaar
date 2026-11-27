using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Providers.ClaudeCode.Windows;

/// <summary>Adds the Windows Job Object guard to the Claude Code provider. Registers nothing on other systems.</summary>
public sealed class ClaudeCodeWindowsComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "claude-code-cli-windows",
        Version: "0.1.0",
        Kind: ComponentKind.Provider,
        DependsOn: ["claude-code-cli"],
        Provides: ["claude-code:child-process-container"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        AddJob(services);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddJob(IServiceCollection services) => services.AddSingleton<IChildProcessContainer>(_ => new WindowsJobObject());
}
