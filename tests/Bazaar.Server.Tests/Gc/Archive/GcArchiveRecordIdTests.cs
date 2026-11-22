using Bazaar.Server.Gc.Archive;

namespace Bazaar.Server.Tests.Gc.Archive;

/// <summary>Record id parsing for the GC restore target (no database).</summary>
public sealed class GcArchiveRecordIdTests
{
    [Fact]
    public void Record_ids_parse_for_gc_and_project_units()
    {
        var gc = Guid.NewGuid();
        var gcUnit = new Bazaar.Archive.ArchiveUnit(Bazaar.Archive.ArchiveUnitKind.GroupChat, gc.ToString("D"));
        Assert.True(PgGcArchiveRestoreTarget.TryParse(gcUnit, "seq-7", out var g1, out var s1));
        Assert.Equal((gc, 7L), (g1, s1));

        var other = Guid.NewGuid();
        var project = new Bazaar.Archive.ArchiveUnit(Bazaar.Archive.ArchiveUnitKind.Project, gc.ToString("D"));
        Assert.True(PgGcArchiveRestoreTarget.TryParse(project, other.ToString("D") + "/seq-3", out var g2, out var s2));
        Assert.Equal((other, 3L), (g2, s2));

        Assert.False(PgGcArchiveRestoreTarget.TryParse(project, "seq-3", out _, out _));
        Assert.False(PgGcArchiveRestoreTarget.TryParse(gcUnit, "seq-x", out _, out _));
        Assert.False(PgGcArchiveRestoreTarget.TryParse(gcUnit, "seq--1", out _, out _));
    }
}
