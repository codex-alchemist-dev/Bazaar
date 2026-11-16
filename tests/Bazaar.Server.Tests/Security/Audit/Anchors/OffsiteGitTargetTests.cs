using System.Text;
using Bazaar.Server.Security.Audit.Anchors;

namespace Bazaar.Server.Tests.Security.Audit.Anchors;

/// <summary>The offsite Git target, against a fake git runner. No git process runs and no remote is contacted.</summary>
public sealed class OffsiteGitTargetTests : IDisposable
{
    private const string _commit = "0123456789abcdef0123456789abcdef01234567";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-anchor-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] _digest = Enumerable.Repeat((byte)0xAB, 32).ToArray();

    private static readonly string _hex = Convert.ToHexString(_digest).ToLowerInvariant();

    public OffsiteGitTargetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private OffsiteGitTargetOptions Options() => new() { LocalPath = _dir };

    [Fact]
    public async Task Anchor_writes_the_digest_file_commits_and_pushes_the_commit()
    {
        var git = new FakeGitRunner();
        var target = new OffsiteGitTarget(git, Options(), new FixedTime());

        var receipt = await target.AnchorAsync(_digest, Ct);

        Assert.Equal("offsite-git", receipt.TargetId);
        Assert.Equal(_commit, Encoding.ASCII.GetString(receipt.ReceiptBytes));
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero), receipt.AnchoredAt);
        Assert.Equal($"{_hex}\n", await File.ReadAllTextAsync(Path.Combine(_dir, $"anchor-{_hex}.txt"), Ct));

        Assert.Equal(
            [
                "add -- anchor-" + _hex + ".txt",
                "-c user.name=Bazaar audit anchor -c user.email=audit-anchor@example.invalid commit --allow-empty --no-verify -m audit anchor " + _hex,
                "rev-parse --verify HEAD",
                "push origin " + _commit + ":refs/heads/audit-anchors",
            ],
            git.Calls.Select(c => c.Arguments).Select(Join));
        Assert.All(git.Calls, call => Assert.Equal(_dir, call.WorkingDirectory));
    }

    [Fact]
    public async Task A_refused_push_throws_without_echoing_git_output()
    {
        var git = new FakeGitRunner { PushExitCode = 1 };
        var target = new OffsiteGitTarget(git, Options(), new FixedTime());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => target.AnchorAsync(_digest, Ct));

        Assert.Equal("git push failed with exit code 1.", ex.Message);
    }

    [Fact]
    public async Task Verify_accepts_a_commit_that_holds_the_digest_file()
    {
        var git = new FakeGitRunner();
        var target = new OffsiteGitTarget(git, Options(), new FixedTime());
        var receipt = await target.AnchorAsync(_digest, Ct);

        Assert.True(await target.VerifyAsync(receipt, _digest, Ct));
        Assert.Equal(["show", $"{_commit}:anchor-{_hex}.txt"], git.Calls.Last().Arguments);
    }

    [Fact]
    public async Task Verify_rejects_a_missing_file_a_mismatched_digest_or_a_bad_commit_id()
    {
        var git = new FakeGitRunner();
        var target = new OffsiteGitTarget(git, Options(), new FixedTime());
        var receipt = await target.AnchorAsync(_digest, Ct);
        var other = Enumerable.Repeat((byte)0xCD, 32).ToArray();

        git.ShowExitCode = 128;
        Assert.False(await target.VerifyAsync(receipt, _digest, Ct));

        git.ShowExitCode = 0;
        git.ShowOutput = "ffffffff\n";
        Assert.False(await target.VerifyAsync(receipt, _digest, Ct));

        Assert.False(await target.VerifyAsync(new AuditAnchorReceipt("offsite-git", Encoding.ASCII.GetBytes("not-a-commit"), receipt.AnchoredAt), _digest, Ct));
        Assert.False(await target.VerifyAsync(receipt, other, Ct));
    }

    [Fact]
    public void A_target_with_no_configuration_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new OffsiteGitTarget(new FakeGitRunner(), new OffsiteGitTargetOptions(), new FixedTime()));
    }

    [Theory]
    [InlineData("-rf")]
    [InlineData("origin --mirror")]
    [InlineData("")]
    public void A_remote_or_branch_that_could_read_as_an_option_is_refused(string remote)
    {
        Assert.Throws<ArgumentException>(() => new OffsiteGitTarget(new FakeGitRunner(), new OffsiteGitTargetOptions { LocalPath = _dir, RemoteName = remote }, new FixedTime()));
        Assert.Throws<ArgumentException>(() => new OffsiteGitTarget(new FakeGitRunner(), new OffsiteGitTargetOptions { LocalPath = _dir, Branch = remote }, new FixedTime()));
    }

    private static string Join(IReadOnlyList<string> arguments) => string.Join(' ', arguments);

    /// <summary>Records each call and answers from fixed settings. The commit id and show output are configurable.</summary>
    private sealed class FakeGitRunner : IGitRunner
    {
        public List<(string WorkingDirectory, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public int PushExitCode { get; init; }

        public int ShowExitCode { get; set; }

        public string? ShowOutput { get; set; }

        public Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Calls.Add((workingDirectory, arguments));
            var result = arguments[0] switch
            {
                "push" => new GitResult(PushExitCode, string.Empty),
                "rev-parse" => new GitResult(0, _commit + "\n"),
                "show" => new GitResult(ShowExitCode, ShowOutput ?? _hex + "\n"),
                _ => new GitResult(0, string.Empty),
            };
            return Task.FromResult(result);
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    }
}
