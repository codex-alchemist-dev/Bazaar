using System.Text;

namespace Bazaar.Server.Portability.Tests;

public class ExportBundleTests
{
    private static readonly DateTimeOffset _created = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    private static BundleFile F(string path, string text) => new(path, Encoding.UTF8.GetBytes(text));

    private static List<BundleFile> L(params BundleFile[] files) => [.. files];

    private static string[] Lines(params string[] values) => values;

    private static List<BundleFile> SampleFiles() =>
    [
        F("gc/alpha/messages.jsonl", "{\"n\":1}\n"),
        F("vault/notes.md", "# Notes\n"),
    ];

    [Fact]
    public void Output_is_deterministic_regardless_of_input_order()
    {
        var forward = ExportBundleWriter.Build("inst-1", _created, SampleFiles());
        var reversed = ExportBundleWriter.Build("inst-1", _created, SampleFiles().AsEnumerable().Reverse().ToList());

        Assert.Equal(forward.Manifest, reversed.Manifest);
        Assert.Equal(
            forward.Files.Select(f => f.Path).ToArray(),
            reversed.Files.Select(f => f.Path).ToArray());
    }

    [Fact]
    public void Manifest_carries_schema_instance_time_hashes_and_totals()
    {
        var bundle = ExportBundleWriter.Build("inst-1", _created, SampleFiles());

        Assert.Contains("\"schemaVersion\":\"1.0.0\"", bundle.Manifest, StringComparison.Ordinal);
        Assert.Contains("\"instanceId\":\"inst-1\"", bundle.Manifest, StringComparison.Ordinal);
        Assert.Contains("\"createdAt\":\"2026-11-15T10:00:00Z\"", bundle.Manifest, StringComparison.Ordinal);
        Assert.Contains("\"fileCount\":2", bundle.Manifest, StringComparison.Ordinal);
        Assert.Contains("\"totalBytes\":", bundle.Manifest, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"vault/notes.md\"", bundle.Manifest, StringComparison.Ordinal);
        // Files include manifest.json itself, sorted with the rest.
        Assert.Equal(Lines("gc/alpha/messages.jsonl", "manifest.json", "vault/notes.md"), bundle.Files.Select(f => f.Path).ToArray());
    }

    [Fact]
    public void Absolute_paths_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => ExportBundleWriter.Build("i", _created, L(F("/etc/passwd", "x"))));
        Assert.Throws<ArgumentException>(() => ExportBundleWriter.Build("i", _created, L(F("C:/x.txt", "x"))));
    }

    [Fact]
    public void Traversal_segments_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => ExportBundleWriter.Build("i", _created, L(F("../x.txt", "x"))));
        Assert.Throws<ArgumentException>(() => ExportBundleWriter.Build("i", _created, L(F("a/../b.txt", "x"))));
        Assert.Throws<ArgumentException>(() => ExportBundleWriter.Build("i", _created, L(F("a/./b.txt", "x"))));
    }

    [Fact]
    public void Backslashes_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => ExportBundleWriter.Build("i", _created, L(F("a\\b.txt", "x"))));
    }

    [Fact]
    public void Duplicate_paths_are_rejected_including_case_only_duplicates()
    {
        Assert.Throws<ArgumentException>(() => ExportBundleWriter.Build("i", _created, L(F("a.txt", "1"), F("a.txt", "2"))));
        Assert.Throws<ArgumentException>(() => ExportBundleWriter.Build("i", _created, L(F("A.txt", "1"), F("a.txt", "2"))));
    }

    [Fact]
    public void Manifest_path_is_reserved()
    {
        Assert.Throws<ArgumentException>(() => ExportBundleWriter.Build("i", _created, L(F("manifest.json", "{}"))));
    }

    [Fact]
    public void Fresh_bundle_verifies_clean()
    {
        var bundle = ExportBundleWriter.Build("inst-1", _created, SampleFiles());

        Assert.Empty(ExportBundleWriter.Verify(bundle));
    }

    [Fact]
    public void Verify_catches_a_tampered_file()
    {
        var bundle = ExportBundleWriter.Build("inst-1", _created, SampleFiles());
        var tampered = bundle.Files
            .Select(f => f.Path == "vault/notes.md" ? F(f.Path, "# Motes\n") : f)
            .ToList();

        var problems = ExportBundleWriter.Verify(bundle with { Files = tampered });

        Assert.Equal(Lines("sha256 mismatch: vault/notes.md"), problems);
    }

    [Fact]
    public void Verify_flags_a_missing_file_and_an_unlisted_file()
    {
        var bundle = ExportBundleWriter.Build("inst-1", _created, SampleFiles());
        var withoutNotes = bundle.Files.Where(f => f.Path != "vault/notes.md").ToList();
        var withExtra = bundle.Files.Append(F("extra.txt", "surprise")).ToList();

        var missing = ExportBundleWriter.Verify(bundle with { Files = withoutNotes });
        var unlisted = ExportBundleWriter.Verify(bundle with { Files = withExtra });

        Assert.Equal(Lines("missing from bundle: vault/notes.md"), missing);
        Assert.Equal(Lines("not in manifest: extra.txt"), unlisted);
    }

    [Fact]
    public void WriteTo_writes_files_and_manifest_into_a_fresh_directory()
    {
        var bundle = ExportBundleWriter.Build("inst-1", _created, SampleFiles());
        var dir = Path.Combine(Path.GetTempPath(), "bazaar-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            ExportBundleWriter.WriteTo(bundle, dir);

            Assert.Equal(
                Encoding.UTF8.GetBytes("# Notes\n"),
                File.ReadAllBytes(Path.Combine(dir, "vault", "notes.md")));
            Assert.Equal(bundle.Manifest, File.ReadAllText(Path.Combine(dir, "manifest.json"), Encoding.UTF8));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_failed_WriteTo_leaves_no_partial_export_behind()
    {
        // "a.txt" is a file, so creating the folder "a.txt" for the second entry fails part-way through the write.
        var files = new List<BundleFile> { new("a.txt", [1, 2, 3]), new("a.txt/b.txt", [4, 5, 6]) };
        var bundle = ExportBundleWriter.Build("inst-1", _created, files);
        var dir = Path.Combine(Path.GetTempPath(), "bazaar-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.ThrowsAny<IOException>(() => ExportBundleWriter.WriteTo(bundle, dir));

            Assert.False(Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any());

            // The same directory can be used again once the cause is gone.
            ExportBundleWriter.WriteTo(ExportBundleWriter.Build("inst-1", _created, SampleFiles()), dir);
            Assert.True(File.Exists(Path.Combine(dir, "manifest.json")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WriteTo_refuses_a_non_empty_directory()
    {
        var bundle = ExportBundleWriter.Build("inst-1", _created, SampleFiles());
        var dir = Path.Combine(Path.GetTempPath(), "bazaar-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "keep.txt"), "existing");

            Assert.Throws<InvalidOperationException>(() => ExportBundleWriter.WriteTo(bundle, dir));
            Assert.True(File.Exists(Path.Combine(dir, "keep.txt")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
