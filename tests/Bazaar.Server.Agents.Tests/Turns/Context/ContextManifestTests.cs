using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Context;
using static Bazaar.Server.Tests.Agents.Turns.Context.ContextTestData;

namespace Bazaar.Server.Tests.Agents.Turns.Context;

public class ContextManifestTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] _expectedTopLevelOrder = ["schemaVersion", "agentId", "turnId", "role", "budgets", "sections"];

    private const string _memoryMarker = "CONFIDENTIAL-MEMORY-BODY-4471";
    private const string _inboxMarker = "PRIVATE-INBOX-LINE-9920";
    private const string _backreadMarker = "SECRET-GC-MESSAGE-3085";

    private static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static async Task<(ContextPlan Plan, JsonElement Root)> BuildWithManifestAsync()
    {
        var budget = ContextBudget.Defaults;
        var assembler = Assembler(
            budget,
            Item(ContextSection.System, "sys-constitution", "You are a careful agent.", "sv3"),
            Item(ContextSection.Memory, "note-42", Sized(_memoryMarker, 8), "mv11"),
            Item(ContextSection.GcBackread, "gc-trigger", Sized(_backreadMarker, 6), "gv7"),
            Item(ContextSection.Inbox, "inbox-5", _inboxMarker, "iv2"));

        var plan = await assembler.BuildAsync(Request("gc-trigger", 7), Ct);
        using var doc = JsonDocument.Parse(plan.ManifestJson);
        return (plan, doc.RootElement.Clone());
    }

    [Fact]
    public async Task Manifest_lists_each_section_with_item_ids_version_ids_and_hashes()
    {
        var (_, root) = await BuildWithManifestAsync();

        var sections = root.GetProperty("sections").EnumerateArray().ToList();
        Assert.Equal(
            ["System", "Memory", "TaskState", "Vault", "GcBackread", "Inbox"],
            sections.Select(s => s.GetProperty("section").GetString()));

        var memory = sections[1].GetProperty("items").EnumerateArray().Single();
        Assert.Equal("note-42", memory.GetProperty("itemId").GetString());
        Assert.Equal("mv11", memory.GetProperty("version").GetString());
        Assert.Equal(Sha256Hex(Sized(_memoryMarker, 8)), memory.GetProperty("sha256").GetString());

        var backread = sections[4].GetProperty("items").EnumerateArray().Single();
        Assert.Equal("gc-trigger", backread.GetProperty("itemId").GetString());
        Assert.Equal("gv7", backread.GetProperty("version").GetString());
        Assert.Equal(Sha256Hex(Sized(_backreadMarker, 6)), backread.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Manifest_records_the_budgets_and_the_tokens_used_per_section()
    {
        var (_, root) = await BuildWithManifestAsync();

        var budgets = root.GetProperty("budgets");
        Assert.Equal(4000, budgets.GetProperty("systemTokens").GetInt32());
        Assert.Equal(4000, budgets.GetProperty("memoryTokens").GetInt32());
        Assert.Equal(2000, budgets.GetProperty("taskTokens").GetInt32());
        Assert.Equal(4000, budgets.GetProperty("vaultTokens").GetInt32());
        Assert.Equal(12000, budgets.GetProperty("backreadTokens").GetInt32());

        var sections = root.GetProperty("sections").EnumerateArray().ToList();
        Assert.Equal(8, sections[1].GetProperty("tokens").GetInt32());
        Assert.Equal(6, sections[4].GetProperty("tokens").GetInt32());
    }

    [Fact]
    public async Task Manifest_contains_no_item_text()
    {
        var (plan, _) = await BuildWithManifestAsync();

        Assert.DoesNotContain(_memoryMarker, plan.ManifestJson);
        Assert.DoesNotContain(_backreadMarker, plan.ManifestJson);
        Assert.DoesNotContain(_inboxMarker, plan.ManifestJson);
        Assert.DoesNotContain("You are a careful agent.", plan.ManifestJson);
    }

    [Fact]
    public async Task Manifest_carries_the_turn_identity_role_and_schema_version()
    {
        var (_, root) = await BuildWithManifestAsync();

        // Schema version 1 is the documented value; bump it (and this test) when a property changes.
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(AgentId.ToString(), root.GetProperty("agentId").GetString());
        Assert.Equal(TurnId.ToString(), root.GetProperty("turnId").GetString());
        Assert.Equal("Actor", root.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Manifest_uses_a_stable_property_order_and_camel_case_keys()
    {
        var (plan, _) = await BuildWithManifestAsync();

        // Property order is part of the contract: the manifest is compared as text when a turn is replayed.
        var order = _expectedTopLevelOrder
            .Select(name => plan.ManifestJson.IndexOf($"\"{name}\":", StringComparison.Ordinal))
            .ToArray();
        Assert.All(order, index => Assert.True(index >= 0));
        Assert.Equal(order.OrderBy(i => i).ToArray(), order);
        Assert.DoesNotContain("\n", plan.ManifestJson);
    }
}
