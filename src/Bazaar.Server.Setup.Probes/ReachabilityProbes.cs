namespace Bazaar.Server.Setup.Probes;

/// <summary>
/// <c>audit.anchorReachable</c>: the plan's audit anchor URL answers (doc 15). A <c>HEAD</c> with no credentials. Never repeats the URL.
/// No anchor in the plan is not evaluated: the pure anchor-target check owns whether one is required.
/// </summary>
public sealed class AuditAnchorReachableProbe(IReachabilityClient client, TimeSpan? limit = null) : ISetupInfrastructureProbe
{
    /// <inheritdoc />
    public string CheckId => "audit.anchorReachable";

    /// <inheritdoc />
    public async Task<SetupProbeReading> ProbeAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = context.Plan.Custody.AuditAnchorTarget;
        if (target is null)
        {
            return new SetupProbeReading(SetupProbeOutcome.Unavailable, "No external audit anchor is configured");
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            return new SetupProbeReading(SetupProbeOutcome.Failed, "The audit anchor target is not an absolute https URL.", "Set custody.auditAnchorTarget to an https URL.");
        }

        return await client.IsReachableAsync(url, limit ?? TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false)
            ? new SetupProbeReading(SetupProbeOutcome.Healthy, "The audit anchor target answers.")
            : new SetupProbeReading(SetupProbeOutcome.Failed, "The audit anchor target did not answer.", "Check the URL, the network and its TLS certificate.");
    }
}

/// <summary>
/// <c>models.reachable</c>: each model source of the plan that the default catalogue knows answers on its base URL (doc 05). Counts and
/// catalogue ids only: no URL, no key reference. A source the catalogue does not know has no base URL setup can see, so it is not counted.
/// </summary>
public sealed class ModelSourcesReachableProbe(IModelSourceCatalogue catalogue, IReachabilityClient client, TimeSpan? limit = null) : ISetupInfrastructureProbe
{
    /// <inheritdoc />
    public string CheckId => "models.reachable";

    /// <inheritdoc />
    public async Task<SetupProbeReading> ProbeAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var entries = new List<ModelSourceEntry>();
        foreach (var source in context.Plan.Models.Sources)
        {
            if (catalogue.TryGet(source.Id, out var entry) && entry is not null && !entries.Contains(entry))
            {
                entries.Add(entry);
            }
        }

        if (entries.Count == 0)
        {
            return new SetupProbeReading(SetupProbeOutcome.Unavailable, "The plan names no model source with a known base URL");
        }

        var tests = entries.Select(async entry => (entry, ok: Uri.TryCreate(entry.BaseUrl, UriKind.Absolute, out var url)
            && await client.IsReachableAsync(url, limit ?? TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false)));
        var results = await Task.WhenAll(tests).ConfigureAwait(false);
        var down = results.Where(result => !result.ok).Select(result => result.entry.Id).ToList();
        if (down.Count == 0)
        {
            return new SetupProbeReading(SetupProbeOutcome.Healthy, $"All {results.Length} known model source(s) answer.");
        }

        var message = $"{results.Length - down.Count} of {results.Length} known model source(s) answer. Not answering: {string.Join(", ", down)}.";
        const string fix = "Start the local runtime or check the network. A source that is down can be removed from the plan.";
        return down.Count == results.Length
            ? new SetupProbeReading(SetupProbeOutcome.Failed, message, fix)
            : new SetupProbeReading(SetupProbeOutcome.Degraded, message, fix);
    }
}

/// <summary>
/// <c>backups.reachable</c>: a <c>file:</c> backup target exists as a folder. Other target kinds need their own client, which setup does
/// not have, so they are not evaluated. The path is never repeated.
/// </summary>
public sealed class BackupTargetReachableProbe(Func<string, bool>? directoryExists = null) : ISetupInfrastructureProbe
{
    private const string _filePrefix = "file:";

    private readonly Func<string, bool> _directoryExists = directoryExists ?? Directory.Exists;

    /// <inheritdoc />
    public string CheckId => "backups.reachable";

    /// <inheritdoc />
    public Task<SetupProbeReading> ProbeAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = context.Plan.Backups.Target;
        if (string.IsNullOrWhiteSpace(target))
        {
            return Task.FromResult(new SetupProbeReading(SetupProbeOutcome.Unavailable, "No backup target is configured"));
        }

        if (!target.StartsWith(_filePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new SetupProbeReading(SetupProbeOutcome.Unavailable, "Only a file: backup target can be reached from setup"));
        }

        var path = target[_filePrefix.Length..].Trim();
        if (path.Length == 0)
        {
            return Task.FromResult(new SetupProbeReading(SetupProbeOutcome.Failed, "The backup target names no folder.", "Set backups.target to file:<folder>."));
        }

        return Task.FromResult(_directoryExists(path)
            ? new SetupProbeReading(SetupProbeOutcome.Healthy, "The backup target folder exists.")
            : new SetupProbeReading(SetupProbeOutcome.Failed, "The backup target folder does not exist.", "Create the folder, or mount the backup volume."));
    }
}
