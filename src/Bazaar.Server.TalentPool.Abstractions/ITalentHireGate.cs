namespace Bazaar.Server.TalentPool;

/// <summary>
/// Port for the Council <c>hire</c> motion (docs/design/10, DECISIONS-LOG C32). The Council module adapts it once C32 is settled.
/// The Talent Pool asks it before moving a template out of quarantine, and never decides a hire itself.
/// </summary>
/// <remarks>The default registration is <see cref="DenyAllTalentHireGate"/>: with no adapter, no template becomes instantiable.</remarks>
public interface ITalentHireGate
{
    /// <summary>True only when a hire motion for exactly this content (<paramref name="contentSha"/>) has passed.</summary>
    Task<bool> HasHirePassedAsync(Guid templateId, int version, string contentSha, CancellationToken ct);
}

/// <summary>Fail-closed default gate: no hire motion has ever passed.</summary>
public sealed class DenyAllTalentHireGate : ITalentHireGate
{
    public Task<bool> HasHirePassedAsync(Guid templateId, int version, string contentSha, CancellationToken ct) => Task.FromResult(false);
}
