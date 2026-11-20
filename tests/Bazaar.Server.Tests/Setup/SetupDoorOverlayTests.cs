using System.Text.Json.Nodes;
using Bazaar.Admin.Composition;
using Bazaar.Server.Setup;

namespace Bazaar.Server.Tests.Setup;

/// <summary>
/// The environment and flag doors (doc 45 section 1.2): path normalization, typed values, secret rules, precedence, the entry path
/// rule, and the equivalence the doc requires (the same plan through each door gives the same hash).
/// </summary>
public sealed class SetupDoorOverlayTests
{
    /// <summary>A fake secret value. Every test that places it in an input asserts that no output carries it.</summary>
    private const string _canary = SetupFixtures.Canary;

    private static readonly Dictionary<string, string?> _noEnvironment = [];

    private static SetupOverlayResult Merge(string? file, IReadOnlyList<string>? sets = null, IReadOnlyDictionary<string, string?>? environment = null) =>
        SetupDoorOverlay.Merge(file, sets ?? [], environment ?? _noEnvironment, RegistrySetupSettingCatalog.Known);

    private static Dictionary<string, string?> Env(params (string Name, string? Value)[] variables)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in variables)
        {
            environment[name] = value;
        }

        return environment;
    }

    /// <summary>The loader's result for a merged document. The merge must have produced one.</summary>
    private static SetupLoadResult Load(SetupOverlayResult overlay)
    {
        Assert.False(overlay.HasErrors, string.Join("; ", overlay.Issues.Select(issue => issue.Path + " " + issue.Message)));
        return SetupPlanLoader.Load(overlay.MergedJson!, RegistrySetupSettingCatalog.Known);
    }

    private static bool HasError(SetupOverlayResult overlay, string path) =>
        overlay.Issues.Any(issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == path);

    private static string AllText(SetupOverlayResult overlay) =>
        string.Join("\n", overlay.Issues.Select(issue => issue.Path + " " + issue.Message) + "\n" + (overlay.MergedJson ?? ""));

    [Fact]
    public void Flag_path_matches_names_case_insensitively_and_writes_camel_case()
    {
        var overlay = Merge(SetupFixtures.Valid, ["INSTALL.COMPANYNAME=Other Studio"]);

        var result = Load(overlay);

        Assert.Equal("Other Studio", result.Plan!.Install.CompanyName);
        Assert.Contains("\"companyName\":\"Other Studio\"", overlay.MergedJson!);
        Assert.DoesNotContain("COMPANYNAME", overlay.MergedJson!);
    }

    [Fact]
    public void Environment_names_split_on_double_underscore_and_match_case_insensitively()
    {
        var environment = Env(("BAZAAR__SETUP__Install__companyName", "Env Studio"), ("BAZAAR__SETUP__DATABASE__POSTGRESMAJOR", "19"));

        var result = Load(Merge(SetupFixtures.Valid, environment: environment));

        Assert.Equal("Env Studio", result.Plan!.Install.CompanyName);
        Assert.Equal(19, result.Plan.Database.PostgresMajor);
    }

    [Fact]
    public void A_list_item_is_set_by_its_numeric_segment_in_both_doors()
    {
        var environment = Env(("BAZAAR__SETUP__MODELS__SOURCES__1__ID", "other-source"));
        var flags = new[] { "models.sources.0.id=first-source" };

        var result = Load(Merge(SetupFixtures.Valid, flags, environment));

        Assert.Equal("first-source", result.Plan!.Models.Sources[0].Id);
        Assert.Equal("other-source", result.Plan.Models.Sources[1].Id);
    }

    [Fact]
    public void An_index_past_the_end_of_a_list_names_the_path_and_not_the_value()
    {
        var overlay = Merge(SetupFixtures.Valid, ["models.sources.5.id=canary-value-text"]);

        Assert.True(HasError(overlay, "models.sources[5].id"));
        Assert.DoesNotContain("canary-value-text", AllText(overlay));
    }

    [Fact]
    public void A_new_list_item_can_be_appended_at_the_next_index()
    {
        var overlay = Merge(
            SetupFixtures.Valid,
            ["models.sources.2.id=third", "models.sources.2.keyRef=secret://bazaar/providers/third/key"]);

        var result = Load(overlay);

        Assert.Equal("third", result.Plan!.Models.Sources[2].Id);
        Assert.Equal("secret://bazaar/providers/third/key", result.Plan.Models.Sources[2].KeyRef);
    }

    [Fact]
    public void Typed_values_are_written_as_their_type_by_the_plan_shape()
    {
        var overlay = Merge(
            SetupFixtures.Valid,
            ["owner.requireSecondFactor=false", "database.gcPartitionCount=32", "modules=[\"Bazaar.GC\"]", "owner.recovery.backupOwners=[\"alice\",\"bob\"]"]);

        var result = Load(overlay);

        Assert.False(result.Plan!.Owner.RequireSecondFactor);
        Assert.Equal(32, result.Plan.Database.GcPartitionCount);
        Assert.Equal(["Bazaar.GC"], result.Plan.Modules);
        Assert.Equal(["alice", "bob"], result.Plan.Owner.Recovery.BackupOwners);
    }

    [Fact]
    public void A_list_item_of_a_string_list_is_set_by_index()
    {
        var result = Load(Merge(SetupFixtures.Valid, ["owner.recovery.backupOwners.0=carol"]));

        Assert.Equal(["carol"], result.Plan!.Owner.Recovery.BackupOwners);
    }

    [Fact]
    public void A_value_of_the_wrong_type_is_an_error_naming_the_key()
    {
        var overlay = Merge(SetupFixtures.Valid, ["database.postgresMajor=eighteen", "owner.requireSecondFactor=maybe", "modules=Bazaar.GC"]);

        Assert.True(HasError(overlay, "database.postgresMajor"));
        Assert.True(HasError(overlay, "owner.requireSecondFactor"));
        Assert.True(HasError(overlay, "modules"));
        Assert.DoesNotContain("eighteen", AllText(overlay));
        Assert.DoesNotContain("maybe", AllText(overlay));
        Assert.Null(overlay.MergedJson);
    }

    [Fact]
    public void A_nullable_key_takes_the_text_null_as_json_null()
    {
        var result = Load(Merge(SetupFixtures.Valid, ["backups.target=null"]));

        // No target and no acknowledgement is a hold, not an error (doc 45 section 1.4).
        Assert.Equal(SetupExitCode.Held, result.ExitCode);
        Assert.Null(result.Plan!.Backups.Target);
    }

    [Fact]
    public void Settings_keys_are_registry_names_and_take_their_registry_type()
    {
        var result = Load(Merge(SetupFixtures.Valid, ["settings.HOST.SHUTDOWN_TIMEOUT_SECONDS=30"]));

        Assert.Equal(30, result.Plan!.Settings["host.shutdown_timeout_seconds"].GetInt32());
    }

    [Fact]
    public void An_unknown_settings_key_names_the_full_dotted_path()
    {
        var overlay = Merge(SetupFixtures.Valid, ["settings.host.nope=canary-value-text"]);

        Assert.True(HasError(overlay, "settings.host.nope"));
        Assert.DoesNotContain("canary-value-text", AllText(overlay));
    }

    [Fact]
    public void An_unknown_path_fails_with_the_exact_dotted_path_and_never_the_value()
    {
        var overlay = Merge(SetupFixtures.Valid, [$"install.nope={_canary}"]);

        Assert.True(HasError(overlay, "install.nope"));
        Assert.DoesNotContain(_canary, AllText(overlay));
    }

    [Fact]
    public void An_unknown_environment_path_fails_with_the_dotted_path_and_never_the_value()
    {
        var overlay = Merge(null, environment: Env(("BAZAAR__SETUP__INSTALL__NOPE", _canary)));

        Assert.True(HasError(overlay, "INSTALL.NOPE"));
        Assert.DoesNotContain(_canary, AllText(overlay));
    }

    [Fact]
    public void A_section_is_not_a_value()
    {
        var overlay = Merge(SetupFixtures.Valid, ["install=whatever"]);

        Assert.True(HasError(overlay, "install"));
    }

    [Fact]
    public void A_flag_without_an_equals_sign_is_an_error_that_does_not_echo_its_text()
    {
        var overlay = Merge(SetupFixtures.Valid, [$"install.companyName{_canary}"]);

        Assert.True(HasError(overlay, "--set"));
        Assert.DoesNotContain(_canary, AllText(overlay));
    }

    [Fact]
    public void A_literal_in_a_bootstrap_secret_key_fails_with_the_key_name_only()
    {
        var overlay = Merge(SetupFixtures.Valid, [$"custody.kekRef={_canary}"]);

        Assert.True(HasError(overlay, "custody.kekRef"));
        Assert.DoesNotContain(_canary, AllText(overlay));
    }

    [Fact]
    public void A_literal_in_a_secret_store_key_fails_with_the_key_name_only_through_either_door()
    {
        var fromFlag = Merge(SetupFixtures.Valid, [$"custody.mergeCredential.appIdRef={_canary}"]);
        var fromEnvironment = Merge(SetupFixtures.Valid, environment: Env(("BAZAAR__SETUP__MODELS__SOURCES__0__KEYREF", _canary)));

        Assert.True(HasError(fromFlag, "custody.mergeCredential.appIdRef"));
        Assert.True(HasError(fromEnvironment, "models.sources[0].keyRef"));
        Assert.DoesNotContain(_canary, AllText(fromFlag));
        Assert.DoesNotContain(_canary, AllText(fromEnvironment));
    }

    [Fact]
    public void A_literal_inside_a_whole_list_value_fails_with_the_item_key_name()
    {
        var overlay = Merge(SetupFixtures.Valid, [$"models.sources=[{{\"id\":\"a\",\"keyRef\":\"{_canary}\"}}]"]);

        Assert.True(HasError(overlay, "models.sources[0].keyRef"));
        Assert.DoesNotContain(_canary, AllText(overlay));
    }

    [Fact]
    public void A_valid_reference_passes_the_overlay_and_the_loader()
    {
        var overlay = Merge(SetupFixtures.Valid, ["custody.kekProvider=env", "custody.kekRef=env:BAZAAR_KEK"]);

        var result = Load(overlay);

        Assert.Equal("env:BAZAAR_KEK", result.Plan!.Custody.KekRef);
    }

    [Fact]
    public void Precedence_is_file_then_environment_then_flags()
    {
        var environment = Env(("BAZAAR__SETUP__INSTALL__COMPANYNAME", "Env Studio"));

        var fileOnly = Load(Merge(SetupFixtures.Valid));
        var fileAndEnvironment = Load(Merge(SetupFixtures.Valid, environment: environment));
        var everything = Load(Merge(SetupFixtures.Valid, ["install.companyName=Flag Studio"], environment));

        Assert.Equal("Example Studio", fileOnly.Plan!.Install.CompanyName);
        Assert.Equal("Env Studio", fileAndEnvironment.Plan!.Install.CompanyName);
        Assert.Equal("Flag Studio", everything.Plan!.Install.CompanyName);
    }

    [Fact]
    public void A_repeated_flag_takes_the_later_value()
    {
        var result = Load(Merge(SetupFixtures.Valid, ["install.companyName=First", "install.companyName=Second"]));

        Assert.Equal("Second", result.Plan!.Install.CompanyName);
    }

    [Fact]
    public void Two_environment_names_for_one_key_are_an_error()
    {
        var overlay = Merge(
            SetupFixtures.Valid,
            environment: Env(("BAZAAR__SETUP__INSTALL__COMPANYNAME", "One"), ("BAZAAR__SETUP__install__companyName", "Two")));

        Assert.True(HasError(overlay, "install.companyName"));
    }

    [Fact]
    public void Environment_names_outside_the_prefix_and_empty_values_are_ignored()
    {
        var environment = Env(
            ("OTHER__SETUP__INSTALL__COMPANYNAME", "Not Ours"),
            ("BAZAAR__SETUP__INSTALL__COMPANYNAME", ""),
            ("BAZAAR__SETUP__INSTALL__LOCALE", null));

        var overlay = Merge(SetupFixtures.Valid, environment: environment);

        Assert.Equal(SetupDoors.File, overlay.Doors);
        Assert.Equal("Example Studio", Load(overlay).Plan!.Install.CompanyName);
        Assert.False(SetupDoorOverlay.HasEnvironmentOverrides(environment));
    }

    [Fact]
    public void A_base_document_that_is_not_json_is_returned_unchanged_for_the_loader_to_report()
    {
        const string broken = "{ \"schemaVersion\": 1,";

        var overlay = Merge(broken, ["install.companyName=Flag"]);

        Assert.False(overlay.HasErrors);
        Assert.Equal(broken, overlay.MergedJson);
        Assert.Equal(SetupExitCode.InvalidInput, SetupPlanLoader.Load(overlay.MergedJson!, RegistrySetupSettingCatalog.Known).ExitCode);
    }

    [Fact]
    public void Without_a_file_the_document_starts_empty_and_the_loader_asks_for_the_required_keys()
    {
        var overlay = Merge(null, ["schemaVersion=1"]);
        var result = SetupPlanLoader.Load(overlay.MergedJson!, RegistrySetupSettingCatalog.Known);

        Assert.Equal(SetupExitCode.InvalidInput, result.ExitCode);
        Assert.Contains(result.Issues, issue => issue.Path == "install" && issue.Message == "is required.");
        Assert.Contains(result.Issues, issue => issue.Path == "company.source" && issue.Message == "is required.");
    }

    [Fact]
    public void A_settings_key_set_by_flag_conflicts_with_the_network_block_as_the_validator_says()
    {
        var result = Load(Merge(SetupFixtures.Valid, ["settings.host.status.bind=loopback"]));

        Assert.Contains(result.Issues, issue => issue.Path == "settings.host.status.bind" && issue.Message.Contains("already set by network.statusBind"));
    }

    [Fact]
    public void The_entry_path_follows_the_highest_precedence_door_that_contributed()
    {
        var environment = Env(("BAZAAR__SETUP__INSTALL__COMPANYNAME", "Env Studio"));

        Assert.Equal(SetupEntryPath.File, Merge(SetupFixtures.Valid).EntryPath);
        Assert.Equal(SetupEntryPath.Env, Merge(SetupFixtures.Valid, environment: environment).EntryPath);
        Assert.Equal(SetupEntryPath.Cli, Merge(SetupFixtures.Valid, ["install.companyName=Flag"]).EntryPath);
        Assert.Equal(SetupEntryPath.Cli, Merge(SetupFixtures.Valid, ["install.companyName=Flag"], environment).EntryPath);
        Assert.Equal(SetupEntryPath.Env, Merge(null, environment: environment).EntryPath);
        Assert.Equal(SetupEntryPath.Cli, Merge(null, ["install.companyName=Flag"]).EntryPath);
    }

    [Fact]
    public void The_doors_used_are_recorded_beside_the_plan()
    {
        var environment = Env(("BAZAAR__SETUP__INSTALL__COMPANYNAME", "Env Studio"));

        var overlay = Merge(SetupFixtures.Valid, ["install.locale=en"], environment);

        Assert.Equal(SetupDoors.File | SetupDoors.EnvironmentVariables | SetupDoors.Flags, overlay.Doors);
        Assert.DoesNotContain("\"doors\"", overlay.MergedJson!);
    }

    [Fact]
    public void The_same_plan_through_each_door_gives_the_same_hash_and_only_the_entry_path_differs()
    {
        // The leaves of the fixture are the plan's own values. Each one is written once as an environment variable and once as a flag.
        var leaves = SetupPlanCanonicalizer.Leaves(JsonNode.Parse(SetupFixtures.Valid));
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        var flags = new List<string>();
        foreach (var (path, text) in leaves.Where(leaf => leaf.Value != "{}"))
        {
            var value = RawValue(text);
            environment["BAZAAR__SETUP__" + string.Join("__", path.Split('.')).ToUpperInvariant()] = value;
            flags.Add(path + "=" + value);
        }

        var fromFile = Load(Merge(SetupFixtures.Valid));
        var fromEnvironment = Load(Merge(null, environment: environment));
        var fromFlags = Load(Merge(null, flags));

        Assert.Equal(fromFile.PlanHash, SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known).PlanHash);
        Assert.Equal(fromFile.PlanHash, fromEnvironment.PlanHash);
        Assert.Equal(fromFile.PlanHash, fromFlags.PlanHash);
        Assert.Equal(SetupEntryPath.File, Merge(SetupFixtures.Valid).EntryPath);
        Assert.Equal(SetupEntryPath.Env, Merge(null, environment: environment).EntryPath);
        Assert.Equal(SetupEntryPath.Cli, Merge(null, flags).EntryPath);
    }

    [Fact]
    public void A_fake_secret_placed_in_every_door_never_appears_in_any_overlay_output()
    {
        // A literal inside the file itself is the loader's to reject (the canary tests in SetupPlanLoaderTests cover it); the overlay
        // checks the values the doors add.
        var overlays = new[]
        {
            Merge(SetupFixtures.Valid, [$"custody.kekRef={_canary}"]),
            Merge(SetupFixtures.Valid, environment: Env(("BAZAAR__SETUP__CUSTODY__KEKREF", _canary))),
            Merge(SetupFixtures.Valid, [$"models.sources.0.keyRef={_canary}"], Env(("BAZAAR__SETUP__CUSTODY__MERGECREDENTIAL__APPIDREF", _canary))),
            Merge(SetupFixtures.Valid, [$"settings.host.shutdown_timeout_seconds={_canary}"]),
        };

        foreach (var overlay in overlays)
        {
            Assert.DoesNotContain(_canary, AllText(overlay));
        }
    }

    [Fact]
    public void Environment_variable_presence_is_detected_only_for_non_empty_prefixed_values()
    {
        Assert.True(SetupDoorOverlay.HasEnvironmentOverrides(Env(("bazaar__setup__install__companyname", "x"))));
        Assert.False(SetupDoorOverlay.HasEnvironmentOverrides(Env(("BAZAAR__SETUP__INSTALL__COMPANYNAME", ""))));
        Assert.False(SetupDoorOverlay.HasEnvironmentOverrides(Env(("PATH", "/usr/bin"))));
    }

    /// <summary>A leaf value as the operator would type it: a string without its JSON quotes, anything else as its JSON text.</summary>
    private static string RawValue(string text)
    {
        return JsonNode.Parse(text) is JsonValue value && value.TryGetValue<string>(out var raw) ? raw : text;
    }
}
