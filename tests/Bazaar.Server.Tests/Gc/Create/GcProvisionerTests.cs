using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Gc.Create;

namespace Bazaar.Server.Tests.Gc.Create;

/// <summary>The provisioning seam other modules use to create a room (doc 40 section 5a, step 5).</summary>
public sealed class GcProvisionerTests(GcDb db) : IClassFixture<GcDb>
{
    private static readonly GcPrincipal _owner = new(Guid.Parse("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa"), PrincipalKind.Owner, "Owner");

    [Fact]
    public async Task A_project_gc_has_no_parent_and_the_owner_as_its_only_member_and_a_retry_changes_nothing()
    {
        GcTestSkip.WithoutDatabase();
        var provisioner = new GcCreateService(db.DataSource);
        Assert.IsAssignableFrom<IGcProvisioner>(provisioner);
        var id = GcCreateService.DeterministicGcId($"fork:{Guid.NewGuid():D}");
        var ct = TestContext.Current.CancellationToken;

        Assert.True(await provisioner.CreateProjectGcAsync(id, "Fork of Plans at seq 4", _owner, ct));
        Assert.False(await provisioner.CreateProjectGcAsync(id, "Fork of Plans at seq 4", _owner, ct));

        await using var cmd = db.DataSource.CreateCommand(
            "SELECT type_key, parent_gc_id, (SELECT count(*) FROM gc_member m WHERE m.gc_id = g.gc_id) FROM gc g WHERE gc_id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal("project", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal(1L, reader.GetInt64(2));
    }
}
