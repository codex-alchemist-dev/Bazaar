using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Client.ServerE2E.Tests;

/// <summary>
/// One real <c>Bazaar.Server.Host</c> process for the test class (docs/design/34, the headless client against a live server):
/// a throwaway PostgreSQL database migrated by the host's own <c>migrate</c> command, a fake model source, and one group chat
/// that the owner and one agent belong to. The host runs as a separate process, exactly as the operator runs it, so
/// <c>Program.cs</c> and its composition are under test, not a copy of them.
/// </summary>
public sealed class RealServerFixture : PgFixture, IAsyncLifetime
{
    private const string _dbEnv = "BAZAAR_E2E_DB";
    private const string _tokenEnv = "BAZAAR_E2E_TOKEN";
    private const string _keyEnv = "BAZAAR_E2E_KEY";

    private readonly StringBuilder _hostOutput = new();
    private Process? _host;
    private FakeModelUpstream? _upstream;
    private string? _dataRoot;

    /// <summary>The dev token the client presents as its sign-in credential.</summary>
    public string Token { get; } = "e2e-" + Guid.NewGuid().ToString("N");

    /// <summary>The HTTP address of the running host.</summary>
    public Uri HttpUri { get; private set; } = null!;

    /// <summary>The socket address of the running host.</summary>
    public Uri SocketUri { get; private set; } = null!;

    /// <summary>The group chat the owner and the agent share. With two members it is a DM, so the agent answers every message.</summary>
    public Guid GcId { get; } = Guid.CreateVersion7();

    /// <summary>The principal the dev token signs in as. A GUID principal id is used as is by the dev authenticator.</summary>
    public Guid OwnerId { get; } = Guid.CreateVersion7();

    /// <summary>The agent's principal id.</summary>
    public Guid AgentId { get; } = Guid.CreateVersion7();

    /// <summary>The fake model source the host calls.</summary>
    public FakeModelUpstream Upstream => _upstream ?? throw new InvalidOperationException("The fixture is not initialised.");

    /// <summary>Everything the host process has written so far, for failure messages.</summary>
    public string HostOutput
    {
        get
        {
            lock (_hostOutput)
            {
                return _hostOutput.ToString();
            }
        }
    }

    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
        {
            return;
        }

        await base.InitializeAsync();
        _upstream = await FakeModelUpstream.StartAsync();
        _dataRoot = Path.Combine(Path.GetTempPath(), "bazaar-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var port = FreePort();
        var environment = new Dictionary<string, string>
        {
            [_dbEnv] = ConnectionString,
            [_tokenEnv] = Token,
            [_keyEnv] = "freellmapi-e2e-key",
            ["Bazaar__Host__DataRoot"] = _dataRoot,
            ["Bazaar__Host__DatabaseConnectionReference"] = "env:" + _dbEnv,
            ["Bazaar__Host__Status__Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["signin__enabled"] = "true",
            ["Bazaar__DevAuth__Enabled"] = "true",
            ["Bazaar__DevAuth__TokenSecret"] = "env:" + _tokenEnv,
            ["Bazaar__DevAuth__PrincipalId"] = OwnerId.ToString("D"),
            ["Bazaar__Gateway__FreeLlmApi__Enabled"] = "true",
            ["Bazaar__Gateway__FreeLlmApi__BaseUrl"] = _upstream.BaseUrl.ToString(),
            ["Bazaar__Gateway__FreeLlmApi__ApiKey"] = "env:" + _keyEnv,
            ["Bazaar__Gateway__FreeLlmApi__DefaultModel"] = "fake/e2e-model",
            ["Bazaar__AgentResponder__Agents__0__AgentId"] = AgentId.ToString("D"),
            ["Bazaar__AgentResponder__Agents__0__Name"] = "E2E Agent",
            ["Bazaar__AgentResponder__Agents__0__SystemPrompt"] = "You are the end-to-end test agent.",
        };

        var migrate = StartHost(environment, "migrate");
        await migrate.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        Assert.True(migrate.ExitCode == 0, "The host's migrate command failed:\n" + HostOutput);

        await SeedAsync();

        _host = StartHost(environment);
        await WaitUntilReadyAsync(port);
        SocketUri = new Uri($"ws://127.0.0.1:{port}/ws");
        HttpUri = new Uri($"http://127.0.0.1:{port}/");
    }

    public new async ValueTask DisposeAsync()
    {
        if (_host is { HasExited: false })
        {
            _host.Kill(entireProcessTree: true);
            await _host.WaitForExitAsync();
        }

        _host?.Dispose();
        if (_upstream is not null)
        {
            await _upstream.DisposeAsync();
        }

        if (_dataRoot is not null && Directory.Exists(_dataRoot))
        {
            try
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
            catch (IOException)
            {
                // A late log write can still hold a file; the temp folder is cleaned by the OS.
            }
        }

        await base.DisposeAsync();
    }

    private async Task SeedAsync()
    {
        var owner = OwnerId;
        await using var connection = await DataSource.OpenConnectionAsync();
        await ExecAsync(connection, "INSERT INTO gc (gc_id, type_key, title, created_by) VALUES ($1, 'general', 'e2e', $2)", GcId, owner);
        await ExecAsync(connection, "INSERT INTO human (id, display_name, role, status) VALUES ($1, 'E2E Owner', 'owner', 'active') ON CONFLICT DO NOTHING", owner);
        foreach (var member in new[] { owner, AgentId })
        {
            await ExecAsync(connection, "INSERT INTO gc_member (gc_id, principal_id, role, joined_seq) VALUES ($1, $2, 'member', 0)", GcId, member);
        }
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql, params object[] args)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        await command.ExecuteNonQueryAsync();
    }

    private Process StartHost(IReadOnlyDictionary<string, string> environment, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(HostLocator.HostAssembly())!,
        };
        start.ArgumentList.Add(HostLocator.HostAssembly());
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment)
        {
            start.Environment[key] = value;
        }

        // The migrate command reads BAZAAR__-prefixed variables, so hand it the same settings under that prefix.
        if (arguments is ["migrate"])
        {
            foreach (var (key, value) in environment.Where(pair => pair.Key.StartsWith("Bazaar__", StringComparison.Ordinal)))
            {
                start.Environment["BAZAAR__" + key] = value;
            }
        }

        var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_hostOutput)
        {
            _hostOutput.AppendLine(line);
        }
    }

    private async Task WaitUntilReadyAsync(int port)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (_host!.HasExited)
            {
                Assert.Fail($"The host exited with code {_host.ExitCode} before it was ready:\n{HostOutput}");
            }

            try
            {
                using var response = await http.GetAsync("/health/ready");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(250);
        }

        Assert.Fail("The host was not ready within 90 seconds:\n" + HostOutput);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

internal static class HostLocator
{
    /// <summary>The built host assembly: <c>src/Bazaar.Server.Host/bin/&lt;config&gt;/&lt;tfm&gt;/Bazaar.Server.Host.dll</c> next to this test's own output layout.</summary>
    public static string HostAssembly()
    {
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var tfm = baseDir.Name;
        var config = baseDir.Parent!.Name;
        for (var dir = baseDir; dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Bazaar.Server.Host", "bin", config, tfm, "Bazaar.Server.Host.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Bazaar.Server.Host.dll was not found; build the solution before running this test.");
    }
}
