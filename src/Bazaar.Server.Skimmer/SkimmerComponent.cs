using Bazaar.Core;
using Bazaar.Server.Gateway;
using Bazaar.Server.Skimmer.Data;
using Bazaar.Server.Skimmer.Rules;
using Bazaar.Server.Skimmer.Scoring;
using Bazaar.Server.Skimmer.Shadow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Bazaar.Server.Skimmer;

/// <summary>
/// The Skimmer Bouncer in shadow mode (doc 13). Off until the operator enables component <c>skimmer</c>, because the scorer spends
/// model tokens. Settings under <c>Bazaar:Skimmer</c>: <c>Mode</c> (<c>shadow</c> or <c>off</c>; <c>defer</c> is refused until the gates
/// pass), <c>ThresholdBp</c> (7000), <c>ScorerModel</c> (<c>anthropic-direct:claude-haiku-5-5</c>), <c>ScorerTimeoutMs</c> (5000),
/// <c>DmAlwaysWakes</c> (true). The host supplies <c>NpgsqlDataSource</c>, <c>IModelGateway</c> and <see cref="IGatekeeperPolicySeed"/>.
/// </summary>
public sealed class SkimmerComponent : IBazaarComponent
{
    /// <summary>The gateway policy subject the scorer calls under.</summary>
    public const string GatekeeperSubject = "gatekeeper";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "skimmer",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["skimmer:shadow-gate"],
        EnabledByDefault: false);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Bazaar:Skimmer");
        var model = section["ScorerModel"] is { Length: > 0 } m ? m : "anthropic-direct:claude-haiku-5-5";
        var mode = Enum.TryParse<SkimmerMode>(section["Mode"], ignoreCase: true, out var parsed) ? parsed : SkimmerMode.Shadow;
        var threshold = int.TryParse(section["ThresholdBp"], out var t) && t is >= 0 and <= 10000 ? t : 7000;
        var timeoutMs = int.TryParse(section["ScorerTimeoutMs"], out var ms) && ms > 0 ? ms : 5000;
        var dmWakes = !bool.TryParse(section["DmAlwaysWakes"], out var dm) || dm;

        services.AddSingleton(new ShadowSettings(mode, threshold, TimeSpan.FromMilliseconds(timeoutMs), new GateRuleSettings(dmWakes)));
        services.TryAddSingleton(sp => new WakeDecisionRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IShadowDecisionStore>(sp => new PostgresShadowDecisionStore(sp.GetRequiredService<WakeDecisionRepository>()));
        services.TryAddSingleton<IShadowAgentDirectory>(sp => new PgShadowAgentDirectory(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IRelevanceScorer>(sp => new GatewayRelevanceScorer(sp.GetRequiredService<IModelGateway>(), GatekeeperSubject, model));
        services.TryAddSingleton(sp => new ShadowGate(sp.GetRequiredService<IRelevanceScorer>()));
        services.TryAddSingleton(sp => new ShadowConsumer(
            sp.GetRequiredService<ShadowGate>(),
            sp.GetRequiredService<IShadowMessageSource>(),
            sp.GetRequiredService<IShadowDecisionStore>(),
            model));
        services.AddHostedService(sp => new GatekeeperPolicySeeder(sp.GetRequiredService<IGatekeeperPolicySeed>(), model));
    }
}

/// <summary>Writes the <c>gatekeeper</c> policy at start, so the scorer's first call is not denied as an unknown agent.</summary>
internal sealed class GatekeeperPolicySeeder(IGatekeeperPolicySeed seed, string model) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => seed.EnsureAsync(model, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
