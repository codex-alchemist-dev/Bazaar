using System.Text;
using Bazaar.Client.Athanor.Drafts;

namespace Bazaar.Client.Athanor.Tests.Drafts;

/// <summary>Runs the shared contract on disk, plus file-specific cases: corrupt files, temp leftovers and path safety.</summary>
public sealed class FileEditorDraftStoreTests : EditorDraftStoreContract, IDisposable
{
    private readonly ManualClock _clock = new();
    private readonly string _parent;
    private readonly string _root;
    private readonly IEditorDraftStore _store;

    public FileEditorDraftStoreTests()
    {
        _parent = Path.Combine(Path.GetTempPath(), "athanor-drafts-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_parent, "drafts");
        Directory.CreateDirectory(_parent);
        _store = new FileEditorDraftStore(_root, _clock);
    }

    protected override ManualClock Clock => _clock;

    protected override IEditorDraftStore Store => _store;

    public void Dispose()
    {
        if (Directory.Exists(_parent))
        {
            Directory.Delete(_parent, recursive: true);
        }
    }

    [Fact]
    public async Task Corrupt_file_is_skipped_by_read_and_list()
    {
        await Store.WriteAsync(Draft(Doc("good.cs"), "good"), CancellationToken.None);
        await Store.WriteAsync(Draft(Doc("bad.cs"), "bad"), CancellationToken.None);
        File.WriteAllText(OnlyFileFor(Doc("bad.cs")), "this is not json {{{");

        Assert.Null(await Store.ReadAsync(Doc("bad.cs"), CancellationToken.None));
        var all = await Store.ListAsync(CancellationToken.None);
        Assert.Equal("good.cs", string.Join(',', all.Select(d => d.Id.Path)));
    }

    [Fact]
    public async Task Truncated_file_is_skipped_by_read_and_list()
    {
        await Store.WriteAsync(Draft(Doc("cut.cs"), "a long draft body that will be cut"), CancellationToken.None);
        var file = OnlyFileFor(Doc("cut.cs"));
        var bytes = File.ReadAllBytes(file);
        File.WriteAllBytes(file, bytes[..(bytes.Length / 2)]);

        Assert.Null(await Store.ReadAsync(Doc("cut.cs"), CancellationToken.None));
        Assert.Empty(await Store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Json_with_missing_fields_is_skipped()
    {
        await Store.WriteAsync(Draft(Doc("partial.cs"), "x"), CancellationToken.None);
        File.WriteAllText(OnlyFileFor(Doc("partial.cs")), "{\"workspaceId\":\"ws-1\"}");

        Assert.Null(await Store.ReadAsync(Doc("partial.cs"), CancellationToken.None));
        Assert.Empty(await Store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Leftover_temp_file_is_ignored()
    {
        await Store.WriteAsync(Draft(Doc("real.cs"), "real"), CancellationToken.None);
        var leftover = Path.Combine(_root, "0123abcd.deadbeef.tmp");
        var file = new { workspaceId = "ws-1", path = "ghost.cs", content = "ghost", baseRevision = "r1", savedAt = _clock.Now };
        File.WriteAllText(leftover, System.Text.Json.JsonSerializer.Serialize(file));

        var all = await Store.ListAsync(CancellationToken.None);

        Assert.Equal("real.cs", string.Join(',', all.Select(d => d.Id.Path)));
        Assert.True(File.Exists(leftover));
    }

    [Fact]
    public async Task Write_leaves_no_temp_file_behind()
    {
        await Store.WriteAsync(Draft(Doc("clean.cs"), "x"), CancellationToken.None);

        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
        Assert.Single(Directory.EnumerateFiles(_root));
    }

    [Theory]
    [InlineData("../../escape.cs")]
    [InlineData("..\\..\\escape.cs")]
    [InlineData("/etc/passwd")]
    [InlineData("a/../../../escape.cs")]
    public async Task Traversal_looking_paths_stay_inside_the_root(string path)
    {
        var id = Doc(path);
        await Store.WriteAsync(Draft(id, "trapped"), CancellationToken.None);

        var inside = Directory.EnumerateFiles(_root).ToArray();
        Assert.Single(inside);
        Assert.Equal(_root, Path.GetDirectoryName(inside[0]));
        Assert.Empty(Directory.EnumerateFiles(_parent));
        Assert.Equal("trapped", (await Store.ReadAsync(id, CancellationToken.None))!.Content);
    }

    [Fact]
    public async Task File_name_is_a_hash_not_the_raw_path()
    {
        await Store.WriteAsync(Draft(Doc("secret/name.cs"), "x"), CancellationToken.None);

        var name = Path.GetFileName(OnlyFileFor(Doc("secret/name.cs")));
        Assert.DoesNotContain("secret", name, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}\\.json$", name);
    }

    [Fact]
    public async Task Missing_root_directory_lists_empty_and_reads_null()
    {
        var store = new FileEditorDraftStore(Path.Combine(_parent, "not-created-yet"), _clock);

        Assert.Empty(await store.ListAsync(CancellationToken.None));
        Assert.Null(await store.ReadAsync(Doc(), CancellationToken.None));
        await store.DeleteAsync(Doc(), CancellationToken.None);
    }

    [Fact]
    public async Task Stored_file_is_plain_utf8_json_with_camel_case_keys()
    {
        await Store.WriteAsync(Draft(Doc("json.cs"), "héllo"), CancellationToken.None);

        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(OnlyFileFor(Doc("json.cs"))));
        Assert.Equal("r1", json.RootElement.GetProperty("baseRevision").GetString());
        Assert.Equal("héllo", json.RootElement.GetProperty("content").GetString());
        Assert.Equal("json.cs", json.RootElement.GetProperty("path").GetString());
    }

    // The file name is the documented key hash; computing it here keeps the test independent of the store's helpers.
    private string OnlyFileFor(EditorDocumentId id)
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes($"{id.WorkspaceId}\n{id.Path}")));
        return Path.Combine(_root, hash + ".json");
    }
}
