using Bazaar.Kodo.Modules;
using Xunit;

namespace Bazaar.Kodo.Testing;

/// <summary>
/// The contract test base a module's test project inherits (doc 46 section 10.3, first slice). It runs the
/// identity, catalogue probe id and stable module id checks, and that <c>CheckAsync</c> completes without throwing.
/// The remaining section 10.3 checks (I/O fakes, budgets, self-test residue) come in later slices.
/// </summary>
public abstract class ModuleHealthContractTests<TModule>
    where TModule : class, IModuleHealthProbe
{
    /// <summary>The module's stable id, for example <c>kodo</c>.</summary>
    protected abstract string ExpectedModuleId { get; }

    /// <summary>Builds the module's health contract as the loader would load it.</summary>
    protected abstract TModule CreateContract();

    [Fact]
    public void ContractIsPresent()
    {
        Assert.NotNull(CreateContract());
    }

    [Fact]
    public async Task ContractPassesFirstSliceChecks()
    {
        IReadOnlyList<string> violations = await ModuleHealthContractCheck.VerifyAsync(
            ExpectedModuleId,
            () => CreateContract(),
            TestContext.Current.CancellationToken);

        if (violations.Count > 0)
        {
            Assert.Fail("Module health contract violations: " + string.Join("; ", violations));
        }
    }
}
