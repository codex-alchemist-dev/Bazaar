namespace Bazaar.Server.Setup;

/// <summary>
/// The environment variables a setup run may read. The process environment is passed in through this interface, so the overlay
/// never reads it directly and tests can supply a dictionary. Names are as the platform reports them.
/// </summary>
public interface ISetupEnvironment
{
    /// <summary>The variables. A null value is an unset variable.</summary>
    IReadOnlyDictionary<string, string?> Variables { get; }
}
