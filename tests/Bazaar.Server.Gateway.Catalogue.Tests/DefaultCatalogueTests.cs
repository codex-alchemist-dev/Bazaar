using System.Text;

namespace Bazaar.Server.Gateway.Catalogue.Tests;

public sealed class DefaultCatalogueTests
{
    private const string _entryTemplate =
        """{"id":"a","displayName":"A","adapter":"openai_chat","baseUrl":"https://x.test/v1","kind":"reverse_proxy","verification":"U","verificationNote":"n","freeTier":"unknown","termsRisk":"unknown","trainsOnFreeData":"unknown"}""";

    private static DefaultCatalogue Parse(string json) => DefaultCatalogueLoader.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private static string Wrap(params string[] entries) => $$"""{"schemaVersion":1,"entries":[{{string.Join(",", entries)}}]}""";

    [Fact]
    public void Shipped_file_loads_with_every_doc_row()
    {
        var catalogue = DefaultCatalogueLoader.Load();

        // 15 table rows in doc 05; the Groq/NVIDIA NIM/Mistral/xAI row and the LM Studio / llama.cpp row expand to separate sources.
        Assert.Equal(20, catalogue.Entries.Count);
        string[] expected =
        [
            "anthropic", "google-gemini", "openai", "openrouter", "huggingface-router", "cerebras", "groq", "nvidia-nim", "mistral",
            "xai", "cloudflare-workers-ai", "deepseek", "freellmapi", "omniroute", "litellm", "ollama", "lm-studio", "llama-cpp",
            "vllm", "custom-openai-compatible",
        ];
        Assert.Equal(expected, catalogue.Entries.Select(e => e.Id));
    }

    [Fact]
    public void Shipped_file_ids_are_unique_and_all_fields_are_set()
    {
        var entries = DefaultCatalogueLoader.Load().Entries;

        Assert.Equal(entries.Count, entries.Select(e => e.Id).Distinct().Count());
        Assert.All(entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.DisplayName));
            Assert.True(Enum.IsDefined(e.Verification));
            Assert.True(Enum.IsDefined(e.Adapter));
            Assert.True(Enum.IsDefined(e.Kind));
            if (e.BaseUrl is not null)
            {
                Assert.True(Uri.TryCreate(e.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https");
            }
        });
    }

    [Fact]
    public void Shipped_file_marks_facts_the_doc_does_not_give_as_unknown()
    {
        Assert.All(DefaultCatalogueLoader.Load().Entries, e =>
        {
            Assert.Equal(FreeTierStatus.Unknown, e.FreeTier);
            Assert.Equal(TermsRisk.Unknown, e.TermsRisk);
            Assert.Equal(TrainsOnFreeData.Unknown, e.TrainsOnFreeData);
        });
    }

    [Fact]
    public void Shipped_file_follows_the_doc_table()
    {
        var byId = DefaultCatalogueLoader.Load().Entries.ToDictionary(e => e.Id);

        Assert.Equal(DefaultCatalogueAdapter.AnthropicMessages, byId["anthropic"].Adapter);
        Assert.Equal(DefaultCatalogueAdapter.GeminiGenerateContent, byId["google-gemini"].Adapter);
        Assert.Equal("https://api.anthropic.com", byId["anthropic"].BaseUrl);
        Assert.Equal(DefaultCatalogueVerification.V, byId["huggingface-router"].Verification);
        Assert.Equal(DefaultCatalogueKind.Gateway, byId["freellmapi"].Kind);
        Assert.Equal("http://localhost:3001/v1", byId["freellmapi"].BaseUrl);
        Assert.Equal(DefaultCatalogueKind.Local, byId["ollama"].Kind);
        Assert.Equal(DefaultCatalogueKind.Aggregator, byId["openrouter"].Kind);
        Assert.Null(byId["omniroute"].BaseUrl);
        Assert.Null(byId["groq"].BaseUrl);
        Assert.Null(byId["cloudflare-workers-ai"].BaseUrl);
    }

    [Fact]
    public void Shipped_file_text_has_no_secret_looking_content()
    {
        using var stream = typeof(DefaultCatalogueLoader).Assembly.GetManifestResourceStream(DefaultCatalogueLoader.ResourceName)!;
        var text = new StreamReader(stream).ReadToEnd();

        Assert.DoesNotContain("sk-", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"apiKey\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_valid_stream_loads()
    {
        var catalogue = Parse(Wrap(_entryTemplate));

        Assert.Equal("a", Assert.Single(catalogue.Entries).Id);
    }

    [Fact]
    public void Duplicate_ids_are_rejected()
    {
        var ex = Assert.Throws<DefaultCatalogueException>(() => Parse(Wrap(_entryTemplate, _entryTemplate)));

        Assert.Contains("Duplicate", ex.Message);
    }

    [Fact]
    public void Unknown_adapter_is_rejected()
    {
        Assert.Throws<DefaultCatalogueException>(() => Parse(Wrap(_entryTemplate.Replace("openai_chat", "carrier_pigeon"))));
    }

    [Theory]
    [InlineData("localhost:3001/v1")]
    [InlineData("ftp://x.test/v1")]
    [InlineData("/v1")]
    public void Base_url_must_be_absolute_http_or_https(string url)
    {
        Assert.Throws<DefaultCatalogueException>(() => Parse(Wrap(_entryTemplate.Replace("https://x.test/v1", url))));
    }

    [Theory]
    [InlineData("apiKey")]
    [InlineData("clientSecret")]
    [InlineData("authToken")]
    [InlineData("Password")]
    public void Secret_looking_property_names_are_rejected(string name)
    {
        var entry = _entryTemplate.Replace("\"id\":\"a\"", $"\"id\":\"a\",\"{name}\":\"x\"");

        var ex = Assert.Throws<DefaultCatalogueException>(() => Parse(Wrap(entry)));

        Assert.Contains("secret", ex.Message);
    }

    [Fact]
    public void Values_starting_with_sk_are_rejected()
    {
        var entry = _entryTemplate.Replace("\"n\"", "\"sk-abc123\"");

        Assert.Throws<DefaultCatalogueException>(() => Parse(Wrap(entry)));
    }

    [Fact]
    public void Malformed_json_and_unknown_properties_are_rejected()
    {
        Assert.Throws<DefaultCatalogueException>(() => Parse("{"));
        Assert.Throws<DefaultCatalogueException>(() => Parse(Wrap(_entryTemplate.Replace("\"id\":\"a\"", "\"id\":\"a\",\"extra\":1"))));
    }
}
