using System.Text;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup;

namespace Bazaar.Server.Tests.Setup;

/// <summary>
/// What a template never contains (doc 45 section 1.8): every deny-list category, the secret-reference value scan, and the
/// rule that a message names a path and never a value. The canary is a fake secret that every test places in the input.
/// </summary>
public sealed class TemplateContentTests
{
    /// <summary>Key names that must be refused, each with the category its message must name.</summary>
    public static TheoryData<string, string> DeniedKeys => new()
    {
        { "apiKey", TemplateContentRules.SecretCategory },
        { "api_key", TemplateContentRules.SecretCategory },
        { "password", TemplateContentRules.SecretCategory },
        { "accessToken", TemplateContentRules.SecretCategory },
        { "token", TemplateContentRules.SecretCategory },
        { "keyRef", TemplateContentRules.SecretCategory },
        { "messages", TemplateContentRules.GcMessageCategory },
        { "gcMessageVersions", TemplateContentRules.GcMessageCategory },
        { "soulInstances", TemplateContentRules.SoulCategory },
        { "memories", TemplateContentRules.SoulCategory },
        { "email", TemplateContentRules.PersonalDataCategory },
        { "dateOfBirth", TemplateContentRules.PersonalDataCategory },
        { "owner", TemplateContentRules.OwnerCategory },
        { "ownerDisplayName", TemplateContentRules.OwnerCategory },
        { "providerKeys", TemplateContentRules.AccountCategory },
        { "githubAccount", TemplateContentRules.AccountCategory },
    };

    /// <summary>Strings that carry a secret reference. Each is placed as a role's display name.</summary>
    public static TheoryData<string> ReferenceValues => new()
    {
        "env:canary-secret-0000",
        "file:canary-secret-0000",
        "dpapi:C:/canary-secret-0000",
        "systemd-creds:canary-secret-0000",
        "secret://bazaar/canary-secret-0000",
        "ENV:canary-secret-0000",
        "rotate with secret://bazaar/canary-secret-0000 soon",
    };

    private static string AllText(TemplateLoadResult result) =>
        string.Join("\n", result.Issues.Select(issue => issue.Path + " " + issue.Message));

