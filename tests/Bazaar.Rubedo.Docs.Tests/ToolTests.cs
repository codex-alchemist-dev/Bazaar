using Bazaar.Rubedo.Docs.Tool;

namespace Bazaar.Rubedo.Docs.Tests;

public sealed class ToolTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rubedo-docs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static int Run(params string[] args) => DocsToolRunner.Run(args, TextWriter.Null, TextWriter.Null);

    [Fact]
    public void Out_then_check_succeeds()
    {
        Assert.Equal(0, Run("--out", _folder));
        Assert.Equal(0, Run("--check", _folder));
        Assert.Equal(RubedoReferenceGenerator.Generate().Count, Directory.GetFiles(_folder, "*.md").Length);
    }

    [Fact]
    public void Check_fails_on_a_changed_missing_or_extra_page()
    {
        Run("--out", _folder);

        File.AppendAllText(Path.Combine(_folder, "functions.md"), "x\n");
        Assert.Equal(1, Run("--check", _folder));

        Run("--out", _folder);
        File.Delete(Path.Combine(_folder, "limits.md"));
        Assert.Equal(1, Run("--check", _folder));

        Run("--out", _folder);
        File.WriteAllText(Path.Combine(_folder, "extra.md"), "x\n");
        Assert.Equal(1, Run("--check", _folder));
    }

    [Fact]
    public void Check_fails_on_a_missing_folder_and_out_removes_stale_pages()
    {
        Assert.Equal(1, Run("--check", _folder));

        Run("--out", _folder);
        File.WriteAllText(Path.Combine(_folder, "stale.md"), "x\n");
        Run("--out", _folder);
        Assert.False(File.Exists(Path.Combine(_folder, "stale.md")));
    }

    [Fact]
    public void A_leading_separator_is_accepted()
    {
        Assert.Equal(0, Run("--", "--out", _folder));
    }

    [Fact]
    public void Bad_usage_returns_2()
    {
        Assert.Equal(2, Run());
        Assert.Equal(2, Run("--out"));
        Assert.Equal(2, Run("--bogus", _folder));
    }
}
