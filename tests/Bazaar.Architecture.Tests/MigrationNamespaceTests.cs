namespace Bazaar.Architecture.Tests;

/// <summary>
/// Wave 2 of BAZAAR-COMPONENTS.md: the shared <c>common</c> migration namespace is frozen. Applied files are never moved, because
/// history rows are keyed by namespace and version, so a moved file would look unapplied. New component SQL goes in its own namespace.
/// </summary>
public class MigrationNamespaceTests
{
    /// <summary>The 20 files applied before the freeze. Adding to this list is a design change, not a routine edit.</summary>
    private static readonly string[] _frozenCommon =
    [
        "0001_core_schema.sql",
        "0002_gateway.sql",
        "0003_settings.sql",
        "0004_audit_log.sql",
        "0006_job.sql",
        "0007_search_document.sql",
        "0008_gc_support_tables.sql",
        "0009_vault.sql",
        "0010_agent_runtime.sql",
        "0011_approval.sql",
        "0012_kanban.sql",
        "0013_reputation_ledger.sql",
        "0014_soul_versions.sql",
        "0015_interrupt_frame.sql",
        "0016_skimmer_gate.sql",
        "0017_calendar.sql",
        "0018_org.sql",
        "0019_identity_keys.sql",
        "0020_constellation_views.sql",
    ];

    [Fact]
    public void Common_migration_folder_holds_only_the_frozen_files()
    {
        var folder = Path.Combine(RepoPaths.SourceDirectory("Bazaar.Server"), "Data", "Sql", "common");
        var present = Directory.GetFiles(folder, "*.sql").Select(Path.GetFileName).OfType<string>();

        var added = present.Except(_frozenCommon, StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList();

        Assert.True(added.Count == 0, "New migrations must go in their component's namespace, not common/: " + string.Join(", ", added));
    }

    [Fact]
    public void Every_frozen_file_is_still_present()
    {
        var folder = Path.Combine(RepoPaths.SourceDirectory("Bazaar.Server"), "Data", "Sql", "common");
        var present = Directory.GetFiles(folder, "*.sql").Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal);

        var missing = _frozenCommon.Where(f => !present.Contains(f)).ToList();

        Assert.True(missing.Count == 0, "Applied common migrations must not be removed or renamed: " + string.Join(", ", missing));
    }
}
