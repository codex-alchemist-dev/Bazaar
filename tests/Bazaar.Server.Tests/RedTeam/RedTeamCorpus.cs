using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Tests.RedTeam;

/// <summary>One simulated attack: external content, the trust tag it arrives with, and the tool call its text tries to make.</summary>
public sealed class RedTeamCase
{
    /// <summary>Stable id, unique across the corpus. Printed on every failure.</summary>
    public string Id { get; init; } = "";

    /// <summary>Attack class; must match the file the case is in.</summary>
    [JsonPropertyName("class")]
    public string AttackClass { get; init; } = "";

    /// <summary>What the attack tries and why the expectation holds.</summary>
    public string Description { get; init; } = "";

    /// <summary>The simulated external content, with hidden characters written as JSON escapes.</summary>
    public string Content { get; init; } = "";

    /// <summary>The <see cref="Bazaar.Server.Security.Taint.ContentTrust"/> name the content arrives with.</summary>
    public string Trust { get; init; } = "";

    /// <summary>For agent text: the taint of the session that produced it. Ignored for other trust tags.</summary>
    public string SourceState { get; init; } = "Clean";

    /// <summary>For agent text: true when lineage shows it quotes or was built from external text.</summary>
    public bool InheritsExternal { get; init; }

    /// <summary>The session role that makes the tool call: <c>Actor</c> or <c>Reader</c>.</summary>
    public string Role { get; init; } = "Actor";

    /// <summary>A fake credential the sanitizer must remove from the content, when the case plants one.</summary>
    public string? PlantedSecret { get; init; }

    /// <summary>The call the injected text tries to make.</summary>
    public RedTeamToolCall ToolCall { get; init; } = new();
}

/// <summary>A tool call an injected text asks for. The model never sets approval; the args here are plain input.</summary>
public sealed class RedTeamToolCall
{
    /// <summary>Tool name exactly as the model would send it.</summary>
    public string Tool { get; init; } = "";

    /// <summary>Target of the action, such as a repository or a recipient.</summary>
    public string? Target { get; init; }

    /// <summary>Arguments as a JSON object.</summary>
    public JsonElement Args { get; init; }
}

/// <summary>Loads the checked-in red-team corpus (RedTeam/Corpus/*.json, one file per attack class).</summary>
public static class RedTeamCorpus
{
    /// <summary>The six attack classes from docs/design/15-security-taint-and-approvals.md, "Testing".</summary>
    public static readonly string[] AttackClasses =
    [
        "hidden_unicode",
        "exfil_markdown",
        "malicious_rules_file",
        "impersonating_gc_message",
        "typosquat_package",
        "tool_description_poisoning",
    ];

    private static readonly JsonSerializerOptions _options = new() { PropertyNameCaseInsensitive = true };
    private static readonly Lazy<List<RedTeamCase>> _all = new(LoadAll);

    /// <summary>Every case in every file, in file order.</summary>
    public static IReadOnlyList<RedTeamCase> All => _all.Value;

    /// <summary>The cases of one attack class.</summary>
    public static IReadOnlyList<RedTeamCase> ForClass(string attackClass) =>
        All.Where(testCase => testCase.AttackClass == attackClass).ToList();

    /// <summary>Looks up one case by id.</summary>
    public static RedTeamCase Find(string id) => All.Single(testCase => testCase.Id == id);

    /// <summary>Path of one class's corpus file, next to the test assembly.</summary>
    public static string PathFor(string attackClass) =>
        Path.Combine(AppContext.BaseDirectory, "RedTeam", "Corpus", attackClass + ".json");

    private static List<RedTeamCase> LoadAll()
    {
        var cases = new List<RedTeamCase>();
        foreach (var attackClass in AttackClasses)
        {
            var path = PathFor(attackClass);
            var loaded = JsonSerializer.Deserialize<List<RedTeamCase>>(File.ReadAllText(path), _options)
                ?? throw new InvalidDataException($"Corpus file '{path}' is empty.");
            cases.AddRange(loaded);
        }

        return cases;
    }
}
