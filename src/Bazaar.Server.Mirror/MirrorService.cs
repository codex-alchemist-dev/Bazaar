namespace Bazaar.Server.Mirror;

/// <summary>
/// The Mirror Agent (doc 26 section 5): counts owner decisions per topic and direction, turns a count at or above
/// <see cref="MirrorOptions.EvidenceThreshold"/> into a rule, and annotates and orders suggestions. It uses counts
/// and retrieved evidence, no fine-tuning, and never removes an item.
/// </summary>
public sealed class MirrorService(IMirrorStore store, MirrorOptions options, TimeProvider time, IMirrorRewriter? rewriter = null) : IMirrorService
{
    private static string Key(string topic, MirrorDirection direction) => $"{direction}:{topic}";

    public async Task<bool> RecordAsync(MirrorSignal signal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (await store.GetPausedAsync(cancellationToken) || !options.SignalKinds.Contains(signal.Kind))
        {
            return false;
        }

        await store.AddSignalAsync(signal, cancellationToken);
        await RebuildAsync(cancellationToken);
        return true;
    }

    public Task<MirrorProfile?> GetProfileAsync(CancellationToken cancellationToken) => store.GetLatestProfileAsync(cancellationToken);

    public async Task<IReadOnlyList<MirrorAnnotatedItem>> AnnotateAsync(IReadOnlyList<MirrorItem> items, CancellationToken cancellationToken)
    {
        var rules = (await store.GetLatestProfileAsync(cancellationToken))?.Rules ?? [];
        var useRewriter = rewriter is not null && options.AllowRewriteOffers
            && (!rewriter.IsHosted || options.ModelSource == MirrorModelSource.OperatorApprovedHosted);

        var result = new List<(int Index, MirrorAnnotatedItem Item)>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var hits = rules.Where(r => item.Topics.Contains(r.Topic, StringComparer.Ordinal)).ToList();
            var notes = hits
                .Select(r => new MirrorAnnotation(r.Topic, r.Direction, r.Count,
                    $"You usually {(r.Direction == MirrorDirection.Prefer ? "prefer" : "avoid")} {r.Topic} ({r.Count} decisions)."))
                .ToList();
            var score = hits.Sum(r => r.Direction == MirrorDirection.Prefer ? r.Count : -r.Count);

            MirrorRewriteOffer? offer = null;
            if (useRewriter && hits.Count > 0)
            {
                var text = await rewriter!.RewriteAsync(item, hits, cancellationToken);
                if (!string.IsNullOrWhiteSpace(text) && text != item.Text)
                {
                    offer = new MirrorRewriteOffer(item.Id, text);
                }
            }

            result.Add((i, new MirrorAnnotatedItem(item, notes, score, offer)));
        }

        // Higher score first; ties keep input order. Nothing is dropped.
        return [.. result.OrderByDescending(r => r.Item.Score).ThenBy(r => r.Index).Select(r => r.Item)];
    }

    public Task SetPausedAsync(bool paused, CancellationToken cancellationToken) => store.SetPausedAsync(paused, cancellationToken);

    public async Task DeleteRuleAsync(string topic, MirrorDirection direction, CancellationToken cancellationToken)
    {
        await store.AddSuppressedAsync(Key(topic, direction), cancellationToken);
        await RebuildAsync(cancellationToken);
    }

    public Task WipeAsync(CancellationToken cancellationToken) => store.WipeAsync(cancellationToken);

    private async Task RebuildAsync(CancellationToken cancellationToken)
    {
        var signals = await store.ListSignalsAsync(cancellationToken);
        var suppressed = await store.ListSuppressedAsync(cancellationToken);

        var rules = signals
            .GroupBy(s => (s.Topic, s.Direction))
            .Where(g => !suppressed.Contains(Key(g.Key.Topic, g.Key.Direction)))
            .Select(g => new MirrorRule(g.Key.Topic, g.Key.Direction, g.Count(), [.. g.Select(s => s.SourceRef).Distinct()]))
            .Where(r => r.Count >= options.EvidenceThreshold)
            // A topic the owner has both liked and disliked applies only in the direction that clearly leads.
            .GroupBy(r => r.Topic)
            .SelectMany(g => g.Count() == 1 ? g : g.OrderByDescending(r => r.Count).Take(1).Where(r => r.Count > g.Min(x => x.Count)))
            .OrderBy(r => r.Topic, StringComparer.Ordinal)
            .ToList();

        var previous = await store.GetLatestProfileAsync(cancellationToken);
        await store.AddProfileAsync(new MirrorProfile((previous?.Version ?? 0) + 1, rules, time.GetUtcNow()), cancellationToken);
    }
}
