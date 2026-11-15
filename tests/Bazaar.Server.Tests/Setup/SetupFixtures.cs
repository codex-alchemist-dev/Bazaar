namespace Bazaar.Server.Tests.Setup;

/// <summary>A valid <c>bazaar-setup.json</c> based on the doc 45 section 1.5 sketch, plus the helpers the setup tests share.</summary>
internal static class SetupFixtures
{
    /// <summary>A fake secret value. Every test that places it in an input asserts that no output contains it.</summary>
    public const string Canary = "canary-secret-0000";

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
}
