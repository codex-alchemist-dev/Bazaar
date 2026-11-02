using Bazaar.Client.Components.Chat;

namespace Bazaar.Client.Components.Chat.Tests;

public sealed class DevChatGcResolverTests
{
    private static readonly Guid _override = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid _cached = Guid.Parse("66666666-6666-6666-6666-666666666666");

    [Fact]
    public void Override_wins_over_the_cached_id()
    {
        var id = DevChatGcResolver.Resolve(["--dev-gc", _override.ToString("D")], _cached.ToString("D"));

        Assert.Equal(_override, id);
    }

    [Fact]
    public void Cached_id_is_used_when_there_is_no_override()
    {
        var id = DevChatGcResolver.Resolve(["--terminal"], _cached.ToString("D"));

        Assert.Equal(_cached, id);
    }

    [Fact]
    public void Resolves_to_null_when_neither_is_set()
    {
        Assert.Null(DevChatGcResolver.Resolve([], null));
        Assert.Null(DevChatGcResolver.Resolve([], "not-a-guid"));
    }

    [Fact]
    public void Override_without_a_valid_guid_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => DevChatGcResolver.Resolve(["--dev-gc", "nope"], _cached.ToString("D")));
        Assert.Throws<ArgumentException>(() => DevChatGcResolver.Resolve(["--dev-gc"], null));
    }
}
