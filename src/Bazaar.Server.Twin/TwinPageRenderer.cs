using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Twin.Prose;

namespace Bazaar.Server.Twin;

/// <summary>
/// Renders a validated draft (the Twin.Prose validator's output) as Vault Markdown (doc 25 Digital Twin). Pure and deterministic: the same page gives the same
/// bytes, with no clock and no random id. Front matter lists the cited source refs (sorted) and the dropped-sentence count;
/// each sentence is one paragraph followed by an HTML comment naming its source.
/// </summary>
public sealed class TwinPageRenderer : ITwinPageRenderer
{
    public const string TwinType = "twin";

    /// <summary>
    /// The page's document id. Derived from the path, so a regenerated page keeps the same id and the same per-document write
    /// queue. Not a secret and not a UUID v7; the Vault only needs it stable.
    /// </summary>
    public static Guid DocId(string project, string page)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(TwinPaths.PagePath(project, page)));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>Renders the accepted sentences of a validated draft as one page. The same input gives the same bytes.</summary>
    public static string Render(string project, string page, string title, ValidatedProse prose) =>
        RenderPage(project, page, title, prose, null);

    /// <summary>
    /// Renders the page for the prose step (<see cref="TwinProseOptions.Renderer"/>): the same page, with the version, trust, generation
    /// time and predecessor in the front matter. The generation time is the step's clock, so the same input still gives the same bytes.
    /// </summary>
    string ITwinPageRenderer.Render(TwinPageRenderInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return RenderPage(input.Project, input.Page, input.Title, input.Prose, input);
    }

    private static string RenderPage(string project, string page, string title, ValidatedProse prose, TwinPageRenderInput? run)
    {
        ArgumentNullException.ThrowIfNull(prose);
        TwinPaths.ValidateTitle(title);

        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append("id: ").Append(ToUlid(DocId(project, page))).Append('\n');
        builder.Append("type: ").Append(TwinType).Append('\n');
        builder.Append("title: ").Append(title).Append('\n');
        builder.Append("project: ").Append(project).Append('\n');
        builder.Append("page: ").Append(page).Append('\n');
        if (run is not null)
        {
            builder.Append("version: ").Append(run.Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append("trust: ").Append(prose.Trust).Append('\n');
            builder.Append("generated_at: ").Append(run.GeneratedAt.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
            if (run.Previous is not null)
            {
                builder.Append("predecessor: ").Append(run.Previous.Path).Append('@').Append(run.Previous.Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        builder.Append("dropped_sentences: ").Append(prose.Dropped).Append('\n');
        builder.Append("source_refs:\n");
        foreach (var reference in prose.Sentences.Select(s => s.SourceRef).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            builder.Append("  - ").Append(reference).Append('\n');
        }

        builder.Append("---\n\n");
        builder.Append("# ").Append(title).Append("\n\n");
        foreach (var sentence in prose.Sentences)
        {
            builder.Append(sentence.Text).Append(" <!-- source: ").Append(sentence.SourceRef).Append(" -->\n\n");
        }

        return builder.ToString();
    }

    /// <summary>The 26-character Crockford base32 form of a document id (doc 18: front-matter ids are ULIDs). Same encoding as the Vault writer's.</summary>
    internal static string ToUlid(Guid id)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var value = new BigInteger(Convert.FromHexString(id.ToString("N")), isUnsigned: true, isBigEndian: true);
        var chars = new char[26];
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            chars[i] = alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return new string(chars);
    }
}
