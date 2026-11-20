using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Modules;

/// <summary>
/// The checks a module's contract test runs (doc 46 section 10.3, first slice). Pure: it returns the violations
/// and does not assert, so the xUnit base class in <c>Bazaar.Kodo.Testing</c> and plain tests can both use it.
/// </summary>
public static class ModuleHealthContractCheck
{
    /// <summary>Runs the first-slice checks and returns one message per violation. An empty list passes.</summary>
    public static async ValueTask<IReadOnlyList<string>> VerifyAsync(
        string expectedModuleId,
        Func<IModuleHealthProbe?> loadContract,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedModuleId);
        ArgumentNullException.ThrowIfNull(loadContract);

        var violations = new List<string>();
        IModuleHealthProbe? contract;
        try
        {
            contract = loadContract();
        }
        catch (Exception error)
        {
            violations.Add($"The health contract could not be created: {error.Message}");
            return violations;
        }

        if (contract is null)
        {
            violations.Add("The module has no health contract.");
            return violations;
        }

        string firstModuleId = contract.ModuleId;
        if (firstModuleId != expectedModuleId)
        {
            violations.Add($"ModuleId is '{firstModuleId}', expected '{expectedModuleId}'.");
        }

        if (contract.ModuleId != firstModuleId)
        {
            violations.Add("ModuleId is not stable across reads.");
        }

        string expectedProbeId = ModuleHealthGate.ProbeIdFor(expectedModuleId);
        if (contract.ProbeId != expectedProbeId)
        {
            violations.Add($"ProbeId is '{contract.ProbeId}', expected '{expectedProbeId}'.");
        }

        // The gate turns a throw, a null result or a wrong identity into contract_error. Report that as a violation.
        ProbeResult gated = await ModuleHealthGate.EvaluateAsync(expectedModuleId, () => contract, ct);
        if (gated.Cause == ProbeCauses.ContractError)
        {
            violations.Add($"CheckAsync failed: {gated.Error?.Message ?? "no result"}");
        }

        return violations;
    }
}
