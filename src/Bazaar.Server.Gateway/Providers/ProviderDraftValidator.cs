using Bazaar.Server.Gateway.Policy;

namespace Bazaar.Server.Gateway.Providers;

/// <summary>The two runtime types the add-provider form covers in this draft.</summary>
public enum ProviderRuntime
{
    ReverseProxy,
    LocalRuntime,
}

/// <summary>One extra header the operator asked for. Names and values are checked by <see cref="ProviderDraftValidator"/>.</summary>
public sealed record ProviderHeader(string Name, string Value);

/// <summary>What the operator typed into the add-provider form, before it is saved.</summary>
public sealed record ProviderDraft(
    ProviderRuntime Runtime,
    string Kind,
    string Adapter,
    string BaseUrl,
    string AuthKind,
    string? SecretRef,
    IReadOnlyList<ProviderHeader> ExtraHeaders,
    IReadOnlyList<string> BodyInclude,
    IReadOnlyList<string> BodyExclude,
    string? ProxyRef = null,
    int? ConcurrencyCap = null,
    int? HealthIntervalSeconds = null);

public enum ProviderDraftErrorCode
{
    BaseUrlRequired,
    BaseUrlNotAbsolute,
    BaseUrlSchemeNotAllowed,
    BaseUrlHttpNotAllowed,
    BaseUrlUserInfo,
    BaseUrlQueryOrFragment,
    KindNotAccepted,
    AdapterNotAccepted,
    AuthKindNotAccepted,
    AuthNoneNotAllowed,
    LocalRuntimeAuthMustBeNone,
    SecretRefRequired,
    SecretRefInvalid,
    SecretRefNotAllowed,
    ConcurrencyCapOutOfRange,
    HealthIntervalNotFixed,
    RuntimeSettingNotAllowed,
    HeaderNameInvalid,
    HeaderForbidden,
    HeaderValueInvalid,
    BodyFieldInvalid,
    BodyFieldDuplicate,
    OutboundProxyNotAvailable,
}

/// <summary>One problem with a draft. <paramref name="Field"/> names the form field it belongs to.</summary>
public sealed record ProviderDraftError(string Field, ProviderDraftErrorCode Code, string Message);

