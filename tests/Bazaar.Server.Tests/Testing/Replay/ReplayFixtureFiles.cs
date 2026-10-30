using Bazaar.Testing.Replay;

namespace Bazaar.Server.Tests.Testing.Replay;

/// <summary>Locates the replay fixtures copied next to the test assembly (see the csproj's Fixtures entry).</summary>
internal static class ReplayFixtureFiles
{
    public static string FilePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Testing", "Replay", "Fixtures", name);

    public static ReplayFixture Load(string name) => ReplayFixtureLoader.Load(FilePath(name));
}
