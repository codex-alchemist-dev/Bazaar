using Bazaar.Wire.Generators;
using Bazaar.Wire.Schema.Generated;

namespace Bazaar.Wire.Schema.Tests;

/// <summary>
/// The schema gate (doc 90 section 3 point 2). The generator renders the schema from the <c>[WirePayload]</c> records; the
/// checked-in <c>docs/wire/schema.lock.json</c> is the last published version. A breaking difference needs a protocol
/// major bump; a compatible one needs the lock (and the other artifacts) regenerated.
/// </summary>
public class WireSchemaLockTests
{
    private const string _lockFile = "schema.lock.json";

    private static SchemaDoc Current() => MiniJson.ToDoc(WireSchemaArtifacts.SchemaJson) ?? throw new InvalidOperationException("The generated schema.json does not parse.");

    private static SchemaDoc? Locked() => MiniJson.ToDoc(WireFiles.Read(_lockFile));

    [Fact]
    public void Update_checked_in_artifacts_when_asked()
    {
        if (!WireFiles.Updating) return;

        var locked = Locked();
        if (locked is not null)
        {
            var breaking = SchemaDiff.Compare(locked, Current()).Where(c => c.Breaking).ToList();
            Assert.True(breaking.Count == 0, "Refusing to update: the change is breaking.\n" + Explain(breaking));
        }

        WireFiles.Write(_lockFile, Utf8(WireSchemaArtifacts.SchemaJson));
        WireFiles.Write("json-schema.json", Utf8(WireSchemaArtifacts.JsonSchema));
        WireFiles.Write("wire.cddl", Utf8(WireSchemaArtifacts.Cddl));
    }

    [Fact]
    public void Schema_has_no_breaking_change_against_the_lock()
    {
        var locked = Locked();
        Assert.True(locked is not null, $"docs/wire/{_lockFile} is missing or unreadable. Create it with: {WireFiles.UpdateCommand}");

        var breaking = SchemaDiff.Compare(locked, Current()).Where(c => c.Breaking).ToList();

        Assert.True(
            breaking.Count == 0,
            "The wire schema has breaking changes against docs/wire/schema.lock.json:\n" + Explain(breaking)
            + "\n\nA breaking change to a published payload needs a protocol major bump (raise Bazaar.Contracts.Wire.WireProtocol.Major, doc 02 versioning),"
            + " then regenerate the lock. Otherwise revert the change: keep field order, keep field types, and add new fields at the end as nullable or with a default.");
    }

    [Fact]
    public void Checked_in_artifacts_match_the_generated_ones()
    {
        var stale = new List<string>();
        Check(_lockFile, WireSchemaArtifacts.SchemaJson, stale);
        Check("json-schema.json", WireSchemaArtifacts.JsonSchema, stale);
        Check("wire.cddl", WireSchemaArtifacts.Cddl, stale);

        Assert.True(
            stale.Count == 0,
            "These files under docs/wire differ from what the generator builds from the payload records (a compatible change, or a missing first run): "
            + string.Join(", ", stale) + $".\nRegenerate with: {WireFiles.UpdateCommand}");
    }

    [Fact]
    public void Lock_hash_is_the_hash_of_its_own_content()
    {
        var doc = Current();
        var rehashed = SchemaEmitters.ComputeHash(doc);

        Assert.Equal(WireSchemaArtifacts.SchemaHash, rehashed);
        Assert.Matches("^sha256:[0-9a-f]{64}$", rehashed);
        Assert.Contains($"\"hash\": \"{rehashed}\"", WireSchemaArtifacts.SchemaJson);
    }

    [Fact]
    public void Every_wire_payload_type_is_in_the_schema()
    {
        var types = Current().Types.Where(t => t.Payload).Select(t => t.Name).ToHashSet();
        var messages = Current().Messages;

        Assert.Contains("Bazaar.Contracts.Wire.Hello", types);
        Assert.Contains(messages, m => m.Type == "gc.send" && m.Payload == "Bazaar.Contracts.Wire.GcSend");
        Assert.Equal(WireProtocolMajor, Current().Major);
    }

    private static int WireProtocolMajor => Bazaar.Contracts.Wire.WireProtocol.Major;

    private static string Explain(IEnumerable<SchemaChange> changes) => string.Join("\n", changes.Select(c => " - " + c.Message));

    private static byte[] Utf8(string text) => new System.Text.UTF8Encoding(false).GetBytes(text);

    private static void Check(string file, string generated, List<string> stale)
    {
        if (WireFiles.Read(file).Replace("\r\n", "\n") != generated) stale.Add(file);
    }
}
