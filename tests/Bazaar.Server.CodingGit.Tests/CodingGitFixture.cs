using Bazaar.Server.CodingGit.Fakes;

namespace Bazaar.Server.CodingGit.Tests;

/// <summary>Real temp directories (so symlink tests are real) plus the service wired to fakes.</summary>
internal sealed class CodingGitFixture : IDisposable
{
    public static readonly RepoRef Repo = new("repo", "main");
    public static readonly ModelIdentity Model = new("Claude Sonnet 5.5", "noreply@anthropic.com");

    public CodingGitFixture(string? worktreeRoot = null, string? passport = null, TimeProvider? time = null, CodingGitOptions? options = null)
    {
        Base = Path.Combine(Path.GetTempPath(), "bazaar-codinggit-" + Guid.NewGuid().ToString("N"));
        Share = Path.Combine(Base, "share");
        Passport = Path.Combine(Base, "passport");
        Directory.CreateDirectory(Share);
        Directory.CreateDirectory(Passport);

        Engine = new InMemoryCodingGitEngine();
        Port = new FakeHostPushPort();
        Gate = new FakePushGate();
        Locks = new WorktreeLocks(time);
        Service = new CodingGitService(
            new FixedWorkShareLayout(worktreeRoot ?? Share, passport ?? Passport),
            Engine,
            Port,
            Gate,
            new FakeAgentGitIdentity(),
            Locks,
            options);
    }

    public string Base { get; }

    public string Share { get; }

    public string Passport { get; }

    public InMemoryCodingGitEngine Engine { get; }

    public FakeHostPushPort Port { get; }

    public FakePushGate Gate { get; }

    public WorktreeLocks Locks { get; }

    public CodingGitService Service { get; }

    public static CreateWorktreeRequest Request(string slug = "fix-login", string agent = "charles") =>
        new(agent, Repo, slug, "gc-1", "task-7");

    public Task<CodingWorktree> OpenAsync(string slug = "fix-login") => Service.CreateWorktreeAsync(Request(slug));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Base, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A clock tests can move.</summary>
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
