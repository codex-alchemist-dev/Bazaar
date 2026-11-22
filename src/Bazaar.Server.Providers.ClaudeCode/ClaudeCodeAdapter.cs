using System.Runtime.CompilerServices;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Secrets;

namespace Bazaar.Server.Providers.ClaudeCode;

/// <summary>
/// Runs one model step through the unmodified Claude Code CLI (docs/design/91-claude-code-cli-provider.md). The CLI has every
/// built-in tool switched off; tools are offered as definitions only and the calls leave as events for the host to carry out.
/// Never throws for an upstream problem: failures surface as <see cref="ModelEvent.Failure"/>.
/// </summary>
public sealed class ClaudeCodeAdapter : IModelAdapter, IDisposable
{
    public const string WireName = "claude_code_cli";

    private static readonly string[] _subscriptionPrefixes = ["sk-ant-oat", "sk-ant-ort"];

    private static readonly string[] _baseEnv =
    [
        "PATH", "HOME", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "SystemRoot", "windir", "COMSPEC", "TEMP", "TMP", "TMPDIR", "LANG", "LC_ALL", "APPDATA", "LOCALAPPDATA",
    ];

    private readonly ClaudeCodeOptions _options;
    private readonly IClaudeCodeProcessRunner _runner;
    private readonly ClaudeCodeLaneState _state;
    private readonly ISecretResolver? _secrets;
    private readonly TimeProvider _time;
    private readonly StepLimiter _limiter;

