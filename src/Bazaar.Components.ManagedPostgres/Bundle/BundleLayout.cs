using System.Globalization;

namespace Bazaar.Components.ManagedPostgres.Bundle;

/// <summary>
/// On-disk layout of the packaged PostgreSQL bundle (packaging.md section 3).
/// Binaries live under <c>{root}/{major}/bin</c>, <c>lib</c> and <c>share</c>, so several majors sit side by side.
/// The cluster for a major lives under <c>{dataRoot}/pg/{major}/data</c>.
/// </summary>
public sealed class BundleLayout
{
    /// <summary>PostgreSQL majors Bazaar ships bundles for.</summary>
    public static IReadOnlyList<int> SupportedMajors { get; } = [18, 19];

    /// <summary>Major used for new clusters. Becomes 19 when PostgreSQL 19 GA ships.</summary>
    public const int DefaultMajor = 18;

    private readonly string _root;
    private readonly string _dataRoot;

    /// <param name="root">The install's pg folder, containing one folder per major.</param>
    /// <param name="dataRoot">The Bazaar data root (DataRoot setting).</param>
    public BundleLayout(string root, string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _root = root;
        _dataRoot = dataRoot;
    }

    /// <summary>Paths for one supported major.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The major is not in <see cref="SupportedMajors"/>.</exception>
    public MajorPaths Resolve(int major)
    {
        if (!SupportedMajors.Contains(major))
        {
            throw new ArgumentOutOfRangeException(nameof(major), major, "Unsupported PostgreSQL major.");
        }

        var folder = major.ToString(CultureInfo.InvariantCulture);
        var majorRoot = Path.Combine(_root, folder);
        return new MajorPaths(
            major,
            Path.Combine(majorRoot, "bin"),
            Path.Combine(majorRoot, "lib"),
            Path.Combine(majorRoot, "share"),
            Path.Combine(_dataRoot, "pg", folder, "data"));
    }

    /// <summary>
    /// Supported majors whose bundle is complete on disk: a postgres executable in <c>bin</c> (POSIX or Windows name)
    /// and the pgvector control file in <c>share/extension</c>.
    /// </summary>
    public IReadOnlyList<int> PresentMajors()
    {
        var present = new List<int>();
        foreach (var major in SupportedMajors)
        {
            var paths = Resolve(major);
            var hasServer = File.Exists(Path.Combine(paths.Bin, "postgres")) || File.Exists(Path.Combine(paths.Bin, "postgres.exe"));
            var hasVector = File.Exists(Path.Combine(paths.Share, "extension", "vector.control"));
            if (hasServer && hasVector)
            {
                present.Add(major);
            }
        }

        return present;
    }
}

/// <summary>Resolved folders for one PostgreSQL major in the bundle.</summary>
/// <param name="Major">PostgreSQL major version.</param>
/// <param name="Bin">Folder with the server and tool executables.</param>
/// <param name="Lib">Folder with the shared libraries, including pgvector.</param>
/// <param name="Share">Folder with extension control files and other data.</param>
/// <param name="DataDirectory">Cluster data directory for this major.</param>
public sealed record MajorPaths(int Major, string Bin, string Lib, string Share, string DataDirectory);
