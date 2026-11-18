using Bazaar.Admin;

namespace Bazaar.Admin.Tests;

/// <summary>The captured result of one run: the exit code and both output streams.</summary>
internal sealed record CliRun(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>Both streams together, for checks that must hold whichever stream a line went to.</summary>
    public string All => Stdout + Stderr;
}

internal static class AdminRun
{
    public static Task<CliRun> Async(params string[] args) => WithGate(null, args);

    /// <summary>Runs the command with an apply gate. Null uses the command's own default gate.</summary>
    public static async Task<CliRun> WithGate(ICliApplyGate? gate, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await AdminCommandLine.RunAsync(
            args,
            stdout,
            stderr,
            applyGate: gate,
            cancellationToken: TestContext.Current.CancellationToken);
        return new CliRun(code, stdout.ToString(), stderr.ToString());
    }
}

/// <summary>A temporary folder for setup files. Deleted when the test ends.</summary>
internal sealed class TempSetupFolder : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-admin-tests-" + Guid.NewGuid().ToString("N"));

    public TempSetupFolder() => Directory.CreateDirectory(_root);

    /// <summary>Writes a file in the folder and returns its full path.</summary>
    public string Write(string text, string name = "bazaar-setup.json")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    public string PathOf(string name) => Path.Combine(_root, name);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

/// <summary>Setup files for the tests. The valid file is the shape doc 45 section 1.5 describes, with fake values only.</summary>
internal static class AdminFixtures
{
    /// <summary>A fake secret value. Every canary test places it in a secret-typed key and checks that no output carries it.</summary>
    public const string Canary = "canary-secret-0000";

    /// <summary>The one plain line an unavailable apply prints (the wording is fixed by the coordinator's decision).</summary>
    public const string ApplyUnavailableLine =
        "apply is unavailable: this build has no durable audit store (doc 15 audit_log); the Host adapter provides it";

    /// <summary>A valid plan with a backup target, so it has no hold.</summary>
    public const string Valid = """
        {
          "schemaVersion": 1,
          "install": {
            "companyName": "Example Studio",
            "companySlug": "example-studio",
            "locale": "en",
            "timezone": "Europe/London",
            "releaseChannel": "beta",
            "updatePolicy": { "security": "automatic", "other": "manual" }
          },
          "database": { "postgresMajor": 18, "dataRoot": "C:\\ProgramData\\Bazaar", "gcPartitionCount": 16 },
          "custody": {
            "kekProvider": "dpapi",
            "kekRef": "dpapi:C:\\ProgramData\\Bazaar\\kek.bin",
            "auditAnchorTarget": "https://anchor.example.invalid/bazaar-audit",
            "mergeCredential": { "kind": "owner_app", "appIdRef": "secret://bazaar/owner/app-id" }
          },
          "owner": {
            "displayName": "Owner",
            "requireSecondFactor": true,
            "recovery": { "printedCodes": false, "backupOwners": [] }
          },
          "privacy": { "profile": "private" },
          "backups": { "target": "file:/mnt/backup/bazaar", "drillIntervalDays": 30, "acknowledgeNoTarget": false },
          "models": {
            "sources": [
              { "id": "freellmapi", "keyRef": "secret://bazaar/providers/freellmapi/key" },
              { "id": "anthropic-owner", "keyRef": "secret://bazaar/providers/anthropic/key" }
            ],
            "defaultPreset": "free-lane"
          },
          "email": { "enabled": false },
          "scamMailProtocol": { "enabled": false },
          "company": { "source": "template:starter@1.0.0" },
          "modules": ["Bazaar.GC", "Bazaar.Constellation", "Bazaar.KODO"],
          "network": { "statusBind": "loopback", "statusPort": 8443 },
          "settings": { "host.shutdown_timeout_seconds": 20 }
        }
        """;

    /// <summary>Replaces one fragment of <see cref="Valid"/>. The fragment must occur exactly once, so a test edits what it means to.</summary>
    public static string With(string oldText, string newText)
    {
        var count = Valid.Split(oldText).Length - 1;
        if (count != 1)
        {
            throw new InvalidOperationException($"The fixture fragment must occur once; it occurs {count} times.");
        }

        return Valid.Replace(oldText, newText, StringComparison.Ordinal);
    }

    /// <summary>The plan hash printed by a run, read from its "Plan hash:" line.</summary>
    public static string HashFrom(string stdout)
    {
        var line = stdout.Split('\n').Single(l => l.StartsWith("Plan hash: ", StringComparison.Ordinal));
        return line["Plan hash: ".Length..].Trim();
    }
}
