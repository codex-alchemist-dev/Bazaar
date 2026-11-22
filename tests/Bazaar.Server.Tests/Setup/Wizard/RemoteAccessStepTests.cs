using Bazaar.Conduit.Settings;
using Bazaar.Server.Modules.Settings;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;

namespace Bazaar.Server.Tests.Setup.Wizard;

public sealed class RemoteAccessStepTests
{
    private const string _kekRef = "dpapi:C:\\ProgramData\\Bazaar\\kek.bin";
    private const string _anchor = "https://anchor.example.invalid/bazaar-audit";
    private const string _backup = "file:/mnt/backup/bazaar";

    private static string[] Answers(params string[] remote) =>
        ["Example Studio", "", "Owner", "", "dpapi", _kekRef, "", _anchor, _backup, "", "", "", "", .. remote, ""];

    private static WizardQuestion Question(string id) =>
        RemoteAccessStep.Create().Questions.Single(question => question.Id == id);

    [Fact]
    public void The_step_is_the_last_one_and_is_named_remote_access()
    {
        var steps = SetupWizardSteps.Create();

        Assert.Equal(RemoteAccessStep.StepId, steps[^1].Id);
        Assert.Equal("Remote access", steps[^1].Title);
    }

    [Fact]
    public async Task Answering_nothing_writes_no_remote_setting()
    {
        var result = await new SetupWizardEngine(new ScriptedWizardPrompter(Answers("", "", "")), new CatalogBackend())
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result.SetArguments, flag => flag.Contains("conduit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Yes_with_a_quick_tunnel_writes_the_conduit_settings()
    {
        var result = await new SetupWizardEngine(new ScriptedWizardPrompter(Answers("yes", "quick", "")), new CatalogBackend())
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Contains("settings.conduit.cloudflared.enabled=true", result.SetArguments);
        Assert.Contains("settings.conduit.cloudflared.mode=quick", result.SetArguments);
        Assert.DoesNotContain(result.SetArguments, flag => flag.Contains("hostname", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Yes_with_a_named_tunnel_writes_the_host_name()
    {
        var result = await new SetupWizardEngine(new ScriptedWizardPrompter(Answers("yes", "named", "bazaar.example.com")), new CatalogBackend())
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Contains("settings.conduit.cloudflared.hostname=bazaar.example.com", result.SetArguments);
    }

    [Theory]
    [InlineData("remote.cloudflaredMode", "fast")]
    [InlineData("remote.cloudflaredHostname", "not a host!")]
    public void A_bad_answer_is_refused(string id, string text) =>
        Assert.NotNull(Question(id).Validate!(text));

    [Fact]
    public void No_question_asks_for_a_tunnel_token()
    {
        Assert.DoesNotContain(
            RemoteAccessStep.Create().Questions,
            question => question.Kind == WizardQuestionKind.Secret || question.PlanPath?.Contains("token", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task A_yes_is_held_for_owner_approval_by_the_real_plan_rules()
    {
        var yes = await new SetupWizardEngine(new ScriptedWizardPrompter(Answers("yes", "quick", "")), new CatalogBackend())
            .RunAsync(TestContext.Current.CancellationToken);
        var none = await new SetupWizardEngine(new ScriptedWizardPrompter(Answers("", "", "")), new CatalogBackend())
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal((int)SetupExitCode.Held, yes.ExitCode);
        Assert.Equal((int)SetupExitCode.Ok, none.ExitCode);
    }

    /// <summary>The real overlay and loader over a catalog that includes the Conduit keys, with the apply recorded instead of run.</summary>
    private sealed class CatalogBackend : ISetupWizardBackend
    {
        private static readonly RegistrySetupSettingCatalog _catalog = new(
            Bazaar.Server.Settings.BuiltInSettingRegistries.WithContributions([new ModuleManagerSettingContribution(), new ConduitSettingContribution()]));

        private static readonly IReadOnlyDictionary<string, string?> _noEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal);

        public SetupWizardReview DryRun(IReadOnlyList<string> setArguments)
        {
            var merged = SetupDoorOverlay.Merge(null, setArguments, _noEnvironment, _catalog);
            if (merged.MergedJson is null)
            {
                return new SetupWizardReview((int)SetupExitCode.InvalidInput, null, ["Error"]);
            }

            var load = SetupPlanLoader.Load(merged.MergedJson, _catalog);
            return new SetupWizardReview((int)load.ExitCode, load.Plan is null ? null : load.PlanHash, [$"Plan hash: {load.PlanHash}"]);
        }

        public Task<SetupWizardReview> ApplyAsync(IReadOnlyList<string> setArguments, string planHash, SetupEntryPath entryPath, CancellationToken cancellationToken) =>
            Task.FromResult(new SetupWizardReview((int)SetupExitCode.Ok, planHash, ["applied"]));
    }
}
