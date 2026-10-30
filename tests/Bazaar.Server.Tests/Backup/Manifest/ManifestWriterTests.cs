using System.Text;
using Bazaar.Server.Backup.Manifest;

namespace Bazaar.Server.Tests.Backup.Manifest;

public sealed class ManifestWriterTests
{
    [Fact]
    public void Sha256_of_abc_matches_the_known_answer()
    {
        // Known answer: FIPS 180-2 test vector for "abc". Recomputed with `printf abc | sha256sum` on the test host.
        var manifest = ManifestWriter.Build([("a.bin", Encoding.UTF8.GetBytes("abc"))]);

        var entry = Assert.Single(manifest.Entries);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", entry.Sha256);
        Assert.Equal(3, entry.Length);
    }

    [Fact]
    public void Entries_are_sorted_by_path_ordinal()
    {
        var manifest = ManifestWriter.Build(
        [
            ("b/z.txt", [1]),
            ("B/a.txt", [2]),
            ("a.txt", [3]),
        ]);

        Assert.Equal(["B/a.txt", "a.txt", "b/z.txt"], manifest.Entries.Select(e => e.Path));
    }

    [Fact]
    public void Build_is_deterministic_for_the_same_input_in_any_order()
    {
        var first = ManifestWriter.Build([("x", [1, 2]), ("y", [3])]);
        var second = ManifestWriter.Build([("y", [3]), ("x", [1, 2])]);

        Assert.Equal(first.Entries, second.Entries);
    }

    [Fact]
    public void Backslash_and_forward_slash_name_the_same_logical_path()
    {
        var manifest = ManifestWriter.Build([("vault\\notes\\a.md", [7])]);

        Assert.Equal("vault/notes/a.md", Assert.Single(manifest.Entries).Path);
        Assert.Equal("vault/notes/a.md", BundlePath.Normalize("vault/notes/a.md"));
    }

    [Fact]
    public void Two_spellings_of_one_path_are_a_duplicate()
    {
        Assert.Throws<ArgumentException>(() => ManifestWriter.Build([("a\\b.txt", [1]), ("a/b.txt", [2])]));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("C:/data/x")]
    [InlineData("../escape.txt")]
    [InlineData("dir/../escape.txt")]
    [InlineData("dir/./x.txt")]
    [InlineData("dir//x.txt")]
    [InlineData("dir/")]
    [InlineData("")]
    [InlineData("line\nbreak")]
    public void Absolute_or_parent_paths_are_rejected(string path)
    {
        Assert.Throws<ArgumentException>(() => ManifestWriter.Build([(path, [1])]));
    }
}
