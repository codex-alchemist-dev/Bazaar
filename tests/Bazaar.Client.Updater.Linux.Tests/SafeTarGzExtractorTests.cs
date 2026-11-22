using System.Text;
using static Bazaar.Client.Updater.Linux.Tests.TarBuilder;

namespace Bazaar.Client.Updater.Linux.Tests;

public class SafeTarGzExtractorTests
{
    private const string _dest = "/opt/bazaar/releases/.partial";

    private static async Task<InMemoryFs> ExtractAsync(byte[] tar, long maxBytes = 1_000_000, int maxEntries = 1000)
    {
        var fs = new InMemoryFs();
        fs.CreateDirectory("/opt/bazaar/releases", UnixFileMode.UserRead);
        await new SafeTarGzExtractor(fs, maxBytes, maxEntries).ExtractAsync(new MemoryStream(tar), _dest, CancellationToken.None);
        return fs;
    }

    private static async Task AssertRejectedAsync(byte[] tar, string? messagePart = null, long maxBytes = 1_000_000, int maxEntries = 1000)
    {
        var e = await Assert.ThrowsAsync<ArchiveRejectedException>(() => ExtractAsync(tar, maxBytes, maxEntries));
        if (messagePart is not null)
        {
            Assert.Contains(messagePart, e.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Files_directories_and_inside_links_are_extracted()
    {
        var fs = await ExtractAsync(Build(Dir("./"), Dir("lib"), File("lib/a.txt", "hello"), Link("lib/b", "a.txt"), Link("up", "lib/a.txt"), Link("lib/c", "../lib/a.txt")));

        Assert.Equal("hello", Encoding.UTF8.GetString(fs.Content(_dest + "/lib/a.txt")!));
        Assert.Equal("a.txt", fs.ReadLink(_dest + "/lib/b"));
        Assert.Equal("lib/a.txt", fs.ReadLink(_dest + "/up"));
        Assert.Equal("../lib/a.txt", fs.ReadLink(_dest + "/lib/c"));
    }

    [Fact]
    public async Task A_dot_slash_prefix_is_accepted()
    {
        var fs = await ExtractAsync(Build(File("./bin/run", "r")));

        Assert.True(fs.FileExists(_dest + "/bin/run"));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/../../evil")]
    [InlineData("a/../b")]
    [InlineData("/etc/passwd")]
    [InlineData("a\\b")]
    public async Task A_traversing_or_absolute_name_is_rejected(string name) =>
        await AssertRejectedAsync(Build(File(name)));

    [Fact]
    public async Task A_traversing_directory_name_is_rejected() =>
        await AssertRejectedAsync(Build(Dir("../outside")));

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../../etc/passwd")]
    [InlineData("a/../../x")]
    [InlineData("lib/../../x")]
    public async Task A_link_pointing_outside_is_rejected(string target) =>
        await AssertRejectedAsync(Build(Link("lib/l", target)));

    [Fact]
    public async Task A_link_may_climb_to_the_root_but_not_past_it()
    {
        await ExtractAsync(Build(Link("a/b/l", "../../x"))); // Resolves to the root itself.
        await AssertRejectedAsync(Build(Link("a/b/l", "../../../x")));
    }

    [Fact]
    public async Task Climbing_after_a_name_is_rejected_because_the_name_could_be_a_link() =>
        await AssertRejectedAsync(Build(Link("d/l", "a/../..")));

    [Fact]
    public async Task Writing_through_a_link_the_archive_created_is_rejected() =>
        await AssertRejectedAsync(Build(Link("sub", "."), File("sub/x", "payload")), "symbolic link");

    [Fact]
    public async Task A_directory_entry_through_a_link_is_rejected() =>
        await AssertRejectedAsync(Build(Link("sub", "."), Dir("sub/deeper")), "symbolic link");

    [Fact]
    public async Task A_file_replacing_an_earlier_link_is_rejected() =>
        await AssertRejectedAsync(Build(Link("x", "y"), File("x")), "symbolic link");

    [Fact]
    public async Task A_hard_link_is_rejected() =>
        await AssertRejectedAsync(Build(File("a"), HardLink("b", "a")), "not allowed");

    [Fact]
    public async Task A_duplicate_file_is_rejected() =>
        await AssertRejectedAsync(Build(File("a", "1"), File("a", "2")), "twice");

    [Fact]
    public async Task A_duplicate_link_is_rejected() =>
        await AssertRejectedAsync(Build(Link("a", "b"), Link("a", "c")), "twice");

    [Fact]
    public async Task Too_many_entries_are_rejected() =>
        await AssertRejectedAsync(Build(File("a"), File("b"), File("c")), "more than 2", maxEntries: 2);

    [Fact]
    public async Task An_archive_that_expands_beyond_the_limit_is_rejected() =>
        await AssertRejectedAsync(Build(File("big", new string('x', 5000))), "expands beyond", maxBytes: 1000);

    [Fact]
    public async Task The_total_across_files_counts()
    {
        var tar = Build(File("a", new string('x', 600)), File("b", new string('x', 600)));

        await AssertRejectedAsync(tar, "expands beyond", maxBytes: 1000);
    }

    [Fact]
    public async Task Garbage_is_rejected_as_an_invalid_archive() =>
        await AssertRejectedAsync("this is not a tarball"u8.ToArray(), "not a valid");

    [Fact]
    public async Task A_truncated_archive_is_rejected()
    {
        var tar = Build(File("a", new string('x', 4000)));

        await AssertRejectedAsync(tar[..(tar.Length / 2)]);
    }
}
