namespace Bazaar.Server.Luau.Tests;

/// <summary>A throwaway project root with a few Luau files. Deleted on dispose.</summary>
internal sealed class TempProject : IDisposable
{
    public TempProject()
    {
        Root = Path.Combine(Path.GetTempPath(), "bazaar-luau-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "scripts"));
        File.WriteAllText(Path.Combine(Root, "scripts", "Main.lua"), "local x = 1\n");
        File.WriteAllText(Path.Combine(Root, "scripts", "Util.lua"), "return 1\n");
    }

    public string Root { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a test may leave a link that the OS still holds open.
        }
    }
}
