using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Soul;
using Bazaar.Server.Souls.Bundles;

namespace Bazaar.Server.Tests.Souls.Bundles;

public sealed class BundleExporterTests
{
    [Fact]
    public void Same_input_gives_identical_bytes_whatever_the_extra_file_order()
    {
        var forward = BundleFixture.Exporter().Export(BundleFixture.Request(
            [BundleFixture.Skill("skills/a/SKILL.md", "Alpha skill."), BundleFixture.Skill("skills/b/SKILL.md", "Beta skill.")]));
        var reversed = BundleFixture.Exporter().Export(BundleFixture.Request(
            [BundleFixture.Skill("skills/b/SKILL.md", "Beta skill."), BundleFixture.Skill("skills/a/SKILL.md", "Alpha skill.")]));

        Assert.Equal(
            forward.Files.Select(f => f.Path),
            reversed.Files.Select(f => f.Path));
        foreach (var (file, other) in forward.Files.Zip(reversed.Files))
            Assert.Equal(file.Content.ToArray(), other.Content.ToArray());
    }

    [Fact]
    public void Files_are_sorted_by_ordinal_path_and_include_manifest_and_signature()
    {
        var result = BundleFixture.Exporter().Export(BundleFixture.Request(
            [BundleFixture.Skill("skills/zeta/SKILL.md", "z"), BundleFixture.Skill("skills/alpha/SKILL.md", "a")]));

        Assert.Equal(
            ["charter.md", "manifest.json", "signature.json", "skills/alpha/SKILL.md", "skills/zeta/SKILL.md"],
            result.Files.Select(f => f.Path).ToArray());
    }

    [Fact]
    public void Manifest_digests_and_sizes_match_the_written_bytes()
    {
        var result = BundleFixture.Exporter().Export(BundleFixture.Request(
            [BundleFixture.Skill("skills/a/SKILL.md", "Alpha skill body.")]));
        var files = BundleFixture.ToMap(result);

        using var manifest = JsonDocument.Parse(files[BundleExporter.ManifestPath]);
        var entries = manifest.RootElement.GetProperty("files").EnumerateArray().ToList();

        // Every bundle file except the manifest and signature appears exactly once, with its digest and size.
        Assert.Equal(
            ["charter.md", "skills/a/SKILL.md"],
            entries.Select(e => e.GetProperty("path").GetString()!).ToArray());
        foreach (var entry in entries)
        {
            var path = entry.GetProperty("path").GetString()!;
            var bytes = files[path];
            Assert.Equal(BundleFixture.Sha256Hex(bytes), entry.GetProperty("sha256").GetString());
            Assert.Equal(bytes.Length, entry.GetProperty("size").GetInt64());
            Assert.Matches("^[0-9a-f]{64}$", entry.GetProperty("sha256").GetString()!);
        }

        Assert.Equal(BundleFixture.Sha256Hex(BundleFixture.Charter), BundleFixture.Sha256Hex(files["charter.md"]));
    }

