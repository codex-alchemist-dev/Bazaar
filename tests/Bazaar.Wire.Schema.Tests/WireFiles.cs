namespace Bazaar.Wire.Schema.Tests;

/// <summary>Locates the checked-in artifacts under <c>docs/wire</c> and implements the update switch.</summary>
internal static class WireFiles
{
    /// <summary>Set to 1 to rewrite the checked-in schema artifacts and golden frames from the current code.</summary>
    public const string UpdateVariable = "BAZAAR_UPDATE_WIRE_SCHEMA";

    public static readonly string UpdateCommand = $"{UpdateVariable}=1 dotnet test tests/Bazaar.Wire.Schema.Tests (then rebuild and commit docs/wire)";

    public static string Root { get; } = FindRoot();

    public static string Docs { get; } = Path.Combine(Root, "docs", "wire");

    public static bool Updating => Environment.GetEnvironmentVariable(UpdateVariable) == "1";

    public static string Read(string relative)
    {
        var path = Path.Combine(Docs, relative);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public static byte[]? ReadBytes(string relative)
    {
        var path = Path.Combine(Docs, relative);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public static void Write(string relative, byte[] content)
    {
        var path = Path.Combine(Docs, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bazaar.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Bazaar.slnx not found above the test binaries.");
    }
}
