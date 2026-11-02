namespace Bazaar.Engines.Godot.SceneDiff;

/// <summary>A .tscn or .tres file is not valid Godot 4 text resource syntax this library understands.</summary>
public sealed class SceneParseException : Exception
{
    /// <summary>Creates the exception for a problem found on a 1-based line.</summary>
    public SceneParseException(int line, string message)
        : base($"Line {line}: {message}")
    {
        Line = line;
    }

    /// <summary>The 1-based line where the problem was found.</summary>
    public int Line { get; }
}
