using System.Globalization;

namespace Bazaar.Client.OwnerId;

/// <summary>Where the phone keeps the owner's human id (doc 32). The Android head implements it over private preferences. It stores an id only: no key is created here.</summary>
public interface IOwnerIdStore
{
    /// <summary>The stored owner id in canonical form, or null when none is stored.</summary>
    string? Load();

    /// <summary>Stores an already validated canonical id. Returns false when it could not be stored.</summary>
    bool Save(string canonicalId);
}

/// <summary>Validation of owner id text typed or pasted by the person.</summary>
public static class OwnerIdText
{
    /// <summary>
    /// Accepts a GUID in any common form, with surrounding space, and returns the canonical lowercase <c>D</c> form. The empty
    /// GUID is refused. Returns false for anything else, so nothing malformed is kept.
    /// </summary>
    public static bool TryNormalize(string? text, out string canonical)
    {
        canonical = "";
        if (text is null || !Guid.TryParse(text.Trim(), out var id) || id == Guid.Empty)
        {
            return false;
        }

        canonical = id.ToString("D", CultureInfo.InvariantCulture);
        return true;
    }
}
