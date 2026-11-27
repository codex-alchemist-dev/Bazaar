using Bazaar.Server.PsychWard.Abstractions;

namespace Bazaar.Server.PsychWard;

/// <summary>
/// The admission triggers beyond the loop guard (doc 26, section 1). It applies each rule to the facts it is given and admits
/// through <see cref="IPsychWardService"/>. Details are short and secret-free: they name the tag, limit or count, never content.
/// </summary>
public sealed class WardTriggerIntake(IPsychWardService ward, PsychWardSettings settings) : IWardTriggerIntake
{
    /// <inheritdoc />
    public Task<WardOutcome?> ReportOutcomesAsync(
        Guid agentId, string tag, IReadOnlyList<bool> recentFailedNewestFirst, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentNullException.ThrowIfNull(recentFailedNewestFirst);

        var count = settings.RepeatedFailureCount;
        var tripped = recentFailedNewestFirst.Count >= count && recentFailedNewestFirst.Take(count).All(failed => failed);
        return tripped
            ? AdmitNullableAsync(agentId, WardTrigger.RepeatedFailure, $"{count} consecutive failed outcomes on tag '{tag}'", cancellationToken)
            : Task.FromResult<WardOutcome?>(null);
    }

    /// <inheritdoc />
    public Task<WardOutcome> ReportCanaryUseAsync(Guid agentId, string detail, CancellationToken cancellationToken) =>
        ward.AdmitAsync(agentId, WardTrigger.DeniedCallOrCanary, $"canary use: {Clean(detail)}", cancellationToken);

    /// <inheritdoc />
    public Task<WardOutcome?> ReportDeniedCallsAsync(Guid agentId, int deniedCount, string detail, CancellationToken cancellationToken) =>
        deniedCount >= settings.DeniedCallThreshold
            ? AdmitNullableAsync(agentId, WardTrigger.DeniedCallOrCanary, $"{deniedCount} denied calls: {Clean(detail)}", cancellationToken)
            : Task.FromResult<WardOutcome?>(null);

    /// <inheritdoc />
    public Task<WardOutcome> ReportTokenHardStopAsync(Guid agentId, string limitName, CancellationToken cancellationToken) =>
        ward.AdmitAsync(agentId, WardTrigger.TokenHardStop, $"token hard stop: {Clean(limitName)}", cancellationToken);

    /// <inheritdoc />
    public Task<WardOutcome> SelfReferAsync(Guid agentId, string reason, CancellationToken cancellationToken) =>
        ward.AdmitAsync(agentId, WardTrigger.SelfReferral, Clean(reason), cancellationToken);

    /// <inheritdoc />
    public Task<WardOutcome> OwnerReferAsync(Guid agentId, string reason, CancellationToken cancellationToken) =>
        ward.AdmitAsync(agentId, WardTrigger.OwnerReferral, Clean(reason), cancellationToken);

    private async Task<WardOutcome?> AdmitNullableAsync(Guid agentId, WardTrigger trigger, string detail, CancellationToken ct) =>
        await ward.AdmitAsync(agentId, trigger, detail, ct).ConfigureAwait(false);

    /// <summary>One line, at most 200 characters, so a detail can never carry a long pasted passage.</summary>
    private static string Clean(string? text)
    {
        var line = string.Join(' ', (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length <= 200 ? line : line[..200];
    }
}
