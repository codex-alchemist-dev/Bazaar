using System.Text.Json;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Adapters;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Providers.ClaudeCode.Tests;

/// <summary>Stream-json lines the fake CLI plays. Shapes follow the documented stream-json events.</summary>
internal static class Lines
{
    public const string Init = """{"type":"system","subtype":"init","model":"claude-haiku-5-5","tools":["mcp__bazaar__echo"],"mcp_servers":[{"name":"bazaar","status":"connected"}]}""";
    public const string InitWithBuiltIn = """{"type":"system","subtype":"init","model":"claude-haiku-5-5","tools":["Bash","mcp__bazaar__echo"],"mcp_servers":[{"name":"bazaar","status":"connected"}]}""";
    public const string InitNoBridge = """{"type":"system","subtype":"init","model":"claude-haiku-5-5","tools":[],"mcp_servers":[]}""";
    public const string MessageStart = """{"type":"stream_event","event":{"type":"message_start","message":{"model":"claude-haiku-5-5","usage":{"input_tokens":12,"cache_read_input_tokens":3}}}}""";
    public const string TextStart = """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text"}}}""";
    public const string Text1 = """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}}""";
    public const string Text2 = """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" world"}}}""";
    public const string BlockStop0 = """{"type":"stream_event","event":{"type":"content_block_stop","index":0}}""";
    public const string EndTurn = """{"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5}}}""";
    public const string MessageStop = """{"type":"stream_event","event":{"type":"message_stop"}}""";
    public const string ResultOk = """{"type":"result","subtype":"success","is_error":false,"result":"Hello world","usage":{"input_tokens":12,"output_tokens":5,"cache_read_input_tokens":3}}""";
    public const string ToolStart = """{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"mcp__bazaar__echo"}}}""";
    public const string ToolArgs = """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"msg\":\"hi\"}"}}}""";
    public const string ToolStop = """{"type":"stream_event","event":{"type":"content_block_stop","index":1}}""";
    public const string ToolUseEnd = """{"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":9}}}""";
    public const string ToolStartUnknown = """{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_9","name":"mcp__bazaar__nope"}}}""";

    public static readonly string[] HappyPath = [Init, MessageStart, TextStart, Text1, Text2, BlockStop0, EndTurn, MessageStop, ResultOk];
}

/// <summary>Runs the real process runner against the fake CLI, adding the per-test script and dump paths to the child environment.</summary>
internal sealed class FakeCliRunner(string script, string dump, IClaudeCodeProcessRunner inner) : IClaudeCodeProcessRunner
{
    public int Starts { get; private set; }

    public Task<IClaudeCodeRun> StartAsync(ClaudeCodeInvocation invocation, CancellationToken ct)
    {
        Starts++;
        var env = new Dictionary<string, string>(invocation.Environment) { ["FAKE_CLI_SCRIPT"] = script, ["FAKE_CLI_DUMP"] = dump };
        foreach (var name in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64" })
        {
            if (Environment.GetEnvironmentVariable(name) is { } v)
            {
                env[name] = v;
            }
        }

        return inner.StartAsync(invocation with { Environment = env }, ct);
    }
}

internal sealed class Harness : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bazaar-claude-test-").FullName;

    public Harness(Action<ClaudeCodeOptions>? configure = null, ISecretResolverFake? secrets = null)
    {
        Options = new ClaudeCodeOptions
        {
            Enabled = true,
            BinaryPath = FakePath(),
            ConfigDir = Path.Combine(_dir, "home"),
            LoginOwner = "user:owner",
            TermsAcknowledgedAt = DateTimeOffset.UnixEpoch,
            MinSpacing = TimeSpan.Zero,
            FirstEventTimeout = TimeSpan.FromSeconds(20),
            IdleTimeout = TimeSpan.FromSeconds(20),
            TotalTimeout = TimeSpan.FromSeconds(60),
        };
        configure?.Invoke(Options);
        Script = Path.Combine(_dir, "script.txt");
        Dump = Path.Combine(_dir, "dump.json");
        Time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-20T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        State = new ClaudeCodeLaneState(Time);
        Runner = new FakeCliRunner(Script, Dump, new ClaudeCodeProcessRunner(Options.MaxLineChars));
        Adapter = new ClaudeCodeAdapter(Options, Runner, State, secrets?.Resolver, Time);
    }

    public ClaudeCodeOptions Options { get; }

    public string Script { get; }

    public string Dump { get; }

    public FakeTimeProvider Time { get; }

    public ClaudeCodeLaneState State { get; }

    public FakeCliRunner Runner { get; }

    public ClaudeCodeAdapter Adapter { get; }

    public static ModelToolDefinition Echo { get; } = new("echo", "Echo a message.", """{"type":"object","properties":{"msg":{"type":"string"}}}""");

    public void Play(params string[] lines) => File.WriteAllLines(Script, lines);

    public Task<List<ModelEvent>> RunAsync(IReadOnlyList<ModelMessage>? messages = null, string model = "claude-haiku-5-5") => RunCancellableAsync(TestContext.Current.CancellationToken, messages, model);

    public async Task<List<ModelEvent>> RunCancellableAsync(CancellationToken ct, IReadOnlyList<ModelMessage>? messages = null, string model = "claude-haiku-5-5")
    {
        messages ??= [new ModelMessage(ModelRole.System, "You are Ada."), new ModelMessage(ModelRole.User, "Say hello.")];
        var request = new ModelRequest("agent-1", "claude-code-cli:" + model, messages, [Echo]);
        var events = new List<ModelEvent>();
        await foreach (var e in Adapter.StreamAsync(new ProviderEndpoint("cli://claude-code", model), request, ct))
        {
            events.Add(e);
        }

        return events;
    }

    public JsonElement ReadDump()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Dump));
        return doc.RootElement.Clone();
    }

    public void Dispose()
    {
        Adapter.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string FakePath()
    {
        var name = OperatingSystem.IsWindows() ? "fake-claude.exe" : "fake-claude";
        var p = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(p) ? p : throw new FileNotFoundException("The fake CLI was not built next to the tests.", p);
    }
}

internal interface ISecretResolverFake
{
    Bazaar.Server.Secrets.ISecretResolver Resolver { get; }
}

internal sealed class FixedSecret(string value) : ISecretResolverFake, Bazaar.Server.Secrets.ISecretResolver
{
    public Bazaar.Server.Secrets.ISecretResolver Resolver => this;

    public bool CanResolve(string reference) => true;

    public ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken) => ValueTask.FromResult(value);
}
