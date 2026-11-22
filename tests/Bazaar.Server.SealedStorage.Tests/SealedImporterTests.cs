using System.Text;
using Bazaar.Server.Identity.Kek;

namespace Bazaar.Server.SealedStorage.Tests;

public sealed class SealedImporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-import-" + Guid.NewGuid().ToString("N"));

    public SealedImporterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<ISealedScope> ScopeAsync()
    {
        var store = new SealedStore(
            new SealedStoreOptions { RootPath = Path.Combine(_dir, "root") },
            FileKekProvider.Open(Path.Combine(_dir, "kek.key")),
            new InMemorySealAnchor(),
            new NullSink());
        return await store.OpenScopeAsync("soul:import");
    }

    private string Plain(params (string Path, string Text)[] files)
    {
        var src = Path.Combine(_dir, "plain");
        foreach (var (p, t) in files)
        {
            var full = Path.Combine(src, p);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, t);
        }

        return src;
    }

    [Fact]
    public async Task Imports_nested_files_and_removes_plaintext()
    {
        var src = Plain(("a.md", "one"), ("sub/b.md", "two"));
        using var scope = await ScopeAsync();
        var report = await SealedImporter.ImportDirectoryAsync(scope, src, removePlaintext: true);
        Assert.Equal(2, report.Imported);
        Assert.Equal(2, report.RemovedPlaintext.Count);
        Assert.Empty(Directory.GetFiles(src, "*", SearchOption.AllDirectories));
        Assert.Equal("two", Encoding.UTF8.GetString((await scope.ReadAsync("sub/b.md"))!.Content));
    }

    [Fact]
    public async Task Imports_only_included_files_and_leaves_the_rest_untouched()
    {
        var src = Plain(("keep/a.md", "one"), ("Cache/b.bin", "two"));
        using var scope = await ScopeAsync();

        var report = await SealedImporter.ImportDirectoryAsync(scope, src, removePlaintext: true, logical => !logical.StartsWith("Cache/", StringComparison.Ordinal));

        Assert.Equal(1, report.Imported);
        Assert.True(File.Exists(Path.Combine(src, "Cache", "b.bin")));
        Assert.False(File.Exists(Path.Combine(src, "keep", "a.md")));
        Assert.Null(await scope.ReadAsync("Cache/b.bin"));
    }

    [Fact]
    public async Task Refuses_a_linked_source_and_never_follows_links_inside_it()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "victim"), "keep me");
        var src = Plain(("a.md", "one"));
        File.CreateSymbolicLink(Path.Combine(src, "link"), Path.Combine(outside, "victim"));
        using var scope = await ScopeAsync();

        var report = await SealedImporter.ImportDirectoryAsync(scope, src, removePlaintext: true);

        Assert.Equal(1, report.Imported);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(outside, "victim")));
        Assert.Null(await scope.ReadAsync("link"));

        var linkedSource = Path.Combine(_dir, "linked-source");
        Directory.CreateSymbolicLink(linkedSource, outside);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await SealedImporter.ImportDirectoryAsync(scope, linkedSource, removePlaintext: true));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(outside, "victim")));
    }

    [Fact]
    public async Task Keeps_plaintext_when_not_asked_to_remove_and_never_overwrites_sealed_paths()
    {
        var src = Plain(("a.md", "plain"));
        using var scope = await ScopeAsync();
        await scope.WriteAsync("a.md", Encoding.UTF8.GetBytes("sealed"), 0);
        var report = await SealedImporter.ImportDirectoryAsync(scope, src, removePlaintext: true);
        Assert.Equal(0, report.Imported);
        Assert.Equal(1, report.SkippedExisting);
        Assert.True(File.Exists(Path.Combine(src, "a.md")));
        Assert.Equal("sealed", Encoding.UTF8.GetString((await scope.ReadAsync("a.md"))!.Content));
    }

    private sealed class NullSink : ISealAuditSink
    {
        public ValueTask RecordAsync(SealAuditEvent auditEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
