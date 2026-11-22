namespace Bazaar.Server.CodingEdit;

/// <summary>One edit format's tool. The router picks the tool for an agent's <see cref="EditFormat"/>.</summary>
public interface IEditTool
{
    /// <summary>
    /// The capability class the permission gate binds for every edit tool (doc 17, "Tool set": edit is Read in the worktree).
    /// A later change binds this name to <c>Bazaar.Server.CodingPermissions.Abstractions</c>.
    /// </summary>
    const string CapabilityClassName = "Read";

    /// <summary>The format this tool implements.</summary>
    EditFormat Format { get; }

    /// <summary>
    /// Applies one edit. Model input never throws: a malformed or unsafe edit comes back as a refused <see cref="EditResult"/>.
    /// </summary>
    Task<EditResult> ApplyAsync(EditRequest request, CancellationToken ct);
}
