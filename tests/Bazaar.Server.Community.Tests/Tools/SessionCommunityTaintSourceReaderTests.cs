using System.Collections.Immutable;
using System.Text.Json;
using Bazaar.Server.Community.Tools;
using Bazaar.Server.Security.Taint;
using Xunit;

namespace Bazaar.Server.Community.Tests.Tools;

public sealed class SessionCommunityTaintSourceReaderTests
{
    private sealed class FakeTaint(SessionTaint taint) : ISessionTaintReader
    {
        public Task<SessionTaint> LoadAsync(Guid sessionId, CancellationToken cancellationToken) => Task.FromResult(taint);
    }

    [Fact]
    public async Task A_clean_session_gives_an_empty_array()
    {
        var json = await new SessionCommunityTaintSourceReader(new FakeTaint(SessionTaint.Clean)).ReadJsonAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        Assert.Equal("[]", json);
    }

    [Fact]
    public async Task Sources_are_listed_as_guid_strings_in_order()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var taint = new SessionTaint(TaintState.Hard, [new TaintSource(a, ContentTrust.External, TaintState.Hard), new TaintSource(b, ContentTrust.External, TaintState.Hard)]);

        var json = await new SessionCommunityTaintSourceReader(new FakeTaint(taint)).ReadJsonAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal([a.ToString("D"), b.ToString("D")], JsonSerializer.Deserialize<string[]>(json)!);
    }
}
