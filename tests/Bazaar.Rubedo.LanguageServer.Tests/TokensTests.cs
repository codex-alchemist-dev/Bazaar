using Bazaar.Rubedo.LanguageServer;
using Bazaar.Rubedo.Views;

namespace Bazaar.Rubedo.LanguageServer.Tests;

public class TokensTests
{
    [Fact]
    public void A_token_file_becomes_a_catalog()
    {
        Assert.True(ServerOptions.TryParseTokens("[ { \"name\": \"color.hue.3\", \"kind\": \"Colour\" }, { \"name\": \"motion.interaction.x\", \"kind\": \"Interaction\" } ]", out IViewTokenCatalog? catalog, out _));

        Assert.Equal(TokenKind.Colour, catalog!.KindOf("color.hue.3"));
        Assert.Equal(TokenKind.Interaction, catalog.KindOf("motion.interaction.x"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[ { \"name\": \"a\", \"kind\": \"Shiny\" } ]")]
    [InlineData("[ { \"kind\": \"Colour\" } ]")]
    [InlineData("not json")]
    public void A_bad_token_file_is_refused_with_a_message(string json)
    {
        Assert.False(ServerOptions.TryParseTokens(json, out IViewTokenCatalog? catalog, out string? problem));
        Assert.Null(catalog);
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Fact]
    public void The_flag_needs_a_path_and_other_arguments_still_parse()
    {
        Assert.False(ServerOptions.TryParse(["--tokens"], out _, out string? problem));
        Assert.Contains("--tokens", problem);
        Assert.True(ServerOptions.TryParse(["--socket"], out ServerOptions socket, out _));
        Assert.Null(socket.Tokens);
    }

    [Fact]
    public void An_unknown_token_in_a_view_is_reported_when_a_catalog_is_loaded()
    {
        Assert.True(ServerOptions.TryParseTokens("[ { \"name\": \"color.hue.3\", \"kind\": \"Colour\" } ]", out IViewTokenCatalog? catalog, out _));
        var core = new LanguageServerCore(tokens: catalog);
        string json = "{ \"dslVersion\": 1, \"name\": \"bazaar.test.view\", \"root\": { \"widget\": \"text\", \"props\": { \"label\": \"x\", \"color\": { \"token\": \"color.hue.9\" } } } }";

        var codes = Bazaar.Rubedo.Analysis.RubedoAnalyzer.DiagnoseView(json, catalog).Select(d => d.Code);
        Assert.Contains("view.unknown_token", codes);
        Assert.NotNull(core);
    }
}
