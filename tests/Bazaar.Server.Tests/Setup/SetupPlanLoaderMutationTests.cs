using System.Text.Json.Nodes;
using Bazaar.Server.Setup;

namespace Bazaar.Server.Tests.Setup;

/// <summary>Every value of the valid fixture replaced by hostile ones: the loader answers with issues and never throws.</summary>
public sealed class SetupPlanLoaderMutationTests
{
    private static readonly string[] _replacements =
    [
        "null", "0", "-1", "2147483648", "99999999999999999999999", "1e999", "-0.0", "true", "\"\"", "\" \"", "\"\\u0000\"",
        "\"a\\nb\"", "[]", "{}", "[null]", "[[[[1]]]]", "{\"a\":{\"b\":1}}", "\"%s\"", "\"../../etc/x\"",
        "\"secret://\"", "\"env:\"", "\"file:\"", "\"dpapi:\"", "\"https://\"", "\"file:\\u0000\"",
    ];

    public static TheoryData<string> Paths()
    {
        var data = new TheoryData<string>();
        foreach (var path in LeafPaths(JsonNode.Parse(SetupFixtures.Valid)!, string.Empty))
        {
            data.Add(path);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void A_hostile_value_at_any_path_never_throws_out_of_the_loader(string path)
    {
        foreach (var replacement in _replacements)
        {
            var root = JsonNode.Parse(SetupFixtures.Valid)!;
            Set(root, path, JsonNode.Parse(replacement.Replace("%s", new string('x', 100_000))));
            var json = root.ToJsonString();

            var result = SetupPlanLoader.Load(json);

            Assert.True(result.Plan is null || result.Issues.All(issue => issue.Severity != SetupIssueSeverity.Error));
        }
    }

    [Fact]
    public void A_lone_surrogate_in_a_name_is_reported_not_thrown()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.With("Example Studio", "\\ud800"));

        Assert.True(result.Plan is null || result.Issues.All(issue => issue.Severity != SetupIssueSeverity.Error));
    }

    private static IEnumerable<string> LeafPaths(JsonNode node, string prefix)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    foreach (var p in LeafPaths(value!, prefix.Length == 0 ? key : prefix + "/" + key))
                    {
                        yield return p;
                    }
                }

                yield return prefix;
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    foreach (var p in LeafPaths(array[i]!, prefix + "/" + i))
                    {
                        yield return p;
                    }
                }

                yield return prefix;
                break;
            default:
                yield return prefix;
                break;
        }
    }

    private static void Set(JsonNode root, string path, JsonNode? value)
    {
        var parts = path.Split('/');
        var current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            current = current is JsonArray a ? a[int.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture)]! : current[parts[i]]!;
        }

        var last = parts[^1];
        if (current is JsonArray array)
        {
            array[int.Parse(last, System.Globalization.CultureInfo.InvariantCulture)] = value;
        }
        else
        {
            current[last] = value;
        }
    }
}
