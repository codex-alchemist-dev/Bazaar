using Bazaar.Server.Gateway.Providers;

namespace Bazaar.Server.Tests.Gateway.Providers;

public class ProviderDraftValidatorTests
{
    private const string _validSecretRef = "secret://bazaar/providers/acme-proxy/api-key";

    private static ProviderDraft ReverseProxy(
        string baseUrl = "https://api.example.com/v1",
        string authKind = "api_key",
        string? secretRef = _validSecretRef,
        string adapter = "openai_chat") =>
        new(ProviderRuntime.ReverseProxy, "reverse_proxy", adapter, baseUrl, authKind, secretRef, [], [], []);

    private static ProviderDraft Local(
        string baseUrl = "http://127.0.0.1:1234/v1",
        int? concurrencyCap = 2,
        int? healthIntervalSeconds = 60,
        string authKind = "none",
        string? secretRef = null) =>
        new(ProviderRuntime.LocalRuntime, "local", "openai_chat", baseUrl, authKind, secretRef, [], [], [], null, concurrencyCap, healthIntervalSeconds);

    private static ProviderDraftValidation Validate(ProviderDraft draft, bool proxyUi = false) =>
        ProviderDraftValidator.Validate(draft, proxyUi);

    private static void AssertHasError(ProviderDraftValidation result, ProviderDraftErrorCode code)
    {
        Assert.Contains(result.Errors, e => e.Code == code);
    }

    private static void AssertNoError(ProviderDraftValidation result, ProviderDraftErrorCode code)
    {
        Assert.DoesNotContain(result.Errors, e => e.Code == code);
    }

    // Happy paths: one per runtime type, checking the final request URL the operator sees.

    [Fact]
    public void Reverse_proxy_happy_path_shows_the_chat_completions_url()
    {
        var result = Validate(ReverseProxy());

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Equal("https://api.example.com/v1/chat/completions", result.RequestUrl);
    }

    [Fact]
    public void Local_runtime_happy_path_shows_the_loopback_chat_completions_url()
    {
        var result = Validate(Local());

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Equal("http://127.0.0.1:1234/v1/chat/completions", result.RequestUrl);
    }

