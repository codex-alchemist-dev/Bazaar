using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Hive.Abstractions;

namespace Bazaar.Server.Hive;

/// <summary>Builds the Vault path and Markdown of a copy (doc 08 section 6: <c>hive/&lt;kind&gt;/&lt;slug&gt;.md</c>).</summary>
internal static class HiveDocument
{
    public const string StatusKey = "status";
    public const string Active = "active";
    public const string Deprecated = "deprecated";

    /// <summary>Slug from the title plus a short hash of (from, memory, to), so two copies never share a path.</summary>
    public static string PathFor(string kind, string title, ProjectId from, string originMemory, ProjectId to)
    {
        var sb = new StringBuilder();
        foreach (var c in title.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
            if (sb.Length >= 48)
                break;
        }

        var slug = sb.ToString().Trim('-');
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{from.Value}\n{originMemory}\n{to.Value}")))[..8];
        return $"hive/{kind}/{(slug.Length == 0 ? "lesson" : slug)}-{hash}.md";
    }

    public static string Render(HiveCopy copy) =>
        $"""
        ---
        origin_project: {Quote(copy.FromProject.Value)}
        origin_memory: {Quote(copy.OriginMemory)}
        trust: {copy.Trust}
        origin_kind: {Quote(copy.OriginKind)}
        kind: {copy.Kind}
        target_project: {Quote(copy.ToProject.Value)}
        {StatusKey}: {Active}
        ---
        # {copy.Title.ReplaceLineEndings(" ")}

        {copy.Body}
        """.ReplaceLineEndings("\n") + "\n";

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .ReplaceLineEndings(" ") + "\"";
}
