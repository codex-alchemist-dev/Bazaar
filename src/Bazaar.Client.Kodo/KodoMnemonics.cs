namespace Bazaar.Client.Kodo;

/// <summary>One proposed command for the palette: stable id, title and mnemonic (doc 46 section 14.4).</summary>
public sealed record KodoMnemonic(string CommandId, string Title, string Mnemonic);

/// <summary>
/// The Kodo mnemonics this package proposes (doc 46 section 14.4, PROPOSED). This is a seam: the shared registry lives in
/// <c>Bazaar.Ex</c> (<c>MnemonicAssignment</c>, built-in tier), and a head turns these rows into assignments when it
/// composes its command set. This project does not reference that registry, so it neither registers nor resolves clashes.
/// </summary>
public static class KodoMnemonics
{
    public const string MonitorCommandId = "kodo.monitor.open";
    public const string StatusCommandId = "kodo.status.open";
    public const string FleetCommandId = "kodo.fleet.open";

    /// <summary>The Monitor, Status and Fleet commands.</summary>
    public static IReadOnlyList<KodoMnemonic> Proposed { get; } =
    [
        new(MonitorCommandId, "Monitor", "kodo"),
        new(StatusCommandId, "Monitor status", "kstat"),
        new(FleetCommandId, "Monitor fleet", "kfleet"),
    ];
}
