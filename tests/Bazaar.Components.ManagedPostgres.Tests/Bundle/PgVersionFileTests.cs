using Bazaar.Components.ManagedPostgres.Bundle;

namespace Bazaar.Components.ManagedPostgres.Tests.Bundle;

public sealed class PgVersionFileTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "bazaar-p4-pgversion-" + Guid.NewGuid().ToString("N"));

    public PgVersionFileTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    [Fact]
    public void Read_returns_null_when_the_file_is_missing()
    {
        Assert.Null(PgVersionFile.Read(Path.Combine(_temp, "data")));
    }

    [Fact]
    public void Write_then_Read_round_trips_the_major()
    {
        var data = Path.Combine(_temp, "data");

        PgVersionFile.Write(data, 19);

        Assert.Equal(19, PgVersionFile.Read(data));
        Assert.Equal("19" + Environment.NewLine, File.ReadAllText(Path.Combine(data, PgVersionFile.FileName)));
    }

    [Fact]
    public void Read_accepts_surrounding_whitespace()
    {
        var data = Path.Combine(_temp, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, PgVersionFile.FileName), "  18\n");

        Assert.Equal(18, PgVersionFile.Read(data));
    }

    [Theory]
    [InlineData("eighteen")]
    [InlineData("18.6")]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-18")]
    public void Read_throws_on_malformed_content(string content)
    {
        var data = Path.Combine(_temp, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, PgVersionFile.FileName), content);

        Assert.Throws<InvalidDataException>(() => PgVersionFile.Read(data));
    }
}
