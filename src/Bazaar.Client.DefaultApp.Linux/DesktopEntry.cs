using System.Buffers;
using System.Text;

namespace Bazaar.Client.DefaultApp.Linux;

/// <summary>Builds the text of a per-user <c>.desktop</c> file, escaped per the Desktop Entry specification.</summary>
internal static class DesktopEntry
{
    /// <summary>A key only this module writes. Removal refuses a file that lacks it, so a user's own entry is never deleted.</summary>
    public const string ManagedMarker = "X-Bazaar-Managed=true";

    private static readonly SearchValues<char> _execReserved = SearchValues.Create(" \t\n\"'\\><~|&;$*?#()`");

    /// <summary>Renders the whole file. The settings must already be validated.</summary>
    public static string Render(DefaultAppSettings settings)
    {
        var sb = new StringBuilder();
        sb.Append("[Desktop Entry]\n");
        sb.Append("Type=Application\n");
        sb.Append("Name=").Append(EscapeString(settings.DisplayName)).Append('\n');
        sb.Append("Exec=").Append(EscapeString(ExecLine(settings.ExecPath))).Append('\n');
        sb.Append("Terminal=false\n");
        sb.Append("Categories=Development;Network;\n");
        sb.Append("MimeType=").Append(string.Join(';', settings.MimeTypes)).Append(";\n");
        sb.Append(ManagedMarker).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// The <c>Exec</c> value before string escaping: the path as one quoted argument when it holds reserved characters
    /// (spec: backslash-escape <c>"</c>, <c>`</c>, <c>$</c> and <c>\</c> inside double quotes), with <c>%</c> doubled so it is
    /// not read as a field code, then the <c>%u</c> field code for one URL or file.
    /// </summary>
    internal static string ExecLine(string path)
    {
        string arg = path;
        if (path.AsSpan().IndexOfAny(_execReserved) >= 0)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in path)
            {
                if (c is '"' or '`' or '$' or '\\')
                {
                    sb.Append('\\');
                }

                sb.Append(c);
            }

            arg = sb.Append('"').ToString();
        }

        return arg.Replace("%", "%%", StringComparison.Ordinal) + " %u";
    }

    /// <summary>String-value escaping: a backslash becomes two. Control characters are rejected earlier.</summary>
    internal static string EscapeString(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);
}
