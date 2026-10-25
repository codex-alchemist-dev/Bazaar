namespace Bazaar.Client.Core.Commands;

/// <summary>Which command field produced the match. Lower values rank first.</summary>
public enum CommandMatchField
{
    Title = 0,
    Keyword = 1,
    Id = 2,
}

/// <summary>
/// One palette result. <see cref="Score"/> includes the recency boost.
/// <see cref="Positions"/> index into <see cref="MatchedText"/>, which is the title, keyword or id that matched.
/// Highlight the title only when <see cref="Field"/> is <see cref="CommandMatchField.Title"/>.
/// </summary>
public sealed record CommandMatch(
    Command Command,
    CommandMatchField Field,
    string MatchedText,
    int Score,
    IReadOnlyList<int> Positions);

/// <summary>
/// Fuzzy palette search over a registry. Pure, with no network and no UI types.
/// Order: field tier (title, then keywords, then id), score descending, title ordinal, id ordinal.
/// </summary>
public sealed class CommandMatcher
{
    private readonly CommandRecency _recency;

    public CommandMatcher(CommandRecency? recency = null)
    {
        _recency = recency ?? new CommandRecency();
    }

    public IReadOnlyList<CommandMatch> Search(CommandRegistry registry, string query)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(query);

        var results = new List<CommandMatch>();
        foreach (var command in registry.Commands)
        {
            // Enabled is evaluated now, at query time, so disabled commands never appear.
            if (!command.IsEnabled())
            {
                continue;
            }

            var match = MatchCommand(command, query);
            if (match is null)
            {
                continue;
            }

            results.Add(match with { Score = match.Score + RecencyBoost(command.Id) });
        }

        return results
            .OrderBy(r => r.Field)
            .ThenByDescending(r => r.Score)
            .ThenBy(r => r.Command.Title, StringComparer.Ordinal)
            .ThenBy(r => r.Command.Id, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
    }

    private static CommandMatch? MatchCommand(Command command, string query)
    {
        var title = FuzzyScorer.Match(query, command.Title);
        if (title is not null)
        {
            return new CommandMatch(command, CommandMatchField.Title, command.Title, title.Score, title.Positions);
        }

        CommandMatch? bestKeyword = null;
        foreach (var keyword in command.Keywords)
        {
            var result = FuzzyScorer.Match(query, keyword);
            if (result is not null && (bestKeyword is null || result.Score > bestKeyword.Score))
            {
                bestKeyword = new CommandMatch(command, CommandMatchField.Keyword, keyword, result.Score, result.Positions);
            }
        }

        if (bestKeyword is not null)
        {
            return bestKeyword;
        }

        var id = FuzzyScorer.Match(query, command.Id);
        return id is null
            ? null
            : new CommandMatch(command, CommandMatchField.Id, command.Id, id.Score, id.Positions);
    }

    /// <summary>Small boost: the most recent id gets the full capacity, each older rank one point less.</summary>
    private int RecencyBoost(string id)
    {
        int rank = _recency.Rank(id);
        return rank < 0 ? 0 : _recency.Capacity - rank;
    }
}
