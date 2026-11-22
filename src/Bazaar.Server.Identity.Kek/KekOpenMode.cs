namespace Bazaar.Server.Identity.Kek;

/// <summary>Whether opening a KEK may create it (doc 63 section 5, step 1).</summary>
public enum KekOpenMode
{
    /// <summary>Open the existing key or throw <see cref="KekMissingException"/>. The host uses this at start.</summary>
    OpenExisting,

    /// <summary>Create the key when it is absent. Only setup and the restore wizard use this.</summary>
    CreateIfMissing,
}
