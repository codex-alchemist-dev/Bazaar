using Bazaar.CompanyIdentity;
using Bazaar.Server.CompanyBuild;
using Bazaar.Server.CompanyBuild.Abstractions;
using Bazaar.Server.CompanyBuild.State;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Operator settings for the company build (<c>Bazaar:CompanyBuild</c>, docs/design/51 section 5a). The executor is registered only
/// when <see cref="ServerAddress"/>, <see cref="ServerKeyPin"/> and at least one target are set; until then a rebuild is refused with a reason.
/// </summary>
public sealed class CompanyBuildExecutorOptions
{
    public const string SectionName = "Bazaar:CompanyBuild";

    /// <summary>Where company output folders go. Default: <c>company-build/out</c> under <c>Bazaar:Host:DataRoot</c>.</summary>
    public string? OutputRoot { get; set; }

    /// <summary>The client source copy that receives <c>CompanyBuildIdentity.cs</c>. Default: <c>company-build/identity-source</c> under the data root.</summary>
    public string? IdentitySourceDirectory { get; set; }

    /// <summary>The address clients connect to. https, or http on loopback.</summary>
    public string? ServerAddress { get; set; }

    /// <summary>Lowercase hex SHA-256 pin of the server key (64 characters).</summary>
    public string? ServerKeyPin { get; set; }

    /// <summary>Target id to client project path, for example <c>windows-msix</c>.</summary>
    public Dictionary<string, string> Targets { get; set; } = [];

    /// <summary>Attempts per target, 1 to <see cref="CompanyBuildRunner.MaxAllowedAttempts"/>.</summary>
    public int MaxAttempts { get; set; } = CompanyBuildRunner.DefaultMaxAttempts;
}

/// <summary>
/// Binds <see cref="ICompanyBuildExecutor"/> to <see cref="CompanyBuildPipeline"/>. Targets, identity and output folder are
/// operator settings. Signing stays with the company signing flow: sets are reported unsigned here. Failure text and paths from the
/// pipeline are not returned (they stay in the output folder's BUILD-REPORT.txt for the operator), and nothing is logged.
/// Cancellation propagates as <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class CompanyBuildExecutor : ICompanyBuildExecutor
{
    private readonly CompanyServerIdentity _identity;
    private readonly IReadOnlyList<CompanyBuildTarget> _targets;
    private readonly string _outputRoot;
    private readonly string _identitySourceDirectory;
    private readonly int _maxAttempts;
    private readonly Func<CompanyBuildTarget, string, CancellationToken, Task> _buildTarget;
    private readonly TimeProvider _time;

    public CompanyBuildExecutor(
        CompanyServerIdentity identity,
        IReadOnlyList<CompanyBuildTarget> targets,
        string outputRoot,
        string identitySourceDirectory,
        int maxAttempts = CompanyBuildRunner.DefaultMaxAttempts,
        Func<CompanyBuildTarget, string, CancellationToken, Task>? buildTarget = null,
        TimeProvider? time = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _outputRoot = RequireSafeFolder(outputRoot, nameof(outputRoot));
        _identitySourceDirectory = RequireSafeFolder(identitySourceDirectory, nameof(identitySourceDirectory));
        _maxAttempts = maxAttempts;
        _buildTarget = buildTarget ?? ((target, directory, ct) => DotnetPublishBuildStep.RunAsync(target.ProjectPath, directory, ct));
        _time = time ?? TimeProvider.System;
    }

    public async Task<CompanyBuildSetReport> RunAsync(CompanyBuildPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var report = await CompanyBuildPipeline.RunAsync(
            plan, _identity, _targets, _outputRoot, _identitySourceDirectory, _buildTarget,
            isSigned: false, _maxAttempts, _time, ct).ConfigureAwait(false);

        // Counts and flags only: no messages, no paths.
        var targets = report.Targets
            .Select(t => new CompanyBuildTargetReport(
                t.TargetId,
                new CompanyBuildReport(
                    t.Report.Succeeded,
                    t.Report.Attempts,
                    t.Report.Retries,
                    [.. t.Report.FailureMessages.Select((_, i) => $"Attempt {i + 1} failed.")],
                    string.Empty)))
            .ToList();
        return report with { Targets = targets, OutputDirectory = string.Empty };
    }

    private static string RequireSafeFolder(string path, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, name);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The folder must be an absolute path.", name);
        }

        var full = Path.GetFullPath(path);
        if (string.Equals(full, Path.GetPathRoot(full), StringComparison.Ordinal))
        {
            throw new ArgumentException("The folder must not be a drive or filesystem root.", name);
        }

        return full;
    }
}

public static class CompanyBuildExecutorServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ICompanyBuildExecutor"/> when the operator has configured it. Missing settings leave it unregistered
    /// (rebuild stays refused). Invalid settings throw at start-up with the setting name, never a value.
    /// </summary>
    public static IServiceCollection AddBazaarCompanyBuildExecutor(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(CompanyBuildExecutorOptions.SectionName).Get<CompanyBuildExecutorOptions>()
            ?? new CompanyBuildExecutorOptions();
        if (string.IsNullOrWhiteSpace(options.ServerAddress) || string.IsNullOrWhiteSpace(options.ServerKeyPin) || options.Targets.Count == 0)
        {
            return services;
        }

        var dataRoot = configuration["Bazaar:Host:DataRoot"];
        string Resolve(string? configured, string leaf, string setting)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            return string.IsNullOrWhiteSpace(dataRoot)
                ? throw new InvalidOperationException($"{CompanyBuildExecutorOptions.SectionName}:{setting} or Bazaar:Host:DataRoot must be set.")
                : Path.Combine(dataRoot, "company-build", leaf);
        }

        CompanyServerIdentity identity;
        try
        {
            identity = CompanyServerIdentity.Create(new Uri(options.ServerAddress, UriKind.Absolute), options.ServerKeyPin);
        }
        catch (Exception e) when (e is ArgumentException or UriFormatException)
        {
            throw new InvalidOperationException($"{CompanyBuildExecutorOptions.SectionName}:ServerAddress or ServerKeyPin is not acceptable.");
        }

        if (options.MaxAttempts is < 1 or > CompanyBuildRunner.MaxAllowedAttempts)
        {
            throw new InvalidOperationException($"{CompanyBuildExecutorOptions.SectionName}:MaxAttempts must be between 1 and {CompanyBuildRunner.MaxAllowedAttempts}.");
        }

        var targets = options.Targets.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => new CompanyBuildTarget(t.Key, t.Value)).ToList();
        var outputRoot = Resolve(options.OutputRoot, "out", "OutputRoot");
        var identityDirectory = Resolve(options.IdentitySourceDirectory, "identity-source", "IdentitySourceDirectory");

        // Constructing validates the folders now, so a bad setting fails at start-up and not mid-build.
        var executor = new CompanyBuildExecutor(identity, targets, outputRoot, identityDirectory, options.MaxAttempts);
        services.TryAddSingleton<ICompanyBuildExecutor>(executor);
        return services;
    }
}
