using Avalonia.Controls;
using Bazaar.Client.Input;
using Bazaar.Contracts.Modules;

namespace Bazaar.Ex.Head;

/// <summary>
/// The one call a head makes to get keyboard mnemonics (doc 41 section 4.12). It gathers the first-party navigation defaults, the
/// commands the loaded client modules contributed and the saved remaps into a <see cref="MnemonicRuntime"/>, then creates a
/// <see cref="MnemonicShell"/> whose palette and screen-reader region live in the panel the head provides. The head decides what
/// a chosen command does by listening to <see cref="MnemonicShell.CommandInvoked"/>.
/// </summary>
public static class MnemonicHeadSetup
{
    /// <summary>The typed mnemonic that opens the command palette. It is reserved: a module cannot take it.</summary>
    public const string PaletteMnemonic = "palette";

    /// <summary>The file, inside a head's state folder, that holds this person's mnemonic remaps.</summary>
    public const string OverridesFileName = "mnemonics.json";

    /// <summary>
    /// Builds the runtime and the shell. The host's current content moves into a <see cref="Grid"/> that also carries the palette
    /// (added while open) and the screen-reader region, so call this after the head has set its content. Mnemonics stay off, and
    /// the reason is logged, when the combined defaults do not resolve.
    /// </summary>
    public static async Task<MnemonicShell> InstallAsync(
        TopLevel top,
        ContentControl host,
        IEnumerable<ClientCommandContribution> moduleCommands,
        string overridesPath,
        Action<string> log,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(top);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(moduleCommands);
        ArgumentException.ThrowIfNullOrWhiteSpace(overridesPath);
        ArgumentNullException.ThrowIfNull(log);

        // A module that contributes one of the shell's own navigation commands (the Constellation opens itself, for one) owns it:
        // the shell default for that id is dropped, so the same command is not assigned twice in one tier.
        var contributed = MnemonicAssignment.FromContributions(moduleCommands);
        var owned = contributed.Select(a => a.CommandId).ToHashSet(StringComparer.Ordinal);
        var defaults = NavigationDefaults.Assignments
            .Where(a => !owned.Contains(a.CommandId))
            .Concat(contributed)
            .ToList();
        var runtime = await MnemonicRuntime.CreateAsync(
            defaults,
            new FileMnemonicOverrideStore(overridesPath),
            PaletteMnemonic.Select(c => KeyStroke.OfChar(c)),
            new MnemonicNoticeLog(message => log($"Bazaar: {message}")),
            ct: ct).ConfigureAwait(true);
        if (runtime.Warning is { } warning)
        {
            log($"Bazaar: {warning}");
        }

        var overlay = new Grid();
        var content = host.Content as Control;
        host.Content = null;
        if (content is not null)
        {
            overlay.Children.Add(content);
        }

        host.Content = overlay;
        var region = new MnemonicLiveRegion();
        overlay.Children.Add(region.Control);
        return new MnemonicShell(
            top,
            runtime,
            region,
            view =>
            {
                overlay.Children.Add(view);
                view.FocusQuery();
            },
            view => overlay.Children.Remove(view));
    }
}
