using System.Text;
using Bazaar.Server.Gateway.Adapters.Common;

namespace Bazaar.Server.Tests.Gateway.Adapters;

public class SseReaderTests
{
    private static async Task<List<SseMessage>> Read(string text)
    {
        var list = new List<SseMessage>();
        await foreach (var m in SseReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), TestContext.Current.CancellationToken))
        {
            list.Add(m);
        }

        return list;
    }

    [Fact]
    public async Task Parses_events_data_and_comments()
    {
        var got = await Read(": keepalive\nevent: a\ndata: one\n\ndata: two\ndata: three\n\n");
        Assert.Equal([new SseMessage("a", "one"), new SseMessage(null, "two\nthree")], got);
    }

    [Fact]
    public async Task Flushes_unterminated_final_event()
    {
        var got = await Read("data: last");
        Assert.Equal([new SseMessage(null, "last")], got);
    }
}