    [Theory]
    [MemberData(nameof(DeniedKeys))]
    public async Task A_denied_key_is_refused_at_its_path_and_its_message_names_the_category_not_the_value(string key, string category)
    {
        var manifest = TemplateFixtures.With("\"workdayHours\": 8", $"\"workdayHours\": 8, \"{key}\": \"{SetupFixtures.Canary}\"");

        var result = await TemplateFixtures.Load(TemplateFixtures.Source(manifest));

        Assert.Null(result.Template);
        var issue = result.Issues.Single(issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == $"calendarPresets.standard.{key}");
        Assert.Contains(category, issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SetupFixtures.Canary, AllText(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_denied_key_at_the_top_level_is_refused_before_it_is_called_unknown()
    {
        var result = await TemplateFixtures.Load(TemplateFixtures.Source(TemplateFixtures.With("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"messages\": [\"canary-secret-0000\"],")));

        var issue = result.Issues.Single(issue => issue.Path == "messages");
        Assert.Contains(TemplateContentRules.GcMessageCategory, issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("canary", AllText(result), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ReferenceValues))]
    public async Task A_string_with_a_secret_reference_is_refused_at_its_path_and_the_value_is_never_echoed(string value)
    {
        var result = await TemplateFixtures.Load(TemplateFixtures.Source(
            TemplateFixtures.With("\"displayName\": \"Reviewer\"", $"\"displayName\": \"{value}\"")));

        Assert.Null(result.Template);
        var issue = result.Issues.Single(issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == "roles[0].displayName");
        Assert.Contains(TemplateContentRules.SecretCategory, issue.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("canary", AllText(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reference_inside_an_array_item_is_refused_at_the_item_path()
    {
        var result = await TemplateFixtures.Load(TemplateFixtures.Source(
            TemplateFixtures.With("\"permissions\": [\"review.write\", \"gc.read\"]", "\"permissions\": [\"review.write\", \"env:canary-secret-0000\"]")));

        Assert.Contains(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == "roles[0].permissions[1]");
        Assert.DoesNotContain("canary", AllText(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reference_used_as_a_key_name_is_reported_at_the_parent_so_the_key_text_never_appears()
    {
        var result = await TemplateFixtures.Load(TemplateFixtures.Source(
            TemplateFixtures.With("\"workdayHours\": 8", "\"workdayHours\": 8, \"env:canary-secret-0000\": 1")));

        Assert.Null(result.Template);
        Assert.Contains(result.Issues, issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == "calendarPresets.standard");
        Assert.DoesNotContain("canary", AllText(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prose_that_only_looks_like_a_prefix_is_not_a_reference()
    {
        var result = await TemplateFixtures.Load(TemplateFixtures.Source(
            TemplateFixtures.With("\"displayName\": \"Reviewer\"", "\"displayName\": \"profile: standard, tokenizer: default\"")));

        Assert.Empty(result.Issues);
        Assert.NotNull(result.Template);
    }

    [Fact]
    public async Task Quota_keys_that_mention_tokens_as_a_budget_are_allowed()
    {
        var result = await TemplateFixtures.Load(TemplateFixtures.Source(
            TemplateFixtures.With("\"messagesPerDay\": 500", "\"messagesPerDay\": 500, \"dailyTokens\": 100000")));

        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Seed_files_are_not_scanned_for_key_or_reference_rules_but_their_hash_covers_them()
    {
        var seedWithDeniedWords = Encoding.UTF8.GetBytes("# Notes\n\nThe GC messages are kept in the log.\n");
        var source = new InMemoryTemplateSource(
        [
            new KeyValuePair<string, byte[]>(SetupTemplateConstants.ManifestFileName, Encoding.UTF8.GetBytes(TemplateFixtures.Manifest)),
            new KeyValuePair<string, byte[]>(TemplateFixtures.ConstitutionPath, seedWithDeniedWords),
            new KeyValuePair<string, byte[]>(TemplateFixtures.CharterPath, Encoding.UTF8.GetBytes(TemplateFixtures.CharterText)),
            new KeyValuePair<string, byte[]>(TemplateFixtures.VaultPath, Encoding.UTF8.GetBytes(TemplateFixtures.VaultText)),
        ]);

        var result = await TemplateFixtures.Load(source);

        Assert.Empty(result.Issues);
        Assert.NotNull(result.TemplateHash);
    }

    [Fact]
    public void No_setting_name_in_the_registry_is_on_the_deny_list()
    {
        var denied = BuiltInSettingRegistries.Known.Definitions
        // The owner merge credential keys (doc 14) are denied to templates on purpose: a template must never carry the credential choice
        // or its secret reference. Their names stay as doc 14 gives them, so they are the only registry names allowed on the deny list.
        string[] deniedOnPurpose = ["identity.merge_credential", "identity.merge_credential.app_id_ref"];
        var denied = BuiltInSettingRegistries.Known.Definitions
            .Where(definition => !deniedOnPurpose.Contains(definition.Name, StringComparer.Ordinal))
            .Where(definition => TemplateContentRules.KeyCategory(definition.Name) is not null)
            .Select(definition => definition.Name)
            .ToList();

        Assert.Empty(denied);
    }

    [Theory]
    [InlineData("identity.merge_credential")]
    [InlineData("identity.merge_credential.app_id_ref")]
    public void A_template_cannot_carry_the_merge_credential_keys(string name) =>
        Assert.NotNull(TemplateContentRules.KeyCategory(name));

    [Theory]
    [InlineData("API-Key", TemplateContentRules.SecretCategory)]
    [InlineData("api_key", TemplateContentRules.SecretCategory)]
    [InlineData("clientSecret", TemplateContentRules.SecretCategory)]
    [InlineData("mergeCredential", TemplateContentRules.SecretCategory)]
    [InlineData("Message", TemplateContentRules.GcMessageCategory)]
    [InlineData("personal_data", TemplateContentRules.PersonalDataCategory)]
    [InlineData("Owner_Email", TemplateContentRules.OwnerCategory)]
    [InlineData("GitHub.Account", TemplateContentRules.AccountCategory)]
    [InlineData("tokenBudget", null)]
    [InlineData("messagesPerDay", null)]
    [InlineData("workdayHours", null)]
    public void Key_names_are_matched_after_normalization(string name, string? expected)
    {
        Assert.Equal(expected, TemplateContentRules.KeyCategory(name));
    }
}
