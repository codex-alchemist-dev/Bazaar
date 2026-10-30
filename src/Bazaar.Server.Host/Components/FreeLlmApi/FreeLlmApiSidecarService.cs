using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Bazaar.Server.Gateway.FreeLlmApi;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Components.FreeLlmApi;

/// <summary>
/// Runs FreeLLMAPI as a managed child process when <c>Bazaar:Gateway:FreeLlmApi:Sidecar:Managed</c> is true. Start prepares the
/// private state, starts the process, waits for it to answer, logs in as the sidecar admin and writes the unified key where the
/// gateway reads it, so start fails closed when any step fails. While running, a crash restarts the process with backoff.
/// Secret values are never logged.
/// </summary>
public sealed partial class FreeLlmApiSidecarService(
    IOptions<FreeLlmApiOptions> options,
    ILogger<FreeLlmApiSidecarService> logger) : BackgroundService
{
    private static readonly TimeSpan _readyPoll = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan _maxBackoff = TimeSpan.FromMinutes(1);

    private readonly FreeLlmApiOptions _options = options.Value;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private FreeLlmApiSidecarPlan? _plan;
    private Process? _process;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Sidecar.Managed)
        {
            await base.StartAsync(cancellationToken);
            return;
        }

        _plan = Prepare();
        await StartProcessAndProvisionAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Sidecar.Managed)
        {
            return;
        }

        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _process!.WaitForExitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            LogExited(logger, _process!.ExitCode, backoff);
            try
            {
                await Task.Delay(backoff, stoppingToken);
                await StartProcessAndProvisionAsync(stoppingToken);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LogRestartFailed(logger, ex);
                backoff = Min(backoff * 2, _maxBackoff);
                _process = null;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_process is { HasExited: false } running)
        {
            running.Kill(entireProcessTree: true);
            await running.WaitForExitAsync(cancellationToken);
        }

        _http.Dispose();
    }

    public override void Dispose()
    {
        _process?.Dispose();
        base.Dispose();
    }

    private FreeLlmApiSidecarPlan Prepare()
    {
        var sidecar = _options.Sidecar;
        Directory.CreateDirectory(sidecar.ResolvedStateDirectory);
        var entry = FreeLlmApiSidecarPlan.Create(sidecar, FreeLlmApiSidecarState.EnsureEncryptionKey(sidecar.ResolvedStateDirectory), Path.Combine(sidecar.ResolvedStateDirectory, FreeLlmApiSidecarState.DeclarativeConfigFile));
        if (!File.Exists(entry.EntryPointPath))
        {
            throw new InvalidOperationException(
                $"FreeLLMAPI {FreeLlmApiSidecarOptions.PinnedVersion} is not built at '{sidecar.ResolvedInstallDirectory}'. " +
                "Run scripts/freellmapi/install.sh or set Bazaar:Gateway:FreeLlmApi:Sidecar:InstallDirectory.");
        }

        return entry;
    }

    private async Task StartProcessAndProvisionAsync(CancellationToken ct)
    {
        var sidecar = _options.Sidecar;
        var keys = FreeLlmApiSidecarState.ReadProviderKeys(sidecar.ResolvedProviderKeysFile);
        // Rewritten on every start: the admin entry is included only until the account exists, and provider keys stay current.
        // The plan already points FreeLLMAPI at this file, so writing it here is all that is needed before each start.
        FreeLlmApiSidecarState.WriteDeclarativeConfig(sidecar.ResolvedStateDirectory, keys);
        var plan = _plan ?? throw new InvalidOperationException("FreeLLMAPI sidecar was not prepared.");

        var start = new ProcessStartInfo(plan.Executable)
        {
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in plan.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in plan.Environment)
        {
            start.Environment[name] = value;
        }

        _process = Process.Start(start) ?? throw new InvalidOperationException("FreeLLMAPI sidecar did not start.");
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) LogOutput(logger, e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) LogErrorOutput(logger, e.Data); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitUntilAnsweringAsync(plan.BaseUrl, ct);
        var unifiedKey = await LoginAndReadUnifiedKeyAsync(sidecar, ct);
        FreeLlmApiSidecarState.WriteUnifiedKey(sidecar.UnifiedKeyFile, unifiedKey);
        FreeLlmApiSidecarState.MarkAdminCreated(sidecar.ResolvedStateDirectory);
        LogReady(logger, FreeLlmApiSidecarOptions.PinnedVersion, plan.BaseUrl);
    }

    private async Task WaitUntilAnsweringAsync(string baseUrl, CancellationToken ct)
    {
        var root = new Uri(baseUrl.Replace("/v1", "", StringComparison.Ordinal), UriKind.Absolute);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(_options.Sidecar.StartupTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_process!.HasExited)
            {
                throw new InvalidOperationException($"FreeLLMAPI exited during start with code {_process.ExitCode}. See the log lines above.");
            }

            try
            {
                using var response = await _http.GetAsync(new Uri(root, "/api/auth/status"), ct);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet. Keep polling until the deadline.
            }

            await Task.Delay(_readyPoll, ct);
        }

        throw new TimeoutException($"FreeLLMAPI did not answer on {baseUrl} within {_options.Sidecar.StartupTimeoutSeconds} seconds.");
    }

    private async Task<string> LoginAndReadUnifiedKeyAsync(FreeLlmApiSidecarOptions sidecar, CancellationToken ct)
    {
        var root = new Uri($"http://{FreeLlmApiSidecarOptions.BindHost}:{sidecar.Port}/", UriKind.Absolute);
        var password = FreeLlmApiSidecarState.EnsureAdminPassword(sidecar.ResolvedStateDirectory);
        using var login = await _http.PostAsJsonAsync(new Uri(root, "/api/auth/login"), new { email = FreeLlmApiSidecarState.AdminEmail, password }, ct);
        if (!login.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"FreeLLMAPI sidecar admin login failed with HTTP {(int)login.StatusCode}. The admin account may predate this host; reset the sidecar state directory.");
        }

        using var session = JsonDocument.Parse(await login.Content.ReadAsStreamAsync(ct));
        var token = session.RootElement.GetProperty("token").GetString() ?? throw new InvalidOperationException("FreeLLMAPI returned no session token.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "/api/settings/api-key"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var keyResponse = await _http.SendAsync(request, ct);
        keyResponse.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await keyResponse.Content.ReadAsStreamAsync(ct));
        var key = body.RootElement.GetProperty("apiKey").GetString();
        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith("freellmapi-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("FreeLLMAPI returned no unified key.");
        }

        using var logout = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "/api/auth/logout"));
        logout.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var _ = await _http.SendAsync(logout, ct);
        return key;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "freellmapi: {Line}")]
    private static partial void LogOutput(ILogger logger, string line);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "freellmapi: {Line}")]
    private static partial void LogErrorOutput(ILogger logger, string line);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "FreeLLMAPI sidecar exited with code {ExitCode}. Restarting in {Delay}.")]
    private static partial void LogExited(ILogger logger, int exitCode, TimeSpan delay);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "FreeLLMAPI sidecar restart failed.")]
    private static partial void LogRestartFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "FreeLLMAPI sidecar {Version} is answering on {BaseUrl}; unified key stored.")]
    private static partial void LogReady(ILogger logger, string version, string baseUrl);
}
