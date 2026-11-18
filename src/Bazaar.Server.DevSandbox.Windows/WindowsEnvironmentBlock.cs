using System.Text;

namespace Bazaar.Server.DevSandbox.Windows;

/// <summary>Builds the Unicode environment block for <c>CreateProcess</c>: <c>NAME=value</c> entries, each NUL-terminated, sorted by name ignoring case, ended by an extra NUL.</summary>
internal static class WindowsEnvironmentBlock
{
    /// <summary>
    /// The scrubbed environment of a confined run: <c>SystemRoot</c> (many system DLLs need it), and <c>TEMP</c> and <c>TMP</c> pointing at
    /// the per-run work directory (the only place the process can write). No <c>PATH</c>, no user variables, nothing from the host.
    /// </summary>
    internal static string ForRun(string systemRoot, string workDirectory) =>
        Build(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = systemRoot,
            ["TEMP"] = workDirectory,
            ["TMP"] = workDirectory,
        });

    /// <summary>Builds the block. A name must be non-empty, without <c>=</c> or NUL; a value must not hold NUL.</summary>
    internal static string Build(IReadOnlyDictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var sb = new StringBuilder();
        foreach (var (name, value) in variables.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(name) || name.Contains('=', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException($"Invalid environment variable name '{name}'.", nameof(variables));
            }

            if (value is null || value.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException($"Invalid value for environment variable '{name}'.", nameof(variables));
            }

            sb.Append(name).Append('=').Append(value).Append('\0');
        }

        // The block ends with an extra NUL; an empty block is two NULs.
        if (sb.Length == 0)
        {
            sb.Append('\0');
        }

        sb.Append('\0');
        return sb.ToString();
    }
}
