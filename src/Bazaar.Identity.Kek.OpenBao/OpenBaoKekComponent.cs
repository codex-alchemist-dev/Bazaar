using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Identity.Kek.OpenBao;

/// <summary>
/// Makes <c>identity.kek_provider = openbao</c> available (doc 14). It only registers the kind; the KEK module picks it when the
/// setting asks for it, so a host that never selects OpenBao opens no connection and needs no OpenBao settings.
/// </summary>
public sealed class OpenBaoKekComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "identity-kek-openbao",
        Version: "0.1.0",
        Kind: ComponentKind.Provider,
        DependsOn: ["identity-kek"],
        Provides: ["identity:kek:kind:openbao"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton<IKekProviderKind, OpenBaoKekKind>();
}

/// <summary>The <c>openbao</c> value of <c>identity.kek_provider</c>.</summary>
public sealed class OpenBaoKekKind : IKekProviderKind
{
    public string Kind => "openbao";

    public IKekProvider Create(Func<string, string?> setting, string dataRoot)
    {
        var options = OpenBaoOptions.From(setting);
        return new OpenBaoKekProvider(options, () => TokenReference.Read(options.TokenRef));
    }
}
