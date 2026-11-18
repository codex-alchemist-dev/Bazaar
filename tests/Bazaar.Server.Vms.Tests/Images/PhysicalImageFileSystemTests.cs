using Bazaar.Server.Vms.Images;

namespace Bazaar.Server.Vms.Tests.Images;

public sealed class PhysicalImageFileSystemTests
{
    private const UnixFileMode _writeBits = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    [Fact]
    public void Reports_existence_and_length()
    {
        using var dir = new TempImageDirectory();
        var path = dir.WriteFile("parent.img", "hello");
        var fs = new PhysicalImageFileSystem();

        Assert.True(fs.FileExists(path));
        Assert.False(fs.FileExists(dir.FilePath("missing.img")));
        Assert.Equal(5, fs.GetLength(path));
    }

    [Fact]
    public void Opens_the_file_for_reading()
    {
        using var dir = new TempImageDirectory();
        var path = dir.WriteFile("parent.img", "hello");
        var fs = new PhysicalImageFileSystem();

        using var stream = fs.OpenRead(path);
        using var reader = new StreamReader(stream);

        Assert.Equal("hello", reader.ReadToEnd());
    }

    [Fact]
    public void Setting_read_only_protects_the_file_and_clearing_it_restores_writes()
    {
        using var dir = new TempImageDirectory();
        var path = dir.WriteFile("parent.img", "hello");
        var fs = new PhysicalImageFileSystem();

        Assert.False(fs.IsReadOnly(path));

        fs.SetReadOnly(path, isReadOnly: true);
        Assert.True(fs.IsReadOnly(path));
        AssertNoWriteAccessBits(path, expectReadOnly: true);

        fs.SetReadOnly(path, isReadOnly: false);
        Assert.False(fs.IsReadOnly(path));
        AssertNoWriteAccessBits(path, expectReadOnly: false);
        File.WriteAllText(path, "changed");
    }

    [Fact]
    public void Setting_the_same_state_twice_is_harmless()
    {
        using var dir = new TempImageDirectory();
        var path = dir.WriteFile("parent.img", "hello");
        var fs = new PhysicalImageFileSystem();

        fs.SetReadOnly(path, isReadOnly: true);
        fs.SetReadOnly(path, isReadOnly: true);

        Assert.True(fs.IsReadOnly(path));
    }

    [Fact]
    public void Missing_file_is_reported_by_opening_it()
    {
        using var dir = new TempImageDirectory();
        var fs = new PhysicalImageFileSystem();

        Assert.Throws<FileNotFoundException>(() => fs.OpenRead(dir.FilePath("missing.img")));
    }

    private static void AssertNoWriteAccessBits(string path, bool expectReadOnly)
    {
        if (OperatingSystem.IsWindows())
        {
            var readOnly = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
            Assert.Equal(expectReadOnly, readOnly);
            return;
        }

        var hasNoWriteBits = (File.GetUnixFileMode(path) & _writeBits) == 0;
        Assert.Equal(expectReadOnly, hasNoWriteBits);
    }
}
