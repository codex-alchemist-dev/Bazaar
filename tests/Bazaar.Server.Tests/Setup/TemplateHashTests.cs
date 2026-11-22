using System.Text;
using System.Text.Json.Nodes;
using Bazaar.Server.Setup;

namespace Bazaar.Server.Tests.Setup;

/// <summary>The template hash: stable across spelling, order and load, and changed by any byte that a template names.</summary>
public sealed class TemplateHashTests
{
    /// <summary>The v1 golden value for the starter fixture. A change here means the hash scheme or the template model changed.</summary>
    private const string _goldenStarterHash = "c7eb2b59b594601434bf81a5ee4d43e77c760fa9ffdb98c98ab79ca0c37e1133";

    private static async Task<string> HashOf(ITemplateSource source) =>
        (await TemplateFixtures.Load(source)).TemplateHash ?? throw new InvalidOperationException("The template did not load.");

    private static string ReverseTopLevelKeys(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        var keys = node.Select(property => property.Key).ToList();
        keys.Reverse();
        var reversed = new JsonObject();
        foreach (var key in keys)
        {
            reversed[key] = node[key]!.DeepClone();
        }

        return reversed.ToJsonString();
    }

    private static InMemoryTemplateSource WithFiles(string manifest, params (string Path, string Text)[] extra)
    {
        var files = new List<KeyValuePair<string, byte[]>>
        {
            new(SetupTemplateConstants.ManifestFileName, Encoding.UTF8.GetBytes(manifest)),
            new(TemplateFixtures.ConstitutionPath, Encoding.UTF8.GetBytes(TemplateFixtures.ConstitutionText)),
            new(TemplateFixtures.CharterPath, Encoding.UTF8.GetBytes(TemplateFixtures.CharterText)),
            new(TemplateFixtures.VaultPath, Encoding.UTF8.GetBytes(TemplateFixtures.VaultText)),
        };
        files.AddRange(extra.Select(file => new KeyValuePair<string, byte[]>(file.Path, Encoding.UTF8.GetBytes(file.Text))));
        return new InMemoryTemplateSource(files);
    }

    [Fact]
    public async Task The_hash_is_stable_across_loads_of_the_same_content()
    {
        var first = await HashOf(TemplateFixtures.Starter());
        var second = await HashOf(TemplateFixtures.Starter());

        Assert.Matches("^[0-9a-f]{64}$", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task The_hash_ignores_key_order_and_whitespace_in_template_json()
    {
        var baseline = await HashOf(TemplateFixtures.Starter());
        var reordered = await HashOf(TemplateFixtures.Source(ReverseTopLevelKeys(TemplateFixtures.Manifest)));
        var compact = await HashOf(TemplateFixtures.Source(JsonNode.Parse(TemplateFixtures.Manifest)!.ToJsonString()));

        Assert.Equal(baseline, reordered);
        Assert.Equal(baseline, compact);
    }

    [Fact]
    public async Task The_hash_ignores_the_order_of_set_like_lists_and_the_order_files_were_supplied_in()
    {
        var charters = TemplateFixtures.ReplaceOnce(
            TemplateFixtures.Manifest,
            "\"roleCharters\": [\"seeds/charters/reviewer.md\"]",
            "\"roleCharters\": [\"seeds/charters/writer.md\", \"seeds/charters/reviewer.md\"]");
        var swapped = TemplateFixtures.ReplaceOnce(
            charters,
            "\"roleCharters\": [\"seeds/charters/writer.md\", \"seeds/charters/reviewer.md\"]",
            "\"roleCharters\": [\"seeds/charters/reviewer.md\", \"seeds/charters/writer.md\"]");
        var writer = ("seeds/charters/writer.md", "# Writer\n\nDrafts the notes.\n");

        var one = await HashOf(WithFiles(charters, writer));
        var two = await HashOf(WithFiles(swapped, writer));
        var permissionsSwapped = await HashOf(WithFiles(TemplateFixtures.ReplaceOnce(charters, "[\"review.write\", \"gc.read\"]", "[\"gc.read\", \"review.write\"]"), writer));

        Assert.Equal(one, two);
        Assert.Equal(one, permissionsSwapped);

        var reversedFiles = new InMemoryTemplateSource(
        [
            new KeyValuePair<string, byte[]>(TemplateFixtures.VaultPath, Encoding.UTF8.GetBytes(TemplateFixtures.VaultText)),
            new KeyValuePair<string, byte[]>(TemplateFixtures.CharterPath, Encoding.UTF8.GetBytes(TemplateFixtures.CharterText)),
            new KeyValuePair<string, byte[]>(TemplateFixtures.ConstitutionPath, Encoding.UTF8.GetBytes(TemplateFixtures.ConstitutionText)),
            new KeyValuePair<string, byte[]>(SetupTemplateConstants.ManifestFileName, Encoding.UTF8.GetBytes(TemplateFixtures.Manifest)),
        ]);
        Assert.Equal(await HashOf(TemplateFixtures.Starter()), await HashOf(reversedFiles));
    }

    [Fact]
    public async Task The_hash_changes_when_one_byte_of_a_named_seed_changes()
    {
        var baseline = await HashOf(TemplateFixtures.Starter());
        var changed = new InMemoryTemplateSource(
        [
            new KeyValuePair<string, byte[]>(SetupTemplateConstants.ManifestFileName, Encoding.UTF8.GetBytes(TemplateFixtures.Manifest)),
            new KeyValuePair<string, byte[]>(TemplateFixtures.ConstitutionPath, Encoding.UTF8.GetBytes(TemplateFixtures.ConstitutionText.Replace("open", "opem", StringComparison.Ordinal))),
            new KeyValuePair<string, byte[]>(TemplateFixtures.CharterPath, Encoding.UTF8.GetBytes(TemplateFixtures.CharterText)),
            new KeyValuePair<string, byte[]>(TemplateFixtures.VaultPath, Encoding.UTF8.GetBytes(TemplateFixtures.VaultText)),
        ]);

        Assert.NotEqual(baseline, await HashOf(changed));
    }

    [Fact]
    public async Task The_hash_changes_when_the_version_changes()
    {
        var baseline = await HashOf(TemplateFixtures.Starter());
        var bumped = await HashOf(TemplateFixtures.Source(TemplateFixtures.With("\"version\": \"1.0.0\"", "\"version\": \"1.0.1\"")));

        Assert.NotEqual(baseline, bumped);
    }

    [Fact]
    public async Task The_hash_changes_when_an_ordered_list_changes()
    {
        var baseline = await HashOf(TemplateFixtures.Starter());
        var reordered = await HashOf(TemplateFixtures.Source(TemplateFixtures.With(
            "[\"Backlog\", \"Ready\", \"In progress\", \"Review\", \"Done\"]",
            "[\"Ready\", \"Backlog\", \"In progress\", \"Review\", \"Done\"]")));

        Assert.NotEqual(baseline, reordered);
    }

    [Fact]
    public async Task The_hash_ignores_files_the_template_does_not_name()
    {
        var baseline = await HashOf(TemplateFixtures.Starter());
        var withExtra = await HashOf(WithFiles(TemplateFixtures.Manifest, ("seeds/unused.md", "# Not named\n")));

        Assert.Equal(baseline, withExtra);
    }

    [Fact]
    public async Task The_v1_scheme_is_pinned_by_a_golden_value()
    {
        // Recompute only when the scheme or the template model changes on purpose, and say so in the change.
        Assert.Equal(_goldenStarterHash, await HashOf(TemplateFixtures.Starter()));
    }
}
