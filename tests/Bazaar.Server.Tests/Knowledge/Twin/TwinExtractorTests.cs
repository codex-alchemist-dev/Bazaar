using System.Xml;
using Bazaar.Server.Knowledge.Metrics;
using Bazaar.Server.Knowledge.Twin;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Knowledge.Twin;

public sealed class TwinExtractorTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private const string _commit = "0123456789abcdef0123456789abcdef01234567";

    // Facts hold their source lists by reference, so equality checks compare the parts that matter.
    private static List<(string Key, string Value, double Confidence, string Sources)> Describe(IEnumerable<TwinFact> facts) =>
        facts.Select(f => (f.Key, f.Value, f.Confidence, string.Join(";", f.SourceRefs.Select(s => $"{s.Kind}|{s.Ref}|{s.Commit}")))).ToList();

    private static TwinFactExtractor CreateExtractor(out FakeTimeProvider time)
    {
        time = new FakeTimeProvider(_now);
        return new TwinFactExtractor(time);
    }

    private const string _propsXml = """
        <Project>
          <ItemGroup>
            <PackageVersion Include="Npgsql" Version="10.0.1" />
            <PackageVersion Include="Markdig" Version="0.40.0" />
          </ItemGroup>
          <ItemGroup>
            <PackageVersion Update="xunit.v3.mtp-v2" Version="3.1.0" />
            <PackageVersion Include="NoVersionHere" />
          </ItemGroup>
        </Project>
        """;

    [Fact]
    public void Dependencies_come_from_every_package_version_item_sorted_by_package()
    {
        var facts = CreateExtractor(out _).ExtractDependencies("bazaar", "Directory.Packages.props", _commit, _propsXml);

        Assert.Equal(["Markdig", "Npgsql", "xunit.v3.mtp-v2"], facts.Select(f => f.Subject));
        Assert.All(facts, f => Assert.Equal(TwinFactKinds.Dependency, f.Kind));
        Assert.Equal("10.0.1", facts.Single(f => f.Subject == "Npgsql").Value);
        Assert.Equal("3.1.0", facts.Single(f => f.Subject == "xunit.v3.mtp-v2").Value);
    }

    [Fact]
    public void Every_dependency_fact_names_its_repo_file_and_commit()
    {
        var facts = CreateExtractor(out _).ExtractDependencies("bazaar", "Directory.Packages.props", _commit, _propsXml);

        foreach (var fact in facts)
        {
            var source = Assert.Single(fact.SourceRefs);
            Assert.Equal(TwinSourceKinds.RepoFile, source.Kind);
            Assert.Equal("Directory.Packages.props", source.Ref);
            Assert.Equal(_commit, source.Commit);
            Assert.Equal("bazaar", fact.ProjectId);
            Assert.Null(fact.ObservedAt);
            Assert.Equal(1.0, fact.Confidence);
            Assert.Equal(TwinFactStatus.Active, fact.Status);
        }
    }

    [Fact]
    public void A_package_repeated_with_the_same_version_is_one_fact_at_full_confidence()
    {
        const string xml = """<Project><PackageVersion Include="Npgsql" Version="10.0.1" /><PackageVersion Include="npgsql" Version="10.0.1" /></Project>""";

        var facts = CreateExtractor(out _).ExtractDependencies("bazaar", "Directory.Packages.props", _commit, xml);

        var fact = Assert.Single(facts);
        Assert.Equal(1.0, fact.Confidence);
    }

    [Fact]
    public void A_package_with_conflicting_versions_keeps_the_first_and_lowers_confidence()
    {
        const string xml = """<Project><PackageVersion Include="Npgsql" Version="10.0.1" /><PackageVersion Include="Npgsql" Version="9.0.0" /></Project>""";

        var fact = Assert.Single(CreateExtractor(out _).ExtractDependencies("bazaar", "Directory.Packages.props", _commit, xml));

        Assert.Equal("10.0.1", fact.Value);
        Assert.Equal(0.5, fact.Confidence);
    }

    [Fact]
    public void Dependency_extraction_is_repeatable_for_the_same_input()
    {
        var extractor = CreateExtractor(out _);

        var first = extractor.ExtractDependencies("bazaar", "Directory.Packages.props", _commit, _propsXml);
        var second = extractor.ExtractDependencies("bazaar", "Directory.Packages.props", _commit, _propsXml);

        Assert.Equal(Describe(first), Describe(second));
    }

    [Fact]
    public void Extracted_at_comes_from_the_injected_time_provider()
    {
        var extractor = CreateExtractor(out var time);
        time.Advance(TimeSpan.FromHours(3));

        var fact = Assert.Single(extractor.ExtractDependencies("bazaar", "Directory.Packages.props", _commit,
            """<Project><PackageVersion Include="Npgsql" Version="10.0.1" /></Project>"""));

        Assert.Equal(_now.AddHours(3), fact.ExtractedAt);
    }

    [Fact]
    public void Malformed_package_file_throws_so_the_caller_records_a_gap()
    {
        var extractor = CreateExtractor(out _);

        Assert.Throws<XmlException>(() => extractor.ExtractDependencies("bazaar", "Directory.Packages.props", _commit, "<Project><ItemGroup>"));
    }

    [Fact]
    public void Dependency_extraction_rejects_blank_identity_inputs()
    {
        var extractor = CreateExtractor(out _);

        Assert.Throws<ArgumentException>(() => extractor.ExtractDependencies(" ", "Directory.Packages.props", _commit, _propsXml));
        Assert.Throws<ArgumentException>(() => extractor.ExtractDependencies("bazaar", "", _commit, _propsXml));
        Assert.Throws<ArgumentException>(() => extractor.ExtractDependencies("bazaar", "Directory.Packages.props", "", _propsXml));
    }

    [Fact]
    public void A_pull_request_takes_the_state_of_its_newest_event_whatever_the_input_order()
    {
        var opened = new PullRequestEventFact("e1", "PR_1", "opened", false, _now, "sha1");
        var merged = new PullRequestEventFact("e3", "PR_1", "closed", true, _now.AddHours(2), "sha2");
        var reopenedBefore = new PullRequestEventFact("e2", "PR_1", "reopened", false, _now.AddHours(1), "sha1");

        var extractor = CreateExtractor(out _);
        var inOrder = extractor.ExtractPullRequestStates("bazaar", [opened, reopenedBefore, merged]);
        var reversed = extractor.ExtractPullRequestStates("bazaar", [merged, reopenedBefore, opened]);

        var fact = Assert.Single(inOrder);
        Assert.Equal("merged", fact.Value);
        Assert.Equal(TwinFactKinds.PullRequestState, fact.Kind);
        Assert.Equal("PR_1", fact.Subject);
        Assert.Equal(_now.AddHours(2), fact.ObservedAt);
        Assert.Equal(new TwinSourceRef(TwinSourceKinds.ForgeEvent, "e3", "sha2"), Assert.Single(fact.SourceRefs));
        Assert.Equal(Describe(inOrder), Describe(reversed));
    }

    [Fact]
    public void A_closed_pull_request_without_merge_is_closed_and_a_reopen_after_it_is_open_again()
    {
        var closed = new PullRequestEventFact("e1", "PR_2", "closed", false, _now, null);
        var reopened = new PullRequestEventFact("e2", "PR_2", "reopened", false, _now.AddMinutes(5));

        var extractor = CreateExtractor(out _);

        Assert.Equal("closed", Assert.Single(extractor.ExtractPullRequestStates("bazaar", [closed])).Value);
        Assert.Equal("open", Assert.Single(extractor.ExtractPullRequestStates("bazaar", [closed, reopened])).Value);
    }

    [Fact]
    public void Events_at_the_same_time_are_ordered_by_event_id()
    {
        var a = new PullRequestEventFact("a", "PR_3", "opened", false, _now);
        var b = new PullRequestEventFact("b", "PR_3", "closed", true, _now);

        var fact = Assert.Single(CreateExtractor(out _).ExtractPullRequestStates("bazaar", [a, b]));

        Assert.Equal("merged", fact.Value);
        Assert.Equal("b", Assert.Single(fact.SourceRefs).Ref);
    }

    [Fact]
    public void Actions_that_do_not_change_pull_request_state_produce_no_fact()
    {
        var labelled = new PullRequestEventFact("e1", "PR_4", "labeled", false, _now);
        var noNode = new PullRequestEventFact("e2", "", "opened", false, _now);

        Assert.Empty(CreateExtractor(out _).ExtractPullRequestStates("bazaar", [labelled, noNode]));
    }

    [Fact]
    public void A_run_id_repeated_keeps_its_latest_completion()
    {
        var runs = new[]
        {
            new CiRunFact("42", _now, false),
            new CiRunFact("42", _now.AddMinutes(10), true),
            new CiRunFact("7", _now.AddMinutes(1), true),
        };

        var facts = CreateExtractor(out _).ExtractCiRuns("bazaar", runs);

        Assert.Equal(["42", "7"], facts.Select(f => f.Subject));
        Assert.Equal("passed", facts.Single(f => f.Subject == "42").Value);
        Assert.Equal("passed", facts.Single(f => f.Subject == "7").Value);
        Assert.Equal(_now.AddMinutes(10), facts.Single(f => f.Subject == "42").ObservedAt);
    }

    [Fact]
    public void Each_ci_fact_references_its_run()
    {
        var facts = CreateExtractor(out _).ExtractCiRuns("bazaar", [new CiRunFact("99", _now, false)]);

        var fact = Assert.Single(facts);
        Assert.Equal("failed", fact.Value);
        Assert.Equal(new TwinSourceRef(TwinSourceKinds.CiRun, "99"), Assert.Single(fact.SourceRefs));
    }

    [Fact]
    public void Empty_inputs_give_no_facts()
    {
        var extractor = CreateExtractor(out _);

        Assert.Empty(extractor.ExtractPullRequestStates("bazaar", []));
        Assert.Empty(extractor.ExtractCiRuns("bazaar", []));
        Assert.Empty(extractor.ExtractDependencies("bazaar", "Directory.Packages.props", _commit, "<Project />"));
    }

    [Fact]
    public void Fact_keys_are_project_kind_and_subject()
    {
        var fact = Assert.Single(CreateExtractor(out _).ExtractCiRuns("bazaar", [new CiRunFact("5", _now, true)]));

        Assert.Equal("bazaar\u001fci-run\u001f5", fact.Key);
    }
}
