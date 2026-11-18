namespace Bazaar.Server.TalentPool;

/// <summary>
/// The Talent Pool (docs/design/25). Submitting stores a quarantined template; nothing is instantiable until a Council <c>hire</c> motion
/// has passed (the <see cref="ITalentHireGate"/>).
/// </summary>
public interface ITalentPool
{
    /// <summary>Validates and stores a template as a new version in quarantine. A refused manifest is not stored.</summary>
    Task<TemplateSubmitResult> SubmitAsync(TalentManifest manifest, TalentActor actor, CancellationToken ct);

    /// <summary>
    /// Asks the hire gate about a quarantined version and, only if it says a hire motion passed, marks it hired. Returns the version's
    /// status afterwards, or null when it does not exist.
    /// </summary>
    Task<TalentTemplateStatus?> ConfirmHireAsync(Guid templateId, int version, CancellationToken ct);

    /// <summary>Decides whether a version may be instantiated, and whether the instantiation is Tier 2. Creates nothing.</summary>
    Task<TalentInstantiationDecision> EvaluateInstantiationAsync(TalentInstantiateRequest request, CancellationToken ct);
}
