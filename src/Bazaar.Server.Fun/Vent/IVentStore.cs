namespace Bazaar.Server.Fun.Vent;

/// <summary>Stores vent drafts keyed by failure reference (doc 26: restarts are idempotent).</summary>
public interface IVentStore
{
    /// <summary>Returns the existing draft for the failure, or stores the candidate and returns it.</summary>
    VentDraft GetOrAdd(VentDraft candidate);
}
