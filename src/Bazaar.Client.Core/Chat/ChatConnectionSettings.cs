namespace Bazaar.Client.Core.Chat;

/// <summary>What a chat head connects to and shows. Built from the environment; no I/O.</summary>
/// <param name="Server">WebSocket endpoint, ws:// or wss://.</param>
/// <param name="Token">Bearer token, sent in <c>hello</c>.</param>
/// <param name="GcId">The group chat to show and post to.</param>
/// <param name="Name">Label for this client's own messages.</param>
public sealed record ChatConnectionSettings(Uri Server, string Token, Guid GcId, string Name)
{
    /// <summary>Environment variable holding the WebSocket endpoint.</summary>
    public const string ServerVariable = "BAZAAR_SERVER";

    /// <summary>Environment variable holding the group chat id.</summary>
    public const string GcVariable = "BAZAAR_GC";

    /// <summary>Environment variable holding the bearer token. Prefer it over command-line arguments.</summary>
    public const string TokenVariable = "BAZAAR_TOKEN";

    /// <summary>Environment variable holding the label for this client's own messages.</summary>
    public const string NameVariable = "BAZAAR_NAME";

    /// <summary>
    /// Reads the settings from <paramref name="env"/>. Returns null when neither the server nor the group chat is
    /// set, so the head can show a setup notice instead of failing.
    /// </summary>
    /// <exception cref="ArgumentException">A value is set but invalid, or only half of the pair is set.</exception>
    public static ChatConnectionSettings? FromEnvironment(Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);

        string? server = Clean(env(ServerVariable));
        string? gc = Clean(env(GcVariable));
        if (server is null && gc is null) return null;
        if (server is null) throw new ArgumentException($"{GcVariable} is set but {ServerVariable} is not.");
        if (gc is null) throw new ArgumentException($"{ServerVariable} is set but {GcVariable} is not.");

        if (!Uri.TryCreate(server, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme is not ("ws" or "wss"))
        {
            throw new ArgumentException($"{ServerVariable} must be a ws:// or wss:// URL, got '{server}'.");
        }

        if (!Guid.TryParse(gc, out Guid gcId)) throw new ArgumentException($"{GcVariable} must be a GUID, got '{gc}'.");

        string token = Clean(env(TokenVariable)) ?? throw new ArgumentException($"{TokenVariable} is not set.");
        return new ChatConnectionSettings(endpoint, token, gcId, Clean(env(NameVariable)) ?? "you");
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
