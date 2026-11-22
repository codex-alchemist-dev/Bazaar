namespace Bazaar.Rubedo.Docs;

/// <summary>One generated page: a forward-slash path relative to the reference folder, and its Markdown (LF newlines, ends with one newline).</summary>
public sealed record RubedoReferencePage(string RelativePath, string Text);
