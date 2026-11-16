using Bazaar.Core;
using Bazaar.Server.CompanyBuild;
using Bazaar.Server.CompanyBuild.Abstractions;
using Bazaar.CompanyIdentity;

namespace Bazaar.Server.CompanyBuild.Tests;

public sealed class CompanyBuildTests
{
    private static ComponentManifest M(string id, string[]? hard = null, string[]? soft = null) =>
        new(id, "0.1.0", ComponentKind.Feature, hard ?? [], [], soft);

    private static CompanyServerIdentity Identity() =>
        CompanyServerIdentity.Create(new Uri("https://company.example/"), new string('a', 64));

    [Fact]
    public void Compose_adds_hard_dependencies_and_reports_them()
    {
        var catalogue = new[] { M("gc"), M("agents", hard: ["gc"]), M("vault") };

        var plan = CompanyBuildComposer.Compose(catalogue, new CompanyBuildRequest("acme", ["agents"]));

        Assert.Equal(["gc", "agents"], plan.Modules.Select(m => m.Id));
        Assert.Equal(["gc"], plan.AddedDependencies);
    }

    [Fact]
    public void Compose_leaves_out_unselected_modules()
    {
        var catalogue = new[] { M("gc"), M("vault") };

        var plan = CompanyBuildComposer.Compose(catalogue, new CompanyBuildRequest("acme", ["gc"]));

        Assert.Equal(["gc"], plan.Modules.Select(m => m.Id));
        Assert.Empty(plan.AddedDependencies);
    }

    [Fact]
    public void Compose_drops_soft_dependencies_that_are_not_in_the_plan()
    {
        var catalogue = new[] { M("kanban", soft: ["constellation"]), M("constellation") };

        var plan = CompanyBuildComposer.Compose(catalogue, new CompanyBuildRequest("acme", ["kanban"]));

        var kanban = Assert.Single(plan.Modules);
        Assert.Empty(kanban.Soft);
    }

    [Fact]
    public void Compose_allows_mutual_soft_dependencies_without_reordering()
    {
        var catalogue = new[] { M("a", soft: ["b"]), M("b", soft: ["a"]) };

        var plan = CompanyBuildComposer.Compose(catalogue, new CompanyBuildRequest("acme", ["b", "a"]));

        Assert.Equal(["a", "b"], plan.Modules.Select(m => m.Id));
    }

    [Fact]
    public void Compose_refuses_a_hard_cycle()
    {
        var catalogue = new[] { M("x", hard: ["y"]), M("y", hard: ["x"]) };

        Assert.Throws<ComponentGraphException>(() =>
            CompanyBuildComposer.Compose(catalogue, new CompanyBuildRequest("acme", ["x"])));
    }

    [Fact]
    public void Compose_refuses_an_unknown_selection()
    {
        var catalogue = new[] { M("gc") };

        Assert.Throws<CompanyBuildException>(() =>
            CompanyBuildComposer.Compose(catalogue, new CompanyBuildRequest("acme", ["missing"])));
    }

    [Fact]
    public async Task Runner_writes_identity_and_reports_a_clean_first_attempt()
    {
        var plan = new CompanyBuildPlan("acme", [M("gc")], []);
        var dir = Path.Combine(Path.GetTempPath(), "bazaar-cb-" + Guid.NewGuid().ToString("N"));

        var report = await CompanyBuildRunner.RunAsync(plan, Identity(), dir, (_, _) => Task.CompletedTask, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.Attempts);
        Assert.Equal(0, report.Retries);
        Assert.Empty(report.FailureMessages);
        Assert.Contains(new string('a', 64), File.ReadAllText(report.IdentityPath));
    }

    [Fact]
    public async Task Runner_reports_retries_even_when_a_later_attempt_succeeds()
    {
        var plan = new CompanyBuildPlan("acme", [M("gc")], []);
        var dir = Path.Combine(Path.GetTempPath(), "bazaar-cb-" + Guid.NewGuid().ToString("N"));

        var report = await CompanyBuildRunner.RunAsync(plan, Identity(), dir, (attempt, _) =>
            attempt < 3 ? throw new InvalidOperationException("toolchain busy") : Task.CompletedTask, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded);
        Assert.Equal(3, report.Attempts);
        Assert.Equal(2, report.Retries);
        Assert.Equal(2, report.FailureMessages.Count);
    }

    [Fact]
    public async Task Runner_stops_after_the_bounded_attempts_and_reports_each_failure()
    {
        var plan = new CompanyBuildPlan("acme", [M("gc")], []);
        var dir = Path.Combine(Path.GetTempPath(), "bazaar-cb-" + Guid.NewGuid().ToString("N"));
        var calls = 0;

        var report = await CompanyBuildRunner.RunAsync(plan, Identity(), dir, (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("sdk missing");
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(report.Succeeded);
        Assert.Equal(CompanyBuildRunner.DefaultMaxAttempts, calls);
        Assert.Equal(CompanyBuildRunner.DefaultMaxAttempts, report.Attempts);
        Assert.Equal(CompanyBuildRunner.DefaultMaxAttempts, report.FailureMessages.Count);
    }

    [Fact]
    public async Task Runner_refuses_an_attempt_count_out_of_range()
    {
        var plan = new CompanyBuildPlan("acme", [M("gc")], []);
        var dir = Path.Combine(Path.GetTempPath(), "bazaar-cb-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            CompanyBuildRunner.RunAsync(plan, Identity(), dir, (_, _) => Task.CompletedTask, maxAttempts: 0, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Component_manifest_is_a_valid_kernel_module()
    {
        var component = new CompanyBuildComponent();

        var ordered = ComponentGraph.Order([component.Manifest]);

        Assert.Equal("company-build", Assert.Single(ordered).Id);
    }
}

public sealed class DotnetPublishBuildStepTests
{
    [Fact]
    public void Arguments_are_a_publish_list_with_no_shell()
    {
        var args = DotnetPublishBuildStep.BuildArguments("src/Client/Client.csproj", "out/acme");

        Assert.Equal(["publish", "src/Client/Client.csproj", "-c", "Release", "-o", "out/acme", "--nologo"], args);
    }

    [Fact]
    public async Task A_missing_project_fails_with_the_exit_code()
    {
        var missing = Path.Combine(Path.GetTempPath(), "bazaar-missing-" + Guid.NewGuid().ToString("N") + ".csproj");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DotnetPublishBuildStep.RunAsync(missing, Path.GetTempPath(), TestContext.Current.CancellationToken));

        Assert.Contains("exited with code", ex.Message);
    }
}
