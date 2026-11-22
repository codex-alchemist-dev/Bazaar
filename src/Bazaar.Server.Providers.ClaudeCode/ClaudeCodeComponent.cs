using Bazaar.Core;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Modules.Health;
using Bazaar.Server.Secrets;
using Microsoft.Extensions.Configuration;
using Bazaar.Server.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Providers.ClaudeCode;

/// <summary>
/// The Claude Code CLI provider as a Bazaar Component (docs/design/91-claude-code-cli-provider.md). Off by default: when
/// <c>Bazaar:Gateway:ClaudeCode:Enabled</c> is false nothing is registered.
/// </summary>
public sealed class ClaudeCodeComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "claude-code-cli",
        Version: "0.1.0",
        Kind: ComponentKind.Provider,
        DependsOn: [],
        Provides: ["gateway:provider:claude-code", "gateway:adapter:" + ClaudeCodeAdapter.WireName]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Guided setup is how an operator turns the provider on, so it is available while the provider is off.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISettingContribution, ClaudeCodeSettingContribution>());
        services.TryAddSingleton<IClaudeCodeAcknowledgementStore, InMemoryClaudeCodeAcknowledgementStore>();
        services.AddSingleton(sp => new ClaudeCodeGuidedSetup(
            sp.GetRequiredService<IClaudeCodeAcknowledgementStore>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System));
        var probe = new ClaudeCodeOptions();
        configuration.GetSection(ClaudeCodeOptions.SectionName).Bind(probe);
        if (!probe.Enabled)
        {
            return;
        }

        services.AddOptions<ClaudeCodeOptions>()
            .Configure(o => configuration.GetSection(ClaudeCodeOptions.SectionName).Bind(o))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ClaudeCodeOptions>, ClaudeCodeOptionsValidator>();
        services.AddSingleton(sp => new ClaudeCodeLaneState(sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddSingleton<IClaudeCodeLaneState>(sp => sp.GetRequiredService<ClaudeCodeLaneState>());
        services.AddSingleton<IModuleHealthContract>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ClaudeCodeOptions>>().Value;
            return new ClaudeCodeHealth(
                options,
                sp.GetRequiredService<IClaudeCodeLaneState>(),
                new ClaudeCodeProcessRunner(options.MaxLineChars, sp.GetService<IChildProcessContainer>()),
                sp.GetService<TimeProvider>() ?? TimeProvider.System);
        });
        services.AddHostedService(sp => new ClaudeCodeOrphanSweepService(
            sp.GetRequiredService<IOptions<ClaudeCodeOptions>>().Value,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ClaudeCodeOrphanSweepService>>()));
        services.AddSingleton(new ModelAdapterRegistration(
            ClaudeCodeAdapter.WireName,
            sp =>
            {
                var options = sp.GetRequiredService<IOptions<ClaudeCodeOptions>>().Value;
                return new ClaudeCodeAdapter(
                    options,
                    new ClaudeCodeProcessRunner(options.MaxLineChars, sp.GetService<IChildProcessContainer>()),
                    sp.GetRequiredService<ClaudeCodeLaneState>(),
                    sp.GetService<ISecretResolver>(),
                    sp.GetService<TimeProvider>() ?? TimeProvider.System);
            }));
    }
}

/// <summary>Start-time checks. Each failure names its configuration key and never echoes a secret.</summary>
public sealed class ClaudeCodeOptionsValidator : IValidateOptions<ClaudeCodeOptions>
{
    public ValidateOptionsResult Validate(string? name, ClaudeCodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var s = ClaudeCodeOptions.SectionName;
        var failures = new List<string>();
        if (options.AuthMode == ClaudeCodeAuthMode.Login)
        {
            if (string.IsNullOrWhiteSpace(options.ConfigDir))
            {
                failures.Add($"{s}:ConfigDir is required in login mode.");
            }

            if (!options.LoginOwner.StartsWith("user:", StringComparison.Ordinal) || options.LoginOwner.Length <= 5)
            {
                failures.Add($"{s}:LoginOwner must name one user principal, for example user:owner (one login serves one human).");
            }

            if (options.TermsAcknowledgedAt is null)
            {
                failures.Add($"{s}:TermsAcknowledgedAt is required: the operator must accept the terms summary of docs/design/91 section 2.");
            }
        }
        else if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            failures.Add($"{s}:ApiKey (a secret reference) is required in API key mode.");
        }

        if (options.MaxConcurrent < 1)
        {
            failures.Add($"{s}:MaxConcurrent must be at least 1.");
        }

        if (options.MaxStepsPerHour < 0)
        {
            failures.Add($"{s}:MaxStepsPerHour must not be negative.");
        }

        foreach (var m in options.EffectiveModels().Where(m => !ClaudeCodeArguments.IsValidModelId(m)))
        {
            failures.Add($"{s}:Models has an id that is not allowed: {(m.Length > 40 ? m[..40] : m)}");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
