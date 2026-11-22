using Bazaar.Server.DataVolume;
using Bazaar.Server.Identity.Kek;

namespace Bazaar.Server.Host.Startup;

/// <summary>
/// <c>Bazaar.Server.Host volume open|close|status</c> (doc 63 section 4). Wiring only: it reads the same host configuration as
/// the service and hands the operators and providers of this OS to <see cref="VolumeCommand"/>.
/// </summary>
public static class VolumeSubcommand
{
    public static Task<int> RunAsync(string[] args, TextWriter output, IConfiguration? configuration = null, CancellationToken ct = default)
    {
        configuration ??= new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables("BAZAAR__")
            .Build();

        var (operators, providers) = VolumePlatform.Create();
        return VolumeCommand.RunAsync(args, configuration, operators, providers, () => KekComponent.OpenExisting(configuration), output, ct);
    }
}
