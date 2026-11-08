using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Gateway.FreeLlmApi;

/// <summary>One hop that failed before the request was served, as named in <c>X-Fallback-Trail</c>.</summary>
public sealed record FreeLlmApiHop(string Platform, string Model, string? KeyRef, string? Reason);

/// <summary>
/// What FreeLLMAPI reported about how a chat call was routed, read from its response headers (docs: REST API,
/// "Response headers"). <c>RoutedPlatform</c> and <c>RoutedModel</c> come from <c>X-Routed-Via: platform/model</c>.
/// <c>TrailMalformed</c> is set when <c>X-Fallback-Trail</c> is present but cannot be read in full, so the auditor can
/// fail closed. A missing trail is not malformed: it means no fallback happened.
/// </summary>
public sealed record FreeLlmApiRouting(
    string? RoutedPlatform,
    string? RoutedModel,
    int? Attempts,
    IReadOnlyList<FreeLlmApiHop> Trail,
    bool TrailMalformed)
{
    public const string RoutedViaHeader = "X-Routed-Via";
    public const string AttemptsHeader = "X-Fallback-Attempts";
    public const string TrailHeader = "X-Fallback-Trail";

    private static readonly Regex _hopPattern = new(
        @"^(?<platform>[^/\s]+)/(?<model>\S+)(?:\s+(?<key>\S+?)=(?<reason>.+))?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    /// <summary>Reads the routing headers. Never throws: absent or odd headers become null, an empty trail, or <c>TrailMalformed</c>.</summary>
    public static FreeLlmApiRouting Parse(HttpHeaders headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var (platform, model) = SplitPlatformModel(FirstValue(headers, RoutedViaHeader));

        int? attempts = int.TryParse(FirstValue(headers, AttemptsHeader), out var n) ? n : null;

        var hops = new List<FreeLlmApiHop>();
        var malformed = false;
        var trailText = FirstValue(headers, TrailHeader);
        if (trailText is not null)
        {
            foreach (var raw in trailText.Split(';'))
            {
                var entry = raw.Trim();
                if (entry.Length == 0)
                {
                    continue;
                }

                var match = _hopPattern.Match(entry);
                if (!match.Success)
                {
                    malformed = true;
                    continue;
                }

                hops.Add(new FreeLlmApiHop(
                    match.Groups["platform"].Value,
                    match.Groups["model"].Value,
                    match.Groups["key"].Success ? match.Groups["key"].Value : null,
                    match.Groups["reason"].Success ? match.Groups["reason"].Value : null));
            }
        }

        return new FreeLlmApiRouting(platform, model, attempts, hops, malformed);
    }

    /// <summary>The model ids named by the trail, in order. Platform is dropped: the expected set lists model ids.</summary>
    public IEnumerable<string> TrailModels => Trail.Select(h => h.Model);

    /// <summary>Splits <c>platform/model</c> at the first slash, so a model id that itself contains a slash survives.</summary>
    public static (string? Platform, string? Model) SplitPlatformModel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        var value = text.Trim();
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        return slash <= 0 || slash == value.Length - 1
            ? (null, null)
            : (value[..slash], value[(slash + 1)..]);
    }

    private static string? FirstValue(HttpHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
