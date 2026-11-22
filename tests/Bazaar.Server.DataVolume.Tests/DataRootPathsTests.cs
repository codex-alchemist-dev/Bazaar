namespace Bazaar.Server.DataVolume.Tests;

/// <summary>Database files must stay inside the data root, with no link on the way (doc 63 section 5, step 3).</summary>
public sealed class DataRootPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-dv-paths-" + Guid.CreateVersion7().ToString("N"));
    private readonly string _data;
    private readonly string _outside;

    public DataRootPathsTests()
    {
        _data = Path.Combine(_root, "data");
        _outside = Path.Combine(_root, "plain");
        Directory.CreateDirectory(Path.Combine(_data, "pg", "18", "data"));
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private DataVolumeRefusal Refusal(string path) =>
        Assert.Throws<DataVolumeRefusedException>(() => DataRootPaths.EnsureInside(_data, [path])).Reason;

    [Fact]
    public void Paths_inside_the_data_root_pass_even_when_they_do_not_exist_yet()
    {
        DataRootPaths.EnsureInside(_data, [_data, Path.Combine(_data, "pg", "18", "data"), Path.Combine(_data, "pg", "19", "data", "pg_wal")]);
    }

    [Fact]
    public void A_path_outside_the_data_root_is_refused()
    {
        Assert.Equal(DataVolumeRefusal.PostgresFilesOutsideVolume, Refusal(_outside));
    }

    [Fact]
    public void A_sibling_folder_sharing_the_name_prefix_is_refused()
    {
        Assert.Equal(DataVolumeRefusal.PostgresFilesOutsideVolume, Refusal(_data + "-copy"));
    }

    [Fact]
    public void A_path_that_climbs_out_with_dot_dot_is_refused()
    {
        Assert.Equal(DataVolumeRefusal.PostgresFilesOutsideVolume, Refusal(Path.Combine(_data, "pg", "..", "..", "plain")));
    }

    [Fact]
    public void A_relative_path_is_refused()
    {
        Assert.Equal(DataVolumeRefusal.PostgresFilesOutsideVolume, Refusal("pg"));
    }

    [Fact]
    public void A_wal_folder_linked_to_a_plain_folder_is_refused()
    {
        var wal = Path.Combine(_data, "pg", "18", "data", "pg_wal");
        try
        {
            Directory.CreateSymbolicLink(wal, _outside);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("This account may not create symbolic links (Windows without Developer Mode).");
        }

        Assert.Equal(DataVolumeRefusal.PostgresFilesOutsideVolume, Refusal(wal));
        Assert.Equal(DataVolumeRefusal.PostgresFilesOutsideVolume, Refusal(Path.Combine(wal, "000000010000000000000001")));
    }
}
