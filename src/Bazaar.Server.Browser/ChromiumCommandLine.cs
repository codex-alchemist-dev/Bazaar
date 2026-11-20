using Bazaar.Server.Browser.Abstractions;

namespace Bazaar.Server.Browser;

/// <summary>
/// Builds the Chromium argument vector for one agent browser session (doc 16, "Network and egress"). The result is
/// the argument list only; the executable path comes from <see cref="BrowserOptions"/> and the guest host adds it.
/// </summary>
/// <remarks>
/// Every session routes through the host egress proxy and resolves no DNS locally. Anonymous sessions run as
/// incognito. Account sessions do not, so their profile directory keeps the Passport-backed cookies. The argument list
/// never contains <c>--no-sandbox</c>, <c>--remote-debugging-port</c> or <c>--disable-web-security</c>.
/// </remarks>
public static class ChromiumCommandLine
{
    /// <summary>
    /// Returns the Chromium arguments for one session, in a fixed order, with the start URL (when given) last.
    /// </summary>
    /// <param name="profileClass">Anonymous adds <c>--incognito</c>; Account does not.</param>
    /// <param name="display">Headless adds <c>--headless=new</c>; Headed adds nothing.</param>
    /// <param name="os">The guest OS the browser runs in. It does not change the argument list; it is validated only.</param>
    /// <param name="profileDirectory">Path passed as <c>--user-data-dir</c>. Must not be empty or whitespace.</param>
    /// <param name="egress">The host egress proxy. Its port must be 1-65535 and its host must be a plain host name or IPv4 literal.</param>
    /// <param name="startUrl">An absolute http or https URL, or null for no start page. It is always the last argument.</param>
    /// <returns>The Chromium arguments, without the executable.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="profileDirectory"/> is empty or whitespace, <paramref name="egress"/> has a host that cannot be
    /// used safely in a command line, or <paramref name="startUrl"/> is not an absolute http or https URL.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The enum values are undefined, or <paramref name="egress"/> has a port outside 1-65535.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="egress"/> is null.</exception>
    public static IReadOnlyList<string> Build(
        BrowserProfileClass profileClass,
        BrowserDisplayMode display,
        BrowserGuestOs os,
        string profileDirectory,
        BrowserEgressEndpoint egress,
        string? startUrl)
    {
        ArgumentNullException.ThrowIfNull(egress);
        if (!Enum.IsDefined(profileClass))
        {
            throw new ArgumentOutOfRangeException(nameof(profileClass), profileClass, "Unknown browser profile class.");
        }

        if (!Enum.IsDefined(display))
        {
            throw new ArgumentOutOfRangeException(nameof(display), display, "Unknown browser display mode.");
        }

        if (!Enum.IsDefined(os))
        {
            throw new ArgumentOutOfRangeException(nameof(os), os, "Unknown browser guest OS.");
        }

        if (string.IsNullOrWhiteSpace(profileDirectory))
        {
            throw new ArgumentException("The profile directory must not be empty.", nameof(profileDirectory));
        }

        ValidateEgressHost(egress.Host);
        if (egress.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(egress), egress.Port, "The egress port must be between 1 and 65535.");
        }

        var startUri = startUrl is null ? null : ParseStartUrl(startUrl);

        var args = new List<string>(capacity: 16)
        {
            $"--user-data-dir={profileDirectory}",
            $"--proxy-server=http://{egress.Host}:{egress.Port}",
            $"--host-resolver-rules=MAP * ~NOTFOUND , EXCLUDE {egress.Host}",
            "--remote-debugging-pipe",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-sync",
            "--disable-background-networking",
            "--disable-extensions",
            "--disable-component-update",
            "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
        };

        if (display == BrowserDisplayMode.Headless)
        {
            args.Add("--headless=new");
        }

        if (profileClass == BrowserProfileClass.Anonymous)
        {
            args.Add("--incognito");
        }

        if (startUri is not null)
        {
            args.Add(startUri);
        }

        return args;
    }

    private static void ValidateEgressHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("The egress host must not be empty.", nameof(host));
        }

        // The host is spliced into --proxy-server and --host-resolver-rules, so only plain host names and IPv4
        // literals are allowed. Anything else could add rules to the resolver string.
        foreach (var c in host)
        {
            var allowed = char.IsAsciiLetterOrDigit(c) || c is '.' or '-';
            if (!allowed)
            {
                throw new ArgumentException("The egress host may contain only letters, digits, dots and hyphens.", nameof(host));
            }
        }
    }

    private static string ParseStartUrl(string startUrl)
    {
        if (!Uri.TryCreate(startUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The start URL must be an absolute http or https URI.", nameof(startUrl));
        }

        return startUrl;
    }
}
