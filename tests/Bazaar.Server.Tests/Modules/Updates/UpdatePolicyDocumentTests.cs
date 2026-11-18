using Bazaar.Server.Modules.Updates;

namespace Bazaar.Server.Tests.Modules.Updates;

/// <summary>The policy-as-code document: load, refusal and round trip (doc 49 section 7, "Policy-as-code").</summary>
public sealed class UpdatePolicyDocumentTests
{
    private const string _sample = """
        {
          "schemaVersion": 1,
          "policies": [
            {
              "scope": { "moduleId": "postgresql", "category": "security", "versionType": "generalAvailability", "channel": "stable" },
              "conditions": { "isSecurity": true, "matrixPassed": true },
              "windows": [
                { "days": ["sunday"], "start": "02:00", "end": "04:00", "zone": { "timeZone": "Europe/Berlin" } }
              ],
              "action": "auto"
            },
            { "action": "manual" }
          ]
        }
        """;

    /// <summary>Wraps policy entries in a version 1 document.</summary>
    private static string Document(string policies) => "{ \"schemaVersion\": 1, \"policies\": [" + policies + "] }";

    private static List<UpdatePolicyProblemKind> Kinds(UpdatePolicyLoadResult result) => result.Problems.Select(p => p.Kind).ToList();

    [Fact]
    public void Valid_document_loads_into_update_policies()
    {
        var result = UpdatePolicyDocumentLoader.Load(_sample);

        Assert.True(result.IsLoaded);
        Assert.Empty(result.Problems);
        Assert.Equal(2, result.Policies.Count);

        var first = result.Policies[0];
        Assert.Equal(new UpdatePolicyScope("postgresql", "security", VersionType.GeneralAvailability, "stable"), first.Scope);
        Assert.Equal(new UpdatePolicyConditions(IsSecurity: true, MatrixPassed: true), first.Conditions);
        Assert.Equal(UpdatePolicyAction.Auto, first.Action);
        var window = Assert.Single(first.Windows);
        Assert.Equal([DayOfWeek.Sunday], window.Days);
        Assert.Equal(new TimeOnly(2, 0), window.Start);
        Assert.Equal(new TimeOnly(4, 0), window.End);
        Assert.Equal("Europe/Berlin", window.Zone.NamedZone?.Id);

        var second = result.Policies[1];
        Assert.Equal(new UpdatePolicyScope(), second.Scope);
        Assert.Equal(new UpdatePolicyConditions(), second.Conditions);
        Assert.Empty(second.Windows);
        Assert.Equal(UpdatePolicyAction.Manual, second.Action);
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 2, \"policies\": [] }")]
    [InlineData("{ \"schemaVersion\": 0, \"policies\": [] }")]
    public void Schema_version_other_than_one_is_refused(string json)
    {
        var result = UpdatePolicyDocumentLoader.Load(json);

        Assert.False(result.IsLoaded);
        Assert.Null(result.Document);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(UpdatePolicyProblemKind.UnknownSchemaVersion, problem.Kind);
        Assert.Equal("$.schemaVersion", problem.Path);
    }

    [Fact]
    public void Missing_schema_version_is_refused()
    {
        var result = UpdatePolicyDocumentLoader.Load("{ \"policies\": [] }");

        Assert.False(result.IsLoaded);
        Assert.Equal([UpdatePolicyProblemKind.MissingProperty], Kinds(result));
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 1, \"policies\": [], \"comment\": \"hi\" }", "$.comment")]
    [InlineData("{ \"schemaVersion\": 1, \"policies\": [{ \"action\": \"manual\", \"priority\": 3 }] }", "$.policies[0].priority")]
    [InlineData("{ \"schemaVersion\": 1, \"policies\": [{ \"scope\": { \"module\": \"x\" }, \"action\": \"manual\" }] }", "$.policies[0].scope.module")]
    [InlineData("{ \"schemaVersion\": 1, \"policies\": [{ \"action\": \"manual\", \"conditions\": { \"security\": true } }] }", "$.policies[0].conditions.security")]
    [InlineData("{ \"schemaVersion\": 1, \"policies\": [{ \"action\": \"auto\", \"windows\": [{ \"days\": [\"monday\"], \"start\": \"01:00\", \"end\": \"02:00\", \"zone\": { \"timeZone\": \"UTC\", \"offset\": \"+00:00\" } }] }] }", "$.policies[0].windows[0].zone.offset")]
    public void Unknown_property_is_refused_with_its_path(string json, string expectedPath)
    {
        var result = UpdatePolicyDocumentLoader.Load(json);

        Assert.False(result.IsLoaded);
        Assert.Contains(result.Problems, p => p.Kind == UpdatePolicyProblemKind.UnknownProperty && p.Path == expectedPath);
    }

    [Theory]
    [InlineData("\"versionType\": \"patch\"", "$.policies[0].scope.versionType")]
    [InlineData("\"versionType\": \"Beta\"", "$.policies[0].scope.versionType")]
    [InlineData("\"action\": \"Auto\"", "$.policies[0].action")]
    [InlineData("\"action\": \"delete\"", "$.policies[0].action")]
    public void Unknown_enum_value_is_refused(string fragment, string expectedPath)
    {
        var json = fragment.StartsWith("\"action\"", StringComparison.Ordinal)
            ? Document("{ " + fragment + " }")
            : Document("{ \"scope\": { " + fragment + " }, \"action\": \"manual\" }");

        var result = UpdatePolicyDocumentLoader.Load(json);

        Assert.False(result.IsLoaded);
        Assert.Contains(result.Problems, p => p.Kind == UpdatePolicyProblemKind.UnknownEnumValue && p.Path == expectedPath);
    }

    [Fact]
    public void Unknown_day_name_is_refused()
    {
        var json = Document("{ \"action\": \"manual\", \"windows\": [{ \"days\": [\"Sunday\"], \"start\": \"02:00\", \"end\": \"04:00\", \"zone\": { \"utcOffset\": \"+00:00\" } }] }");

        var result = UpdatePolicyDocumentLoader.Load(json);

        Assert.Contains(result.Problems, p => p.Kind == UpdatePolicyProblemKind.UnknownEnumValue && p.Path == "$.policies[0].windows[0].days[0]");
    }

    [Theory]
    [InlineData("\"start\": \"25:00\", \"end\": \"04:00\"")]
    [InlineData("\"start\": \"2:00\", \"end\": \"04:00\"")]
    public void Malformed_time_is_refused(string times)
    {
        var json = Document("{ \"action\": \"manual\", \"windows\": [{ \"days\": [\"sunday\"], " + times + ", \"zone\": { \"utcOffset\": \"+00:00\" } }] }");

        var result = UpdatePolicyDocumentLoader.Load(json);

        Assert.Contains(result.Problems, p => p.Kind == UpdatePolicyProblemKind.InvalidValue && p.Path == "$.policies[0].windows[0].start");
    }

    [Fact]
    public void Invalid_window_is_refused_by_the_window_validator()
    {
        // Two Monday windows that share 11:00 to 12:00 overlap; the second window carries the problem.
        var json = Document(
            "{ \"action\": \"manual\", \"windows\": [" +
            "{ \"days\": [\"monday\"], \"start\": \"10:00\", \"end\": \"12:00\", \"zone\": { \"utcOffset\": \"+00:00\" } }," +
            "{ \"days\": [\"monday\"], \"start\": \"11:00\", \"end\": \"13:00\", \"zone\": { \"utcOffset\": \"+00:00\" } }] }");

        var result = UpdatePolicyDocumentLoader.Load(json);

        Assert.False(result.IsLoaded);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(UpdatePolicyProblemKind.InvalidWindow, problem.Kind);
        Assert.Equal("$.policies[0].windows[1]", problem.Path);
        Assert.Contains("Overlap", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Window_that_ends_before_it_starts_is_refused()
    {
        var json = Document("{ \"action\": \"manual\", \"windows\": [{ \"days\": [\"friday\"], \"start\": \"05:00\", \"end\": \"04:00\", \"zone\": { \"utcOffset\": \"+00:00\" } }] }");

        var result = UpdatePolicyDocumentLoader.Load(json);

        var problem = Assert.Single(result.Problems);
        Assert.Equal(UpdatePolicyProblemKind.InvalidWindow, problem.Kind);
        Assert.Contains("InvertedRange", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Window_with_no_days_is_refused()
    {
        var json = Document("{ \"action\": \"manual\", \"windows\": [{ \"days\": [], \"start\": \"02:00\", \"end\": \"04:00\", \"zone\": { \"utcOffset\": \"+00:00\" } }] }");

        var result = UpdatePolicyDocumentLoader.Load(json);

        Assert.Contains(result.Problems, p => p.Kind == UpdatePolicyProblemKind.InvalidWindow && p.Detail.Contains("NoDays", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"zone\": { \"timeZone\": \"Mars/Olympus\" }")]
    [InlineData("\"zone\": { \"utcOffset\": \"+15:00\" }")]
    [InlineData("\"zone\": { \"utcOffset\": \"0200\" }")]
    [InlineData("\"zone\": { }")]
    [InlineData("\"zone\": { \"timeZone\": \"UTC\", \"utcOffset\": \"+00:00\" }")]
    public void Bad_zone_is_refused(string zone)
    {
        var json = Document("{ \"action\": \"manual\", \"windows\": [{ \"days\": [\"sunday\"], \"start\": \"02:00\", \"end\": \"04:00\", " + zone + " }] }");

        var result = UpdatePolicyDocumentLoader.Load(json);

        Assert.False(result.IsLoaded);
        Assert.Contains(result.Problems, p => p.Kind == UpdatePolicyProblemKind.InvalidValue && p.Path.StartsWith("$.policies[0].windows[0].zone", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_scope_is_refused()
    {
        var json = Document(
            "{ \"scope\": { \"moduleId\": \"postgresql\", \"channel\": \"stable\" }, \"action\": \"manual\" }," +
            "{ \"scope\": { \"moduleId\": \"postgresql\", \"channel\": \"stable\" }, \"action\": \"defer\" }");

        var result = UpdatePolicyDocumentLoader.Load(json);

        Assert.False(result.IsLoaded);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(UpdatePolicyProblemKind.DuplicateScope, problem.Kind);
        Assert.Equal("$.policies[1].scope", problem.Path);
    }

    [Fact]
    public void Scopes_that_differ_in_one_field_are_not_duplicates()
    {
        var json = Document(
            "{ \"scope\": { \"moduleId\": \"postgresql\", \"channel\": \"stable\" }, \"action\": \"manual\" }," +
            "{ \"scope\": { \"moduleId\": \"postgresql\", \"channel\": \"beta\" }, \"action\": \"manual\" }");

        Assert.True(UpdatePolicyDocumentLoader.Load(json).IsLoaded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{ \"schemaVersion\": 1, \"policies\": [ }")]
    [InlineData("not json at all")]
    [InlineData("{ /* comments are not JSON */ \"schemaVersion\": 1, \"policies\": [] }")]
    public void Malformed_json_is_refused_without_throwing(string json)
    {
        var result = UpdatePolicyDocumentLoader.Load(json);

        Assert.False(result.IsLoaded);
        Assert.Equal([UpdatePolicyProblemKind.MalformedJson], Kinds(result));
    }

    [Fact]
    public void Wrong_value_type_is_refused()
    {
        var json = Document("{ \"conditions\": { \"isSecurity\": \"yes\" }, \"action\": \"manual\" }");

        var result = UpdatePolicyDocumentLoader.Load(json);

        Assert.Contains(result.Problems, p => p.Kind == UpdatePolicyProblemKind.WrongType && p.Path == "$.policies[0].conditions.isSecurity");
    }

    [Fact]
    public void Document_root_that_is_not_an_object_is_refused()
    {
        var result = UpdatePolicyDocumentLoader.Load("[1, 2, 3]");

        Assert.Equal([UpdatePolicyProblemKind.WrongType], Kinds(result));
    }

    [Theory]
    [InlineData("beta")]
    [InlineData("releaseCandidate")]
    public void Auto_policy_scoped_to_a_pre_release_version_type_is_refused(string versionType)
    {
        var json = Document("{ \"scope\": { \"versionType\": \"" + versionType + "\" }, \"action\": \"auto\" }");

        var result = UpdatePolicyDocumentLoader.Load(json);

        Assert.False(result.IsLoaded);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(UpdatePolicyProblemKind.PreReleaseAutoRefused, problem.Kind);
        Assert.Equal("$.policies[0].action", problem.Path);
    }

    [Fact]
    public void Manual_policy_scoped_to_beta_is_accepted()
    {
        var json = Document("{ \"scope\": { \"versionType\": \"beta\" }, \"action\": \"manual\" }");

        Assert.True(UpdatePolicyDocumentLoader.Load(json).IsLoaded);
    }

    [Fact]
    public void Round_trip_yields_an_equal_document()
    {
        var loaded = UpdatePolicyDocumentLoader.Load(_sample);
        var document = Assert.IsType<UpdatePolicyDocument>(loaded.Document);

        var json = UpdatePolicyDocumentLoader.Serialize(document);
        var reloaded = UpdatePolicyDocumentLoader.Load(json);

        Assert.True(reloaded.IsLoaded);
        Assert.True(SameDocument(document, reloaded.Document!), json);
        Assert.Equal(json, UpdatePolicyDocumentLoader.Serialize(reloaded.Document!));
    }

    [Fact]
    public void Round_trip_keeps_fixed_offsets_and_every_field()
    {
        var document = new UpdatePolicyDocument(
        [
            new UpdatePolicy(
                new UpdatePolicyScope("gateway", "dependency", VersionType.GeneralAvailability, "lts"),
                new UpdatePolicyConditions(IsSecurity: false, MatrixPassed: true),
                [
                    new MaintenanceWindow(
                        [DayOfWeek.Saturday, DayOfWeek.Sunday],
                        new TimeOnly(23, 30),
                        new TimeOnly(23, 59),
                        MaintenanceZone.Fixed(TimeSpan.FromHours(-5))),
                ],
                UpdatePolicyAction.Defer),
        ]);

        var json = UpdatePolicyDocumentLoader.Serialize(document);
        var reloaded = UpdatePolicyDocumentLoader.Load(json);

        Assert.True(reloaded.IsLoaded);
        Assert.True(SameDocument(document, reloaded.Document!), json);
        Assert.Contains("\"utcOffset\": \"-05:00\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_document_round_trips()
    {
        var json = UpdatePolicyDocumentLoader.Serialize(new UpdatePolicyDocument([]));
        var reloaded = UpdatePolicyDocumentLoader.Load(json);

        Assert.True(reloaded.IsLoaded);
        Assert.Empty(reloaded.Policies);
    }

    /// <summary>
    /// Structural comparison. Records compare their lists by reference, so the test compares the content: each scope and
    /// condition field, each window's days and times, and each zone by its named id or its fixed offset.
    /// </summary>
    private static bool SameDocument(UpdatePolicyDocument left, UpdatePolicyDocument right) =>
        left.Policies.Count == right.Policies.Count
        && left.Policies.Zip(right.Policies).All(pair => SamePolicy(pair.First, pair.Second));

    private static bool SamePolicy(UpdatePolicy left, UpdatePolicy right) =>
        left.Scope == right.Scope
        && left.Conditions == right.Conditions
        && left.Action == right.Action
        && left.Windows.Count == right.Windows.Count
        && left.Windows.Zip(right.Windows).All(pair => SameWindow(pair.First, pair.Second));

    private static bool SameWindow(MaintenanceWindow left, MaintenanceWindow right) =>
        left.Days.SequenceEqual(right.Days)
        && left.Start == right.Start
        && left.End == right.End
        && left.Zone.NamedZone?.Id == right.Zone.NamedZone?.Id
        && left.Zone.FixedOffset == right.Zone.FixedOffset;
}
