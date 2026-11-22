using System.Text;
using Bazaar.Server.Setup;

namespace Bazaar.Server.Tests.Setup;

/// <summary>Loading a template folder: the valid case, strict parsing, the schema and semantic rules, seeds, size caps and settings presets.</summary>
public sealed class TemplateLoaderTests
{
    private static bool HasError(TemplateLoadResult result, string path) =>
        result.Issues.Any(issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == path);

    private static string MessageAt(TemplateLoadResult result, string path) =>
        result.Issues.Single(issue => issue.Path == path).Message;

    private static Task<TemplateLoadResult> LoadManifest(string manifest) =>
        TemplateFixtures.Load(TemplateFixtures.Source(manifest));

    [Fact]
    public async Task Valid_template_loads_with_normalized_content_seeds_and_a_hash()
    {
        var result = await TemplateFixtures.Load(TemplateFixtures.Starter());

        Assert.Empty(result.Issues);
        Assert.Equal(SetupExitCode.Ok, result.ExitCode);
        var template = Assert.IsType<SetupTemplate>(result.Template);
        Assert.Equal("starter", template.Id);
        Assert.Equal("1.0.0", template.Version);
        Assert.Equal(["Bazaar.Calendar", "Bazaar.GC"], template.Modules);
        Assert.Equal(["project", "standup"], template.GcTypes);
        Assert.Equal(["gc.read", "review.write"], template.Roles[0].Permissions);
        Assert.Equal(["Backlog", "Ready", "In progress", "Review", "Done"], template.KanbanColumns);
        Assert.Matches("^[0-9a-f]{64}$", result.TemplateHash);
        Assert.Equal(3, result.Seeds.Count);
        Assert.Equal(TemplateFixtures.ConstitutionText, Encoding.UTF8.GetString(result.Seeds[TemplateFixtures.ConstitutionPath]));
    }

    [Fact]
    public async Task Missing_kanban_columns_default_to_the_five_columns()
    {
        var manifest = TemplateFixtures.With("\"kanbanColumns\": [\"Backlog\", \"Ready\", \"In progress\", \"Review\", \"Done\"],", string.Empty);

        var result = await LoadManifest(manifest);

        Assert.Empty(result.Issues);
        Assert.Equal(SetupTemplateConstants.DefaultKanbanColumns, result.Template!.KanbanColumns);
    }

    [Fact]
    public async Task Unknown_top_level_key_is_rejected_and_no_template_is_returned()
    {
        var result = await LoadManifest(TemplateFixtures.With("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"surprise\": true,"));

        Assert.Null(result.Template);
        Assert.Null(result.TemplateHash);
        Assert.Empty(result.Seeds);
        Assert.True(HasError(result, "surprise"));
        Assert.Equal(SetupExitCode.InvalidInput, result.ExitCode);
    }

    [Fact]
    public async Task Unknown_key_inside_a_role_is_rejected()
    {
        var result = await LoadManifest(TemplateFixtures.With("\"displayName\": \"Reviewer\"", "\"displayName\": \"Reviewer\", \"behaviour\": \"x\""));

        Assert.True(HasError(result, "roles[0].behaviour"));
    }

    [Fact]
    public async Task Missing_required_modules_is_rejected()
    {
        var result = await LoadManifest(TemplateFixtures.With("\"modules\": [\"Bazaar.GC\", \"Bazaar.Calendar\"],", string.Empty));

        Assert.True(HasError(result, "modules"));
    }

