using System.Text.Json;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Edit;
using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Tests.Gc.Edit;

public class MessageHistoryGoldenTests
{
    private static readonly Guid GcId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MessageId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AuthorId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    // Indented copy of GcJson.Options so the golden file can be reviewed by eye.
    private static readonly JsonSerializerOptions IndentedOptions = new(GcJson.Options) { WriteIndented = true };

    [Fact]
    public void Three_version_history_matches_the_golden_json()
    {
        var versions = new[]
        {
            new MessageVersionRecord(GcId, MessageId, 1, "first draft", AuthorId, 10, T0.AddMinutes(1)),
            new MessageVersionRecord(GcId, MessageId, 2, "second draft", AuthorId, 20, T0.AddMinutes(2)),
            new MessageVersionRecord(GcId, MessageId, 3, "final text", AuthorId, 30, T0.AddMinutes(3)),
        };

        var result = MessageHistoryReader.GetHistory(
            versions,
            new GcViewer(AuthorId, PrincipalKind.Human),
            _ => true);

        var history = Assert.IsType<MessageHistoryFound>(result).History;

        var actual = JsonSerializer.Serialize(history, IndentedOptions);

        var path = Path.Combine(AppContext.BaseDirectory, "Gc", "Edit", "Golden", "history-three-versions.json");
        var expected = File.ReadAllText(path).ReplaceLineEndings("\n").TrimEnd('\n');

        Assert.Equal(expected, actual.ReplaceLineEndings("\n"));
    }
}