    public ClaudeCodeAdapter(ClaudeCodeOptions options, IClaudeCodeProcessRunner runner, ClaudeCodeLaneState state, ISecretResolver? secrets, TimeProvider time)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _secrets = secrets;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _limiter = new StepLimiter(options, time);
    }

    public AdapterKind Kind => AdapterKind.ClaudeCodeCli;

    public void Dispose() => _limiter.Dispose();

    public async IAsyncEnumerable<ModelEvent> StreamAsync(ProviderEndpoint endpoint, ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(request);

        var status = _state.Current;
        if (!status.Up)
        {
            yield return new ModelEvent.Failure("provider_error", $"The Claude Code lane is down: {status.DownReason}");
            yield break;
        }

        if (status.RateLimitedUntil is { } until)
        {
            yield return new ModelEvent.Failure("rate_limited", "The Claude Code plan limit is in effect.", Seconds(until - _time.GetUtcNow()));
            yield break;
        }

        if (!ClaudeCodeArguments.IsValidModelId(endpoint.ModelId))
        {
            yield return new ModelEvent.Failure("provider_error", "The model id is not allowed for the Claude Code provider.");
            yield break;
        }

        var executable = ClaudeCodeBinary.Resolve(_options.BinaryPath);
        if (executable is null)
        {
            yield return new ModelEvent.Failure("provider_error", "The claude executable was not found.");
            yield break;
        }

        string? apiKey = null;
        if (_options.AuthMode == ClaudeCodeAuthMode.ApiKey)
        {
            var (key, error) = await ResolveKeyAsync(ct).ConfigureAwait(false);
            if (key is null)
            {
                yield return new ModelEvent.Failure("provider_error", error!);
                yield break;
            }

            apiKey = key;
        }

        TimeSpan retry = default;
        if (!await _limiter.TryEnterAsync(r => retry = r, ct).ConfigureAwait(false))
        {
            yield return new ModelEvent.Failure("rate_limited", "The hourly step cap of the Claude Code lane is used up.", Seconds(retry));
            yield break;
        }

        var tempRoot = Directory.CreateTempSubdirectory("bazaar-claude-");
        ToolBridge? bridge = null;
        IClaudeCodeRun? run = null;
        try
        {
            var work = Directory.CreateDirectory(Path.Combine(tempRoot.FullName, "work"));
            var nonce = PromptRenderer.NewNonce();
            var (system, prompt) = PromptRenderer.Render(request.Messages, nonce);
            var systemFile = Path.Combine(tempRoot.FullName, "system.txt");
            var mcpFile = Path.Combine(tempRoot.FullName, "mcp.json");
            await File.WriteAllTextAsync(systemFile, system, ct).ConfigureAwait(false);
            bridge = ToolBridge.Start(request.Tools);
            await File.WriteAllTextAsync(mcpFile, bridge.BuildMcpConfigJson(), ct).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(systemFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(mcpFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var invocation = new ClaudeCodeInvocation(
                executable,
                ClaudeCodeArguments.Build(_options.AuthMode, endpoint.ModelId, systemFile, mcpFile),
                prompt,
                work.FullName,
                BuildEnvironment(apiKey));
            var parser = new StreamParser(request.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal), _options, _time);

            run = await _runner.StartAsync(invocation, ct).ConfigureAwait(false);
            using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
            total.CancelAfter(_options.TotalTimeout);
            await using var lines = run.ReadLinesAsync(total.Token).GetAsyncEnumerator(total.Token);
            var first = true;
            Task? pendingRead = null;
            ModelEvent? terminal = null;
            while (terminal is null)
            {
                string? line;
                var timeout = first ? _options.FirstEventTimeout : _options.IdleTimeout;
                var step = ReadNextAsync(lines, timeout, t => pendingRead = t, total.Token);
                var outcome = await step.ConfigureAwait(false);
                if (outcome.Kind == ReadKind.Cancelled)
                {
                    run.KillTree();
                    await DrainAsync(pendingRead).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    terminal = new ModelEvent.Failure("timeout", "The Claude Code run exceeded its total time limit.");
                    break;
                }

                if (outcome.Kind == ReadKind.Timeout)
                {
                    terminal = new ModelEvent.Failure("timeout", first ? "The Claude Code CLI sent nothing in time." : "The Claude Code CLI went quiet.");
                    break;
                }

                if (outcome.Kind == ReadKind.Error)
                {
                    terminal = new ModelEvent.Failure("provider_error", outcome.Message ?? "The Claude Code output could not be read.");
                    break;
                }

                if (outcome.Kind == ReadKind.End)
                {
                    break;
                }

                first = false;
                line = outcome.Line!;
                foreach (var ev in parser.Feed(line))
                {
                    if (ev is ModelEvent.Failure)
                    {
                        terminal = ev;
                        break;
                    }

                    yield return ev;
                }

                if (terminal is not null)
                {
                    break;
                }

                if (parser.InitViolation is { } violation)
                {
                    _state.MarkDown(violation, "capability_check");
                    terminal = new ModelEvent.Failure("provider_error", $"The Claude Code lane failed its safety check: {violation}");
                    break;
                }

                if (parser.Halt || parser.ResultSeen)
                {
                    break;
                }
            }

            if (terminal is null)
            {
                terminal = Finish(parser, run, bridge);
            }

            run.KillTree();
            await DrainAsync(pendingRead).ConfigureAwait(false);

            if (terminal is ModelEvent.Done)
            {
                yield return new ModelEvent.UsageReported(parser.Usage);
                _state.MarkSuccess(parser.Ignored);
            }
            else
            {
                _state.MarkError((terminal as ModelEvent.Failure)?.Code ?? "unknown", parser.Ignored);
            }

            yield return terminal;
        }
        finally
        {
            if (run is not null)
            {
                await run.DisposeAsync().ConfigureAwait(false);
            }

            if (bridge is not null)
            {
                await bridge.DisposeAsync().ConfigureAwait(false);
            }

            _limiter.Exit();
            try
            {
                tempRoot.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // A read that is still pending must finish (the kill ends the pipe) before its enumerator can be disposed.
    private static async Task DrainAsync(Task? pending)
    {
        if (pending is { IsCompleted: false })
        {
            try
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or InvalidDataException)
            {
            }
        }
    }

    private ModelEvent Finish(StreamParser parser, IClaudeCodeRun run, ToolBridge bridge)
    {
        if (parser.ResultFailure is { } rf)
        {
            return FromClassified(rf);
        }

        if (parser.Halt || parser.ResultSeen)
        {
            return new ModelEvent.Done(parser.Halt ? StopReason.ToolUse : parser.StopReasonValue, parser.ServedModel);
        }

        // The stream ended with no result. Use the exit code and the error text to say why.
        var classified = RateLimitClassifier.Classify(parser.LastApiRetryError, run.StderrTail, _time.GetUtcNow(), _options);
        if (classified.Class is FailureClass.RateLimited or FailureClass.Account)
        {
            return FromClassified(classified);
        }

        _ = bridge;
        return new ModelEvent.Failure("stream_dropped", $"The Claude Code CLI ended without a result (exit code {run.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}).");
    }

    private ModelEvent.Failure FromClassified(ClassifiedFailure f)
    {
        switch (f.Class)
        {
            case FailureClass.RateLimited:
                var after = f.RetryAfter ?? _options.RateLimitDefaultBackoff;
                _state.MarkRateLimited(after);
                return new ModelEvent.Failure("rate_limited", "The Claude plan usage limit was reached.", Seconds(after));
            case FailureClass.Account:
                _state.MarkDown($"account error: {f.Detail}. Sign in again or switch to API key mode.", f.Detail);
                return new ModelEvent.Failure("provider_error", $"Claude Code account error: {f.Detail}");
            case FailureClass.Transient:
                return new ModelEvent.Failure("provider_error", $"Claude upstream error: {f.Detail}");
            default:
                return new ModelEvent.Failure("provider_error", "The Claude Code CLI reported an error.");
        }
    }

    private Dictionary<string, string> BuildEnvironment(string? apiKey)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = _baseEnv.Concat(_options.EnvPassthrough);
        foreach (var name in names)
        {
            if (name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Environment.GetEnvironmentVariable(name) is { } v)
            {
                env[name] = v;
            }
        }

        env[ClaudeCodeOrphanMarker.EnvName] = ClaudeCodeOrphanMarker.For(_options);
        if (_options.AuthMode == ClaudeCodeAuthMode.Login)
        {
            if (!string.IsNullOrWhiteSpace(_options.ConfigDir))
            {
                env["CLAUDE_CONFIG_DIR"] = _options.ConfigDir;
            }
        }
        else if (apiKey is not null)
        {
            env["ANTHROPIC_API_KEY"] = apiKey;
        }

        return env;
    }

    private async Task<(string? Key, string? Error)> ResolveKeyAsync(CancellationToken ct)
    {
        if (_secrets is null || string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return (null, $"{ClaudeCodeOptions.SectionName}:ApiKey is not configured.");
        }

        string key;
        try
        {
            key = await _secrets.ResolveAsync(_options.ApiKey, ct).ConfigureAwait(false);
        }
        catch (SecretResolutionException)
        {
            return (null, $"{ClaudeCodeOptions.SectionName}:ApiKey could not be resolved.");
        }

        if (_subscriptionPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
        {
            return (null, $"{ClaudeCodeOptions.SectionName}:ApiKey holds a subscription token, which Bazaar never accepts.");
        }

        return (key, null);
    }

    private static int Seconds(TimeSpan t) => (int)Math.Clamp(Math.Ceiling(t.TotalSeconds), 1, int.MaxValue);

    private static async Task<ReadOutcome> ReadNextAsync(IAsyncEnumerator<string> lines, TimeSpan timeout, Action<Task> setPending, CancellationToken total)
    {
        try
        {
            var move = lines.MoveNextAsync().AsTask();
            setPending(move);
            var more = await move.WaitAsync(timeout, total).ConfigureAwait(false);
            return more ? new ReadOutcome(ReadKind.Line, lines.Current, null) : new ReadOutcome(ReadKind.End, null, null);
        }
        catch (TimeoutException)
        {
            return new ReadOutcome(ReadKind.Timeout, null, null);
        }
        catch (OperationCanceledException)
        {
            return new ReadOutcome(ReadKind.Cancelled, null, null);
        }
        catch (InvalidDataException ex)
        {
            return new ReadOutcome(ReadKind.Error, null, ex.Message);
        }
    }

    private enum ReadKind
    {
        Line,
        End,
        Timeout,
        Cancelled,
        Error,
    }

    private sealed record ReadOutcome(ReadKind Kind, string? Line, string? Message);
}

/// <summary>Finds the real <c>claude</c> executable. A Windows shell shim (<c>.cmd</c>) is never used.</summary>
internal static class ClaudeCodeBinary
{
    public static string? Resolve(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        }

        var file = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
