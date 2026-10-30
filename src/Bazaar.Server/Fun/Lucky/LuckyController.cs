using System.Collections.Immutable;
using Bazaar.Server.Sanitization;

namespace Bazaar.Server.Fun.Lucky;

/// <summary>
/// The I'm Feeling Lucky run controller (doc 26, section 6). Pure logic: the caller supplies the clock, the seed source,
/// the run count for the day and the candidates, and stores the returned <see cref="LuckyRun"/>. No idea ever becomes work.
/// </summary>
public sealed class LuckyController
{
    private readonly LuckySettings _settings;
    private readonly ITextSanitizer _sanitizer;

    /// <summary>Creates the controller. Settings are validated once here.</summary>
    /// <param name="settings">The Lucky settings.</param>
    /// <param name="sanitizer">The shared sanitizer, used to detect secret-shaped text.</param>
    public LuckyController(LuckySettings settings, ITextSanitizer sanitizer)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sanitizer);

        settings.Validate();
        _settings = settings;
        _sanitizer = sanitizer;
    }

    /// <summary>
    /// Starts a run. Only the owner may start one, and only while the daily cap allows. Participants are chosen by
    /// <c>lucky.participation</c>; each gets one seed from <paramref name="nextSeed"/>, which the caller records.
    /// </summary>
    /// <param name="runId">The new run's identifier.</param>
    /// <param name="prompt">The owner's prompt.</param>
    /// <param name="requestedByOwner">True only when the owner asked for the run.</param>
    /// <param name="now">The start time.</param>
    /// <param name="runsToday">Runs already started today.</param>
    /// <param name="candidates">The agents that could be invited.</param>
    /// <param name="nextSeed">Supplies one seed per participant, in candidate order.</param>
    public LuckyResult Start(
        string runId,
        string prompt,
        bool requestedByOwner,
        DateTimeOffset now,
        int runsToday,
        IEnumerable<LuckyCandidate> candidates,
        Func<int> nextSeed)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentException.ThrowIfNullOrEmpty(prompt);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(nextSeed);

        if (!requestedByOwner)
        {
            return LuckyResult.Refuse("Only the owner may start a Lucky run.", null);
        }

        if (runsToday >= _settings.MaxRunsPerDay)
        {
            return LuckyResult.Refuse("The daily Lucky run cap is reached.", null);
        }

        var participants = ImmutableArray.CreateBuilder<LuckyParticipant>();
        foreach (var candidate in candidates)
        {
            if (!Invited(candidate))
            {
                continue;
            }

            var seed = nextSeed();
            participants.Add(new LuckyParticipant(candidate.AgentId, seed, LuckyPersonas.Pick(seed)));
        }

        var run = new LuckyRun(
            runId,
            prompt,
            now,
            now.AddMinutes(_settings.RunMinutes),
            participants.ToImmutable(),
            ImmutableArray<LuckyIdea>.Empty);

        return LuckyResult.Accept(run);
    }

    /// <summary>
    /// Records one idea. Refused after the time box, for a non-participant, or past <c>lucky.max_ideas</c> for that agent.
    /// A secret-shaped string quarantines the idea; names and quotes are removed by the sanitizer.
    /// </summary>
    public LuckyResult RecordIdea(LuckyRun run, string agentId, string text, DateTimeOffset now, SensitiveTerms terms)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentException.ThrowIfNullOrEmpty(text);

        if (!run.IsOpen(now))
        {
            return LuckyResult.Refuse("The time box for this run has ended.", run);
        }

        if (!run.Participants.Any(p => p.AgentId == agentId))
        {
            return LuckyResult.Refuse("Only participants may record ideas.", run);
        }

        var ideasSoFar = run.Ideas.Count(i => i.AgentId == agentId);
        if (ideasSoFar >= _settings.MaxIdeas)
        {
            return LuckyResult.Refuse($"This agent has reached the {_settings.MaxIdeas} idea limit for the run.", run);
        }

        var sanitized = _sanitizer.Sanitize(text, terms);
        var quarantined = sanitized.Redactions.Any(r => r.Kind == RedactionKind.Secret);
        var idea = quarantined
            ? new LuckyIdea(agentId, string.Empty, LuckyIdeaStatus.Quarantined)
            : new LuckyIdea(agentId, sanitized.Text, LuckyIdeaStatus.Kept);

        return LuckyResult.Accept(run with { Ideas = run.Ideas.Add(idea) });
    }

    /// <summary>
    /// Marks a kept idea for a Backlog card. Only the owner may mark, and only when <c>lucky.cards_from_ideas</c>
    /// is <c>owner_marked</c>. Quarantined ideas cannot be marked.
    /// </summary>
    public LuckyResult MarkForCard(LuckyRun run, int ideaIndex, bool markedByOwner)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (_settings.CardsFromIdeas != LuckyCardsFromIdeas.OwnerMarked)
        {
            return LuckyResult.Refuse("Ideas do not become cards under this setting.", run);
        }

        if (!markedByOwner)
        {
            return LuckyResult.Refuse("Only the owner may mark an idea for a card.", run);
        }

        if (ideaIndex < 0 || ideaIndex >= run.Ideas.Length)
        {
            return LuckyResult.Refuse("No idea has that index.", run);
        }

        var idea = run.Ideas[ideaIndex];
        if (idea.Status == LuckyIdeaStatus.Quarantined)
        {
            return LuckyResult.Refuse("A quarantined idea cannot become a card.", run);
        }

        return LuckyResult.Accept(run with { Ideas = run.Ideas.SetItem(ideaIndex, idea with { OwnerMarked = true }) });
    }

    /// <summary>The agents to wake once at start under the doc 06 brainstorm reason (not a Skimmer override).</summary>
    public static ImmutableArray<string> WakeList(LuckyRun run) =>
        run.Participants.Select(p => p.AgentId).ToImmutableArray();

    private bool Invited(LuckyCandidate candidate) => _settings.Participation switch
    {
        LuckyParticipation.OptedInOnly => candidate.OptedIn,
        _ => candidate.OnShift || candidate.OptedIn,
    };
}