    [Fact]
    public async Task Empty_module_list_is_rejected()
    {
        var result = await LoadManifest(TemplateFixtures.With("[\"Bazaar.GC\", \"Bazaar.Calendar\"]", "[]"));

        Assert.True(HasError(result, "modules"));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("^1.0.0")]
    [InlineData("~1.0.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("latest")]
    public async Task Bad_or_range_semver_is_rejected_at_version(string version)
    {
        var result = await LoadManifest(TemplateFixtures.With("\"version\": \"1.0.0\"", $"\"version\": \"{version}\""));

        Assert.Null(result.Template);
        Assert.True(HasError(result, "version"));
    }

    [Fact]
    public async Task A_pre_release_semver_is_an_exact_version_and_loads()
    {
        var result = await LoadManifest(TemplateFixtures.With("\"version\": \"1.0.0\"", "\"version\": \"1.2.0-rc.1\""));

        Assert.Empty(result.Issues);
        Assert.Equal("1.2.0-rc.1", result.Template!.Version);
    }

    [Fact]
    public async Task Unsupported_schema_version_is_rejected()
    {
        var result = await LoadManifest(TemplateFixtures.With("\"schemaVersion\": 1", "\"schemaVersion\": 2"));

        Assert.True(HasError(result, "schemaVersion"));
    }

    [Fact]
    public async Task Duplicate_top_level_key_is_rejected()
    {
        var result = await LoadManifest(TemplateFixtures.With("\"id\": \"starter\",", "\"id\": \"starter\", \"id\": \"other\","));

        Assert.True(HasError(result, "id"));
        Assert.Contains("more than once", MessageAt(result, "id"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_key_inside_a_settings_preset_is_rejected()
    {
        var result = await LoadManifest(TemplateFixtures.With(
            "\"host.shutdown_timeout_seconds\": 30,",
            "\"host.shutdown_timeout_seconds\": 30, \"host.shutdown_timeout_seconds\": 31,"));

        Assert.True(HasError(result, "settings.host.shutdown_timeout_seconds"));
    }

    [Fact]
    public async Task Comments_and_trailing_commas_are_rejected_as_not_json()
    {
        var withComment = "// a comment\n" + TemplateFixtures.Manifest;
        var withTrailingComma = TemplateFixtures.With("\"soulPresets\": [\"cautious-reviewer\"],", "\"soulPresets\": [\"cautious-reviewer\"],}");

        Assert.True(HasError(await LoadManifest(withComment), "$"));
        Assert.True(HasError(await LoadManifest(withTrailingComma), "$"));
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("seeds/../../outside.md")]
    [InlineData("/etc/outside.md")]
    [InlineData("C:/outside.md")]
    [InlineData("https://example.invalid/outside.md")]
    [InlineData("seeds/outside.txt")]
    [InlineData(".hidden/outside.md")]
    [InlineData("seeds//outside.md")]
    public async Task Traversal_absolute_and_non_markdown_seed_paths_are_rejected_at_their_key(string path)
    {
        var result = await LoadManifest(TemplateFixtures.With("\"constitutionDraft\": \"seeds/constitution.md\"", $"\"constitutionDraft\": \"{path}\""));

        Assert.Null(result.Template);
        Assert.True(HasError(result, "seeds.constitutionDraft"));
    }

    [Fact]
    public async Task A_backslash_seed_path_is_rejected()
    {
        // In JSON text the escaped backslash is two characters; the path after decoding is seeds\outside.md.
        var result = await LoadManifest(TemplateFixtures.With("\"vaultLayout\": \"seeds/vault-layout.md\"", "\"vaultLayout\": \"seeds\\\\outside.md\""));

        Assert.True(HasError(result, "seeds.vaultLayout"));
    }

    [Fact]
    public async Task A_traversal_seed_in_role_charters_names_its_index()
    {
        // Role charter paths are a set, so they are sorted before they are checked: "../escape.md" sorts first.
        var result = await LoadManifest(TemplateFixtures.With("\"roleCharters\": [\"seeds/charters/reviewer.md\"]", "\"roleCharters\": [\"seeds/charters/reviewer.md\", \"../escape.md\"]"));

        Assert.True(HasError(result, "seeds.roleCharters[0]"));
    }

    [Fact]
    public async Task A_seed_named_twice_is_rejected()
    {
        var result = await LoadManifest(TemplateFixtures.With("\"vaultLayout\": \"seeds/vault-layout.md\"", "\"vaultLayout\": \"seeds/constitution.md\""));

        Assert.True(HasError(result, "seeds.vaultLayout"));
    }

    [Fact]
    public async Task A_named_seed_missing_from_the_folder_is_reported_at_its_key()
    {
        var source = new InMemoryTemplateSource(
        [
            new KeyValuePair<string, byte[]>(SetupTemplateConstants.ManifestFileName, Encoding.UTF8.GetBytes(TemplateFixtures.Manifest)),
            new KeyValuePair<string, byte[]>(TemplateFixtures.CharterPath, Encoding.UTF8.GetBytes(TemplateFixtures.CharterText)),
            new KeyValuePair<string, byte[]>(TemplateFixtures.VaultPath, Encoding.UTF8.GetBytes(TemplateFixtures.VaultText)),
        ]);

        var result = await TemplateFixtures.Load(source);

        Assert.Null(result.Template);
        Assert.True(HasError(result, "seeds.constitutionDraft"));
        Assert.Contains("missing", MessageAt(result, "seeds.constitutionDraft"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_seed_that_is_not_utf8_text_is_rejected()
    {
        var source = new InMemoryTemplateSource(
        [
            new KeyValuePair<string, byte[]>(SetupTemplateConstants.ManifestFileName, Encoding.UTF8.GetBytes(TemplateFixtures.Manifest)),
            new KeyValuePair<string, byte[]>(TemplateFixtures.ConstitutionPath, [0xFF, 0xFE, 0xFD]),
            new KeyValuePair<string, byte[]>(TemplateFixtures.CharterPath, Encoding.UTF8.GetBytes(TemplateFixtures.CharterText)),
            new KeyValuePair<string, byte[]>(TemplateFixtures.VaultPath, Encoding.UTF8.GetBytes(TemplateFixtures.VaultText)),
        ]);

        var result = await TemplateFixtures.Load(source);

        Assert.True(HasError(result, "seeds.constitutionDraft"));
        Assert.Contains("UTF-8", MessageAt(result, "seeds.constitutionDraft"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_oversize_template_json_is_rejected_before_it_is_parsed()
    {
        var oversize = new string(' ', SetupTemplateConstants.MaxManifestBytes) + TemplateFixtures.Manifest;

        var result = await LoadManifest(oversize);

        Assert.Null(result.Template);
        Assert.True(HasError(result, SetupTemplateConstants.ManifestFileName));
        Assert.Contains("larger than the limit", MessageAt(result, SetupTemplateConstants.ManifestFileName), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_oversize_seed_is_rejected_at_its_key()
    {
        var source = new InMemoryTemplateSource(
        [
            new KeyValuePair<string, byte[]>(SetupTemplateConstants.ManifestFileName, Encoding.UTF8.GetBytes(TemplateFixtures.Manifest)),
            new KeyValuePair<string, byte[]>(TemplateFixtures.ConstitutionPath, new byte[SetupTemplateConstants.MaxSeedBytes + 1]),
            new KeyValuePair<string, byte[]>(TemplateFixtures.CharterPath, Encoding.UTF8.GetBytes(TemplateFixtures.CharterText)),
            new KeyValuePair<string, byte[]>(TemplateFixtures.VaultPath, Encoding.UTF8.GetBytes(TemplateFixtures.VaultText)),
        ]);

        var result = await TemplateFixtures.Load(source);

        Assert.True(HasError(result, "seeds.constitutionDraft"));
        Assert.Empty(result.Seeds);
    }

    [Fact]
    public async Task Seeds_that_together_exceed_the_total_limit_are_rejected()
    {
        // Eighteen seeds of 240 KiB are each under the per-file limit and together over the 4 MiB total.
        var seedBytes = new byte[240 * 1024];
        var files = new List<KeyValuePair<string, byte[]>>
        {
            new(SetupTemplateConstants.ManifestFileName, Encoding.UTF8.GetBytes(BigSeedManifest(18))),
            new(TemplateFixtures.ConstitutionPath, Encoding.UTF8.GetBytes(TemplateFixtures.ConstitutionText)),
            new(TemplateFixtures.VaultPath, Encoding.UTF8.GetBytes(TemplateFixtures.VaultText)),
        };
        for (var i = 0; i < 18; i++)
        {
            files.Add(new KeyValuePair<string, byte[]>($"seeds/charters/role-{i:D2}.md", seedBytes));
        }

        var result = await TemplateFixtures.Load(new InMemoryTemplateSource(files));

        Assert.Null(result.Template);
        Assert.True(HasError(result, "seeds"));
        Assert.Contains("total limit", MessageAt(result, "seeds"), StringComparison.Ordinal);
    }

    private static string BigSeedManifest(int count)
    {
        var charters = string.Join(", ", Enumerable.Range(0, count).Select(i => $"\"seeds/charters/role-{i:D2}.md\""));
        return TemplateFixtures.ReplaceOnce(
            TemplateFixtures.Manifest,
            "\"roleCharters\": [\"seeds/charters/reviewer.md\"]",
            "\"roleCharters\": [" + charters + "]");
    }

    [Theory]
    [InlineData("\"Backlog\", \"Blocked\"", "kanbanColumns[1]")]
    [InlineData("\"Backlog\", \"BLOCKED\"", "kanbanColumns[1]")]
    public async Task Blocked_is_never_a_listed_kanban_column(string columns, string path)
    {
        var result = await LoadManifest(TemplateFixtures.With("\"Backlog\", \"Ready\", \"In progress\", \"Review\", \"Done\"", columns + ", \"Ready\", \"Done\""));

        Assert.True(HasError(result, path));
        Assert.Contains("automatically", MessageAt(result, path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_kanban_list_is_rejected()
    {
        var result = await LoadManifest(TemplateFixtures.With("[\"Backlog\", \"Ready\", \"In progress\", \"Review\", \"Done\"]", "[]"));

        Assert.True(HasError(result, "kanbanColumns"));
    }

    [Fact]
    public async Task Duplicate_kanban_columns_ignoring_case_are_rejected()
    {
        var result = await LoadManifest(TemplateFixtures.With("\"Ready\", \"In progress\"", "\"Ready\", \"ready\""));

        Assert.True(HasError(result, "kanbanColumns[2]"));
    }

    [Fact]
    public async Task Kanban_column_order_is_kept()
    {
        var result = await LoadManifest(TemplateFixtures.With("[\"Backlog\", \"Ready\", \"In progress\", \"Review\", \"Done\"]", "[\"Done\", \"Backlog\"]"));

        Assert.Empty(result.Issues);
        Assert.Equal(["Done", "Backlog"], result.Template!.KanbanColumns);
    }

    [Fact]
    public async Task Duplicate_role_ids_are_rejected()
    {
        var manifest = TemplateFixtures.With(
            "{ \"id\": \"reviewer\", \"displayName\": \"Reviewer\", \"permissions\": [\"review.write\", \"gc.read\"] }",
            "{ \"id\": \"reviewer\", \"displayName\": \"Reviewer\", \"permissions\": [] }, { \"id\": \"reviewer\", \"displayName\": \"Second\", \"permissions\": [] }");

        var result = await LoadManifest(manifest);

        Assert.True(HasError(result, "roles[1].id"));
    }

    [Fact]
    public async Task A_blank_role_display_name_is_rejected()
    {
        var result = await LoadManifest(TemplateFixtures.With("\"displayName\": \"Reviewer\"", "\"displayName\": \"  \""));

        Assert.True(HasError(result, "roles[0].displayName"));
    }

    [Fact]
    public async Task Permissions_are_opaque_but_must_have_the_permission_shape()
    {
        var result = await LoadManifest(TemplateFixtures.With("\"review.write\"", "\"Review Write!\""));

        Assert.True(HasError(result, "roles[0].permissions[0]"));
    }

    [Fact]
    public async Task Invalid_module_names_are_rejected_at_their_index()
    {
        var result = await LoadManifest(TemplateFixtures.With("[\"Bazaar.GC\", \"Bazaar.Calendar\"]", "[\"Bazaar.GC\", \"GC\"]"));

        Assert.True(HasError(result, "modules[1]"));
    }

    [Fact]
    public async Task Gc_types_and_soul_presets_must_be_slugs_listed_once()
    {
        var gcTypes = await LoadManifest(TemplateFixtures.With("[\"standup\", \"project\"]", "[\"standup\", \"standup\"]"));
        var soul = await LoadManifest(TemplateFixtures.With("[\"cautious-reviewer\"]", "[\"Not A Slug\"]"));

        Assert.True(HasError(gcTypes, "gcTypes[1]"));
        Assert.True(HasError(soul, "soulPresets[0]"));
    }

    [Fact]
    public async Task Preset_values_must_be_scalars()
    {
        var result = await LoadManifest(TemplateFixtures.With("\"workdayHours\": 8", "\"workdayHours\": { \"start\": 9 }"));

        Assert.True(HasError(result, "calendarPresets.standard.workdayHours"));
    }

    [Fact]
    public async Task Preset_names_and_keys_must_be_well_formed()
    {
        var name = await LoadManifest(TemplateFixtures.With("\"small\": {", "\"Small Set\": {"));
        var key = await LoadManifest(TemplateFixtures.With("\"messagesPerDay\": 500", "\"9lives\": 500"));

        Assert.True(HasError(name, "quotaPresets.Small Set"));
        Assert.True(HasError(key, "quotaPresets.small.9lives"));
    }

    [Fact]
    public async Task A_null_value_or_a_null_list_item_is_rejected()
    {
        var nullValue = await LoadManifest(TemplateFixtures.With("\"gcTypes\": [\"standup\", \"project\"]", "\"gcTypes\": null"));
        var nullItem = await LoadManifest(TemplateFixtures.With("\"gcTypes\": [\"standup\", \"project\"]", "\"gcTypes\": [\"standup\", null]"));

        Assert.True(HasError(nullValue, "gcTypes"));
        Assert.True(HasError(nullItem, "gcTypes[1]"));
    }

    [Fact]
    public async Task A_setting_below_its_floor_is_rejected_as_the_plan_rejects_it()
    {
        var result = await LoadManifest(TemplateFixtures.With("\"host.log.retention_days\": 7", "\"host.log.retention_days\": 0"));

        Assert.True(HasError(result, "settings.host.log.retention_days"));
        Assert.Null(result.Template);
    }

    [Fact]
    public async Task An_unknown_settings_key_is_rejected()
    {
        var result = await LoadManifest(TemplateFixtures.With("\"host.log.retention_days\": 7", "\"host.not.a.key\": 7"));

        Assert.True(HasError(result, "settings.host.not.a.key"));
    }

    [Fact]
    public async Task A_project_scope_setting_is_refused_because_templates_set_install_values()
    {
        var result = await LoadManifest(TemplateFixtures.With("\"host.log.retention_days\": 7", "\"constellation.orbit_radius\": \"age\""));

        Assert.True(HasError(result, "settings.constellation.orbit_radius"));
    }

    [Fact]
    public async Task The_network_block_owns_the_status_settings_so_a_template_cannot_set_them()
    {
        var result = await LoadManifest(TemplateFixtures.With("\"host.log.retention_days\": 7", "\"host.status.bind\": \"loopback\""));

        Assert.True(HasError(result, "settings.host.status.bind"));
    }

    [Fact]
    public async Task A_setting_that_needs_approval_is_held_not_refused_and_the_template_still_loads()
    {
        var result = await LoadManifest(TemplateFixtures.With("\"host.log.retention_days\": 7", "\"host.service_account\": \"bazaar-alt\""));

        Assert.Equal(SetupExitCode.Held, result.ExitCode);
        Assert.NotNull(result.Template);
        Assert.NotNull(result.TemplateHash);
        Assert.Contains(result.Issues, issue => issue.Severity == SetupIssueSeverity.Hold && issue.Path == "settings.host.service_account");
    }

    [Fact]
    public async Task A_folder_without_a_manifest_is_reported_as_missing()
    {
        var result = await TemplateFixtures.Load(new InMemoryTemplateSource([]));

        Assert.Null(result.Template);
        Assert.True(HasError(result, SetupTemplateConstants.ManifestFileName));
        Assert.Contains("missing", MessageAt(result, SetupTemplateConstants.ManifestFileName), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manifest_that_is_not_an_object_is_rejected()
    {
        var result = await LoadManifest("[1, 2, 3]");

        Assert.True(HasError(result, "$"));
    }
}
