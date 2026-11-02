using System.Security.Cryptography;
using System.Text;
using Bazaar.Contracts.Soul;
using Bazaar.Server.Souls;
using Bazaar.Server.Souls.Bundles;

namespace Bazaar.Server.Tests.Souls.Bundles;

/// <summary>Shared inputs for the bundle exporter tests.</summary>
internal static class BundleFixture
{
    public static readonly Guid SoulId = Guid.Parse("6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7");
    public static readonly DateTimeOffset ExportedAt = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    public const string ExporterName = "codex-alchemist-dev/Bazaar tests";
    public const string PreferencesJson = "{\"tone\":\"dry\",\"verbosity\":2}";

    public static readonly byte[] Charter = Encoding.UTF8.GetBytes("# Claude architect\n\nReviews designs carefully.\n");

    public static SoulVersion SoulVersionFor(byte[]? charter = null, string? preferences = null)
    {
        var charterSha = Sha256Hex(charter ?? Charter);
        var canonical = SoulCanonicalJson.Canonicalize(preferences ?? PreferencesJson);
        return new SoulVersion(
            SoulId,
            3,
            2,
            charterSha,
            canonical,
            SoulVersioningService.ContentHash(charterSha, canonical),
            "owner",
            "fixture",
            null,
            ExportedAt);
    }

    public static BundleExportRequest Request(
        IReadOnlyList<BundleInputFile>? extras = null,
        bool includePreferences = false,
        string? preferences = null) =>
        new(SoulVersionFor(preferences: preferences), Charter, preferences ?? PreferencesJson, extras ?? [], ExporterName, ExportedAt, includePreferences);

    public static BundleInputFile Skill(string path, string text) =>
        new(path, Encoding.UTF8.GetBytes(text), BundleCategories.Skill);

    public static BundleInputFile Excluded(string path, string text, string category) =>
        new(path, Encoding.UTF8.GetBytes(text), category);

    public static BundleExporter Exporter(string key = "test-signing-key") =>
        new(FakeBundleSigner.WithHmacKey("test-key-1", Encoding.UTF8.GetBytes(key)));

    public static Dictionary<string, byte[]> ToMap(BundleExportResult result) =>
        result.Files.ToDictionary(f => f.Path, f => f.Content.ToArray(), StringComparer.Ordinal);

    public static string Sha256Hex(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));
}
