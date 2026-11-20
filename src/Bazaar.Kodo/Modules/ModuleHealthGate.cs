using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Modules;

/// <summary>
/// The loader gate for the module health contract (doc 46 section 10). A module with no contract, a contract
/// that throws, or a contract that reports under another identity is unknown, never green.
/// </summary>
public static class ModuleHealthGate
{
    /// <summary>The probe id the catalogue gives a module's health probe (doc 46 section 9.1).</summary>
    public static string ProbeIdFor(string moduleId) => $"module.{moduleId}.health";

    /// <summary>
    /// Loads the module's contract and reports its health. <paramref name="loadContract"/> may return null
    /// (no contract) or throw (a broken contract); both give unknown. Cancellation is not a contract failure
    /// and propagates to the caller.
    /// </summary>
    public static async ValueTask<ProbeResult> EvaluateAsync(
        string moduleId,
        Func<IModuleHealthProbe?> loadContract,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentNullException.ThrowIfNull(loadContract);

        try
        {
            IModuleHealthProbe? contract = loadContract();
            if (contract is null)
            {
                return ProbeResult.ContractMissing();
            }

            if (contract.ModuleId != moduleId || contract.ProbeId != ProbeIdFor(moduleId))
            {
                return ProbeResult.ContractError(new InvalidOperationException(
                    $"Health contract identity {contract.ModuleId}/{contract.ProbeId} does not match module {moduleId}."));
            }

            ProbeResult? result = await contract.CheckAsync(ct);
            return result ?? ProbeResult.ContractError(new InvalidOperationException("Health contract returned no result."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return ProbeResult.ContractError(error);
        }
    }
}
