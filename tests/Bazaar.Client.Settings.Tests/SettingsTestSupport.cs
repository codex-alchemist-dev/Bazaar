using Bazaar.Server.Settings;

namespace Bazaar.Client.Settings.Tests;

internal enum Mode
{
    Quiet,
    LoudMode,
}

internal static class SettingsTestSupport
{
    internal static readonly SettingActor Operator = new("op", SettingRole.Operator);
    internal static readonly SettingActor Nobody = new("nobody", SettingRole.None);

    internal static readonly SettingKey<bool> Flag = new("host.flag", false, SettingScope.Install, SettingRole.Operator, "A switch.");
    internal static readonly SettingKey<Mode> ModeKey = new("host.mode", Mode.Quiet, SettingScope.Install, SettingRole.Operator, "A mode.");
    internal static readonly SettingKey<int> Days = new("backup.days", 7, SettingScope.Install, SettingRole.Operator, "Days between drills.", validate: v => v is < 1 or > 90 ? "Days must be 1 to 90." : null);
    internal static readonly SettingKey<string> Name = new("mail.name", "bazaar", SettingScope.Install, SettingRole.Owner, "Display name.");
    internal static readonly SettingKey<string> Bind = new("host.bind", "127.0.0.1", SettingScope.Install, SettingRole.Operator, "Bind address.", ApprovalTier.Tier2, requiresApprovalFor: v => v != "127.0.0.1");
    internal static readonly SettingKey<IReadOnlyList<string>> Hosts = new("host.allowed", ["a", "b"], SettingScope.Install, SettingRole.Operator, "Allowed hosts.");

    internal static SettingDefinition[] All => [Flag, ModeKey, Days, Name, Bind, Hosts];

    internal static SettingsSnapshot Snapshot(SettingActor actor, params (SettingDefinition Key, string? Json)[] stored) =>
        new(actor, [.. All.Select(d => new SettingsEntry(d, stored.FirstOrDefault(s => s.Key == d).Json))]);
}

internal sealed class FakeClient(SettingsSnapshot snapshot, SettingsChangeResult? reply = null) : ISettingsClient
{
    internal List<(string Name, string Json)> Writes { get; } = [];

    public Task<SettingsSnapshot> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);

    public Task<SettingsEntry?> GetAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(snapshot.Entries.FirstOrDefault(e => e.Definition.Name == name));

    public Task<SettingsChangeResult> SetAsync(string name, string valueJson, CancellationToken cancellationToken = default)
    {
        Writes.Add((name, valueJson));
        return Task.FromResult(reply ?? new SettingsChangeResult(SettingWriteOutcome.Accepted, null));
    }
}
