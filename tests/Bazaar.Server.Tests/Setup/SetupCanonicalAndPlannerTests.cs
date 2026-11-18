using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup;

namespace Bazaar.Server.Tests.Setup;

/// <summary>The canonical form, the plan hash, the dry-run change list and the exit-code mapping. Pure: no database, no files.</summary>
public sealed class SetupCanonicalAndPlannerTests
{
    private static SetupPlan ValidPlan() => SetupPlanLoader.Load(SetupFixtures.Valid, BuiltInSettingRegistries.Known).Plan!;

    [Fact]
    public void Canonical_text_has_sorted_keys_and_no_insignificant_whitespace()
    {
        var text = Encoding.UTF8.GetString(SetupPlanCanonicalizer.ToCanonicalUtf8(ValidPlan()));

        Assert.StartsWith("{\"backups\":", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain(": ", text, StringComparison.Ordinal);
        Assert.DoesNotContain(", ", text, StringComparison.Ordinal);
        // The keys of the install block come out in ordinal order.
        Assert.Contains("\"install\":{\"companyName\":\"Example Studio\",\"companySlug\":\"example-studio\",\"locale\":\"en\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Canonical_text_parses_back_to_the_same_document()
    {
        var bytes = SetupPlanCanonicalizer.ToCanonicalUtf8(ValidPlan());

        using var document = JsonDocument.Parse(bytes);
        Assert.Equal("Example Studio", document.RootElement.GetProperty("install").GetProperty("companyName").GetString());
    }

    [Fact]
    public void Plan_hash_is_lowercase_hex_sha256_over_the_canonical_bytes()
    {
        var plan = ValidPlan();
        var bytes = SetupPlanCanonicalizer.ToCanonicalUtf8(plan);

        var expected = Convert.ToHexStringLower(SHA256.HashData(bytes));

        Assert.Equal(expected, SetupPlanCanonicalizer.Hash(plan));
        Assert.Equal(expected, SetupPlanCanonicalizer.HashOf(bytes));
        Assert.Matches("^[0-9a-f]{64}$", expected);
    }

    [Theory]
    [InlineData(SetupExitCode.Ok, 0)]
    [InlineData(SetupExitCode.InvalidInput, 1)]
    [InlineData(SetupExitCode.Held, 2)]
    [InlineData(SetupExitCode.ApplyFailedRolledBack, 3)]
    public void Exit_codes_have_the_numbers_doc_45_gives(SetupExitCode code, int number)
    {
        Assert.Equal(number, (int)code);
    }

    [Fact]
    public void No_issues_maps_to_ok()
    {
        Assert.Equal(SetupExitCode.Ok, SetupExitCodes.ForIssues([]));
    }

    [Fact]
    public void Holds_alone_map_to_held()
    {
        var holds = new[] { new SetupValidationIssue(SetupIssueSeverity.Hold, "network.statusBind", "held") };

        Assert.Equal(SetupExitCode.Held, SetupExitCodes.ForIssues(holds));
    }

    [Fact]
    public void Errors_map_to_invalid_input_and_win_over_holds()
    {
        var errors = new[] { new SetupValidationIssue(SetupIssueSeverity.Error, "install.companySlug", "bad") };
        var both = errors.Concat(new[] { new SetupValidationIssue(SetupIssueSeverity.Hold, "network.statusBind", "held") });

        Assert.Equal(SetupExitCode.InvalidInput, SetupExitCodes.ForIssues(errors));
        Assert.Equal(SetupExitCode.InvalidInput, SetupExitCodes.ForIssues(both));
    }

    [Fact]
    public void Empty_install_makes_every_planned_leaf_a_create()
    {
        var plan = ValidPlan();

        var set = SetupChangePlanner.Compute(plan, SetupStateSnapshot.Empty);

        Assert.Equal(SetupPlanCanonicalizer.Hash(plan), set.PlanHash);
        Assert.NotEmpty(set.Changes);
        Assert.All(set.Changes, change => Assert.Equal(SetupChangeKind.Create, change.Kind));
        Assert.All(set.Changes, change => Assert.Null(change.CurrentValue));
        Assert.Contains(set.Changes, change => change.Path == "install.companyName" && change.PlannedValue == "\"Example Studio\"");
        Assert.Contains(set.Changes, change => change.Path == "modules" && change.PlannedValue == "[\"Bazaar.Constellation\",\"Bazaar.GC\",\"Bazaar.KODO\"]");
    }

    [Fact]
    public void Matching_state_has_no_changes()
    {
        var plan = ValidPlan();
        var state = SetupStateSnapshot.FromNode(SetupPlanCanonicalizer.ToNode(plan));

        var set = SetupChangePlanner.Compute(plan, state);

        Assert.Empty(set.Changes);
    }

    [Fact]
    public void One_changed_value_is_one_update_and_a_stored_extra_leaf_is_a_remove()
    {
        var plan = ValidPlan();
        var leaves = SetupPlanCanonicalizer.Leaves(SetupPlanCanonicalizer.ToNode(plan));
        var state = new Dictionary<string, string>(leaves, StringComparer.Ordinal)
        {
            ["install.companyName"] = "\"Old Name\"",
            ["install.legacyFlag"] = "true",
        };

        var set = SetupChangePlanner.Compute(plan, new SetupStateSnapshot(state));

        var update = Assert.Single(set.Changes, change => change.Kind == SetupChangeKind.Update);
        Assert.Equal("install.companyName", update.Path);
        Assert.Equal("\"Old Name\"", update.CurrentValue);
        Assert.Equal("\"Example Studio\"", update.PlannedValue);

        var remove = Assert.Single(set.Changes, change => change.Kind == SetupChangeKind.Remove);
        Assert.Equal("install.legacyFlag", remove.Path);
        Assert.Null(remove.PlannedValue);
    }

    [Fact]
    public void Changes_are_sorted_by_path()
    {
        var set = SetupChangePlanner.Compute(ValidPlan(), SetupStateSnapshot.Empty);

        var paths = set.Changes.Select(change => change.Path).ToList();
        Assert.Equal(paths.OrderBy(p => p, StringComparer.Ordinal), paths);
    }

    [Fact]
    public void A_changed_plan_gets_a_different_hash_from_the_planner()
    {
        var original = SetupChangePlanner.Compute(ValidPlan(), SetupStateSnapshot.Empty);
        var changed = SetupChangePlanner.Compute(
            SetupPlanLoader.Load(SetupFixtures.With("\"companySlug\": \"example-studio\"", "\"companySlug\": \"other-studio\""), BuiltInSettingRegistries.Known).Plan!,
            SetupStateSnapshot.Empty);

        Assert.NotEqual(original.PlanHash, changed.PlanHash);
    }

    [Fact]
    public void Leaves_keep_arrays_whole_and_flatten_objects()
    {
        var node = JsonNode.Parse("""{"a":{"b":1,"c":[2,3]},"d":{}}""");

        var leaves = SetupPlanCanonicalizer.Leaves(node);

        Assert.Equal("1", leaves["a.b"]);
        Assert.Equal("[2,3]", leaves["a.c"]);
        Assert.Equal("{}", leaves["d"]);
    }
}
