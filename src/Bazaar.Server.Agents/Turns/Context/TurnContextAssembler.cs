using System.Security.Cryptography;
using System.Text;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Agents.Turns.Context;

/// <summary>
/// Builds the model context for one turn (doc 06 "Context assembly").
/// <para>
/// Sections are assembled in the fixed order of <see cref="ContextSection"/>. A budgeted section keeps its earliest
/// items in source order and stops at the first item that does not fit, so nothing is truncated mid-item. The GC
/// backread reserves the trigger thread before it fills the rest, and it throws when the trigger thread is absent or
/// cannot fit, never dropping it silently. Inbox content is included verbatim and is not budgeted.
/// </para>
/// <para>
/// Each section becomes one message: System for the System section, User for the rest. Empty sections are omitted.
/// The manifest lists ids, version ids and SHA-256 hashes per section, with the budgets used, and never item text.
/// </para>
/// </summary>
public sealed class TurnContextAssembler : ITurnContextAssembler
{
    // Items of one section are joined with a blank line in the message text. Inbox items are not changed.
    private const string _itemSeparator = "\n\n";

    private readonly IReadOnlyList<IContextSource> _sources;
    private readonly ContextBudget _budget;
    private readonly ITokenEstimator _estimator;

    /// <summary>
    /// The budget is the operator's <c>runtime.budget.*</c> set. Per-agent budgets bind through the settings registry
    /// (doc 06 "Settings") when that lands; until then the caller passes the budget for each agent.
    /// </summary>
    public TurnContextAssembler(IEnumerable<IContextSource> sources, ContextBudget budget, ITokenEstimator? estimator = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(budget);
        budget.EnsureValid();

        _sources = sources.ToList();
        _budget = budget;
        _estimator = estimator ?? CharQuarterTokenEstimator.Instance;
    }

    public async Task<ContextPlan> BuildAsync(TurnContextRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var candidates = new List<ContextSourceItem>();
        foreach (var source in _sources)
        {
            candidates.AddRange(await source.ReadAsync(request, cancellationToken));
        }

        var messages = new List<ModelMessage>();
        var manifestSections = new List<ContextManifestSection>();
        foreach (var section in Enum.GetValues<ContextSection>())
        {
            var inSection = candidates.Where(item => item.Section == section).ToList();
            var kept = ChooseItems(section, inSection, request.TriggerGcId);

            var tokens = kept.Sum(item => _estimator.Estimate(item.Text));
            var items = kept.Select(item => new ContextManifestItem(item.ItemId, item.Version, Sha256Hex(item.Text))).ToList();
            manifestSections.Add(new ContextManifestSection(section.ToString(), tokens, items));

            if (kept.Count == 0)
            {
                continue;
            }

            var role = section == ContextSection.System ? ModelRole.System : ModelRole.User;
            messages.Add(new ModelMessage(role, string.Join(_itemSeparator, kept.Select(item => item.Text))));
        }

        var manifest = new ContextManifest(
            ContextManifestJson.SchemaVersion,
            request.AgentId,
            request.TurnId,
            request.Role.ToString(),
            _budget,
            manifestSections);

        return new ContextPlan(messages, ContextManifestJson.Serialize(manifest));
    }

    /// <summary>Chooses the items of one section, in source order, under its ceiling.</summary>
    /// <exception cref="InvalidOperationException">The trigger thread is missing from the backread or cannot fit its ceiling.</exception>
    private List<ContextSourceItem> ChooseItems(ContextSection section, List<ContextSourceItem> items, string? triggerGcId)
    {
        if (section == ContextSection.Inbox)
        {
            return items.ToList();
        }

        var ceiling = _budget.For(section);
        var costs = items.Select(item => _estimator.Estimate(item.Text)).ToArray();
        var keep = new bool[items.Count];
        var used = 0;

        if (section == ContextSection.GcBackread && triggerGcId is not null)
        {
            var trigger = -1;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].ItemId == triggerGcId)
                {
                    trigger = i;
                    break;
                }
            }

            if (trigger < 0)
            {
                throw new InvalidOperationException(
                    $"Trigger thread '{triggerGcId}' is not in the GC backread. It must always be included, so the turn cannot be assembled.");
            }

            if (costs[trigger] > ceiling)
            {
                throw new InvalidOperationException(
                    $"Trigger thread '{triggerGcId}' needs {costs[trigger]} tokens but the backread budget is {ceiling}. " +
                    "It is never dropped; raise runtime.budget.backread_tokens.");
            }

            keep[trigger] = true;
            used = costs[trigger];
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (keep[i])
            {
                continue;
            }

            if (used + costs[i] > ceiling)
            {
                break;
            }

            keep[i] = true;
            used += costs[i];
        }

        var selected = new List<ContextSourceItem>();
        for (var i = 0; i < items.Count; i++)
        {
            if (keep[i])
            {
                selected.Add(items[i]);
            }
        }

        return selected;
    }

    private static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
