using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Hive.Abstractions;
using Bazaar.Server.Security.Approvals;

namespace Bazaar.Server.Hive.Approvals;

/// <summary>
/// <see cref="IHiveGrantApprovals"/> over the owner's Tier 2 queue (doc 15, doc 25 "Hive grant wire request"). It files one
/// approval card per distinct change and records the change in the <see cref="IHiveGrantChangeLedger"/>; it never applies it.
/// Asking again for the same change returns the same ticket. Refusals (rate cap, a store that cannot save) become a refused
/// ticket, so the client never sees a queued change that is not queued.
/// </summary>
public sealed class OwnerQueueHiveGrantApprovals(IOwnerApprovalEnqueuer enqueuer, IHiveGrantChangeLedger ledger) : IHiveGrantApprovals
{
    /// <summary>The tool name recorded on every Hive grant approval.</summary>
    public const string ToolName = "hive.grant_change";

    /// <summary>Prefix of the action key.</summary>
    public const string ActionKeyPrefix = "hive-grant:";

    private const int _fieldLimit = 80;

    private readonly IOwnerApprovalEnqueuer _enqueuer = enqueuer ?? throw new ArgumentNullException(nameof(enqueuer));
    private readonly IHiveGrantChangeLedger _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

    /// <inheritdoc />
    public async Task<HiveApprovalTicket> RequestAsync(HiveGrantChange change, string requestedBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (string.IsNullOrWhiteSpace(requestedBy))
        {
            return HiveApprovalTicket.Refused("requester unknown");
        }

        try
        {
            var id = await _enqueuer.EnqueueAsync(
                new OwnerApprovalEnqueueRequest(requestedBy.Trim(), ToolName, ActionKey(change), Summary(change)), ct).ConfigureAwait(false);
            await _ledger.AddAsync(id, change, ct).ConfigureAwait(false);
            return HiveApprovalTicket.Queued(id);
        }
        catch (OwnerApprovalRateLimitedException)
        {
            return HiveApprovalTicket.Refused("too many approval requests, try again later");
        }
        catch (ArgumentException)
        {
            return HiveApprovalTicket.Refused("request not accepted");
        }
        catch (InvalidOperationException)
        {
            return HiveApprovalTicket.Refused("approvals not available");
        }
    }

    /// <summary>Stable key for a change: the same change always maps to the same key, so a repeat makes no second card.</summary>
    public static string ActionKey(HiveGrantChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var text = $"{change.Action}|{change.FromProject}|{change.ToProject}|{HiveKinds.Name(change.Kind)}";
        return ActionKeyPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    /// <summary>What the owner reads on the card. Control characters become spaces and long names are cut.</summary>
    public static string Summary(HiveGrantChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var verb = change.Action == HiveGrantAction.Set ? "grant" : "revoke";
        return $"Hive: {verb} {HiveKinds.Name(change.Kind)} lessons from project {Clean(change.FromProject)} to project {Clean(change.ToProject)}.";
    }

    private static string Clean(string value)
    {
        var text = new string([.. value.Select(c => char.IsControl(c) ? ' ' : c)]).Trim();
        return text.Length <= _fieldLimit ? text : text[.._fieldLimit] + "...";
    }
}
