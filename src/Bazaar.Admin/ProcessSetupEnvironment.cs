using System.Collections;
using Bazaar.Server.Setup;

namespace Bazaar.Admin;

/// <summary>
/// The real process environment, read once per call. This is the only place the admin command reads environment variables; the
/// setup overlay receives them through <see cref="ISetupEnvironment"/>, so its tests never touch the process.
/// </summary>
internal sealed class ProcessSetupEnvironment : ISetupEnvironment
{
    /// <summary>The shared instance. It holds no state, so one instance serves every run.</summary>
    public static ProcessSetupEnvironment Instance { get; } = new();

    public IReadOnlyDictionary<string, string?> Variables
    {
        get
        {
            var variables = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string name)
                {
                    variables[name] = entry.Value as string;
                }
            }

            return variables;
        }
    }
}
