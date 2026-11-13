using Bazaar.Client.Components.Navigation;

namespace Bazaar.Client.Components.Navigation.Tests;

public class MnemonicOverrideStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-mnemonics-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string PathFor(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task Missing_file_loads_as_empty_without_a_warning()
    {
        var load = await new FileMnemonicOverrideStore(PathFor("mnemonics.json")).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(load.Document.Overrides);
        Assert.Null(load.Warning);
    }

    [Fact]
    public async Task Save_then_load_round_trips_and_leaves_no_temp_file()
    {
        var store = new FileMnemonicOverrideStore(PathFor("mnemonics.json"));
        var document = new MnemonicOverrideDocument(
            MnemonicOverrideDocument.CurrentVersion,
            [new MnemonicOverride(NavigationDefaults.GcFirstCommandId, "gf")]);

        await store.SaveAsync(document, TestContext.Current.CancellationToken);
        var load = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(load.Warning);
        Assert.Equal(new MnemonicOverride(NavigationDefaults.GcFirstCommandId, "gf"), Assert.Single(load.Document.Overrides));
        Assert.False(File.Exists(PathFor("mnemonics.json.tmp")));
    }

    [Fact]
    public async Task Corrupt_file_loads_as_empty_with_a_warning_and_does_not_throw()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(PathFor("mnemonics.json"), "{ not json", TestContext.Current.CancellationToken);

        var load = await new FileMnemonicOverrideStore(PathFor("mnemonics.json")).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(load.Document.Overrides);
        Assert.NotNull(load.Warning);
    }

    [Fact]
    public async Task Unknown_version_is_ignored_with_a_warning()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(PathFor("mnemonics.json"), """{"version": 99, "overrides": []}""", TestContext.Current.CancellationToken);

        var load = await new FileMnemonicOverrideStore(PathFor("mnemonics.json")).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(load.Document.Overrides);
        Assert.Contains("unknown version", load.Warning);
    }

    [Fact]
    public void Saved_remaps_become_person_assignments_and_unknown_commands_are_dropped()
    {
        var document = new MnemonicOverrideDocument(
            MnemonicOverrideDocument.CurrentVersion,
            [
                new MnemonicOverride(NavigationDefaults.GcFirstCommandId, "gf"),
                new MnemonicOverride("mod.removed", "zz"),
            ]);
        var titles = new Dictionary<string, string> { [NavigationDefaults.GcFirstCommandId] = "GC list, first chat" };

        var assignments = FileMnemonicOverrideStore.ToAssignments(document, titles);

        var only = Assert.Single(assignments);
        Assert.Equal(MnemonicTier.Person, only.Tier);
        Assert.Equal("gf", only.Mnemonic);
    }
}
