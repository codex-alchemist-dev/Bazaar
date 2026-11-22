using Xunit;

namespace Bazaar.Server.Providers.ClaudeCode.Tests;

public sealed class GuidedSetupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-gs-" + Guid.NewGuid().ToString("N"));
    private readonly string _binary;
    private readonly string _login;

    public GuidedSetupTests()
    {
        _login = Path.Combine(_dir, "login");
        Directory.CreateDirectory(_login);
        _binary = Path.Combine(_dir, OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        File.WriteAllText(_binary, "x");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ClaudeCodeSetupRequest Request(bool accept = true) => new()
    {
        AcceptedTerms = accept,
        BinaryPath = _binary,
        ConfigDir = _login,
        LoginOwner = "user:owner",
        Models = ["claude-haiku-5-5"],
    };

    private static Task<ClaudeCodeSetupReport> Go(ClaudeCodeGuidedSetup setup, ClaudeCodeSetupRequest request) =>
        setup.RunAsync(request, TestContext.Current.CancellationToken);

    private static ClaudeCodeGuidedSetup Setup(string path = "") => new(new InMemoryClaudeCodeAcknowledgementStore(), pathVariable: path);

    [Fact]
    public async Task Complete_request_is_ready_and_stamps_the_acknowledgement()
    {
        var report = await Go(Setup(), Request());
        Assert.True(report.Ready);
        Assert.All(report.Steps, s => Assert.Equal(ClaudeCodeStepStatus.Ok, s.Status));
        Assert.NotNull(report.Settings!.TermsAcknowledgedAt);
        Assert.True(report.Settings.Enabled);
        Assert.Empty(new ClaudeCodeOptionsValidator().Validate(null, report.Settings).Failures ?? []);
    }

    [Fact]
    public async Task Terms_not_accepted_stops_everything_else()
    {
        var report = await Go(Setup(), Request(accept: false));
        Assert.False(report.Ready);
        Assert.Equal(ClaudeCodeStepStatus.Failed, report.Steps[0].Status);
        Assert.All(report.Steps.Skip(1), s => Assert.Equal(ClaudeCodeStepStatus.Skipped, s.Status));
    }

    [Fact]
    public async Task Acceptance_is_remembered_for_the_next_run()
    {
        var setup = Setup();
        await Go(setup, Request());
        Assert.True((await Go(setup, Request(accept: false))).Ready);
    }

    [Fact]
    public async Task Missing_login_folder_names_the_sign_in_command()
    {
        var report = await Go(Setup(), Request() with { ConfigDir = Path.Combine(_dir, "nope") });
        var step = report.Steps.Single(s => s.Status == ClaudeCodeStepStatus.Failed);
        Assert.Equal(ClaudeCodeSetupStep.LoginFolder, step.Step);
        Assert.Contains("CLAUDE_CONFIG_DIR=", step.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Binary_is_found_on_path_and_missing_binary_fails()
    {
        Assert.True((await Go(Setup(_dir), Request() with { BinaryPath = "" })).Ready);
        var missing = await Go(Setup(Path.Combine(_dir, "empty")), Request() with { BinaryPath = "" });
        Assert.Equal(ClaudeCodeSetupStep.Binary, missing.Steps.Single(s => s.Status == ClaudeCodeStepStatus.Failed).Step);
    }

    [Fact]
    public async Task Login_owner_must_be_a_user_principal()
    {
        var report = await Go(Setup(), Request() with { LoginOwner = "owner" });
        Assert.Equal(ClaudeCodeSetupStep.LoginOwner, report.Steps.Single(s => s.Status == ClaudeCodeStepStatus.Failed).Step);
    }
}
