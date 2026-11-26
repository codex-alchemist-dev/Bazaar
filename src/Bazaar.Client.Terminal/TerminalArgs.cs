namespace Bazaar.Client.Terminal;

/// <summary>Settings for one terminal session.</summary>
/// <param name="Server">WebSocket endpoint, ws:// or wss://.</param>
/// <param name="Token">Bearer token, sent in <c>hello</c>.</param>
/// <param name="GcId">The group chat to show and post to.</param>
/// <param name="Name">Label for this terminal's own messages.</param>
/// <param name="Members">Display names for other members; agents are marked.</param>
public sealed record TerminalOptions(Uri Server, string Token, Guid GcId, string Name, IReadOnlyDictionary<Guid, Speaker> Members);

/// <summary>Parses command-line arguments with environment fallbacks. Pure: the environment is passed in.</summary>
public static class TerminalArgs
{
    /// <summary>Usage text shown for <c>--help</c> and for argument errors.</summary>
    public const string Usage = """
        usage: bazaar-terminal --server <ws-url> --gc <gc-id> [options]
          --server <url>         WebSocket endpoint, ws:// or wss:// (env BAZAAR_SERVER)
          --gc <id>              group chat id (env BAZAAR_GC)
          --token <token>        bearer token (env BAZAAR_TOKEN; prefer the env var, arguments show in shell history)
          --name <name>          label for your own messages (default: you)
          --member <id>=<name>   name another member (repeatable)
          --agent <id>=<name>    name an agent; its messages are marked [agent] (repeatable)
        Type a line and press Enter to send. /quit leaves; Ctrl+C also exits.
        """;

    /// <summary>Parses <paramref name="args"/>; values from the command line win over <paramref name="env"/>.</summary>
    /// <exception cref="ArgumentException">An argument or required setting is missing or invalid.</exception>
    public static TerminalOptions Parse(IReadOnlyList<string> args, Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(env);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var members = new Dictionary<Guid, Speaker>();
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument '{arg}'.");

            string key = arg;
            string? value = null;
            int eq = arg.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                key = arg[..eq];
                value = arg[(eq + 1)..];
            }
            else if (i + 1 < args.Count)
            {
                value = args[++i];
            }

            if (value is null) throw new ArgumentException($"{key} needs a value.");
            switch (key)
            {
                case "--server":
                case "--gc":
                case "--token":
                case "--name":
                    values[key] = value;
                    break;
                case "--member":
                    AddMember(members, key, value, agent: false);
                    break;
                case "--agent":
                    AddMember(members, key, value, agent: true);
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{key}'.");
            }
        }

        string server = Pick(values, "--server", env, "BAZAAR_SERVER")
            ?? throw new ArgumentException("Missing --server (or BAZAAR_SERVER).");
        if (!Uri.TryCreate(server, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme is not ("ws" or "wss"))
        {
            throw new ArgumentException($"--server must be a ws:// or wss:// URL, got '{server}'.");
        }

        string token = (Pick(values, "--token", env, "BAZAAR_TOKEN") ?? string.Empty).Trim();
        if (token.Length == 0) throw new ArgumentException("Missing --token (or BAZAAR_TOKEN).");

        string gcText = Pick(values, "--gc", env, "BAZAAR_GC") ?? throw new ArgumentException("Missing --gc (or BAZAAR_GC).");
        if (!Guid.TryParse(gcText, out Guid gc)) throw new ArgumentException($"--gc must be a GUID, got '{gcText}'.");

        string name = values.TryGetValue("--name", out var n) ? n.Trim() : string.Empty;
        if (name.Length == 0) name = "you";

        return new TerminalOptions(endpoint, token, gc, name, members);
    }

    private static string? Pick(Dictionary<string, string> values, string key, Func<string, string?> env, string envName)
    {
        if (values.TryGetValue(key, out var fromArgs)) return fromArgs;
        string? fromEnv = env(envName);
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }

    private static void AddMember(Dictionary<Guid, Speaker> members, string key, string value, bool agent)
    {
        int eq = value.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0 || !Guid.TryParse(value[..eq], out Guid id) || value[(eq + 1)..].Trim().Length == 0)
        {
            throw new ArgumentException($"{key} expects <id>=<name>, got '{value}'.");
        }

        members[id] = new Speaker(value[(eq + 1)..].Trim(), agent);
    }
}
