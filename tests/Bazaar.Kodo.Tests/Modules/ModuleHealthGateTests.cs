using Bazaar.Kodo.Modules;
using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Tests.Modules;

public sealed class ModuleHealthGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ProbeIdFollowsTheCatalogue()
    {
        Assert.Equal("module.kodo.health", ModuleHealthGate.ProbeIdFor("kodo"));
    }

    [Fact]
    public async Task MissingContractIsUnknownWithContractMissing()
    {
        ProbeResult result = await ModuleHealthGate.EvaluateAsync("kodo", () => null, Ct);

        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(ProbeCauses.ContractMissing, result.Cause);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ContractThatThrowsOnLoadIsUnknownWithContractError()
    {
        var error = new InvalidOperationException("load failed");

        ProbeResult result = await ModuleHealthGate.EvaluateAsync("kodo", () => throw error, Ct);

        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(ProbeCauses.ContractError, result.Cause);
        Assert.Same(error, result.Error);
    }

    [Fact]
    public async Task ContractThatThrowsInCheckIsUnknownWithContractError()
    {
        var error = new InvalidOperationException("check failed");
        var contract = new FakeModuleHealthProbe("kodo", _ => throw error);

        ProbeResult result = await ModuleHealthGate.EvaluateAsync("kodo", () => contract, Ct);

        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(ProbeCauses.ContractError, result.Cause);
        Assert.Same(error, result.Error);
    }

    [Fact]
    public async Task ContractThatFaultsAsyncIsUnknownWithContractError()
    {
        var error = new InvalidOperationException("async fault");
        var contract = new FakeModuleHealthProbe(
            "kodo",
            async _ =>
            {
                await Task.Yield();
                throw error;
            });

        ProbeResult result = await ModuleHealthGate.EvaluateAsync("kodo", () => contract, Ct);

        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(ProbeCauses.ContractError, result.Cause);
        Assert.Same(error, result.Error);
    }

    [Fact]
    public async Task ContractThatReturnsNoResultIsUnknownWithContractError()
    {
        var contract = new FakeModuleHealthProbe("kodo", _ => new ValueTask<ProbeResult>((ProbeResult)null!));

        ProbeResult result = await ModuleHealthGate.EvaluateAsync("kodo", () => contract, Ct);

        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(ProbeCauses.ContractError, result.Cause);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ContractWithAnotherModuleIdIsUnknownWithContractError()
    {
        var contract = new FakeModuleHealthProbe("other");

        ProbeResult result = await ModuleHealthGate.EvaluateAsync("kodo", () => contract, Ct);

        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(ProbeCauses.ContractError, result.Cause);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ContractWithAnotherProbeIdIsUnknownWithContractError()
    {
        var contract = new FakeModuleHealthProbe("kodo", probeId: "module.kodo.live");

        ProbeResult result = await ModuleHealthGate.EvaluateAsync("kodo", () => contract, Ct);

        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(ProbeCauses.ContractError, result.Cause);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData(ProbeStatus.Green, null)]
    [InlineData(ProbeStatus.Amber, ProbeCauses.DiskUsageWarn)]
    [InlineData(ProbeStatus.Red, ProbeCauses.JournalNotWritable)]
    public async Task HealthyContractResultPassesThroughUnchanged(ProbeStatus status, string? cause)
    {
        var reported = new ProbeResult(status, cause);
        var contract = new FakeModuleHealthProbe("kodo", _ => ValueTask.FromResult(reported));

        ProbeResult result = await ModuleHealthGate.EvaluateAsync("kodo", () => contract, Ct);

        Assert.Equal(reported, result);
    }

    [Fact]
    public async Task CancellationPropagatesInsteadOfBecomingContractError()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var contract = new FakeModuleHealthProbe("kodo", ct => throw new OperationCanceledException(ct));

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await ModuleHealthGate.EvaluateAsync("kodo", () => contract, cts.Token));
    }

    [Fact]
    public async Task OperationCanceledWithoutCancellationIsContractError()
    {
        var contract = new FakeModuleHealthProbe("kodo", _ => throw new OperationCanceledException());

        ProbeResult result = await ModuleHealthGate.EvaluateAsync("kodo", () => contract, Ct);

        Assert.Equal(ProbeCauses.ContractError, result.Cause);
    }

    [Fact]
    public async Task EmptyModuleIdIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ModuleHealthGate.EvaluateAsync(" ", () => null, Ct));
    }
}
