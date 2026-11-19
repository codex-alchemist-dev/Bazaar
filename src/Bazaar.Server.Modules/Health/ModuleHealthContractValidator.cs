namespace Bazaar.Server.Modules.Health;

/// <summary>The four probes every module must declare (doc 42 section 11, D10).</summary>
public enum ModuleHealthProbe
{
    Liveness,
    Readiness,
    Dependencies,
    SelfTest,
}

/// <summary>What a module's manifest declares for the health contract (the manifest's <c>health</c> block).</summary>
/// <param name="ModuleId">The module's id.</param>
/// <param name="DeclaredProbes">The probes the manifest declares. Null means none.</param>
public sealed record ModuleHealthDeclaration(string ModuleId, IReadOnlyCollection<ModuleHealthProbe>? DeclaredProbes);

/// <summary>The result of validating a declaration. A module with missing probes does not load (doc 46 section 10).</summary>
/// <param name="IsValid">True when the id is set and all four probes are declared.</param>
/// <param name="MissingProbes">The probes not declared, in enum order. Empty when valid.</param>
/// <param name="Problems">Human-readable problems, one per failed rule. Empty when valid.</param>
public sealed record ModuleHealthContractValidation(
    bool IsValid,
    IReadOnlyList<ModuleHealthProbe> MissingProbes,
    IReadOnlyList<string> Problems);

/// <summary>
/// Checks that a module's declaration names all four health probes. The CI contract tests of doc 46 run the same rule
/// against real modules. Pure.
/// </summary>
public static class ModuleHealthContractValidator
{
    /// <summary>The full set of probes a module must declare.</summary>
    public static readonly IReadOnlyList<ModuleHealthProbe> RequiredProbes =
    [
        ModuleHealthProbe.Liveness,
        ModuleHealthProbe.Readiness,
        ModuleHealthProbe.Dependencies,
        ModuleHealthProbe.SelfTest,
    ];

    public static ModuleHealthContractValidation Validate(ModuleHealthDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);

        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(declaration.ModuleId))
        {
            problems.Add("Module id is blank.");
        }

        var declared = declaration.DeclaredProbes is null
            ? new HashSet<ModuleHealthProbe>()
            : new HashSet<ModuleHealthProbe>(declaration.DeclaredProbes);

        var missing = RequiredProbes.Where(p => !declared.Contains(p)).ToList();
        foreach (var probe in missing)
        {
            problems.Add($"Health probe '{probe}' is not declared.");
        }

        return new ModuleHealthContractValidation(problems.Count == 0, missing, problems);
    }
}