    [Fact]
    public void Manifest_carries_the_soul_version_identity_and_format()
    {
        var result = BundleFixture.Exporter().Export(BundleFixture.Request());
        using var manifest = JsonDocument.Parse(BundleFixture.ToMap(result)[BundleExporter.ManifestPath]);
        var root = manifest.RootElement;

        Assert.Equal(BundleExporter.FormatVersion, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal(BundleFixture.SoulId, root.GetProperty("soulId").GetGuid());
        Assert.Equal(3, root.GetProperty("soulVersion").GetInt32());
        Assert.Equal(BundleFixture.SoulVersionFor().ContentSha256, root.GetProperty("contentSha256").GetString());
        Assert.Equal(BundleFixture.ExporterName, root.GetProperty("exporter").GetString());
        Assert.Equal(BundleFixture.ExportedAt, root.GetProperty("exportedAt").GetDateTimeOffset());
    }

    [Fact]
    public void Signature_is_over_the_exact_manifest_bytes()
    {
        var result = BundleFixture.Exporter("secret-for-test").Export(BundleFixture.Request());
        var files = BundleFixture.ToMap(result);

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret-for-test"), files[BundleExporter.ManifestPath]);
        Assert.Equal(Convert.ToBase64String(expected), result.Signature.SignatureBase64);
        Assert.Equal("test-key-1", result.Signature.KeyId);
        Assert.Equal(FakeBundleSigner.HmacAlgorithm, result.Signature.Algorithm);

        using var signature = JsonDocument.Parse(files[BundleExporter.SignaturePath]);
        Assert.Equal(result.Signature.SignatureBase64, signature.RootElement.GetProperty("signatureBase64").GetString());
    }

    [Fact]
    public void Fixed_signature_signer_returns_its_value_regardless_of_input()
    {
        var fixedSignature = new BundleSignature("fixed-key", "Ed25519", "AAAA");
        var exporter = new BundleExporter(FakeBundleSigner.WithFixedSignature(fixedSignature));

        var result = exporter.Export(BundleFixture.Request());

        Assert.Equal(fixedSignature, result.Signature);
        using var signature = JsonDocument.Parse(BundleFixture.ToMap(result)[BundleExporter.SignaturePath]);
        Assert.Equal("fixed-key", signature.RootElement.GetProperty("keyId").GetString());
    }

    [Fact]
    public void Preferences_are_excluded_by_default_and_the_exclusion_is_recorded()
    {
        var result = BundleFixture.Exporter().Export(BundleFixture.Request());

        Assert.DoesNotContain(BundleExporter.PreferencesPath, result.Files.Select(f => f.Path));
        Assert.Contains(BundleCategories.Preferences, result.Manifest.Exclusions);
    }

    [Fact]
    public void Included_preferences_are_written_as_canonical_json_whatever_the_input_layout()
    {
        var loose = BundleFixture.Exporter().Export(BundleFixture.Request(
            includePreferences: true,
            preferences: "{\n  \"verbosity\": 2,\n  \"tone\": \"dry\"\n}"));
        var canonical = BundleFixture.Exporter().Export(BundleFixture.Request(includePreferences: true));

        var prefs = BundleFixture.ToMap(loose)[BundleExporter.PreferencesPath];
        Assert.Equal(BundleFixture.PreferencesJson, Encoding.UTF8.GetString(prefs));
        Assert.Equal(BundleFixture.ToMap(canonical)[BundleExporter.PreferencesPath], prefs);
        Assert.DoesNotContain(BundleCategories.Preferences, loose.Manifest.Exclusions);
    }

    [Fact]
    public void Excluded_categories_are_dropped_recorded_and_their_content_never_appears()
    {
        const string passportMarker = "PASSPORT-ENTRY-MARKER-7731";
        const string cookieMarker = "SESSION-COOKIE-MARKER-7732";
        var result = BundleFixture.Exporter().Export(BundleFixture.Request(
        [
            BundleFixture.Skill("skills/a/SKILL.md", "Keeps its place."),
            BundleFixture.Excluded("passport/entries.json", passportMarker, BundleCategories.PassportEntries),
            BundleFixture.Excluded("profile/cookies.bin", cookieMarker, BundleCategories.SessionCookies),
            BundleFixture.Excluded("memory/private/note.md", "private note", BundleCategories.PrivateMemory),
            BundleFixture.Excluded("vm/disk.vhdx", "disk", BundleCategories.VmDisks),
        ]));

        var paths = result.Files.Select(f => f.Path).ToArray();
        Assert.DoesNotContain("passport/entries.json", paths);
        Assert.DoesNotContain("profile/cookies.bin", paths);
        Assert.Equal(
            [BundleCategories.PassportEntries, BundleCategories.Preferences, BundleCategories.PrivateMemory, BundleCategories.SessionCookies, BundleCategories.VmDisks],
            result.Manifest.Exclusions);

        foreach (var file in result.Files)
        {
            var text = Encoding.UTF8.GetString(file.Content.Span);
            Assert.DoesNotContain(passportMarker, text);
            Assert.DoesNotContain(cookieMarker, text);
        }
    }

    [Fact]
    public void Manifest_lists_only_exported_files_in_its_file_entries()
    {
        var result = BundleFixture.Exporter().Export(BundleFixture.Request(
            [BundleFixture.Excluded("vm/disk.vhdx", "disk", BundleCategories.VmDisks)]));

        Assert.DoesNotContain(result.Manifest.Files, f => f.Path.Contains("disk", StringComparison.Ordinal));
        Assert.Contains(BundleCategories.VmDisks, result.Manifest.Exclusions);
    }

    [Fact]
    public void Charter_bytes_that_do_not_match_the_version_are_refused()
    {
        var request = BundleFixture.Request() with { CharterBytes = Encoding.UTF8.GetBytes("edited charter") };

        Assert.Throws<ArgumentException>(() => BundleFixture.Exporter().Export(request));
    }

    [Fact]
    public void Preferences_that_do_not_match_the_content_hash_are_refused()
    {
        var version = BundleFixture.SoulVersionFor() with { ContentSha256 = new string('0', 64) };
        var request = BundleFixture.Request() with { Version = version };

        Assert.Throws<ArgumentException>(() => BundleFixture.Exporter().Export(request));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_exporter_is_refused(string exporter)
    {
        var request = BundleFixture.Request() with { Exporter = exporter };

        Assert.Throws<ArgumentException>(() => BundleFixture.Exporter().Export(request));
    }

    [Fact]
    public void Invalid_category_names_are_refused()
    {
        var request = BundleFixture.Request([new BundleInputFile("skills/a/SKILL.md", "x"u8.ToArray(), "Bad Category")]);

        Assert.Throws<ArgumentException>(() => BundleFixture.Exporter().Export(request));
    }

    [Fact]
    public void Reserved_and_duplicate_paths_are_refused()
    {
        var reserved = BundleFixture.Request([BundleFixture.Skill("manifest.json", "x")]);
        var duplicate = BundleFixture.Request(
            [BundleFixture.Skill("skills/a/SKILL.md", "x"), BundleFixture.Skill("skills/a/SKILL.md", "y")]);

        Assert.Throws<ArgumentException>(() => BundleFixture.Exporter().Export(reserved));
        Assert.Throws<ArgumentException>(() => BundleFixture.Exporter().Export(duplicate));
    }
}
