using System.Text.Json;
using Bazaar.Server.Security.Readers;

namespace Bazaar.Server.Tests.Security.Readers;

/// <summary>Shared fixtures for the hand-over tests: one test-only schema and helpers to parse payloads.</summary>
internal static class ReaderTestData
{
    /// <summary>The only schema the tests register. The server ships none.</summary>
    public static HandoverSchema SampleSchema { get; } = HandoverSchema.Create(
        "test.sample",
        new StringField("subject", 20),
        new EnumField("priority", ["low", "medium", "high"]),
        new StringListField("tags", 3, 10, Required: false),
        new IntegerField("score", 0, 10),
        new BoolField("urgent", Required: false),
        new UrlField("link", 200, Required: false));

    /// <summary>A valid payload with only the required fields.</summary>
    public const string MinimalJson = """{"subject":"Hello","priority":"low","score":1}""";

    public static HandoverValidator Validator() =>
        new(new HandoverSchemaRegistry.Builder().Register(SampleSchema).Build());

    /// <summary>Parses JSON text into a detached element, so the tests use the same type the reader path does.</summary>
    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;
}
