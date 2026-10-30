namespace Bazaar.Client.LocalInference.Models;

/// <summary>A model version installed in the store: its verified manifest and folder.</summary>
public sealed record InstalledModel(ModelManifest Manifest, string InstallPath);

/// <summary>
/// Local model store laid out as <c>&lt;root&gt;/&lt;model&gt;/&lt;version&gt;/</c> with <c>manifest.json</c> and the listed
/// files. Installs are staged in a hidden folder and moved into place with one rename, so a failed install leaves no
/// partial version folder. Only manifest-listed models can be selected.
/// </summary>
public sealed class LocalModelStore
{
    private const string StagingPrefix = ".staging-";

    private readonly ManifestPolicy _policy;
    private readonly IManifestVerifier _verifier;

    public LocalModelStore(string root, ManifestPolicy policy, IManifestVerifier verifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    }

    public string Root { get; }

    /// <summary>
    /// Installs the model in a source folder (its <c>manifest.json</c> plus the listed files). Files are copied into a
    /// staging folder, verified there, and moved into place. Any failure removes the staging folder.
    /// </summary>
    public async Task<InstalledModel> InstallAsync(string sourceDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);

        var manifestPath = Path.Combine(sourceDirectory, ModelManifestReader.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new ModelStoreException($"Source folder has no {ModelManifestReader.ManifestFileName}.");
        }

        var manifest = ModelManifestReader.Parse(await File.ReadAllTextAsync(manifestPath, ct));
        ModelIntegrity.VerifySignature(manifest, _policy, _verifier);

        var modelDirectory = Path.Combine(Root, manifest.Model);
        var target = Path.Combine(modelDirectory, manifest.Version);
        if (Directory.Exists(target))
        {
            throw new ModelStoreException($"Model {manifest.Model} {manifest.Version} is already installed.");
        }

        var createdModelDirectory = !Directory.Exists(modelDirectory);
        Directory.CreateDirectory(modelDirectory);
        var staging = Path.Combine(modelDirectory, StagingPrefix + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(staging);
            foreach (var file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                var source = Path.Combine(sourceDirectory, file.Name);
                if (!File.Exists(source))
                {
                    throw new ModelIntegrityException(ModelIntegrityFailure.Missing, file.Name, $"Model file '{file.Name}' is missing from the source folder.");
                }

                File.Copy(source, Path.Combine(staging, file.Name));
            }

            await File.WriteAllTextAsync(Path.Combine(staging, ModelManifestReader.ManifestFileName), ModelManifestReader.Serialize(manifest), ct);
            await ModelIntegrity.VerifyFilesAsync(manifest, staging, ct);
            Directory.Move(staging, target);
        }
        catch
        {
            RemoveQuietly(staging);
            if (createdModelDirectory)
            {
                RemoveEmptyDirectoryQuietly(modelDirectory);
            }

            throw;
        }

        return new InstalledModel(manifest, target);
    }

    /// <summary>
    /// Lists installed models whose manifest parses and sits in the folder named for its own model and version. Files are
    /// not hashed here; <see cref="ResolveFileAsync"/> checks them before use.
    /// </summary>
    public IReadOnlyList<InstalledModel> ListInstalled()
    {
        var installed = new List<InstalledModel>();
        if (!Directory.Exists(Root))
        {
            return installed;
        }

        foreach (var modelDirectory in Directory.EnumerateDirectories(Root))
        {
            var model = Path.GetFileName(modelDirectory);
            if (model.StartsWith('.'))
            {
                continue;
            }

            foreach (var versionDirectory in Directory.EnumerateDirectories(modelDirectory))
            {
                var version = Path.GetFileName(versionDirectory);
                if (version.StartsWith('.') || !File.Exists(Path.Combine(versionDirectory, ModelManifestReader.ManifestFileName)))
                {
                    continue;
                }

                var found = TryReadInstalled(model, version);
                if (found is not null)
                {
                    installed.Add(found);
                }
            }
        }

        return installed;
    }

    /// <summary>
    /// Returns the full path of a file listed in an installed model's manifest, after the signature and that file's length
    /// and SHA-256 have been checked. Unknown models, unlisted files and mismatched files are refused.
    /// </summary>
    public async Task<string> ResolveFileAsync(string model, string version, string fileName, CancellationToken ct = default)
    {
        var installed = ModelManifestReader.IsSafeName(model) && ModelManifestReader.IsSafeName(version)
            ? TryReadInstalled(model, version)
            : null;
        if (installed is null)
        {
            throw new ModelStoreException($"Model {model} {version} is not installed.");
        }

        ModelIntegrity.VerifySignature(installed.Manifest, _policy, _verifier);

        var entry = installed.Manifest.Files.FirstOrDefault(file => string.Equals(file.Name, fileName, StringComparison.Ordinal));
        if (entry is null)
        {
            throw new ModelStoreException($"File '{fileName}' is not listed in the manifest of {model} {version}.");
        }

        await ModelIntegrity.VerifyFileAsync(entry, installed.InstallPath, ct);
        return Path.Combine(installed.InstallPath, entry.Name);
    }

    private InstalledModel? TryReadInstalled(string model, string version)
    {
        var folder = Path.Combine(Root, model, version);
        var manifestPath = Path.Combine(folder, ModelManifestReader.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var manifest = ModelManifestReader.Parse(File.ReadAllText(manifestPath));
            if (manifest.Model != model || manifest.Version != version)
            {
                return null;
            }

            return new InstalledModel(manifest, folder);
        }
        catch (ModelFormatException)
        {
            return null;
        }
    }

    private static void RemoveQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void RemoveEmptyDirectoryQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
