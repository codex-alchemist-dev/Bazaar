#pragma warning disable CA1861 // Test data is written inline as constant arrays.
using Bazaar.Client.Calcinato.Abstractions;
using Bazaar.Client.Calcinato.Mnemonics;
using Bazaar.Client.Calcinato.Modal;
using Bazaar.Client.Calcinato.Tests.Modal;
using Bazaar.Client.Core.Input;
using Bazaar.Ex;
using static Bazaar.Client.Calcinato.Tests.Modal.Input;

namespace Bazaar.Client.Calcinato.Tests.Mnemonics;

public sealed class ExMnemonicBridgeTests
{
    private static readonly IReadOnlyList<KeyStroke> _paletteKeys = "palette".Select(c => KeyStroke.OfChar(c)).ToArray();

    private static MnemonicResolution ClientWide(params MnemonicAssignment[] extra) =>
        MnemonicResolver.Resolve(NavigationDefaults.Assignments.Concat(CalcinatoCommands.BridgeAssignments).Concat(extra));

    private static ExMnemonicBridge Bridge(List<string> ran, MnemonicResolution? resolution = null) =>
        ExMnemonicBridge.TryCreate(resolution ?? ClientWide(), _paletteKeys, id =>
        {
            ran.Add(id);
            return $"Ran {id}";
        }) ?? throw new InvalidOperationException("The client-wide table did not build.");

    [Fact]
    public void BridgeResolvesAClientWideCommandThroughTheExTable()
    {
        var ran = new List<string>();
        var bridge = Bridge(ran);

        bool handled = bridge.TryHandle("ngc1", out string? speech);

        Assert.True(handled);
        Assert.Equal("Ran nav.gc.first", speech);
        Assert.Equal(new[] { NavigationDefaults.GcFirstCommandId }, ran);
    }

    [Fact]
    public void BridgeResolvesTheModuleCommands()
    {
        var ran = new List<string>();
        var bridge = Bridge(ran);

        Assert.True(bridge.TryHandle("terminal", out _));
        Assert.True(bridge.TryHandle("newterm", out _));
        Assert.True(bridge.TryHandle("attach", out _));

        Assert.Equal(
            new[] { CalcinatoCommands.TerminalCommandId, CalcinatoCommands.NewTerminalCommandId, CalcinatoCommands.AttachCommandId },
            ran);
    }

    [Fact]
    public void AnUnknownSequenceIsNotHandled()
    {
        var ran = new List<string>();

        bool handled = Bridge(ran).TryHandle("nosuch", out string? speech);

        Assert.False(handled);
        Assert.Null(speech);
        Assert.Empty(ran);
    }

    [Theory]
    [InlineData("n", true, false)]
    [InlineData("ngc", true, false)]
    [InlineData("ngc1", false, true)]
    [InlineData("newter", true, false)]
    [InlineData("newterm", false, true)]
    [InlineData("zz", false, false)]
    public void PrefixAndExactReportWhatTheTableKnows(string typed, bool prefix, bool exact)
    {
        var bridge = Bridge(new List<string>());

        Assert.Equal(prefix, bridge.IsPrefix(typed));
        Assert.Equal(exact, bridge.IsExact(typed));
    }

    [Fact]
    public void ExactMatchThatIsAlsoAPrefixReportsBoth()
    {
        var bridge = Bridge(new List<string>(), ClientWide(new MnemonicAssignment("test.ngc", "Test", "ngc", MnemonicTier.Person, "test")));

        Assert.True(bridge.IsExact("ngc"));
        Assert.True(bridge.IsPrefix("ngc"));
    }

    [Fact]
    public void TheClientWideTableIsRefusedWhenAModuleCollidesInItsOwnTier()
    {
        var clash = new MnemonicAssignment("test.clash", "Clash", "terminal", MnemonicTier.Module, "test");

        var bridge = ExMnemonicBridge.TryCreate(ClientWide(clash), _paletteKeys, _ => null);

        Assert.Null(bridge);
    }

    [Fact]
    public void IntrIsKeptOutOfTheBridgeBecauseBazaarExReservesI()
    {
        Assert.Contains(CalcinatoCommands.Assignments, a => a.Mnemonic == "intr");
        Assert.DoesNotContain(CalcinatoCommands.BridgeAssignments, a => a.Mnemonic == "intr");

        var resolution = MnemonicResolver.Resolve(CalcinatoCommands.Assignments);
        Assert.False(resolution.IsValid);
        Assert.Contains(resolution.Errors, error => error.Contains("reserved Insert key", StringComparison.Ordinal));
    }

    [Fact]
    public void EngineRunsAClientWideCommandFromALeaderSequence()
    {
        var ran = new List<string>();
        var engine = new ModalEngine(new FakeScreen(1, 40, Array.Empty<string>(), new[] { "$ " }), Bridge(ran));

        var effects = Keys(engine, " newterm");

        Assert.Equal(new[] { CalcinatoCommands.NewTerminalCommandId }, ran);
        Assert.Equal("Ran calcinato.newterm", LastStatus(effects));
    }

    [Fact]
    public void EngineRunsAnNgcMnemonicThroughTheLeaderEvenThoughNIsAMotion()
    {
        var ran = new List<string>();
        var engine = new ModalEngine(new FakeScreen(1, 40, Array.Empty<string>(), new[] { "$ " }), Bridge(ran));

        Keys(engine, " ngc1");

        Assert.Equal(new[] { NavigationDefaults.GcFirstCommandId }, ran);
    }

    [Fact]
    public void TryCreateRefusesAResolutionWithErrors()
    {
        var resolution = MnemonicResolver.Resolve(CalcinatoCommands.Assignments);

        Assert.Null(ExMnemonicBridge.TryCreate(resolution, _paletteKeys, _ => null));
    }

    [Fact]
    public void PaletteCanListEveryCalcinatoCommandWithItsMnemonic()
    {
        var rows = MnemonicListing.Build(NavigationDefaults.Assignments.Concat(CalcinatoCommands.Assignments));

        foreach (var assignment in CalcinatoCommands.Assignments)
        {
            Assert.Contains(rows, row => row.CommandId == assignment.CommandId && row.Mnemonic == assignment.Mnemonic && row.Source == CalcinatoCommands.ModuleId);
        }

        // Ranked first: the mnemonic prefix match. Fuzzy subsequence matches of other rows may follow it.
        var matches = MnemonicPalette.Filter(rows, "intr");
        Assert.Equal(CalcinatoCommands.InterruptCommandId, matches[0].CommandId);
    }

    [Fact]
    public void PaletteFilterFindsTheTerminalCommandByTitle()
    {
        var rows = MnemonicListing.Build(CalcinatoCommands.Assignments);

        var matches = MnemonicPalette.Filter(rows, "Terminal");

        Assert.Contains(matches, row => row.CommandId == CalcinatoCommands.TerminalCommandId);
    }
}
