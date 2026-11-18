using Bazaar.Client.Core.Input;
using Bazaar.Ex;

namespace Bazaar.Client.Calcinato.Mnemonics;

/// <summary>
/// The commands the Calcinato module contributes, with their default mnemonics (doc 55, section 7). The palette lists all of
/// them. The client-wide table takes the ones it can hold (<see cref="BridgeAssignments"/>).
/// </summary>
public static class CalcinatoCommands
{
    /// <summary>The module id in the client manifest. It is dotted, because the manifest validator requires a dotted id.</summary>
    public const string ModuleId = "bazaar.calcinato";

    public const string TerminalCommandId = "calcinato.terminal";
    public const string NewTerminalCommandId = "calcinato.newterm";
    public const string AttachCommandId = "calcinato.attach";
    public const string InterruptCommandId = "calcinato.intr";

    /// <summary>Every command with its default mnemonic, in declaration order.</summary>
    public static IReadOnlyList<MnemonicAssignment> Assignments { get; } = new[]
    {
        new MnemonicAssignment(TerminalCommandId, "Terminal, open the terminal pane", "terminal", MnemonicTier.Module, ModuleId),
        new MnemonicAssignment(NewTerminalCommandId, "New terminal in this pane", "newterm", MnemonicTier.Module, ModuleId),
        new MnemonicAssignment(AttachCommandId, "Attach to a server terminal", "attach", MnemonicTier.Module, ModuleId),
        new MnemonicAssignment(InterruptCommandId, "Send Ctrl-C to the shell", "intr", MnemonicTier.Module, ModuleId),
    };

    /// <summary>
    /// The assignments the client-wide table can hold. Bazaar.Ex reserves the letter i (Insert), so <c>intr</c> is left out.
    /// The modal engine sends it as a built-in, and only inside a leader sequence.
    /// </summary>
    public static IReadOnlyList<MnemonicAssignment> BridgeAssignments { get; } = Assignments
        .Where(assignment => assignment.Mnemonic[0] != MnemonicTable.ReservedInsertKey)
        .ToList();

    /// <summary>Command ids in declaration order, as the manifest lists them.</summary>
    public static IReadOnlyList<string> CommandIds { get; } = Assignments.Select(assignment => assignment.CommandId).ToList();
}