    [Fact]
    public void Valid_extra_header_and_body_fields_pass()
    {
        var draft = ReverseProxy() with
        {
            ExtraHeaders = [new ProviderHeader("X-Team-Id", "research")],
            BodyInclude = ["model", "temperature"],
            BodyExclude = ["metadata"],
        };

        var result = Validate(draft);

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    [Theory]
    [InlineData("openai_chat", "https://api.example.com/v1/", "https://api.example.com/v1/chat/completions")]
    [InlineData("anthropic_messages", "https://api.example.com/", "https://api.example.com/v1/messages")]
    [InlineData("gemini_generate_content", "https://api.example.com/v1beta/", "https://api.example.com/v1beta/models/{model}:streamGenerateContent?alt=sse")]
    public void Adapter_path_is_appended_to_the_base_url_with_its_trailing_slash_trimmed(string adapter, string baseUrl, string expected)
    {
        var result = Validate(ReverseProxy(baseUrl: baseUrl, adapter: adapter));

        Assert.Equal(expected, result.RequestUrl);
    }

    [Fact]
    public void Unknown_adapter_is_rejected()
    {
        var result = Validate(ReverseProxy(adapter: "mystery_wire"));

        AssertHasError(result, ProviderDraftErrorCode.AdapterNotAccepted);
        Assert.Null(result.RequestUrl);
    }

    // Base URL rules.

    [Fact]
    public void Reverse_proxy_over_plain_http_is_rejected()
    {
        var result = Validate(ReverseProxy(baseUrl: "http://api.example.com/v1"));

        AssertHasError(result, ProviderDraftErrorCode.BaseUrlHttpNotAllowed);
    }

    [Fact]
    public void Reverse_proxy_over_plain_http_to_loopback_is_rejected()
    {
        var result = Validate(ReverseProxy(baseUrl: "http://localhost:8080/v1"));

        AssertHasError(result, ProviderDraftErrorCode.BaseUrlHttpNotAllowed);
    }

    [Fact]
    public void Local_runtime_over_plain_http_to_a_non_loopback_host_is_rejected()
    {
        var result = Validate(Local(baseUrl: "http://192.168.1.10:1234/v1"));

        AssertHasError(result, ProviderDraftErrorCode.BaseUrlHttpNotAllowed);
    }

    [Theory]
    [InlineData("http://localhost:1234/v1")]
    [InlineData("http://127.0.0.1:1234/v1")]
    [InlineData("http://[::1]:1234/v1")]
    public void Local_runtime_may_use_plain_http_on_a_loopback_host(string baseUrl)
    {
        var result = Validate(Local(baseUrl: baseUrl));

        AssertNoError(result, ProviderDraftErrorCode.BaseUrlHttpNotAllowed);
        Assert.NotNull(result.RequestUrl);
    }

    [Fact]
    public void Base_url_with_a_non_http_scheme_is_rejected()
    {
        var result = Validate(ReverseProxy(baseUrl: "ftp://api.example.com/v1"));

        AssertHasError(result, ProviderDraftErrorCode.BaseUrlSchemeNotAllowed);
    }

    [Fact]
    public void Relative_base_url_is_rejected()
    {
        var result = Validate(ReverseProxy(baseUrl: "api.example.com/v1"));

        AssertHasError(result, ProviderDraftErrorCode.BaseUrlNotAbsolute);
        Assert.Null(result.RequestUrl);
    }

    [Fact]
    public void Empty_base_url_is_required()
    {
        var result = Validate(ReverseProxy(baseUrl: ""));

        AssertHasError(result, ProviderDraftErrorCode.BaseUrlRequired);
        Assert.Null(result.RequestUrl);
    }

    [Fact]
    public void Base_url_with_userinfo_is_rejected_and_its_url_is_not_shown()
    {
        var result = Validate(ReverseProxy(baseUrl: "https://user:pw@api.example.com/v1"));

        AssertHasError(result, ProviderDraftErrorCode.BaseUrlUserInfo);
        Assert.Null(result.RequestUrl);
    }

    [Fact]
    public void Base_url_with_a_query_string_is_rejected()
    {
        var result = Validate(ReverseProxy(baseUrl: "https://api.example.com/v1?key=abc"));

        AssertHasError(result, ProviderDraftErrorCode.BaseUrlQueryOrFragment);
        Assert.Null(result.RequestUrl);
    }

    [Fact]
    public void Base_url_with_a_fragment_is_rejected()
    {
        var result = Validate(ReverseProxy(baseUrl: "https://api.example.com/v1#section"));

        AssertHasError(result, ProviderDraftErrorCode.BaseUrlQueryOrFragment);
        Assert.Null(result.RequestUrl);
    }

    [Fact]
    public void Kind_must_match_the_runtime_type()
    {
        var draft = Local() with { Kind = "reverse_proxy" };

        var result = Validate(draft);

        AssertHasError(result, ProviderDraftErrorCode.KindNotAccepted);
    }

    // Auth kind and secret reference rules.

    [Theory]
    [InlineData("subscription")]
    [InlineData("oauth")]
    [InlineData("claude_pro_login")]
    [InlineData("chatgpt_session")]
    [InlineData("")]
    public void Subscription_oauth_and_other_non_api_key_auth_kinds_are_rejected(string authKind)
    {
        var result = Validate(ReverseProxy(authKind: authKind));

        AssertHasError(result, ProviderDraftErrorCode.AuthKindNotAccepted);
    }

    [Theory]
    [InlineData("sk-live-literal-key")]
    [InlineData("https://keys.example.com/key")]
    [InlineData("secret://other-vault/providers/acme/key")]
    [InlineData("secret://bazaar/providers/")]
    public void Secret_ref_must_use_the_providers_prefix_and_name_a_secret(string secretRef)
    {
        var result = Validate(ReverseProxy(secretRef: secretRef));

        AssertHasError(result, ProviderDraftErrorCode.SecretRefInvalid);
    }

    [Fact]
    public void Api_key_auth_needs_a_secret_reference()
    {
        var result = Validate(ReverseProxy(secretRef: null));

        AssertHasError(result, ProviderDraftErrorCode.SecretRefRequired);
    }

    [Fact]
    public void Auth_none_is_rejected_for_a_reverse_proxy()
    {
        var result = Validate(ReverseProxy(authKind: "none", secretRef: null));

        AssertHasError(result, ProviderDraftErrorCode.AuthNoneNotAllowed);
    }

    [Fact]
    public void Local_runtime_with_an_api_key_is_rejected()
    {
        var result = Validate(Local(authKind: "api_key", secretRef: _validSecretRef));

        AssertHasError(result, ProviderDraftErrorCode.LocalRuntimeAuthMustBeNone);
    }

    [Fact]
    public void Auth_none_takes_no_secret_reference()
    {
        var result = Validate(Local(secretRef: _validSecretRef));

        AssertHasError(result, ProviderDraftErrorCode.SecretRefNotAllowed);
    }

    // Local runtime rules.

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    [InlineData(null)]
    public void Local_runtime_concurrency_cap_must_be_between_1_and_4(int? cap)
    {
        var result = Validate(Local(concurrencyCap: cap));

        AssertHasError(result, ProviderDraftErrorCode.ConcurrencyCapOutOfRange);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Local_runtime_concurrency_cap_accepts_the_bounds(int cap)
    {
        var result = Validate(Local(concurrencyCap: cap));

        AssertNoError(result, ProviderDraftErrorCode.ConcurrencyCapOutOfRange);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(120)]
    [InlineData(null)]
    public void Local_runtime_health_interval_is_fixed_at_60_seconds(int? interval)
    {
        var result = Validate(Local(healthIntervalSeconds: interval));

        AssertHasError(result, ProviderDraftErrorCode.HealthIntervalNotFixed);
    }

    [Fact]
    public void Reverse_proxy_rejects_a_local_runtime_concurrency_cap_or_health_interval()
    {
        var draft = ReverseProxy() with { ConcurrencyCap = 2 };

        var result = Validate(draft);

        AssertHasError(result, ProviderDraftErrorCode.RuntimeSettingNotAllowed);
    }

    // Extra header rules.

    [Theory]
    [InlineData("Host")]
    [InlineData("Content-Length")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Connection")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Cookie")]
    [InlineData("Authorization")]
    [InlineData("host")]
    public void Headers_that_override_routing_or_carry_credentials_are_rejected(string name)
    {
        var draft = ReverseProxy() with { ExtraHeaders = [new ProviderHeader(name, "value")] };

        var result = Validate(draft);

        AssertHasError(result, ProviderDraftErrorCode.HeaderForbidden);
    }

    [Fact]
    public void Authorization_is_rejected_for_a_bearer_draft_too()
    {
        var draft = ReverseProxy(authKind: "bearer") with { ExtraHeaders = [new ProviderHeader("Authorization", "Bearer literal")] };

        var result = Validate(draft);

        AssertHasError(result, ProviderDraftErrorCode.HeaderForbidden);
    }

    [Theory]
    [InlineData("X Bad")]
    [InlineData("X:Bad")]
    [InlineData("")]
    [InlineData("X-Bad(")]
    public void Header_names_must_be_valid_tokens(string name)
    {
        var draft = ReverseProxy() with { ExtraHeaders = [new ProviderHeader(name, "value")] };

        var result = Validate(draft);

        AssertHasError(result, ProviderDraftErrorCode.HeaderNameInvalid);
    }

    [Theory]
    [InlineData("a\r\nX-Injected: 1")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    public void Header_values_must_not_contain_line_breaks_or_control_characters(string value)
    {
        var draft = ReverseProxy() with { ExtraHeaders = [new ProviderHeader("X-Team-Id", value)] };

        var result = Validate(draft);

        AssertHasError(result, ProviderDraftErrorCode.HeaderValueInvalid);
    }

    // Body include and exclude rules.

    [Theory]
    [InlineData("messages.0")]
    [InlineData("options[0]")]
    [InlineData("a/b")]
    [InlineData("two words")]
    [InlineData("")]
    public void Body_fields_are_plain_names_without_nesting_paths(string field)
    {
        var draft = ReverseProxy() with { BodyInclude = [field] };

        var result = Validate(draft);

        AssertHasError(result, ProviderDraftErrorCode.BodyFieldInvalid);
    }

    [Fact]
    public void Body_field_listed_in_both_include_and_exclude_is_a_duplicate()
    {
        var draft = ReverseProxy() with { BodyInclude = ["temperature"], BodyExclude = ["temperature"] };

        var result = Validate(draft);

        AssertHasError(result, ProviderDraftErrorCode.BodyFieldDuplicate);
    }

    [Fact]
    public void Body_field_listed_twice_in_one_list_is_a_duplicate()
    {
        var draft = ReverseProxy() with { BodyExclude = ["metadata", "metadata"] };

        var result = Validate(draft);

        AssertHasError(result, ProviderDraftErrorCode.BodyFieldDuplicate);
    }

    // Outbound proxy rule: refused while socks5 is off, whatever the UI flag says.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Outbound_proxy_reference_is_rejected_while_socks5_is_not_enabled(bool proxyUiOn)
    {
        var draft = ReverseProxy() with { ProxyRef = "proxy://bazaar/outbound/main" };

        var result = Validate(draft, proxyUiOn);

        AssertHasError(result, ProviderDraftErrorCode.OutboundProxyNotAvailable);
    }

    // All errors at once.

    [Fact]
    public void Every_problem_is_reported_in_one_pass()
    {
        var draft = new ProviderDraft(
            ProviderRuntime.ReverseProxy,
            "reverse_proxy",
            "openai_chat",
            "https://user:pw@api.example.com/v1?key=abc",
            "oauth",
            _validSecretRef,
            [new ProviderHeader("Cookie", "session=1")],
            ["messages.0"],
            ["temperature"],
            ProxyRef: "proxy://bazaar/outbound/main");

        var result = Validate(draft);

        Assert.False(result.IsValid);
        AssertHasError(result, ProviderDraftErrorCode.BaseUrlUserInfo);
        AssertHasError(result, ProviderDraftErrorCode.BaseUrlQueryOrFragment);
        AssertHasError(result, ProviderDraftErrorCode.AuthKindNotAccepted);
        AssertHasError(result, ProviderDraftErrorCode.HeaderForbidden);
        AssertHasError(result, ProviderDraftErrorCode.BodyFieldInvalid);
        AssertHasError(result, ProviderDraftErrorCode.OutboundProxyNotAvailable);
        Assert.Null(result.RequestUrl);
    }
}
