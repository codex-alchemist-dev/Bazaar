using System.Net;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Probes;

namespace Bazaar.Server.Tests.Setup.Probes;

public sealed class ReachabilityProbeTests
{
    private const string _anchor = "https://anchor.internal.example.invalid/audit-chain";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SetupPlan AnchorPlan(string? target) => new() { Custody = new SetupCustodySection { AuditAnchorTarget = target } };

    [Fact]
    public async Task The_anchor_probe_is_green_when_it_answers_and_never_repeats_the_url()
    {
        var client = new FakeReachability(_ => true);

        var reading = await new AuditAnchorReachableProbe(client).ProbeAsync(ProbeTestPlans.Context(AnchorPlan(_anchor)), Ct);

        Assert.Equal(SetupProbeOutcome.Healthy, reading.Outcome);
        Assert.Equal(new Uri(_anchor), Assert.Single(client.Asked));
        Assert.DoesNotContain("internal.example", ProbeTestPlans.AllText(reading), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_anchor_probe_fails_when_it_does_not_answer_and_never_repeats_the_url()
    {
        var reading = await new AuditAnchorReachableProbe(new FakeReachability(_ => false)).ProbeAsync(ProbeTestPlans.Context(AnchorPlan(_anchor)), Ct);

        Assert.Equal(SetupProbeOutcome.Failed, reading.Outcome);
        Assert.DoesNotContain("internal.example", ProbeTestPlans.AllText(reading), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://anchor.example.invalid/x")]
    [InlineData("not a url")]
    public async Task A_non_https_anchor_fails_without_asking_the_network(string target)
    {
        var client = new FakeReachability(_ => true);

        var reading = await new AuditAnchorReachableProbe(client).ProbeAsync(ProbeTestPlans.Context(AnchorPlan(target)), Ct);

        Assert.Equal(SetupProbeOutcome.Failed, reading.Outcome);
        Assert.Empty(client.Asked);
    }

    [Fact]
    public async Task No_anchor_in_the_plan_is_unavailable()
    {
        var reading = await new AuditAnchorReachableProbe(new FakeReachability(_ => true)).ProbeAsync(ProbeTestPlans.Context(AnchorPlan(null)), Ct);

        Assert.Equal(SetupProbeOutcome.Unavailable, reading.Outcome);
    }

    private static SetupPlan ModelPlan(params string[] ids) => new()
    {
        Models = new SetupModelsSection { Sources = [.. ids.Select(id => new SetupModelSource { Id = id })] },
    };

    [Fact]
    public async Task Model_sources_all_answering_is_green_and_none_answering_is_red()
    {
        var plan = ModelPlan("freellmapi", "ollama");

        var up = await new ModelSourcesReachableProbe(ModelSourceCatalogue.Default, new FakeReachability(_ => true)).ProbeAsync(ProbeTestPlans.Context(plan), Ct);
        var down = await new ModelSourcesReachableProbe(ModelSourceCatalogue.Default, new FakeReachability(_ => false)).ProbeAsync(ProbeTestPlans.Context(plan), Ct);

        Assert.Equal(SetupProbeOutcome.Healthy, up.Outcome);
        Assert.Equal(SetupProbeOutcome.Failed, down.Outcome);
    }

    [Fact]
    public async Task Some_model_sources_down_is_amber_and_names_only_catalogue_ids()
    {
        var plan = ModelPlan("freellmapi", "ollama", "operator-custom-endpoint");
        var client = new FakeReachability(url => url.Port != 11434);

        var reading = await new ModelSourcesReachableProbe(ModelSourceCatalogue.Default, client).ProbeAsync(ProbeTestPlans.Context(plan), Ct);

        Assert.Equal(SetupProbeOutcome.Degraded, reading.Outcome);
        Assert.Contains("ollama", reading.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("11434", ProbeTestPlans.AllText(reading), StringComparison.Ordinal);
        Assert.DoesNotContain("operator-custom-endpoint", ProbeTestPlans.AllText(reading), StringComparison.Ordinal);
        Assert.Equal(2, client.Asked.Count);
    }

    [Fact]
    public async Task A_plan_with_no_known_model_source_is_unavailable()
    {
        var reading = await new ModelSourcesReachableProbe(ModelSourceCatalogue.Default, new FakeReachability(_ => true))
            .ProbeAsync(ProbeTestPlans.Context(ModelPlan("operator-custom-endpoint")), Ct);

        Assert.Equal(SetupProbeOutcome.Unavailable, reading.Outcome);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            return send(request, cancellationToken);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task The_http_client_counts_any_status_as_reachable_and_sends_a_head(HttpStatusCode status)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        using var client = new HttpReachabilityClient(handler);

        Assert.True(await client.IsReachableAsync(new Uri("https://a.invalid/"), TimeSpan.FromSeconds(5), Ct));
        Assert.Equal(HttpMethod.Head, handler.Method);
    }

    [Fact]
    public async Task The_http_client_counts_a_failed_connection_and_a_timeout_as_unreachable()
    {
        using var refused = new HttpReachabilityClient(new StubHandler((_, _) => throw new HttpRequestException("refused")));
        using var hangs = new HttpReachabilityClient(new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage();
        }));

        Assert.False(await refused.IsReachableAsync(new Uri("https://a.invalid/"), TimeSpan.FromSeconds(5), Ct));
        Assert.False(await hangs.IsReachableAsync(new Uri("https://a.invalid/"), TimeSpan.FromMilliseconds(50), Ct));
    }

    [Fact]
    public async Task The_http_client_still_honours_the_callers_own_cancel()
    {
        using var cts = new CancellationTokenSource();
        using var client = new HttpReachabilityClient(new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage();
        }));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.IsReachableAsync(new Uri("https://a.invalid/"), TimeSpan.FromSeconds(5), cts.Token));
    }

    private static SetupPlan BackupPlan(string? target) => new() { Backups = new SetupBackupsSection { Target = target } };

    [Fact]
    public async Task A_file_backup_target_is_green_when_the_folder_exists_and_red_when_it_does_not()
    {
        using var folder = new TempFolder();
        var probe = new BackupTargetReachableProbe();

        var present = await probe.ProbeAsync(ProbeTestPlans.Context(BackupPlan("file:" + folder.Path)), Ct);
        var absent = await probe.ProbeAsync(ProbeTestPlans.Context(BackupPlan("file:" + Path.Combine(folder.Path, "missing-secret-dir"))), Ct);

        Assert.Equal(SetupProbeOutcome.Healthy, present.Outcome);
        Assert.Equal(SetupProbeOutcome.Failed, absent.Outcome);
        Assert.DoesNotContain("missing-secret-dir", ProbeTestPlans.AllText(absent), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("s3:bucket/prefix")]
    [InlineData("sftp:user@host/path")]
    public async Task No_target_or_a_target_kind_setup_cannot_reach_is_unavailable(string? target)
    {
        var reading = await new BackupTargetReachableProbe().ProbeAsync(ProbeTestPlans.Context(BackupPlan(target)), Ct);

        Assert.Equal(SetupProbeOutcome.Unavailable, reading.Outcome);
    }

    [Fact]
    public async Task An_empty_file_target_fails()
    {
        var reading = await new BackupTargetReachableProbe().ProbeAsync(ProbeTestPlans.Context(BackupPlan("file:")), Ct);

        Assert.Equal(SetupProbeOutcome.Failed, reading.Outcome);
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("bazaar-probe-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
