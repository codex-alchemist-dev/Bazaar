using System.Text;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Responder;

namespace Bazaar.Kodo.Tests.Responder;

public sealed class RunbookResumeTests
{
    [Fact]
    public async Task RestartMidRunResumesFromThePersistedStateWithoutRepeatingFinishedSteps()
    {
        Harness h = new();
        h.Gate.Decide = request => request.StepId == "do_a" ? ApprovalDecision.Approved : ApprovalDecision.Pending;
        await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None);
        Assert.Equal(["pre", "do_a"], h.Runner.Calls.Select(c => c.StepId));

        // A new executor and new fakes over the same stored bytes stand in for a restarted process.
        Harness restarted = new();
        foreach ((string key, byte[] bytes) in h.Store.Files)
        {
            restarted.Store.Files[key] = bytes;
        }

        restarted.ApproveAll();
        RunbookRunState? state = await restarted.Executor().ResumeAsync("run1", CancellationToken.None);

        Assert.Equal(RunbookPhase.Succeeded, state!.Phase);
        Assert.Equal(["do_b", "verify"], restarted.Runner.Calls.Select(c => c.StepId));
        Assert.Contains("ap-do_a", state.Approvals.Select(a => a.ApprovalId));
        Assert.Single(restarted.Gate.Requests);
    }

    [Fact]
    public async Task ActionThatWasStartedButNeverRecordedIsNotRunAgain()
    {
        Harness h = new();
        h.ApproveAll();
        await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None);

        // Rebuild the state as it was saved just before do_b's result: step in flight.
        RunbookRunState done = RunbookRunStateCodecAccess.Load(h.Store.Files["run1"]);
        RunbookRunState crashed = done with { Phase = RunbookPhase.Running, Cause = null, Section = RunbookSection.Steps, StepIndex = 1, Attempts = 1, InFlight = true };
        Harness restarted = new();
        restarted.ApproveAll();
        restarted.Store.Files["run1"] = RunbookRunStateCodec.ToUtf8Bytes(crashed);

        RunbookRunState? state = await restarted.Executor().ResumeAsync("run1", CancellationToken.None);

        Assert.Equal(RunbookPhase.NeedsHuman, state!.Phase);
        Assert.Equal("interrupted_action", state.Cause);
        Assert.Empty(restarted.Runner.Calls);
    }

    [Fact]
    public async Task ReadOnlyStepInterruptedMidFlightIsSafelyRepeated()
    {
        Harness h = new();
        h.ApproveAll();
        await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None);
        RunbookRunState done = RunbookRunStateCodecAccess.Load(h.Store.Files["run1"]);
        RunbookRunState crashed = done with { Phase = RunbookPhase.Running, Cause = null, Section = RunbookSection.Steps, StepIndex = 2, Attempts = 1, InFlight = true };
        Harness restarted = new();
        restarted.ApproveAll();
        restarted.Store.Files["run1"] = RunbookRunStateCodec.ToUtf8Bytes(crashed);

        RunbookRunState? state = await restarted.Executor().ResumeAsync("run1", CancellationToken.None);

        Assert.Equal(RunbookPhase.Succeeded, state!.Phase);
        Assert.Equal(["verify"], restarted.Runner.Calls.Select(c => c.StepId));
    }

    [Fact]
    public async Task FinalRunIsReturnedAsItIsAndNeverRunsAgain()
    {
        Harness h = new();
        h.ApproveAll();
        await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None);
        int calls = h.Runner.Calls.Count;

        RunbookRunState? state = await h.Executor().ResumeAsync("run1", CancellationToken.None);

        Assert.Equal(RunbookPhase.Succeeded, state!.Phase);
        Assert.Equal(calls, h.Runner.Calls.Count);
    }

    [Fact]
    public async Task ResumeOfAnUnknownRunReturnsNull()
    {
        Harness h = new();

        Assert.Null(await h.Executor().ResumeAsync("missing", CancellationToken.None));
    }

    [Fact]
    public async Task ResumeRefusesWhenTheStoredDigestNoLongerMatchesTheCatalogue()
    {
        Harness h = new();
        await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None);

        // The catalogue now holds a different, signed definition under the same id and version? That is refused at add time,
        // so model tampering by editing the stored digest instead.
        RunbookRunState stored = RunbookRunStateCodecAccess.Load(h.Store.Files["run1"]);
        h.Store.Files["run1"] = RunbookRunStateCodec.ToUtf8Bytes(stored with { Digest = new string('a', 64) });

        RunbookRunState? state = await h.Executor().ResumeAsync("run1", CancellationToken.None);

        Assert.Equal(RunbookPhase.NeedsHuman, state!.Phase);
        Assert.Equal("runbook_tampered", state.Cause);
        Assert.Equal(["pre"], h.Runner.Calls.Select(c => c.StepId));
    }

    [Fact]
    public async Task ResumeRefusesWhenTheRunbookIsNoLongerInTheCatalogue()
    {
        Harness h = new();
        await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None);
        Harness other = new(ResponderTestKit.Demo(version: 2));
        other.Store.Files["run1"] = h.Store.Files["run1"];

        RunbookRunState? state = await other.Executor().ResumeAsync("run1", CancellationToken.None);

        Assert.Equal("runbook_unknown", state!.Cause);
        Assert.Equal(RunbookPhase.NeedsHuman, state.Phase);
    }

    [Fact]
    public async Task ResumeWithAnOutOfRangeStepIndexStopsForAHuman()
    {
        Harness h = new();
        await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None);
        RunbookRunState stored = RunbookRunStateCodecAccess.Load(h.Store.Files["run1"]);
        h.Store.Files["run1"] = RunbookRunStateCodec.ToUtf8Bytes(stored with { Phase = RunbookPhase.Running, Section = RunbookSection.Steps, StepIndex = 9 });

        RunbookRunState? state = await h.Executor().ResumeAsync("run1", CancellationToken.None);

        Assert.Equal("state_invalid", state!.Cause);
    }
}

