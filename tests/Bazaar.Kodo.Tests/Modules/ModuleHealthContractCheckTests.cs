using Bazaar.Kodo.Modules;

namespace Bazaar.Kodo.Tests.Modules;

public sealed class ModuleHealthContractCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WellFormedContractPassesEveryCheck()
    {
        var contract = new FakeModuleHealthProbe("kodo");

        IReadOnlyList<string> violations = await ModuleHealthContractCheck.VerifyAsync("kodo", () => contract, Ct);

        Assert.Empty(violations);
    }

    [Fact]
    public async Task MissingContractIsAViolation()
    {
        IReadOnlyList<string> violations = await ModuleHealthContractCheck.VerifyAsync("kodo", () => null, Ct);

        Assert.Single(violations);
        Assert.Contains("no health contract", violations[0]);
    }

    [Fact]
    public async Task ContractThatCannotBeCreatedIsAViolation()
    {
        IReadOnlyList<string> violations = await ModuleHealthContractCheck.VerifyAsync(
            "kodo",
            () => throw new InvalidOperationException("no factory"),
            Ct);

        Assert.Single(violations);
        Assert.Contains("could not be created", violations[0]);
    }

    [Fact]
    public async Task WrongModuleIdIsAViolation()
    {
        var contract = new FakeModuleHealthProbe("other");

        IReadOnlyList<string> violations = await ModuleHealthContractCheck.VerifyAsync("kodo", () => contract, Ct);

        Assert.Contains(violations, v => v.StartsWith("ModuleId is 'other'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WrongProbeIdIsAViolation()
    {
        var contract = new FakeModuleHealthProbe("kodo", probeId: "module.kodo.live");

        IReadOnlyList<string> violations = await ModuleHealthContractCheck.VerifyAsync("kodo", () => contract, Ct);

        Assert.Contains(violations, v => v.StartsWith("ProbeId is 'module.kodo.live'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnstableModuleIdIsAViolation()
    {
        var contract = new DriftingIdModuleHealthProbe();

        IReadOnlyList<string> violations = await ModuleHealthContractCheck.VerifyAsync("kodo", () => contract, Ct);

        Assert.Contains(violations, v => v.Contains("not stable across reads", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThrowingCheckIsAViolation()
    {
        var contract = new FakeModuleHealthProbe("kodo", _ => throw new InvalidOperationException("boom"));

        IReadOnlyList<string> violations = await ModuleHealthContractCheck.VerifyAsync("kodo", () => contract, Ct);

        Assert.Contains(violations, v => v.StartsWith("CheckAsync failed: boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyExpectedModuleIdIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ModuleHealthContractCheck.VerifyAsync("", () => null, Ct));
    }
}
