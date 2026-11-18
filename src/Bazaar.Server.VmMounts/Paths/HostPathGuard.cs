using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmMounts.Paths;

/// <summary>A host mount path that passed every check. All paths are canonical and symlink-free.</summary>
internal sealed record GuardedPath(string AgentRoot, string KindRoot, string MountRoot);

/// <summary>
/// The path jail for host mount roots (doc 16 "Scoped mounts", doc 15 "Credentials, mounts, browser"). A path is accepted only if
/// it names an existing directory inside the agent's own Soul or workspace folder, with no link anywhere between the agents root
/// and the mount root, and clear of every denied location. Every refusal is a <see cref="MountRefusedException"/>; the guard never
/// "fixes" a path by trimming or rewriting it, and only reads the file system.
/// </summary>
internal sealed class HostPathGuard
{
    private const int _maxLinkDepth = 32;

    private static readonly HashSet<string> _deniedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "audit", "secrets", "keys", ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".canary",
    };

    private readonly VmMountOptions _options;
    private readonly bool _ci;
    private readonly string _agentsRootLexical;
    private readonly string _agentsRootReal;
    private readonly string[] _denyReal;
    private readonly StringComparison _cmp;

    public HostPathGuard(VmMountOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _ci = options.CaseInsensitive;
        _cmp = _ci ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Path.IsPathFullyQualified(options.AgentsRoot))
            throw new ArgumentException("AgentsRoot must be an absolute path.", nameof(options));
        // The folders of an agent's root must be single safe names and must not alias each other, or the two shares would overlap.
        string[] folders = [options.SoulFolderName, options.WorkspaceFolderName, options.ProfileFolderName, options.StagingFolderName];
        if (folders.Any(f => !SafeNames.IsSafeSegment(f)) || folders.Take(2).Concat(folders.Skip(3)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            throw new ArgumentException("The soul, workspace and staging folder names must be distinct safe names.", nameof(options));
        _agentsRootLexical = Path.GetFullPath(options.AgentsRoot);
        _agentsRootReal = RealPath(_agentsRootLexical, 0);
        _denyReal = [.. options.DenyPaths.Select(d => RealPath(Path.GetFullPath(d), 0))];
    }

    /// <summary>The folder of one agent: <c>AgentsRoot/agentId</c>, after link resolution of the agents root.</summary>
    public string AgentRoot(Guid agentId) => Path.Combine(_agentsRootReal, agentId.ToString("N"));

    public GuardedPath Check(Guid agentId, MountKind kind, string hostPath)
    {
        LexicalCheck(hostPath);
        var trimmed = hostPath.TrimEnd('/', '\\');
        var full = Path.GetFullPath(trimmed);

        var below = SafeNames.SegmentsBelow(_agentsRootLexical, full, _ci) ?? SafeNames.SegmentsBelow(_agentsRootReal, full, _ci);
        if (below is null)
        {
            // Not spelled inside the agents root. A link that leads in is still a link, which is refused as such.
            var real = RealPath(full, 0);
            if (SafeNames.IsUnderOrEqual(_agentsRootReal, real, _ci))
                throw Refuse(MountRefusalReason.LinkNotAllowed, "The host path reaches the agents root through a link.");
            throw Refuse(MountRefusalReason.OutsideAgentRoot, "The host path is outside the agents root.");
        }

        foreach (var segment in below)
        {
            if (!SafeNames.IsSafeSegment(segment))
                throw Refuse(MountRefusalReason.InvalidPath, $"The host path segment '{SafeText.Clean(segment)}' is not a safe name.");
        }

        var agentName = agentId.ToString("N");
        if (below.Length == 0)
            throw Refuse(MountRefusalReason.OutsideAgentRoot, "The host path is the agents root itself.");
        if (!below[0].Equals(agentName, _cmp))
        {
            if (Guid.TryParse(below[0], out _))
                throw Refuse(MountRefusalReason.DeniedPath, "The host path is inside another agent's folder.");
            throw Refuse(MountRefusalReason.OutsideAgentRoot, "The host path is not inside the agent's own root.");
        }

        var kindFolder = kind == MountKind.Soul ? _options.SoulFolderName : _options.WorkspaceFolderName;
        if (below.Length < 2 || !below[1].Equals(kindFolder, _cmp))
            throw Refuse(MountRefusalReason.OutsideAgentRoot, $"A {kind} mount must be the agent's '{kindFolder}' folder or below it.");

        foreach (var segment in below.Skip(2))
        {
            if (_deniedSegments.Contains(segment))
                throw Refuse(MountRefusalReason.DeniedPath, $"The host path segment '{SafeText.Clean(segment)}' is denied.");
        }

        // Deny paths are compared case-insensitively whatever the volume: a wrong "no overlap" would expose a denied folder, while a
        // wrong "overlap" only refuses a mount. This runs before the walk, so a denied path is refused whether or not it exists.
        var lexical = Path.Combine([_agentsRootReal, .. below]);
        foreach (var deny in _denyReal)
        {
            if (SafeNames.IsUnderOrEqual(deny, lexical, caseInsensitive: true) || SafeNames.IsUnderOrEqual(lexical, deny, caseInsensitive: true))
                throw Refuse(MountRefusalReason.DeniedPath, "The host path overlaps a denied location.");
        }

        // Walk from the agents root, one segment at a time. A link anywhere on the way is refused, even if it points back inside.
        var current = _agentsRootReal;
        foreach (var segment in below)
        {
            current = Path.Combine(current, segment);
            if (IsLink(current))
                throw Refuse(MountRefusalReason.LinkNotAllowed, $"'{SafeText.Clean(segment)}' is a link; mounts never follow links.");
            if (!Directory.Exists(current))
                throw Refuse(MountRefusalReason.InvalidPath, $"'{SafeText.Clean(segment)}' is not an existing directory.");
        }

        foreach (var deny in _denyReal)
        {
            if (SafeNames.IsUnderOrEqual(deny, current, caseInsensitive: true) || SafeNames.IsUnderOrEqual(current, deny, caseInsensitive: true))
                throw Refuse(MountRefusalReason.DeniedPath, "The host path overlaps a denied location.");
        }

        return new GuardedPath(AgentRoot(agentId), Path.Combine(AgentRoot(agentId), kindFolder), current);
    }

    /// <summary>Whether an existing path is a symbolic link, a junction or another reparse point (also a broken one).</summary>
    public static bool IsLink(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is not null)
            return true;
        return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0;
    }

    /// <summary>The real path of <paramref name="path"/>: every link on it resolved. A path that does not exist is returned as given.</summary>
    public static string RealPath(string path, int depth)
    {
        if (depth > _maxLinkDepth)
            throw Refuse(MountRefusalReason.LinkNotAllowed, "Too many nested links.");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is null)
                continue;
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
                current = RealPath(target.FullName, depth + 1);
        }

        return current;
    }

    private static void LexicalCheck(string hostPath)
    {
        if (string.IsNullOrWhiteSpace(hostPath) || hostPath.Length > 4096)
            throw Refuse(MountRefusalReason.InvalidPath, "The host path is empty or too long.");
        foreach (var c in hostPath)
        {
            if (c < 0x20 || c == 0x7F)
                throw Refuse(MountRefusalReason.InvalidPath, "The host path holds a control character.");
        }

        // UNC, device and extended-length paths all start with two separators.
        if (hostPath.StartsWith(@"\\", StringComparison.Ordinal) || hostPath.StartsWith("//", StringComparison.Ordinal))
            throw Refuse(MountRefusalReason.InvalidPath, "UNC and device paths are not accepted.");
        if (!Path.IsPathFullyQualified(hostPath))
            throw Refuse(MountRefusalReason.InvalidPath, "The host path must be absolute.");
        if (!OperatingSystem.IsWindows() && hostPath.Contains('\\', StringComparison.Ordinal))
            throw Refuse(MountRefusalReason.InvalidPath, "A backslash in a host path is not accepted on this OS.");

        // A colon is allowed only in a drive prefix ("C:"). Anywhere else it opens an alternate data stream.
        var colon = hostPath.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0 && (colon != 1 || !char.IsAsciiLetter(hostPath[0]) || hostPath.IndexOf(':', 2) >= 0))
            throw Refuse(MountRefusalReason.InvalidPath, "A colon outside a drive prefix is not accepted.");

        foreach (var segment in hostPath.Split('/', '\\'))
        {
            if (segment is "." or "..")
                throw Refuse(MountRefusalReason.InvalidPath, "Relative segments ('.' and '..') are not accepted.");

            // Windows drops a trailing dot or space when it normalises, so the raw spelling is checked before GetFullPath hides it.
            if (segment.Length > 0 && segment[^1] is '.' or ' ')
                throw Refuse(MountRefusalReason.InvalidPath, "A segment ending in a dot or a space is not accepted.");
        }
    }

    private static MountRefusedException Refuse(MountRefusalReason reason, string message) => new(reason, message);
}
