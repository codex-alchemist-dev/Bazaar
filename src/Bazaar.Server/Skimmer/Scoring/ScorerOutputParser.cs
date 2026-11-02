using System.Text.Json;

namespace Bazaar.Server.Skimmer.Scoring;

/// <summary>
/// Strict parser for scorer output (doc 13, closed schema). The root must be an array with exactly one object
/// per requested agent, each with exactly the keys <c>agent</c>, <c>category</c> and <c>relevance_bp</c>.
/// Never throws on bad model output; it returns a failure result instead.
/// </summary>
public static class ScorerOutputParser
{
    private const string _agentKey = "agent";
    private const string _categoryKey = "category";
    private const string _scoreKey = "relevance_bp";

    /// <summary>Parses and validates the scorer's JSON output against the requested agents.</summary>
    public static ScorerParseResult Parse(string json, IReadOnlyList<Guid> requestedAgents)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(requestedAgents);

        var expected = new Dictionary<Guid, int>();
        foreach (var agentId in requestedAgents)
        {
            if (!expected.TryAdd(agentId, 0))
            {
                throw new ArgumentException("Requested agents must be distinct.", nameof(requestedAgents));
            }
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return ScorerParseResult.Fail("invalid JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                return ScorerParseResult.Fail("root is not a JSON array");
            }

            var results = new List<RelevanceResult>();
            foreach (var element in root.EnumerateArray())
            {
                var parsed = TryParseElement(element, expected, out var result, out var reason);
                if (!parsed)
                {
                    return ScorerParseResult.Fail(reason);
                }

                results.Add(result!);
            }

            foreach (var count in expected.Values)
            {
                if (count == 0)
                {
                    return ScorerParseResult.Fail("missing agent");
                }
            }

            return ScorerParseResult.Ok(results);
        }
    }

    private static bool TryParseElement(
        JsonElement element,
        Dictionary<Guid, int> expected,
        out RelevanceResult? result,
        out string reason)
    {
        result = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            reason = "element is not an object";
            return false;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name is not (_agentKey or _categoryKey or _scoreKey))
            {
                reason = "unexpected key";
                return false;
            }

            if (!keys.Add(property.Name))
            {
                reason = "duplicate key";
                return false;
            }
        }

        if (keys.Count != 3)
        {
            reason = "missing key";
            return false;
        }

        var agentElement = element.GetProperty(_agentKey);
        if (agentElement.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(agentElement.GetString(), "D", out var agentId))
        {
            reason = "agent is not a GUID string";
            return false;
        }

        if (!expected.TryGetValue(agentId, out var count))
        {
            reason = "unknown agent";
            return false;
        }

        if (count > 0)
        {
            reason = "duplicate agent";
            return false;
        }

        expected[agentId] = count + 1;

        var categoryElement = element.GetProperty(_categoryKey);
        if (categoryElement.ValueKind != JsonValueKind.String
            || !CategoryWire.TryParseWire(categoryElement.GetString(), out var category))
        {
            reason = "unknown category";
            return false;
        }

        var scoreElement = element.GetProperty(_scoreKey);
        if (scoreElement.ValueKind != JsonValueKind.Number || !scoreElement.TryGetInt32(out var score))
        {
            reason = "relevance_bp is not an integer";
            return false;
        }

        if (score is < 0 or > 10000)
        {
            reason = "relevance_bp is out of range";
            return false;
        }

        result = new RelevanceResult(agentId, category, score);
        reason = string.Empty;
        return true;
    }
}