/// <summary>
/// The result of validating a draft. <see cref="RequestUrl"/> is the URL the operator will see as the final request URL;
/// it is null when the base URL is not usable, so a URL with bad userinfo or a query is never shown.
/// </summary>
public sealed record ProviderDraftValidation(IReadOnlyList<ProviderDraftError> Errors, string? RequestUrl)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Validates an add-provider draft for a reverse proxy or a local runtime (docs/design/05-model-gateway.md,
/// "Reverse proxies and local runtimes"). Pure: no network, no database. Every rule runs, so all errors come back at once.
/// </summary>
/// <remarks>
/// TODO(component): the add-provider form that calls this lives in the component model (PR #125, not merged). The admin
/// panel wiring and the save path wait for that merge; this validator has no registration of its own.
/// </remarks>
public static class ProviderDraftValidator
{
    public const int LocalConcurrencyMin = 1;
    public const int LocalConcurrencyMax = 4;
    public const int LocalHealthIntervalSeconds = 60;

    private const string _kindReverseProxy = "reverse_proxy";
    private const string _kindLocal = "local";

    private static readonly string[] _loopbackHosts = ["localhost", "127.0.0.1", "::1"];

    // Headers that would override the gateway's own request framing or routing, or carry credentials outside the secret store.
    // Authorization is refused for every auth kind: a bearer draft gets its header from the secret reference, and a literal
    // Authorization header would be a credential that bypasses that store.
    private static readonly string[] _forbiddenHeaders =
    [
        "Authorization",
        "Host",
        "Content-Length",
        "Transfer-Encoding",
        "Connection",
        "Proxy-Authorization",
        "Cookie",
    ];

    /// <summary>
    /// Validates <paramref name="draft"/>. <paramref name="outboundProxyUiEnabled"/> is the value of
    /// <c>gateway.outbound_proxy.ui</c>; the proxy field is refused either way while the socks5 path is off.
    /// </summary>
    public static ProviderDraftValidation Validate(ProviderDraft draft, bool outboundProxyUiEnabled)
    {
        var errors = new List<ProviderDraftError>();

        CheckKind(draft, errors);
        var baseUrlOk = CheckBaseUrl(draft, errors);
        CheckAuth(draft, errors);
        CheckRuntimeSettings(draft, errors);
        CheckHeaders(draft, errors);
        CheckBodyFields(draft, errors);
        CheckProxy(draft, outboundProxyUiEnabled, errors);

        string? requestUrl = null;
        var path = AdapterPath(draft.Adapter);
        if (path is null)
        {
            errors.Add(new ProviderDraftError("adapter", ProviderDraftErrorCode.AdapterNotAccepted,
                $"Adapter '{draft.Adapter}' is not one of openai_chat, anthropic_messages, gemini_generate_content."));
        }
        else if (baseUrlOk)
        {
            requestUrl = draft.BaseUrl.Trim().TrimEnd('/') + path;
        }

        return new ProviderDraftValidation(errors, requestUrl);
    }

    private static void CheckKind(ProviderDraft draft, List<ProviderDraftError> errors)
    {
        var expected = draft.Runtime == ProviderRuntime.LocalRuntime ? _kindLocal : _kindReverseProxy;
        if (draft.Kind != expected)
        {
            errors.Add(new ProviderDraftError("kind", ProviderDraftErrorCode.KindNotAccepted,
                $"Kind must be '{expected}' for a {(draft.Runtime == ProviderRuntime.LocalRuntime ? "local runtime" : "reverse proxy")}."));
        }
    }

    /// <summary>Returns true when the base URL is usable: absolute, allowed scheme, no userinfo, query or fragment.</summary>
    private static bool CheckBaseUrl(ProviderDraft draft, List<ProviderDraftError> errors)
    {
        const string field = "base_url";
        var text = draft.BaseUrl?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            errors.Add(new ProviderDraftError(field, ProviderDraftErrorCode.BaseUrlRequired, "Base URL is required."));
            return false;
        }

        var ok = true;
        if (text.Contains('?') || text.Contains('#'))
        {
            errors.Add(new ProviderDraftError(field, ProviderDraftErrorCode.BaseUrlQueryOrFragment,
                "Base URL must not contain a query string or fragment. The adapter adds its own path."));
            ok = false;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            errors.Add(new ProviderDraftError(field, ProviderDraftErrorCode.BaseUrlNotAbsolute,
                "Base URL must be an absolute URL with a host, for example https://api.example.com/v1."));
            return false;
        }

        if (uri.UserInfo.Length > 0)
        {
            errors.Add(new ProviderDraftError(field, ProviderDraftErrorCode.BaseUrlUserInfo,
                "Base URL must not contain userinfo (user:password@). Put credentials in a secret reference."));
            ok = false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return ok;
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            var host = uri.Host.Trim('[', ']');
            var loopback = _loopbackHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
            if (!loopback || draft.Runtime != ProviderRuntime.LocalRuntime)
            {
                errors.Add(new ProviderDraftError(field, ProviderDraftErrorCode.BaseUrlHttpNotAllowed,
                    "Plain http is allowed only for a local runtime on localhost, 127.0.0.1 or ::1. Use https."));
                ok = false;
            }

            return ok;
        }

        errors.Add(new ProviderDraftError(field, ProviderDraftErrorCode.BaseUrlSchemeNotAllowed,
            $"Base URL scheme '{uri.Scheme}' is not allowed. Use https."));
        return false;
    }

    /// <summary>Checks the auth kind and secret reference.</summary>
    private static void CheckAuth(ProviderDraft draft, List<ProviderDraftError> errors)
    {
        const string field = "auth_kind";
        if (!CredentialKinds.TryParse(draft.AuthKind, out var auth))
        {
            errors.Add(new ProviderDraftError(field, ProviderDraftErrorCode.AuthKindNotAccepted,
                $"Auth kind '{draft.AuthKind}' is not accepted. Use api_key, bearer or none. Subscription and OAuth-login credentials are never accepted."));
            return;
        }

        if (draft.Runtime == ProviderRuntime.LocalRuntime && auth != CredentialKind.None)
        {
            errors.Add(new ProviderDraftError(field, ProviderDraftErrorCode.LocalRuntimeAuthMustBeNone,
                "A local runtime uses auth kind none."));
        }

        if (auth == CredentialKind.None)
        {
            if (draft.Runtime != ProviderRuntime.LocalRuntime)
            {
                errors.Add(new ProviderDraftError(field, ProviderDraftErrorCode.AuthNoneNotAllowed,
                    "Auth kind none is allowed only for a local runtime."));
            }

            if (!string.IsNullOrEmpty(draft.SecretRef))
            {
                errors.Add(new ProviderDraftError("secret_ref", ProviderDraftErrorCode.SecretRefNotAllowed,
                    "Auth kind none takes no secret reference."));
            }

            return;
        }

        if (string.IsNullOrEmpty(draft.SecretRef))
        {
            errors.Add(new ProviderDraftError("secret_ref", ProviderDraftErrorCode.SecretRefRequired,
                "This auth kind needs a secret reference."));
        }
        else if (!draft.SecretRef.StartsWith(CredentialKinds.SecretRefPrefix, StringComparison.Ordinal)
                 || draft.SecretRef.Length == CredentialKinds.SecretRefPrefix.Length)
        {
            errors.Add(new ProviderDraftError("secret_ref", ProviderDraftErrorCode.SecretRefInvalid,
                $"Secret reference must start with {CredentialKinds.SecretRefPrefix} and name a secret. A literal key is not accepted."));
        }
    }

    private static void CheckRuntimeSettings(ProviderDraft draft, List<ProviderDraftError> errors)
    {
        if (draft.Runtime == ProviderRuntime.ReverseProxy)
        {
            if (draft.ConcurrencyCap is not null || draft.HealthIntervalSeconds is not null)
            {
                errors.Add(new ProviderDraftError("runtime", ProviderDraftErrorCode.RuntimeSettingNotAllowed,
                    "Concurrency cap and health interval apply only to a local runtime."));
            }

            return;
        }

        if (draft.ConcurrencyCap is null || draft.ConcurrencyCap < LocalConcurrencyMin || draft.ConcurrencyCap > LocalConcurrencyMax)
        {
            errors.Add(new ProviderDraftError("concurrency_cap", ProviderDraftErrorCode.ConcurrencyCapOutOfRange,
                $"A local runtime's concurrency cap must be between {LocalConcurrencyMin} and {LocalConcurrencyMax}."));
        }

        if (draft.HealthIntervalSeconds != LocalHealthIntervalSeconds)
        {
            errors.Add(new ProviderDraftError("health_interval_seconds", ProviderDraftErrorCode.HealthIntervalNotFixed,
                $"A local runtime's health interval is fixed at {LocalHealthIntervalSeconds} seconds in this draft."));
        }
    }

    private static void CheckHeaders(ProviderDraft draft, List<ProviderDraftError> errors)
    {
        foreach (var header in draft.ExtraHeaders)
        {
            if (!IsToken(header.Name))
            {
                errors.Add(new ProviderDraftError("extra_headers", ProviderDraftErrorCode.HeaderNameInvalid,
                    $"Header name '{header.Name}' is not a valid HTTP token."));
            }
            else if (_forbiddenHeaders.Contains(header.Name, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(new ProviderDraftError("extra_headers", ProviderDraftErrorCode.HeaderForbidden,
                    $"Header '{header.Name}' is set by the gateway and cannot be overridden or supplied here."));
            }

            if (header.Value.Any(c => c == '\r' || c == '\n' || (c < ' ' && c != '\t') || c == '\u007f'))
            {
                errors.Add(new ProviderDraftError("extra_headers", ProviderDraftErrorCode.HeaderValueInvalid,
                    $"Value of header '{header.Name}' contains a line break or control character."));
            }
        }
    }

    private static void CheckBodyFields(ProviderDraft draft, List<ProviderDraftError> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in draft.BodyInclude.Concat(draft.BodyExclude))
        {
            if (!IsFieldName(name))
            {
                errors.Add(new ProviderDraftError("body_fields", ProviderDraftErrorCode.BodyFieldInvalid,
                    $"Body field '{name}' must be a plain field name: letters, digits, '_' or '-', no nesting paths."));
                continue;
            }

            if (!seen.Add(name))
            {
                errors.Add(new ProviderDraftError("body_fields", ProviderDraftErrorCode.BodyFieldDuplicate,
                    $"Body field '{name}' is listed more than once across the include and exclude lists."));
            }
        }
    }

    private static void CheckProxy(ProviderDraft draft, bool outboundProxyUiEnabled, List<ProviderDraftError> errors)
    {
        if (string.IsNullOrEmpty(draft.ProxyRef))
        {
            return;
        }

        var reason = outboundProxyUiEnabled
            ? "the socks5 path is not enabled yet (it waits for the socks5 spike, doc 05 work package 16)"
            : "the outbound proxy is hidden unless gateway.outbound_proxy.ui is on, and the socks5 path is not enabled yet";
        errors.Add(new ProviderDraftError("proxy_ref", ProviderDraftErrorCode.OutboundProxyNotAvailable,
            $"An outbound proxy cannot be set on this draft: {reason}. A base URL override is a different setting."));
    }

    private static string? AdapterPath(string adapter) => adapter switch
    {
        "openai_chat" => "/chat/completions",
        "anthropic_messages" => "/v1/messages",
        // The model id is part of the path for this family; the form shows the template.
        "gemini_generate_content" => "/models/{model}:streamGenerateContent?alt=sse",
        _ => null,
    };

    /// <summary>RFC 9110 token: one or more visible ASCII characters other than delimiters.</summary>
    private static bool IsToken(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var c in text)
        {
            var tchar = char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);
            if (!tchar)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A single JSON field name: letters, digits, '_' or '-'. Dots, brackets, slashes and spaces mean a path, so they are refused.</summary>
    private static bool IsFieldName(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-'))
            {
                return false;
            }
        }

        return true;
    }
}
