using System.Text.RegularExpressions;
using Bazaar.Client.LocalInference.Guards;

namespace Bazaar.Client.LocalInference.Speech.Eval;

/// <summary>
/// Runs <see cref="VoiceCleanupPipeline"/> over eval cases and scores each result (doc 29, Testing: cleaner eval).
/// Checks: filler removal, kept numbers, names and URLs (the fixed <see cref="OutputGuard"/>), and no added numbers or URLs.
/// Added names and other added facts are not detected here; they stay with human review.
/// A cancelled token propagates. Every other failure becomes a fallback to the raw transcript, which fails the case.
/// </summary>
public sealed partial class CleanerEvaluator
{
    private readonly ILocalModelBackend _backend;
    private readonly CleanerEvalOptions _options;
    private readonly Regex _fillerPattern;

    public CleanerEvaluator(ILocalModelBackend backend, CleanerEvalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _options = options ?? new CleanerEvalOptions();
        _options.Validate();
        _fillerPattern = BuildFillerPattern(_options.Fillers);
    }

    /// <summary>Runs every case in order on the backend and returns one result per case.</summary>
    public async Task<CleanerEvalReport> RunAsync(IReadOnlyList<CleanerEvalCase> cases, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cases);

        var pipeline = new VoiceCleanupPipeline(_backend, AcceptsCleanedText);
        var results = new List<CleanerEvalResult>(cases.Count);
        foreach (var item in cases)
        {
            results.Add(await ScoreAsync(pipeline, item, ct));
        }

        return new CleanerEvalReport(results);
    }

    private async Task<CleanerEvalResult> ScoreAsync(VoiceCleanupPipeline pipeline, CleanerEvalCase item, CancellationToken ct)
    {
        var output = await pipeline.CleanAsync(item.Input, ct);
        var fellBack = output.CleanedText is null;
        var shown = output.CleanedText ?? output.RawText;

        var fillerRemoved = !_fillerPattern.IsMatch(shown);
        var factsKept = OutputGuard.Check(item.Input, shown).Passed;
        var noAddedFacts = AddedTokens(item.Input, shown).Count == 0;
        var exactMatch = Normalise(shown) == Normalise(item.Expected);
        var passed = !fellBack
            && fillerRemoved
            && factsKept
            && noAddedFacts
            && (!_options.GateOnExactMatch || exactMatch);

        return new CleanerEvalResult(item.Id, shown, fellBack, fillerRemoved, factsKept, noAddedFacts, exactMatch, passed);
    }

    /// <summary>The guard the pipeline uses: output must pass the fixed guards and fit under the cap without truncation.</summary>
    private static bool AcceptsCleanedText(string raw, string cleaned)
    {
        var guarded = OutputGuard.Apply(LocalTaskKind.VoiceCleanup, raw, cleaned);
        return !guarded.FellBack && !guarded.Truncated;
    }

    /// <summary>Numbers and URLs in the output that do not appear in the input. Empty when nothing was added.</summary>
    private static List<string> AddedTokens(string input, string output)
    {
        var added = new List<string>();
        var inputNumbers = Numbers(input).ToHashSet(StringComparer.Ordinal);
        added.AddRange(Numbers(output).Where(n => !inputNumbers.Contains(n)));

        var inputUrls = Urls(input).ToHashSet(StringComparer.Ordinal);
        added.AddRange(Urls(output).Where(u => !inputUrls.Contains(u)));
        return added;
    }

    private static IEnumerable<string> Numbers(string text) =>
        NumberPattern().Matches(UrlPattern().Replace(text, " ")).Select(m => m.Value);

    private static IEnumerable<string> Urls(string text) =>
        UrlPattern().Matches(text)
            .Select(m => m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '\''))
            .Where(u => u.Length > 0);

    private static string Normalise(string text) => WhitespacePattern().Replace(text.Trim(), " ");

    private static Regex BuildFillerPattern(IReadOnlyList<string> fillers)
    {
        var alternatives = fillers
            .Select(filler => string.Join(@"\s+", filler.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape)))
            .Distinct(StringComparer.Ordinal);
        var pattern = @"(?<!\p{L})(?:" + string.Join('|', alternatives) + @")(?!\p{L})";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""“”]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"\d+(?:[.,]\d+)*")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
