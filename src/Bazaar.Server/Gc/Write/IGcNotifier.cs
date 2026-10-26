namespace Bazaar.Server.Gc.Write;

/// <summary>Tells listeners that a GC has reached a seq. Called only after the append has committed.</summary>
public interface IGcNotifier
{
    /// <summary>Records that <paramref name="gcId"/> reached <paramref name="seq"/>. Never blocks and never throws.</summary>
    void Notify(Guid gcId, long seq);
}
