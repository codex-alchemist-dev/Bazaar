using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Vms.WindowsSandbox.WsbCli;

/// <summary>
/// The real <see cref="IWindowsSandboxHost"/>, driven by <c>wsb.exe</c> (verified on the owner's PC, <c>docs/spikes/S-WSB.md</c>).
/// <list type="bullet">
/// <item><c>wsb start -c</c> takes the config as XML text, not a path, so <see cref="LaunchAsync"/> reads the file first.</item>
/// <item><c>wsb stop</c> has no forced mode. <paramref name="force"/> is accepted and ignored; both paths stop the sandbox.</item>
/// <item>Only one sandbox can run per host, and <c>wsb start</c> refuses a second (CO_E_APPSINGLEUSE).</item>
/// </list>
/// </summary>
public sealed class WsbCliSandboxHost(IWsbCommandRunner runner, string configDirectory) : IWindowsSandboxHost
{
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <inheritdoc />
    public async Task<string> WriteConfigAsync(string instanceName, string configXml, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(configXml);
        if (!IsSafeName(instanceName))
        {
            throw new ArgumentException("Instance names may contain only ASCII letters, digits, '-' and '_'.", nameof(instanceName));
        }

        Directory.CreateDirectory(configDirectory);
        var path = Path.Combine(configDirectory, instanceName + ".wsb");
        await File.WriteAllTextAsync(path, configXml, _utf8NoBom, ct);
        return path;
    }

    /// <inheritdoc />
    public async Task<string> LaunchAsync(string configPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(configPath);
        var configXml = await File.ReadAllTextAsync(configPath, ct);

        var result = await runner.RunAsync(["start", "-c", configXml, "--raw"], ct);
        EnsureSuccess(result, "start");

        var id = ReadObjectId(result.StandardOutput);
        return id ?? throw new WindowsSandboxCliException("wsb start returned no sandbox id.");
    }

    /// <inheritdoc />
    public async Task ShutdownAsync(string instanceId, bool force, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);

        // `wsb stop` has no forced mode, so `force` makes no difference here.
        var result = await runner.RunAsync(["stop", "--id", instanceId, "--raw"], ct);
        EnsureSuccess(result, "stop");
    }

    /// <inheritdoc />
    public async Task<bool> IsRunningAsync(string instanceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        var ids = await ListRunningAsync(ct);
        return ids.Contains(instanceId, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<int> CountRunningAsync(CancellationToken ct) => (await ListRunningAsync(ct)).Count;

    /// <inheritdoc />
    public Task DeleteConfigAsync(string configPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(configPath);
        ct.ThrowIfCancellationRequested();
        if (File.Exists(configPath))
        {
            File.Delete(configPath);
        }

        return Task.CompletedTask;
    }

    private async Task<IReadOnlyList<string>> ListRunningAsync(CancellationToken ct)
    {
        var result = await runner.RunAsync(["list", "--raw"], ct);
        EnsureSuccess(result, "list");
        return ReadListIds(result.StandardOutput);
    }

    private static void EnsureSuccess(WsbCommandResult result, string command)
    {
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
            throw new WindowsSandboxCliException($"wsb {command} failed with exit code {result.ExitCode}: {detail.Trim()}");
        }
    }

    // `wsb start --raw` prints {"Id": "<guid>"}.
    private static string? ReadObjectId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
        }
        catch (JsonException ex)
        {
            throw new WindowsSandboxCliException("wsb start printed output that is not JSON.", ex);
        }
    }

    // `wsb list --raw` prints {"WindowsSandboxEnvironments":[{"Id":"<guid>"}, ...]}.
    private static List<string> ReadListIds(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("WindowsSandboxEnvironments", out var environments))
            {
                return [];
            }

            var ids = new List<string>();
            foreach (var environment in environments.EnumerateArray())
            {
                if (environment.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { } value)
                {
                    ids.Add(value);
                }
            }

            return ids;
        }
        catch (JsonException ex)
        {
            throw new WindowsSandboxCliException("wsb list printed output that is not JSON.", ex);
        }
    }

    private static bool IsSafeName(string? name) =>
        !string.IsNullOrEmpty(name) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
