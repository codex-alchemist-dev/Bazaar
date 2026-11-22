using Bazaar.Client.Input;

namespace Bazaar.Ex.Tests;

public class MnemonicRuntimeTests
{
    private static readonly KeyStroke[] _palette = [KeyStroke.OfChar('p')];

    private sealed class MemoryStore(MnemonicOverrideLoad initial) : IMnemonicOverrideStore
    {
        public MnemonicOverrideDocument? Saved { get; private set; }

        public Task<MnemonicOverrideLoad> LoadAsync(CancellationToken ct = default) => Task.FromResult(initial);

        public Task SaveAsync(MnemonicOverrideDocument document, CancellationToken ct = default)
        {
            Saved = document;
            return Task.CompletedTask;
        }
    }

    private static MemoryStore EmptyStore() => new(new MnemonicOverrideLoad(MnemonicOverrideDocument.Empty, null));

    private static Task<MnemonicRuntime> Create(MemoryStore store, List<string>? log = null) =>
        MnemonicRuntime.CreateAsync(NavigationDefaults.Assignments, store, _palette, new MnemonicNoticeLog((log ?? []).Add), ct: TestContext.Current.CancellationToken);

    [Fact]
    public async Task Create_with_no_saved_remaps_has_a_session_and_the_default_rows()
    {
        var runtime = await Create(EmptyStore());

        Assert.NotNull(runtime.Session);
        Assert.Null(runtime.Warning);
        Assert.Equal(MnemonicOverrideDocument.Empty, runtime.Document);
        Assert.Contains(runtime.Rows, r => r.CommandId == NavigationDefaults.SettingsCommandId);
    }

    [Fact]
    public async Task Remap_saves_rebuilds_the_rows_and_raises_changed()
    {
        var store = EmptyStore();
        var runtime = await Create(store);
        var changed = 0;
        runtime.Changed += (_, _) => changed++;

        var result = await runtime.RemapAsync(NavigationDefaults.SettingsCommandId, "zz", TestContext.Current.CancellationToken);

        Assert.True(result.Accepted);
        Assert.Equal(1, changed);
        Assert.Contains(store.Saved!.Overrides, o => o.CommandId == NavigationDefaults.SettingsCommandId && o.Mnemonic == "zz");
        Assert.Contains(runtime.Rows, r => r.Mnemonic == "zz" && r.CommandId == NavigationDefaults.SettingsCommandId);
    }

    [Fact]
    public async Task A_refused_remap_saves_nothing_and_raises_nothing()
    {
        var store = EmptyStore();
        var runtime = await Create(store);
        var changed = 0;
        runtime.Changed += (_, _) => changed++;
        var taken = NavigationDefaults.Assignments.First(a => a.CommandId != NavigationDefaults.SettingsCommandId && !a.Alias).Mnemonic;

        var result = await runtime.RemapAsync(NavigationDefaults.SettingsCommandId, taken, TestContext.Current.CancellationToken);

        Assert.False(result.Accepted);
        Assert.Null(store.Saved);
        Assert.Equal(0, changed);
        Assert.Equal(MnemonicOverrideDocument.Empty, runtime.Document);
    }

    [Fact]
    public async Task Reset_drops_the_remap_and_restores_the_default_row()
    {
        var store = EmptyStore();
        var runtime = await Create(store);
        await runtime.RemapAsync(NavigationDefaults.SettingsCommandId, "zz", TestContext.Current.CancellationToken);

        var result = await runtime.ResetAsync(NavigationDefaults.SettingsCommandId, TestContext.Current.CancellationToken);

        Assert.True(result.Accepted);
        Assert.Empty(store.Saved!.Overrides);
        Assert.DoesNotContain(runtime.Rows, r => r.Mnemonic == "zz");
    }

    [Fact]
    public async Task Saved_remaps_are_applied_on_create()
    {
        var saved = new MnemonicOverrideDocument(
            MnemonicOverrideDocument.CurrentVersion,
            [new MnemonicOverride(NavigationDefaults.SettingsCommandId, "zz")]);

        var runtime = await Create(new MemoryStore(new MnemonicOverrideLoad(saved, null)));

        Assert.Null(runtime.Warning);
        Assert.Contains(runtime.Rows, r => r.Mnemonic == "zz" && r.CommandId == NavigationDefaults.SettingsCommandId);
    }

    [Fact]
    public async Task A_load_warning_is_kept()
    {
        var runtime = await Create(new MemoryStore(new MnemonicOverrideLoad(MnemonicOverrideDocument.Empty, "bad file")));

        Assert.Equal("bad file", runtime.Warning);
        Assert.NotNull(runtime.Session);
    }

    [Fact]
    public async Task Saved_remaps_that_do_not_resolve_are_ignored_with_a_warning()
    {
        // Two commands remapped to the same mnemonic in one tier is an error the resolver refuses.
        var commands = NavigationDefaults.Assignments.Where(a => !a.Alias).Select(a => a.CommandId).Distinct().Take(2).ToList();
        var saved = new MnemonicOverrideDocument(
            MnemonicOverrideDocument.CurrentVersion,
            [new MnemonicOverride(commands[0], "zz"), new MnemonicOverride(commands[1], "zz")]);

        var runtime = await Create(new MemoryStore(new MnemonicOverrideLoad(saved, null)));

        Assert.NotNull(runtime.Session);
        Assert.NotNull(runtime.Warning);
        Assert.Equal(MnemonicOverrideDocument.Empty, runtime.Document);
    }

    [Fact]
    public async Task Notices_are_written_to_the_log_once()
    {
        var log = new List<string>();
        var saved = new MnemonicOverrideDocument(
            MnemonicOverrideDocument.CurrentVersion,
            [new MnemonicOverride(NavigationDefaults.SettingsCommandId, "zz")]);
        var runtime = await Create(new MemoryStore(new MnemonicOverrideLoad(saved, null)), log);

        await runtime.ResetAsync("nav.none", TestContext.Current.CancellationToken);
        var before = log.Count;
        await runtime.ResetAsync("nav.none", TestContext.Current.CancellationToken);

        Assert.Equal(before, log.Count);
    }
}
