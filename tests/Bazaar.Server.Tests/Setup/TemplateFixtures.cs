using System.Text;
using Bazaar.Admin.Composition;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;

namespace Bazaar.Server.Tests.Setup;

/// <summary>A valid <c>template.json</c> with its seeds, plus the helpers the template tests share. Every seed is plain text.</summary>
internal static class TemplateFixtures
{
    public const string ConstitutionPath = "seeds/constitution.md";
    public const string CharterPath = "seeds/charters/reviewer.md";
    public const string VaultPath = "seeds/vault-layout.md";

    public const string ConstitutionText = "# Constitution draft\n\nThe company works in the open.\n";
    public const string CharterText = "# Reviewer\n\nReviews changes before merge.\n";
    public const string VaultText = "# Vault layout\n\n- Notes/\n- Charters/\n";

    /// <summary>A starter template at exactly 1.0.0. The shape is the doc 45 section 1.8 list, with every section filled in once.</summary>
    public const string Manifest = """
        {
          "schemaVersion": 1,
          "version": "1.0.0",
          "id": "starter",
          "modules": ["Bazaar.GC", "Bazaar.Calendar"],
          "settings": { "host.shutdown_timeout_seconds": 30, "host.log.retention_days": 7 },
          "roles": [
            { "id": "reviewer", "displayName": "Reviewer", "permissions": ["review.write", "gc.read"] }
          ],
          "gcTypes": ["standup", "project"],
          "kanbanColumns": ["Backlog", "Ready", "In progress", "Review", "Done"],
          "calendarPresets": { "standard": { "workdayHours": 8, "timezoneNote": "per agent" } },
          "quotaPresets": { "small": { "messagesPerDay": 500 } },
          "soulPresets": ["cautious-reviewer"],
          "seeds": {
            "constitutionDraft": "seeds/constitution.md",
            "roleCharters": ["seeds/charters/reviewer.md"],
            "vaultLayout": "seeds/vault-layout.md"
          }
        }
        """;

    /// <summary>Loads a template from <paramref name="source"/> with the test's cancellation token and the shipped registry.</summary>
    public static Task<TemplateLoadResult> Load(ITemplateSource source) =>
        TemplateLoader.LoadAsync(source, RegistrySetupSettingCatalog.Known, TestContext.Current.CancellationToken);

    /// <summary>The source that holds <see cref="Manifest"/> and its three seeds.</summary>
    public static InMemoryTemplateSource Starter() => Source(Manifest);

    /// <summary>A source with <paramref name="manifest"/> and the three seeds.</summary>
    public static InMemoryTemplateSource Source(string manifest) => new(
    [
        new KeyValuePair<string, byte[]>(SetupTemplateConstants.ManifestFileName, Encoding.UTF8.GetBytes(manifest)),
        new KeyValuePair<string, byte[]>(ConstitutionPath, Encoding.UTF8.GetBytes(ConstitutionText)),
        new KeyValuePair<string, byte[]>(CharterPath, Encoding.UTF8.GetBytes(CharterText)),
        new KeyValuePair<string, byte[]>(VaultPath, Encoding.UTF8.GetBytes(VaultText)),
    ]);

    /// <summary>Replaces one fragment of <see cref="Manifest"/>. The fragment must occur exactly once, so a test edits what it means to.</summary>
    public static string With(string oldText, string newText) => ReplaceOnce(Manifest, oldText, newText);

    /// <summary>Replaces one fragment of <paramref name="text"/>. The fragment must occur exactly once.</summary>
    public static string ReplaceOnce(string text, string oldText, string newText)
    {
        var count = text.Split(oldText).Length - 1;
        if (count != 1)
        {
            throw new InvalidOperationException($"The fixture fragment must occur once; it occurs {count} times.");
        }

        return text.Replace(oldText, newText, StringComparison.Ordinal);
    }
}
