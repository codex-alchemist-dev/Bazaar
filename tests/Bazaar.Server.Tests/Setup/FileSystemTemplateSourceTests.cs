using System.Text;
using Bazaar.Server.Setup;

namespace Bazaar.Server.Tests.Setup;

/// <summary>The folder source: the same content loads and hashes the same from disk, and nothing outside the folder is read.</summary>
public sealed class FileSystemTemplateSourceTests
{
    /// <summary>Runs <paramref name="test"/> with a fresh temporary folder that holds the starter template, then removes it.</summary>
    private static async Task WithStarterFolderAsync(Func<string, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "bazaar-template-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "seeds", "charters"));
            await File.WriteAllTextAsync(Path.Combine(root, SetupTemplateConstants.ManifestFileName), TemplateFixtures.Manifest, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(root, "seeds", "constitution.md"), TemplateFixtures.ConstitutionText, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(root, "seeds", "charters", "reviewer.md"), TemplateFixtures.CharterText, new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path.Combine(root, "seeds", "vault-layout.md"), TemplateFixtures.VaultText, new UTF8Encoding(false));
            await test(root);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_template_folder_on_disk_loads_and_hashes_the_same_as_the_in_memory_copy()
    {
        await WithStarterFolderAsync(async root =>
        {
            var fromDisk = await TemplateFixtures.Load(new FileSystemTemplateSource(root));
            var inMemory = await TemplateFixtures.Load(TemplateFixtures.Starter());

            Assert.Empty(fromDisk.Issues);
            Assert.Equal(inMemory.TemplateHash, fromDisk.TemplateHash);
        });
    }

    [Fact]
    public async Task A_missing_file_is_reported_as_missing()
    {
        await WithStarterFolderAsync(async root =>
        {
            var read = await new FileSystemTemplateSource(root).ReadAsync("seeds/none.md", 1024, TestContext.Current.CancellationToken);

            Assert.Equal(TemplateFileStatus.Missing, read.Status);
            Assert.Null(read.Bytes);
        });
    }

    [Fact]
    public async Task A_path_that_leaves_the_folder_is_rejected_and_never_read()
    {
        await WithStarterFolderAsync(async root =>
        {
            var read = await new FileSystemTemplateSource(root).ReadAsync("../outside.md", 1024, TestContext.Current.CancellationToken);

            Assert.Equal(TemplateFileStatus.Rejected, read.Status);
            Assert.Null(read.Bytes);
        });
    }

    [Fact]
    public async Task A_file_over_the_limit_is_reported_as_too_large_without_being_returned()
    {
        await WithStarterFolderAsync(async root =>
        {
            var read = await new FileSystemTemplateSource(root).ReadAsync(SetupTemplateConstants.ManifestFileName, 16, TestContext.Current.CancellationToken);

            Assert.Equal(TemplateFileStatus.TooLarge, read.Status);
            Assert.Null(read.Bytes);
        });
    }

    [Fact]
    public async Task A_symbolic_link_is_rejected_and_not_followed()
    {
        if (OperatingSystem.IsWindows())
        {
            // Creating links needs a privilege on Windows; the rule is the same there and is covered on Linux.
            return;
        }

        await WithStarterFolderAsync(async root =>
        {
            var outside = Path.Combine(Path.GetTempPath(), "bazaar-outside-" + Guid.NewGuid().ToString("N") + ".md");
            try
            {
                await File.WriteAllTextAsync(outside, "# Outside\n");
                File.CreateSymbolicLink(Path.Combine(root, "seeds", "linked.md"), outside);

                var read = await new FileSystemTemplateSource(root).ReadAsync("seeds/linked.md", 1024, TestContext.Current.CancellationToken);

                Assert.Equal(TemplateFileStatus.Rejected, read.Status);
                Assert.Null(read.Bytes);
            }
            finally
            {
                File.Delete(outside);
            }
        });
    }
}
