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

        // Nothing that loosens the sandbox is ever written, whatever the caller built.
        WindowsSandboxConfigPolicy.Validate(configXml);

        Directory.CreateDirectory(configDirectory);
        var path = Path.Combine(configDirectory, instanceName + ".wsb");
        await File.WriteAllTextAsync(path, configXml, _utf8NoBom, ct);
        return path;
    }

    /// <inheritdoc />
    public async Task<string> LaunchAsync(string configPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(configPath);
        var configXml = await File.ReadAllTextAsync(ConfinedConfigPath(configPath), ct);

        // Checked again at launch: the file could have been edited after it was written.
        WindowsSandboxConfigPolicy.Validate(configXml);

        var result = await runner.RunAsync(["start", "-c", configXml, "--raw"], ct);
        EnsureSuccess(result, "start");

        var id = ReadObjectId(result.StandardOutput);
        return id is null ? throw new WindowsSandboxCliException("wsb start returned no sandbox id.") : RequireSafeId(id);
    }

    /// <inheritdoc />
    public async Task ShutdownAsync(string instanceId, bool force, CancellationToken ct)
    {
        RequireSafeId(instanceId);

        // `wsb stop` has no forced mode, so `force` makes no difference here.
        var result = await runner.RunAsync(["stop", "--id", instanceId, "--raw"], ct);
        EnsureSuccess(result, "stop");
    }

    /// <inheritdoc />
    public async Task<bool> IsRunningAsync(string instanceId, CancellationToken ct)
    {
        RequireSafeId(instanceId);
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

        // Only a .wsb file inside the config directory can be deleted; the path is caller-supplied.
        var confined = ConfinedConfigPath(configPath);
        if (File.Exists(confined))
        {
            File.Delete(confined);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<SandboxExecResult> ExecAsync(string instanceId, IReadOnlyList<string> command, CancellationToken ct)
    {
        RequireSafeId(instanceId);
        ArgumentNullException.ThrowIfNull(command);
        if (command.Count == 0 || string.IsNullOrWhiteSpace(command[0]))
        {
            throw new ArgumentException("A command needs a program path.", nameof(command));
        }

        if (command[0].TrimStart().StartsWith('-') || command.Any(part => part is null || part.Contains('\0', StringComparison.Ordinal)))
        {
            throw new ArgumentException("A command part may not contain NUL, and the program path may not look like an option.", nameof(command));
        }

        // `wsb exec` takes one command line, so each part is quoted for the Windows guest. Nothing goes through a host shell.
        var commandLine = string.Join(' ', command.Select(QuoteForWindows));
        var result = await runner.RunAsync(["exec", "--id", instanceId, "-r", "System", "-c", commandLine, "--raw"], ct);
        return new SandboxExecResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }

    // CommandLineToArgvW rules: wrap in quotes when needed, double the backslashes before a quote, escape inner quotes.
    private static string QuoteForWindows(string argument)
    {
        if (argument.Length > 0 && argument.All(c => !char.IsWhiteSpace(c) && c != '"'))
        {
            return argument;
        }

        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
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
                    ids.Add(RequireSafeId(value));
                }
            }

            return ids;
        }
        catch (JsonException ex)
        {
            throw new WindowsSandboxCliException("wsb list printed output that is not JSON.", ex);
        }
    }

    /// <summary>
    /// A sandbox id is a GUID in practice. Ids read from <c>wsb</c> output and ids passed in are later put on command lines, so
    /// anything that is not a short token of letters, digits and inner hyphens (no leading '-', so never an option) is refused.
    /// </summary>
    private static string RequireSafeId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64 || id[0] == '-' || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new WindowsSandboxCliException("A sandbox id must be a short token of letters, digits and hyphens that does not start with '-'.");
        }

        return id;
    }

    /// <summary>Resolves a config path and requires it to be a <c>.wsb</c> file directly inside the config directory, not a link.</summary>
    private string ConfinedConfigPath(string configPath)
    {
        var directory = Path.GetFullPath(configDirectory);
        var full = Path.GetFullPath(configPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(full), directory.TrimEnd(Path.DirectorySeparatorChar), comparison)
            || !full.EndsWith(".wsb", StringComparison.OrdinalIgnoreCase)
            || new FileInfo(full).LinkTarget is not null)
        {
            throw new ArgumentException("A config path must be a .wsb file directly inside the config directory.", nameof(configPath));
        }

        return full;
    }

    private static bool IsSafeName(string? name) =>
        !string.IsNullOrEmpty(name) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
