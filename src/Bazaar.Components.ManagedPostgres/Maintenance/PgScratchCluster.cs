using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Bazaar.Components.ManagedPostgres.Bundle;

namespace Bazaar.Components.ManagedPostgres.Maintenance;

/// <summary>
/// A throwaway cluster for the dry-run step of a line move (doc 42 section 10.3, step 3). It is created in its own folder
/// under a scratch root, listens on a free loopback port with its own generated superuser password, can receive a restore
/// of a production dump, and is deleted when disposed. It never touches a production data directory: the scratch folder
/// must lie outside every bundle data directory, and an existing folder is refused (doc 33 restore rule 1: never in place).
/// </summary>
public sealed partial class PgScratchCluster : IAsyncDisposable
{
    /// <summary>Database the dry run restores into.</summary>
    public const string DatabaseName = "bazaar_dryrun";

    private readonly IPostgresCommandRunner _runner;
    private readonly BundleLayout _layout;
    private readonly PostgresProcessController _controller;
    private int _disposed;

    private PgScratchCluster(
        IPostgresCommandRunner runner,
        BundleLayout layout,
        PostgresProcessController controller,
        string dataDirectory,
        PgConnection connection)
    {
        _runner = runner;
        _layout = layout;
        _controller = controller;
        DataDirectory = dataDirectory;
        Connection = connection;
    }

    /// <summary>The scratch cluster's data directory.</summary>
    public string DataDirectory { get; }

    /// <summary>How to reach the restored database. The password is for this scratch cluster only.</summary>
    public PgConnection Connection { get; }

    /// <summary>Creates, initialises and starts a scratch cluster of the given major.</summary>
    /// <param name="name">Letters, digits, dash and underscore; becomes the folder name under <paramref name="scratchRoot"/>.</param>
    public static async Task<PgScratchCluster> CreateAsync(
        IPostgresCommandRunner runner,
        BundleLayout layout,
        int major,
        string scratchRoot,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!NamePattern().IsMatch(name))
        {
            throw new ArgumentException("The scratch name may only contain letters, digits, dash and underscore.", nameof(name));
        }

        var paths = layout.Resolve(major);
        var dataDirectory = Path.GetFullPath(Path.Combine(scratchRoot, name));
        var root = Path.GetFullPath(scratchRoot);
        if (!IsUnder(dataDirectory, root))
        {
            throw new PgMaintenanceException("The scratch folder must lie inside the scratch root.");
        }

        foreach (var supported in BundleLayout.SupportedMajors)
        {
            var production = Path.GetFullPath(layout.Resolve(supported).DataDirectory);
            if (IsUnder(dataDirectory, production) || IsUnder(production, dataDirectory))
            {
                throw new PgMaintenanceException("The scratch folder overlaps a production data directory. Nothing was created.");
            }
        }

        if (Directory.Exists(dataDirectory) || File.Exists(dataDirectory))
        {
            throw new PgMaintenanceException("The scratch folder already exists. Nothing was created.");
        }

        Directory.CreateDirectory(root);
        var port = FreeLoopbackPort();
        var options = new PostgresProcessOptions
        {
            BinDirectory = paths.Bin,
            DataDirectory = dataDirectory,
            Port = port,
        };
        var controller = new PostgresProcessController(options, runner);
        string? password = null;
        try
        {
            await controller.InitDbAsync((generated, _) =>
            {
                password = generated;
                return ValueTask.CompletedTask;
            }, cancellationToken);
            await controller.StartAsync(cancellationToken);
        }
        catch
        {
            await controller.DisposeAsync();
            Cleanup(dataDirectory);
            throw;
        }

        var connection = new PgConnection(major, port, options.SuperuserName, password!, DatabaseName);
        return new PgScratchCluster(runner, layout, controller, dataDirectory, connection);
    }

    /// <summary>Restores a logical dump into a new database in the scratch cluster (checksum sidecar verified when present).</summary>
    public Task RestoreAsync(string dumpPath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return PgDumpRestore.RunAsync(_runner, _layout, Connection, dumpPath, cancellationToken);
    }

    /// <summary>Stops the cluster and deletes its folder and bootstrap marker. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _controller.StopAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The folder is removed regardless; a stuck scratch server must not leave data behind.
        }
        finally
        {
            await _controller.DisposeAsync();
            Cleanup(DataDirectory);
        }
    }

    private static void Cleanup(string dataDirectory)
    {
        try
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }

            var marker = dataDirectory + ".bootstrap-pending";
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsUnder(string path, string parent)
    {
        var relative = Path.GetRelativePath(parent, path);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
