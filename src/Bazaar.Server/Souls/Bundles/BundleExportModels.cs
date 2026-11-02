using Bazaar.Contracts.Soul;

namespace Bazaar.Server.Souls.Bundles;

/// <summary>
/// Exclusion categories (docs/design/07-soul-system.md, "Export"). A file whose category is not on the export allow list is
/// left out and its category is recorded in the manifest. Categories are lowercase names such as "passport-entries".
/// </summary>
public static class BundleCategories
{
    /// <summary>Skill folders (<c>skills/&lt;name&gt;/SKILL.md</c>). The only extra-file category that is exported.</summary>
    public const string Skill = "skill";

    /// <summary>Preferences JSON. Excluded unless the request sets <see cref="BundleExportRequest.IncludePreferences"/>.</summary>
    public const string Preferences = "preferences";

    /// <summary>Personal fields (name, email, anything naming a person).</summary>
    public const string PersonalFields = "personal-fields";

    /// <summary>Passport entries and account labels.</summary>
    public const string PassportEntries = "passport-entries";

    /// <summary>Mounted session cookies and account profiles.</summary>
    public const string SessionCookies = "session-cookies";

    /// <summary>VM disks and images.</summary>
    public const string VmDisks = "vm-disks";

    /// <summary>Per-agent memory notes, lessons and other private memory.</summary>
    public const string PrivateMemory = "private-memory";

    /// <summary>GC messages and history.</summary>
    public const string GcMessages = "gc-messages";

    /// <summary>Vault content outside the Soul folder.</summary>
    public const string VaultContent = "vault-content";

    /// <summary>Kanban items.</summary>
    public const string Kanban = "kanban";

    /// <summary>Raw embeddings.</summary>
    public const string RawEmbeddings = "raw-embeddings";

    /// <summary>The Mirror Agent profile (doc 26).</summary>
    public const string MirrorAgentProfile = "mirror-agent-profile";

    /// <summary>Reputation evidence and scores.</summary>
    public const string Reputation = "reputation";

    /// <summary>Returns true when files in this category may be exported. Everything else is excluded (deny by default).</summary>
    public static bool IsExported(string category) => string.Equals(category, Skill, StringComparison.Ordinal);

    /// <summary>Checks a category name: lowercase letters, digits and hyphens, starting with a letter, at most 64 characters.</summary>
    public static bool IsValidName(string category) =>
        category.Length is >= 1 and <= 64
        && char.IsAsciiLetterLower(category[0])
        && category.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}

/// <summary>A file the caller wants considered for the bundle, with the category that decides whether it is exported.</summary>
/// <param name="Path">Bundle-relative path. Validated by the exporter.</param>
/// <param name="Content">The file bytes.</param>
/// <param name="Category">The exclusion category. Required; see <see cref="BundleCategories"/>.</param>
public sealed record BundleInputFile(string Path, ReadOnlyMemory<byte> Content, string Category);

/// <summary>Everything the exporter needs. Nothing is read from the clock or the database.</summary>
/// <param name="Version">The Soul version being exported. Its hashes are checked against the charter and preferences.</param>
/// <param name="CharterBytes">The exact charter Vault file bytes.</param>
/// <param name="PreferencesJson">The preferences as JSON text. Canonicalised before hashing and writing.</param>
/// <param name="ExtraFiles">Other candidate files (skills and anything else, each with its category).</param>
/// <param name="Exporter">Who produced the bundle. Required.</param>
/// <param name="ExportedAt">The export timestamp, supplied by the caller.</param>
/// <param name="IncludePreferences">Write <c>preferences.json</c>. Off by default (docs/design/07-soul-system.md excludes preferences).</param>
public sealed record BundleExportRequest(
    SoulVersion Version,
    ReadOnlyMemory<byte> CharterBytes,
    string PreferencesJson,
    IReadOnlyList<BundleInputFile> ExtraFiles,
    string Exporter,
    DateTimeOffset ExportedAt,
    bool IncludePreferences = false);

/// <summary>One file the exporter produces, with its bundle-relative path.</summary>
/// <param name="Path">Bundle-relative path with forward slashes.</param>
/// <param name="Content">The exact bytes written.</param>
public sealed record BundleOutputFile(string Path, ReadOnlyMemory<byte> Content);

/// <summary>The exporter's output: the files (sorted by path, manifest and signature included), the manifest and the signature.</summary>
/// <param name="Manifest">The manifest record, as written to <c>manifest.json</c>.</param>
/// <param name="Signature">The signature over the exact bytes of <c>manifest.json</c>.</param>
/// <param name="Files">Every file to write, sorted by ordinal path.</param>
public sealed record BundleExportResult(BundleManifest Manifest, BundleSignature Signature, IReadOnlyList<BundleOutputFile> Files);
