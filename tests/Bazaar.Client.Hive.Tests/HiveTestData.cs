using System.Text.Json;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Hive.Grants;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Hive.Tests;

/// <summary>Shared builders: a pane descriptor from JSON text, the registry, and a recording fake of the grant actions.</summary>
internal static class HiveTestData
{
    public const string Model = """
        {"reviewDays":30,
         "grants":[
           {"fromProject":"alpha","toProject":"beta","kind":"procedural","grantedBy":"owner"},
           {"fromProject":"beta","toProject":"alpha","kind":"procedural","grantedBy":"owner"},
           {"fromProject":"alpha","toProject":"gamma","kind":"failure","grantedBy":"owner"}],
         "copies":[
           {"path":"hive/procedure/retry.md","originProject":"alpha","originKind":"procedural","trust":"T2","state":"active","reviewUntil":null},
           {"path":"hive/failure/timeout.md","originProject":"alpha","originKind":"failure","trust":"T2","state":"inReview","reviewUntil":"2026-12-18"},
           {"path":"hive/procedure/old.md","originProject":"beta","originKind":"procedural","trust":"T3","state":"deprecated","reviewUntil":null}]}
        """;

    public static PaneDescriptor Descriptor(string json, string kind = PaneKinds.Hive) =>
        new(kind, "Hive", string.Empty, System.Text.Json.JsonElement.Parse(json));

    public static PaneRegistry CreateRegistry(IHiveGrantActions? actions = null)
    {
        var context = actions is null ? new PaneModuleContext() : new PaneModuleContext().With(actions);
        return PaneRegistry.Compose(HivePanes.Modules, context);
    }
}

/// <summary>Records each request and returns scripted results. A null <see cref="Result"/> makes it throw, like a lost connection.</summary>
internal sealed class FakeHiveGrantActions : IHiveGrantActions
{
    public List<(string Op, HiveGrantKey Key)> Calls { get; } = [];

    public HiveGrantResult? Result { get; set; } = new(true, string.Empty);

    public Task<HiveGrantResult> GrantAsync(HiveGrantKey key, CancellationToken ct) => Record("grant", key);

    public Task<HiveGrantResult> RevokeAsync(HiveGrantKey key, CancellationToken ct) => Record("revoke", key);

    private Task<HiveGrantResult> Record(string op, HiveGrantKey key)
    {
        Calls.Add((op, key));
        return Result is { } result
            ? Task.FromResult(result)
            : Task.FromException<HiveGrantResult>(new InvalidOperationException("connection lost"));
    }
}
