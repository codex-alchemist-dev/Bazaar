using System.Globalization;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Memory.Notes;

/// <summary>Parses a wire value into a typed value, without throwing.</summary>
/// <typeparam name="T">The parsed type.</typeparam>
internal delegate bool TryParseWire<T>(string text, out T value);

/// <summary>Wire names used in note front matter.</summary>
internal static class MemoryNoteWire
{
    public static string KindName(MemoryKind kind) => kind.ToString().ToLowerInvariant();

    public static bool TryParseKind(string text, out MemoryKind kind) => TryParseEnum(text, KindName, out kind);

    public static string StatusName(MemoryNoteStatus status) => status.ToString().ToLowerInvariant();

    public static bool TryParseStatus(string text, out MemoryNoteStatus status) => TryParseEnum(text, StatusName, out status);

    /// <summary>Tiers are written as T0 to T3 (doc 08, section 5).</summary>
    public static string TierName(MemoryTier tier) => "T" + ((int)tier).ToString(CultureInfo.InvariantCulture);

    public static bool TryParseTier(string text, out MemoryTier tier) => TryParseEnum(text, TierName, out tier);

    public static string SourceTrustName(ContentTrust trust) => trust switch
    {
        ContentTrust.Owner => "owner",
        ContentTrust.System => "system",
        ContentTrust.MemberHuman => "member-human",
        ContentTrust.Agent => "agent",
        ContentTrust.External => "external",
        ContentTrust.Quarantined => "quarantined",
        _ => throw new ArgumentOutOfRangeException(nameof(trust), trust, "Unknown source trust."),
    };

    public static bool TryParseSourceTrust(string text, out ContentTrust trust) =>
        TryParseEnum(text, SourceTrustName, out trust);

    private static bool TryParseEnum<T>(string text, Func<T, string> name, out T value) where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(name(candidate), text, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }
}
