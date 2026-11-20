using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Identity.Kek.Tpm;

/// <summary>
/// Makes <c>identity.kek_provider = tpm</c> available (doc 14). It only registers the kind; the KEK module picks it when the setting
/// asks for it, so a host that never selects <c>tpm</c> touches no TPM and needs no TPM settings.
/// </summary>
public sealed class TpmKekComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "identity-kek-tpm",
        Version: "0.1.0",
        Kind: ComponentKind.Provider,
        DependsOn: ["identity-kek"],
        Provides: ["identity:kek:kind:tpm"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton<IKekProviderKind>(new TpmKekKind(new SystemdCredsSealer(), new SystemdVirtualizationProbe()));
}

/// <summary>The <c>tpm</c> value of <c>identity.kek_provider</c>. Linux only (systemd-creds).</summary>
public sealed class TpmKekKind(ITpmSealer sealer, IVirtualizationProbe probe, bool? isLinux = null) : IKekProviderKind
{
    public string Kind => "tpm";

    public IKekProvider Create(Func<string, string?> setting, string dataRoot)
    {
        if (!(isLinux ?? OperatingSystem.IsLinux()))
            throw new PlatformNotSupportedException("identity.kek_provider 'tpm' is the Linux provider. Use 'dpapi' on Windows (doc 14).");
        return TpmKekProvider.Open(TpmOptions.From(setting, dataRoot), sealer, probe);
    }
}
