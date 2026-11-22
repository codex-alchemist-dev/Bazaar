using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Modules;

/// <summary>
/// The health part of a module's contract (doc 46 section 10, first slice; PROPOSED narrowing, see doc 46 section 10.5).
/// A module implements this to report one probe result, keyed by a stable module id and a catalogue probe id.
/// </summary>
public interface IModuleHealthProbe
{
    /// <summary>The stable module id, for example <c>kodo</c>. It does not change between releases.</summary>
    string ModuleId { get; }

    /// <summary>The probe id, <c>module.&lt;id&gt;.health</c> from the built-in catalogue (doc 46 section 9.1).</summary>
    string ProbeId { get; }

    /// <summary>Reports the module's health. A throw is not trusted: the loader gate reports it as <c>contract_error</c>.</summary>
    ValueTask<ProbeResult> CheckAsync(CancellationToken ct);
}
