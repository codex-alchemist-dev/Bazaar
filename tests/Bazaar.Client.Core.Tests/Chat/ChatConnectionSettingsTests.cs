using Bazaar.Client.Core.Chat;

namespace Bazaar.Client.Core.Tests.Chat;

public class ChatConnectionSettingsTests
{
    private static readonly string _gc = "0196a6f0-0000-7000-8000-000000000001";

    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return key => map.TryGetValue(key, out var value) ? value : null;
    }

    [Fact]
    public void Returns_null_when_nothing_is_configured()
    {
        Assert.Null(ChatConnectionSettings.FromEnvironment(Env()));
    }

    [Fact]
    public void Reads_all_values_and_defaults_the_name()
    {
        var settings = ChatConnectionSettings.FromEnvironment(Env(
            (ChatConnectionSettings.ServerVariable, " wss://bazaar.example/ws "),
            (ChatConnectionSettings.GcVariable, _gc),
            (ChatConnectionSettings.TokenVariable, "secret")));

        Assert.NotNull(settings);
        Assert.Equal(new Uri("wss://bazaar.example/ws"), settings.Server);
        Assert.Equal(Guid.Parse(_gc), settings.GcId);
        Assert.Equal("secret", settings.Token);
        Assert.Equal("you", settings.Name);
    }

    [Fact]
    public void Rejects_a_half_configured_pair()
    {
        var onlyGc = Assert.Throws<ArgumentException>(() => ChatConnectionSettings.FromEnvironment(Env((ChatConnectionSettings.GcVariable, _gc))));
        Assert.Contains(ChatConnectionSettings.ServerVariable, onlyGc.Message, StringComparison.Ordinal);

        var onlyServer = Assert.Throws<ArgumentException>(() => ChatConnectionSettings.FromEnvironment(Env((ChatConnectionSettings.ServerVariable, "ws://h/ws"))));
        Assert.Contains(ChatConnectionSettings.GcVariable, onlyServer.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://bazaar.example/ws")]
    [InlineData("not a url")]
    public void Rejects_a_non_websocket_server(string server)
    {
        Assert.Throws<ArgumentException>(() => ChatConnectionSettings.FromEnvironment(Env(
            (ChatConnectionSettings.ServerVariable, server),
            (ChatConnectionSettings.GcVariable, _gc),
            (ChatConnectionSettings.TokenVariable, "t"))));
    }

    [Fact]
    public void Rejects_a_group_chat_id_that_is_not_a_guid()
    {
        Assert.Throws<ArgumentException>(() => ChatConnectionSettings.FromEnvironment(Env(
            (ChatConnectionSettings.ServerVariable, "ws://h/ws"),
            (ChatConnectionSettings.GcVariable, "dev-chat"),
            (ChatConnectionSettings.TokenVariable, "t"))));
    }

    [Fact]
    public void Requires_a_token_once_the_server_is_set()
    {
        var ex = Assert.Throws<ArgumentException>(() => ChatConnectionSettings.FromEnvironment(Env(
            (ChatConnectionSettings.ServerVariable, "ws://h/ws"),
            (ChatConnectionSettings.GcVariable, _gc))));
        Assert.Contains(ChatConnectionSettings.TokenVariable, ex.Message, StringComparison.Ordinal);
    }
}
