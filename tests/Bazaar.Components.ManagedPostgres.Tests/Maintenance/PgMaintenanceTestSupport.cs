using System.Security.Cryptography;
using Bazaar.Components.ManagedPostgres.Bundle;
using Bazaar.Components.ManagedPostgres.Maintenance;

namespace Bazaar.Components.ManagedPostgres.Tests;

/// <summary>A clock the tests move by hand, so dump names are predictable.</summary>
internal sealed class StepClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Shared fixtures for the maintenance tests: a bundle layout over a temp folder and a fake dump writer.</summary>
internal static class MaintenanceFixture
{
    public const string Password = "p4ss-never-on-the-command-line";

    public static PgConnection Connection(int port) => new(18, port, "bazaar_admin", Password, "bazaar");

    /// <summary>A layout whose 18 and 19 data directories exist under a temp root, with PG_VERSION written when asked.</summary>
    public static BundleLayout Layout(string root, bool initialiseNew = true)
    {
        var layout = new BundleLayout(Path.Combine(root, "pg"), Path.Combine(root, "data"));
        Directory.CreateDirectory(layout.Resolve(18).DataDirectory);
        File.WriteAllText(Path.Combine(layout.Resolve(18).DataDirectory, "PG_VERSION"), "18\n");
        Directory.CreateDirectory(layout.Resolve(19).DataDirectory);
        if (initialiseNew)
        {
            File.WriteAllText(Path.Combine(layout.Resolve(19).DataDirectory, "PG_VERSION"), "19\n");
        }

        return layout;
    }

    /// <summary>Answers pg_dump by writing a small payload to the path after -f. Every other tool exits 0 unless the test says otherwise.</summary>
    public static PostgresCommandResult FakeDump(string fileName, IReadOnlyList<string> arguments)
    {
        if (Path.GetFileNameWithoutExtension(fileName) == "pg_dump")
        {
            var list = arguments.ToList();
            File.WriteAllText(list[list.IndexOf("-f") + 1], "PGDMP-fake-payload");
        }

        return new PostgresCommandResult(0, string.Empty);
    }

    public static string Sha256Of(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    public static string NewTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "bazaar-pg-maint", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