internal static class RunbookRunStateCodecAccess
{
    public static RunbookRunState Load(byte[] bytes)
    {
        Assert.True(RunbookRunStateCodec.TryParse(bytes, out RunbookRunState? state));
        return state!;
    }
}

public sealed class RunbookRefusalTests
{
    [Fact]
    public async Task UnknownRunbookIsRefusedAndJournalled()
    {
        Harness h = new();
        RunbookStartRequest request = ResponderTestKit.Start() with { RunbookId = "other.fix" };

        RunbookStartResult result = await h.Executor().StartAsync(request, CancellationToken.None);

        Assert.False(result.Started);
        Assert.Equal("unknown_runbook", result.Refusal);
        Assert.Contains("run_refused", h.Journal.Kinds);
        Assert.Empty(h.Store.Files);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task UnknownVersionIsRefused()
    {
        Harness h = new();

        RunbookStartResult result = await h.Executor().StartAsync(ResponderTestKit.Start() with { RunbookVersion = 7 }, CancellationToken.None);

        Assert.Equal("unknown_runbook", result.Refusal);
    }

    [Fact]
    public async Task ExpectedDigestThatDiffersMeansTheRunbookChangedAndIsRefused()
    {
        Harness h = new();

        RunbookStartResult result = await h.Executor().StartAsync(ResponderTestKit.Start() with { ExpectedDigest = new string('b', 64) }, CancellationToken.None);

        Assert.Equal("tampered_runbook", result.Refusal);
        Assert.Empty(h.Runner.Calls);
    }

    [Fact]
    public async Task ExpectedDigestThatMatchesIsAccepted()
    {
        Harness h = new();
        h.ApproveAll();
        string digest = RunbookCanonical.Digest(h.Definition);

        RunbookStartResult result = await h.Executor().StartAsync(ResponderTestKit.Start() with { ExpectedDigest = digest }, CancellationToken.None);

        Assert.True(result.Started);
    }

    [Fact]
    public void CatalogueRefusesARunbookWithATamperedBodyOrASignatureFromAnotherKey()
    {
        RunbookDefinition signed = ResponderTestKit.Demo();
        byte[] signature = ResponderTestKit.Sign(signed);
        RunbookCatalogue catalogue = new(new Bazaar.Kodo.Runtime.RsaPkcs1Sha256Verifier(), ResponderTestKit.Key.ExportSubjectPublicKeyInfo());

        RunbookDefinition tampered = signed with { Title = "Changed after signing" };
        Assert.Equal("bad_signature", catalogue.TryAdd(tampered, signature));

        using System.Security.Cryptography.RSA other = System.Security.Cryptography.RSA.Create(2048);
        byte[] foreign = other.SignData(RunbookCanonical.Bytes(signed), System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        Assert.Equal("bad_signature", catalogue.TryAdd(signed, foreign));
        Assert.Equal("bad_signature", catalogue.TryAdd(signed, []));

        Assert.Equal(0, catalogue.Count);
        Assert.Null(catalogue.TryAdd(signed, signature));
        Assert.Equal(1, catalogue.Count);
    }

    [Fact]
    public void CatalogueNeverHoldsTwoDifferentBodiesForOneVersion()
    {
        RunbookDefinition v1 = ResponderTestKit.Demo();
        RunbookDefinition changed = v1 with { Title = "Another body" };
        RunbookCatalogue catalogue = ResponderTestKit.Catalogue(v1);

        Assert.Equal("version_conflict", catalogue.TryAdd(changed, ResponderTestKit.Sign(changed)));
        Assert.Null(catalogue.TryAdd(v1, ResponderTestKit.Sign(v1)));
        Assert.Equal(1, catalogue.Count);
    }

    [Fact]
    public void CatalogueRefusesInvalidDefinitionsEvenWhenSigned()
    {
        RunbookCatalogue catalogue = new(new Bazaar.Kodo.Runtime.RsaPkcs1Sha256Verifier(), ResponderTestKit.Key.ExportSubjectPublicKeyInfo());
        RunbookDefinition noVerify = ResponderTestKit.Demo() with
        {
            Steps = [ResponderTestKit.Step("do_a", RunbookStepKind.Action, "demo.do_a")],
        };
        RunbookDefinition unknownControl = ResponderTestKit.Demo() with { RequiredControl = "kodo.root" };
        RunbookDefinition highWithoutStepUp = ResponderTestKit.Demo(RunbookRisk.High, RunbookApprovalTier.Approval);
        RunbookDefinition dupIds = ResponderTestKit.Demo() with
        {
            Steps = [ResponderTestKit.Step("x", RunbookStepKind.Verify, "demo.v"), ResponderTestKit.Step("x", RunbookStepKind.Verify, "demo.w")],
        };

        Assert.Equal("missing_verify_step", catalogue.TryAdd(noVerify, ResponderTestKit.Sign(noVerify)));
        Assert.Equal("invalid_header", catalogue.TryAdd(unknownControl, ResponderTestKit.Sign(unknownControl)));
        Assert.Equal("high_risk_needs_step_up", catalogue.TryAdd(highWithoutStepUp, ResponderTestKit.Sign(highWithoutStepUp)));
        Assert.Equal("duplicate_step_id", catalogue.TryAdd(dupIds, ResponderTestKit.Sign(dupIds)));
    }

    [Fact]
    public async Task MissingControlFrozenRecoveringAndDuplicateRunsAreRefused()
    {
        Harness h = new();
        h.ApproveAll();
        RunbookExecutor executor = h.Executor();

        RunbookStartResult noControl = await executor.StartAsync(ResponderTestKit.Start() with { GrantedControls = new HashSet<string>() }, CancellationToken.None);
        RunbookStartResult frozen = await executor.StartAsync(ResponderTestKit.Start(state: KodoState.Frozen), CancellationToken.None);
        RunbookStartResult recovering = await executor.StartAsync(ResponderTestKit.Start(state: KodoState.Recovering), CancellationToken.None);
        RunbookStartResult first = await executor.StartAsync(ResponderTestKit.Start(), CancellationToken.None);
        RunbookStartResult duplicate = await executor.StartAsync(ResponderTestKit.Start(), CancellationToken.None);

        Assert.Equal("missing_control", noControl.Refusal);
        Assert.Equal("incident_frozen", frozen.Refusal);
        Assert.Equal("recovery_running", recovering.Refusal);
        Assert.True(first.Started);
        Assert.Equal("duplicate_run", duplicate.Refusal);
    }

    [Fact]
    public async Task InvalidRunIdStarterOrInputsAreRefused()
    {
        Harness h = new();
        RunbookExecutor executor = h.Executor();

        Assert.Equal("invalid_run_id", (await executor.StartAsync(ResponderTestKit.Start("bad id!"), CancellationToken.None)).Refusal);
        Assert.Equal("invalid_request", (await executor.StartAsync(ResponderTestKit.Start() with { Starter = "has space" }, CancellationToken.None)).Refusal);
        Assert.Equal(
            "invalid_request",
            (await executor.StartAsync(
                ResponderTestKit.Start() with { Inputs = new Dictionary<string, string> { ["log"] = "free text with spaces" } },
                CancellationToken.None)).Refusal);
    }

    [Fact]
    public void CanonicalBytesIgnoreParameterOrderAndDigestIsStable()
    {
        RunbookStep a = ResponderTestKit.Step("s", RunbookStepKind.Check, "demo.pre") with
        {
            Parameters = new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" },
        };
        RunbookStep b = a with { Parameters = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" } };
        RunbookDefinition d1 = ResponderTestKit.Demo() with { Preconditions = [a] };
        RunbookDefinition d2 = ResponderTestKit.Demo() with { Preconditions = [b] };

        Assert.Equal(RunbookCanonical.Digest(d1), RunbookCanonical.Digest(d2));
        Assert.Equal(64, RunbookCanonical.Digest(d1).Length);
        Assert.NotEqual(RunbookCanonical.Digest(d1), RunbookCanonical.Digest(ResponderTestKit.Demo()));
        Assert.StartsWith("{\"id\":\"demo.fix\"", Encoding.UTF8.GetString(RunbookCanonical.Bytes(d1)), StringComparison.Ordinal);
    }
}

public sealed class RunbookStateCodecTests
{
    [Fact]
    public async Task StateRoundTripsAsCamelCaseJsonWithWordEnums()
    {
        Harness h = new();
        h.ApproveAll();
        await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None);
        byte[] bytes = h.Store.Files["run1"];
        string json = Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"runbookId\":\"demo.fix\"", json);
        Assert.Contains("\"phase\":\"Succeeded\"", json);
        Assert.True(RunbookRunStateCodec.TryParse(bytes, out RunbookRunState? parsed));
        Assert.Equal(RunbookPhase.Succeeded, parsed!.Phase);
        Assert.Equal(bytes, RunbookRunStateCodec.ToUtf8Bytes(parsed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"version\":2}")]
    public void MalformedOrWrongVersionStateIsRejected(string text)
    {
        Assert.False(RunbookRunStateCodec.TryParse(Encoding.UTF8.GetBytes(text), out RunbookRunState? state));
        Assert.Null(state);
    }

    [Fact]
    public async Task UnknownMembersOversizeAndOutOfRangeValuesAreRejected()
    {
        Harness h = new();
        await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None);
        string json = Encoding.UTF8.GetString(h.Store.Files["run1"]);

        string withExtra = json.TrimEnd('}') + ",\"extra\":1}";
        Assert.False(RunbookRunStateCodec.TryParse(Encoding.UTF8.GetBytes(withExtra), out _));
        Assert.False(RunbookRunStateCodec.TryParse(new byte[RunbookRunStateCodec.MaxBytes + 1], out _));
        Assert.False(RunbookRunStateCodec.TryParse(Encoding.UTF8.GetBytes(json.Replace("\"stepIndex\":0", "\"stepIndex\":-3", StringComparison.Ordinal)), out _));
        Assert.False(RunbookRunStateCodec.TryParse(Encoding.UTF8.GetBytes(json.Replace("\"phase\":\"AwaitingApproval\"", "\"phase\":\"Bogus\"", StringComparison.Ordinal)), out _));
    }

    [Fact]
    public async Task CorruptStoredStateMakesTheStoreFailAndStartRefuses()
    {
        Harness h = new();
        h.Store.Files["run1"] = Encoding.UTF8.GetBytes("garbage");

        RunbookStartResult result = await h.Executor().StartAsync(ResponderTestKit.Start(), CancellationToken.None);

        Assert.Equal("state_not_readable", result.Refusal);
    }
}
