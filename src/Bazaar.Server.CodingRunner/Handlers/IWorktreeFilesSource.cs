using Bazaar.Server.CodingEdit;
using Bazaar.Server.CodingGit;

namespace Bazaar.Server.CodingRunner.Handlers;

/// <summary>
/// Gives the read and list handlers the file store for one worktree (doc 17, "Coding tool handlers", decision 7). The host binds it
/// per <see cref="CodingWorktree"/>, over the guest channel. Returning null means no store exists for that worktree, and the call
/// fails closed. No default is registered, the same rule as <c>CodingEditModule</c>.
/// </summary>
public interface IWorktreeFilesSource
{
    /// <summary>Returns the file store for <paramref name="worktree"/>, or null when none is available.</summary>
    IWorktreeFiles? FilesFor(CodingWorktree worktree);
}
