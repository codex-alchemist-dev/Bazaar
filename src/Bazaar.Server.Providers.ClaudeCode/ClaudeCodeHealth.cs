using System.Globalization;
using Bazaar.Server.Modules.Health;

namespace Bazaar.Server.Providers.ClaudeCode;

/// <summary>
/// The Claude Code provider's module health contract (docs/design/91 section 9, doc 46 section 10). Facts only, and no paid call:
/// the self-test runs <c>claude --version</c>. Liveness does no I/O. Readiness reads the lane state, so it cannot hang.
/// </summary>
public sealed class ClaudeCodeHealth(
    ClaudeCodeOptions options,
    IClaudeCodeLaneState lane,
    IClaudeCodeProcessRunner runner,
    TimeProvider? time = null) : IModuleHealthContract
{
    /// <summary>The module id Kodo reports under.</summary>
    public const string Id = "claude-code-cli";

    private const int _versionTextMax = 80;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string ModuleId => Id;

    public ValueTask<ModuleHealthCheck> LivenessAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(Check(ModuleHealthStatus.Healthy, "alive"));

    public ValueTask<ModuleHealthCheck> ReadinessAsync(CancellationToken cancellationToken)
    {
        var status = lane.Current;
        var facts = string.Create(
            CultureInfo.InvariantCulture,
            $"last success {(status.LastSuccessAt is { } ok ? ok.ToString("O", CultureInfo.InvariantCulture) : "never")}, last error {status.LastErrorClass ?? "none"}, ignored events {status.IgnoredEvents}");
        if (!status.Up)
        {
            return ValueTask.FromResult(Check(ModuleHealthStatus.Failing, $"down: {status.DownReason}; {facts}"));
        }

        if (status.RateLimitedUntil is { } until)
        {
            return ValueTask.FromResult(Check(ModuleHealthStatus.Degraded, $"rate limited until {until.ToString("O", CultureInfo.InvariantCulture)}; {facts}"));
        }

        return ValueTask.FromResult(Check(ModuleHealthStatus.Healthy, $"ready; {facts}"));
    }

    public ValueTask<IReadOnlyList<ModuleDependencyHealth>> DependenciesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<ModuleDependencyHealth>>([]);

    public async ValueTask<ModuleHealthCheck> SelfTestAsync(ModuleSelfTestScope scope, CancellationToken cancellationToken)
    {
        var executable = ClaudeCodeBinary.Resolve(options.BinaryPath);
        if (executable is null)
        {
            return Check(ModuleHealthStatus.Failing, "the claude program was not found");
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in ClaudeCodeAdapter.BaseEnvironmentNames)
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                env[name] = value;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var invocation = new ClaudeCodeInvocation(executable, ["--version"], "", Path.GetTempPath(), env);
            await using var run = await runner.StartAsync(invocation, timeout.Token).ConfigureAwait(false);
            string? first = null;
            await foreach (var line in run.ReadLinesAsync(timeout.Token).ConfigureAwait(false))
            {
                first ??= line;
            }

            var version = Clean(first);
            return version.Length == 0
                ? Check(ModuleHealthStatus.Failing, "claude --version printed nothing")
                : Check(ModuleHealthStatus.Healthy, "claude --version: " + version);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Check(ModuleHealthStatus.Failing, "claude --version did not answer in time");
        }
        catch (Exception ex) when (ex is InvalidDataException or System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return Check(ModuleHealthStatus.Failing, "claude --version could not be run");
        }
    }

    /// <summary>Keeps only a short, printable first line, so odd output cannot flood or confuse the report.</summary>
    private static string Clean(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return "";
        }

        var text = new string([.. line.Trim().Where(c => !char.IsControl(c))]);
        return text.Length > _versionTextMax ? text[.._versionTextMax] : text;
    }

    private ModuleHealthCheck Check(ModuleHealthStatus status, string reason) => new(status, reason, _time.GetUtcNow());
}
