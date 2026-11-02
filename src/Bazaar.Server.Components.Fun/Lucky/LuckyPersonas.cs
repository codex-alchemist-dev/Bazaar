namespace Bazaar.Server.Fun.Lucky;

/// <summary>Persona selection from a recorded seed. The same seed always selects the same persona.</summary>
public static class LuckyPersonas
{
    /// <summary>The persona names a seed can select. Order is part of the contract: changing it changes replays.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "skeptic",
        "tinkerer",
        "archivist",
        "cartographer",
        "minimalist",
        "contrarian",
        "storyteller",
        "auditor",
    ];

    /// <summary>Selects the persona for a seed.</summary>
    /// <param name="seed">The recorded seed.</param>
    public static string Pick(int seed) => Names[new Random(seed).Next(Names.Count)];
}
