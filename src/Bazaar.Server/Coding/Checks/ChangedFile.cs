// TODO(component): move to the coding component once PR #125 merges
namespace Bazaar.Server.Coding.Checks;

public enum ChangeKind
{
    Added,
    Modified,
    Deleted,
}

/// <summary>
/// One file in a PR diff. <see cref="OldText"/> is null for an added file and <see cref="NewText"/> is null for a deleted one.
/// The checks read these texts only; they never touch the file system.
/// </summary>
public sealed record ChangedFile(string Path, ChangeKind Kind, string? OldText, string? NewText);
