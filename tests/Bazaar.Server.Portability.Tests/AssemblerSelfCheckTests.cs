using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Portability.Tests;

/// <summary>Doc 44 section 6.2: the assembler re-reads the bundle it wrote, and a bundle that fails is never offered.</summary>
public sealed class AssemblerSelfCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-selfcheck-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private BundleAssemblyOptions Options() => new()
    {
        InstanceId = "inst-1",
        CreatedAt = new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero),
        SigningKey = _key,
        OutputDirectory = Path.Combine(_root, "out"),
    };

    [Fact]
    public async Task A_sound_bundle_passes_the_self_check()
    {
        var source = new DelegateBundleSource("ok.v1", async (sink, ct) =>
        {
            await using var stream = await sink.OpenAsync("data/a.txt", ct);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("hello"), ct);
        });

        var result = await ExportBundleAssembler.AssembleAsync([source], Options(), TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(result.Directory));
    }

    [Fact]
    public async Task A_file_changed_after_it_was_hashed_fails_the_bundle_and_leaves_nothing()
    {
        var options = Options();
        var source = new DelegateBundleSource("damage.v1", async (sink, ct) =>
        {
            await using (var stream = await sink.OpenAsync("data/a.txt", ct))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("hello"), ct);
            }

            // The work directory is a hidden sibling of the output; change the closed file behind the sink's back.
            var work = Directory.EnumerateDirectories(_root, ".out.partial-*").Single();
            await File.AppendAllTextAsync(Path.Combine(work, "data", "a.txt"), "!", ct);
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExportBundleAssembler.AssembleAsync([source], options, TestContext.Current.CancellationToken));

        Assert.Contains("self-check", ex.Message, StringComparison.Ordinal);
        Assert.Contains("data/a.txt", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(options.OutputDirectory));
        Assert.Empty(Directory.EnumerateDirectories(_root));
    }

    [Fact]
    public async Task A_stray_file_in_the_work_directory_fails_the_bundle()
    {
        var options = Options();
        var source = new DelegateBundleSource("stray.v1", async (sink, ct) =>
        {
            await using (var stream = await sink.OpenAsync("data/a.txt", ct))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("hello"), ct);
            }

            var work = Directory.EnumerateDirectories(_root, ".out.partial-*").Single();
            await File.WriteAllTextAsync(Path.Combine(work, "stray.txt"), "x", ct);
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExportBundleAssembler.AssembleAsync([source], options, TestContext.Current.CancellationToken));

        Assert.Contains("stray.txt", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(options.OutputDirectory));
    }
}
