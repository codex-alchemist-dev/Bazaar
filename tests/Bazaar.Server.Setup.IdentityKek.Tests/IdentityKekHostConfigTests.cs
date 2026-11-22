using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Setup.IdentityKek.Tests;

public sealed class IdentityKekHostConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-kekcfg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static SetupPlan Plan(string? kind) => new() { Custody = new SetupCustodySection { IdentityKek = kind } };

    private static SetupApplyContext Context(string hash = "h1") =>
        new(hash, SetupEntryPath.Cli, "operator-test", null, null, SetupStateSnapshot.Empty, []);

    private IdentityKekHostConfigStep Step() => new(() => _root);

    private string File_ => IdentityKekHostConfig.PathFor(_root);

    [Fact]
    public void The_file_is_the_nested_form_of_the_kek_module_key()
    {
        using var document = JsonDocument.Parse(IdentityKekHostConfig.Render("tpm"));

        Assert.Equal("tpm", document.RootElement.GetProperty("Bazaar").GetProperty("Identity").GetProperty("KekProvider").GetString());
        Assert.Equal("Bazaar:Identity:KekProvider", IdentityKekHostConfig.KekProviderKey);
    }

    [Theory]
    [InlineData("hsm")]
    [InlineData("")]
    [InlineData("systemd-creds")]
    public void An_unknown_kind_is_refused(string kind) =>
        Assert.Throws<ArgumentException>(() => IdentityKekHostConfig.Render(kind));

    [Fact]
    public async Task Apply_writes_the_file_and_a_second_apply_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await Step().ApplyAsync(Plan("openbao"), Context(), ct);
        var written = File.GetLastWriteTimeUtc(File_);
        var second = await Step().ApplyAsync(Plan("openbao"), Context("h2"), ct);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Contains("openbao", await File.ReadAllTextAsync(File_, ct), StringComparison.Ordinal);
        Assert.Equal(written, File.GetLastWriteTimeUtc(File_));
    }

    [Fact]
    public async Task An_absent_field_writes_nothing_and_leaves_an_existing_file()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        await File.WriteAllTextAsync(File_, "{}", Encoding.UTF8, ct);

        var result = await Step().ApplyAsync(Plan(null), Context(), ct);

        Assert.True(result.Succeeded);
        Assert.Equal("{}", await File.ReadAllTextAsync(File_, ct));
    }

    [Fact]
    public async Task An_absent_field_creates_no_file()
    {
        var result = await Step().ApplyAsync(Plan(null), Context(), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(File_));
    }

    [Fact]
    public async Task Rollback_removes_a_file_the_run_created()
    {
        var ct = TestContext.Current.CancellationToken;
        var step = Step();
        await step.ApplyAsync(Plan("file"), Context(), ct);

        await step.RollbackAsync(Plan("file"), Context(), ct);

        Assert.False(File.Exists(File_));
    }

    [Fact]
    public async Task Rollback_restores_the_previous_file()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        var before = IdentityKekHostConfig.Render("file");
        await File.WriteAllBytesAsync(File_, before, ct);
        var step = Step();
        await step.ApplyAsync(Plan("tpm"), Context(), ct);

        await step.RollbackAsync(Plan("tpm"), Context(), ct);

        Assert.Equal(before, await File.ReadAllBytesAsync(File_, ct));
    }

    [Fact]
    public async Task A_write_failure_is_a_failed_step_that_does_not_echo_the_path()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "config"), "a file where the folder should be", ct);

        var result = await Step().ApplyAsync(Plan("file"), Context(), ct);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(_root, result.Message ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void The_step_covers_only_the_identity_kek_section()
    {
        Assert.Equal(["custody.identityKek"], Step().Covers);
        Assert.Equal("identity.kek.hostconfig", Step().Id);
    }
}
