using System.Collections.Frozen;
using System.Text.Json;
using Bazaar.Secrets;
using Bazaar.Server.Mail;
using Bazaar.Server.MailBackends.Cloudflare;
using Bazaar.Server.MailBackends.Common;
using Bazaar.Server.MailBackends.Generic;
using Bazaar.Server.MailOutbound;
using MailKit.Security;

namespace Bazaar.Server.MailRuntime;

/// <summary>
/// The login for a mailbox, read from the secret store (doc 14) at call time and never kept. The store entry is named
/// <c>mail.login.&lt;local&gt;.&lt;domain&gt;</c> (the <c>@</c> becomes a dot) and holds a JSON login. The caller is the host component, installation scope.
/// </summary>
public sealed class SecretStoreMailCredentialSource(ISecretStore store) : IMailCredentialSource
{
    private static readonly SecretPurpose _purpose = new("mail.transport");
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The secret name for a mailbox address.</summary>
    public static SecretName NameFor(MailboxRef box) => new("mail.login." + box.Address.Replace('@', '.').ToLowerInvariant());

    public async Task<MailCredential> GetAsync(MailboxRef box, CancellationToken ct)
    {
        using var value = await store.GetAsync(
            new HostCaller("mail-runtime"), SecretScope.Installation, NameFor(box), _purpose, ct).ConfigureAwait(false);
        var login = JsonSerializer.Deserialize<MailCredential>(value.Bytes, _json);
        if (login is null || string.IsNullOrEmpty(login.UserName) || string.IsNullOrEmpty(login.Password))
        {
            throw new InvalidOperationException("The stored mail login is not usable.");
        }

        return login;
    }
}

/// <summary>
/// Builds one back end per active <c>mail_domain</c> row and serves them to the outbound pipeline. <see cref="Resolve"/> is synchronous, so it reads a
/// snapshot that <see cref="RefreshAsync"/> builds (the outbound loop refreshes before each pass). A domain whose configuration is missing or
/// invalid is skipped and reported by error kind only; the others still work. No domain name is hardcoded: every one comes from the row.
/// </summary>
public sealed class MailBackendCatalog(
    IMailDomainSource domains,
    ISecretStore secrets,
    IMailCredentialSource credentials,
    TimeProvider? clock = null,
    HttpClient? http = null) : IMailBackendCatalog, IDisposable
{
    private sealed record ImapConfig(
        string ImapHost, int ImapPort, string ImapSecurity, string SmtpHost, int SmtpPort, string SmtpSecurity);

    private sealed record ApiConfig(string ApiBase);

    private static readonly SecretPurpose _purpose = new("mail.domain-config");
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly HttpClient _http = http ?? new HttpClient();
    private readonly bool _ownsHttp = http is null;
    private FrozenDictionary<string, Bazaar.Server.Mail.IMailBackend> _snapshot = FrozenDictionary<string, Bazaar.Server.Mail.IMailBackend>.Empty;

    public Bazaar.Server.Mail.IMailBackend? Resolve(string domain) =>
        !string.IsNullOrWhiteSpace(domain) && _snapshot.TryGetValue(domain.Trim().ToLowerInvariant(), out var backend) ? backend : null;

    public async Task<MailBackendRefreshResult> RefreshAsync(CancellationToken ct)
    {
        var rows = await domains.ListActiveAsync(ct).ConfigureAwait(false);
        var built = new Dictionary<string, Bazaar.Server.Mail.IMailBackend>(StringComparer.Ordinal);
        var errors = new List<string>();
        var skipped = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var backend = await BuildAsync(row, ct).ConfigureAwait(false);
                if (backend is null)
                {
                    skipped++;
                    continue;
                }

                built[row.Domain.ToLowerInvariant()] = backend;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skipped++;
                errors.Add(ex.GetType().Name);
            }
        }

        _snapshot = built.ToFrozenDictionary(StringComparer.Ordinal);
        return new MailBackendRefreshResult(built.Count, skipped, errors);
    }

    // Kinds served by another path (the Bazaar client, a built-in server) have no back end here: null, not an error.
    private async Task<Bazaar.Server.Mail.IMailBackend?> BuildAsync(MailDomainConfig row, CancellationToken ct)
    {
        switch (row.BackendKind)
        {
            case "imap_smtp":
                {
                    var cfg = await ReadAsync<ImapConfig>(row, ct).ConfigureAwait(false);
                    var options = new ImapSmtpOptions(
                        cfg.ImapHost, cfg.ImapPort, Security(cfg.ImapSecurity), cfg.SmtpHost, cfg.SmtpPort, Security(cfg.SmtpSecurity));
                    options.Validate();
                    return new ImapSmtpBackend(row.Domain, row.Domain, options, credentials, new SendLedger(), OutboundDefaults.Footer, _clock);
                }

            case "cloudflare":
                {
                    var cfg = await ReadAsync<ApiConfig>(row, ct).ConfigureAwait(false);
                    if (!Uri.TryCreate(cfg.ApiBase, UriKind.Absolute, out var apiBase) || apiBase.Scheme != Uri.UriSchemeHttps)
                    {
                        throw new InvalidOperationException("The API base must be an https address.");
                    }

                    // A trailing slash keeps relative path joins inside the base path.
                    if (!apiBase.AbsoluteUri.EndsWith('/'))
                    {
                        apiBase = new Uri(apiBase.AbsoluteUri + "/");
                    }

                    return new HttpsSenderBackend(row.Domain, _http, apiBase, credentials, new SendLedger(), OutboundDefaults.Footer);
                }

            default:
                return null;
        }
    }

    private async Task<T> ReadAsync<T>(MailDomainConfig row, CancellationToken ct)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(row.ConfigRef))
        {
            throw new InvalidOperationException("The domain has no configuration reference.");
        }

        using var value = await secrets.GetAsync(
            new HostCaller("mail-runtime"), SecretScope.Installation, new SecretName(row.ConfigRef), _purpose, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(value.Bytes, _json) ?? throw new InvalidOperationException("The domain configuration is empty.");
    }

    private static SecureSocketOptions Security(string text) =>
        Enum.TryParse<SecureSocketOptions>(text, ignoreCase: true, out var parsed) ? parsed : throw new InvalidOperationException("Unknown transport security.");

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
