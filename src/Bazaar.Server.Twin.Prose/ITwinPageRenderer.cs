namespace Bazaar.Server.Twin.Prose;

/// <summary>Everything a page renderer needs for one prose page. <see cref="Prose"/> holds only validated sentences.</summary>
public sealed record TwinPageRenderInput(
    string Project,
    string Page,
    string Title,
    int Version,
    DateTimeOffset GeneratedAt,
    TwinPageRef? Previous,
    ValidatedProse Prose);

/// <summary>
/// Turns a validated prose page into the full file text, front matter included. The text must carry a <c>version: N</c> front matter
/// line, which the page writer reads back as the page's version.
/// </summary>
public interface ITwinPageRenderer
{
    string Render(TwinPageRenderInput input);
}
