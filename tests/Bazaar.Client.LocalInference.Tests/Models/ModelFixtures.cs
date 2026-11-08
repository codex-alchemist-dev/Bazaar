using System.Security.Cryptography;
using System.Text;
using Bazaar.Client.LocalInference.Models;

namespace Bazaar.Client.LocalInference.Tests.Models;

/// <summary>A temporary folder under the system temp path, unique per test and deleted on Dispose.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "bazaar-local-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Combine(params string[] parts)
    {
        var path = Root;
        foreach (var part in parts)
        {
            path = Path.Combine(path, part);
        }

        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>Builds source model folders and manifests for tests. No real model data is involved.</summary>
public static class ModelFixtures
{
    public static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>Writes the files and an unsigned <c>manifest.json</c> listing them with their real SHA-256 and length.</summary>
    public static ModelManifest WriteSource(string sourceDirectory, string model, string version, params (string Name, byte[] Bytes)[] files)
    {
        Directory.CreateDirectory(sourceDirectory);
        var entries = new List<ManifestFile>();
        foreach (var (name, bytes) in files)
        {
            File.WriteAllBytes(Path.Combine(sourceDirectory, name), bytes);
            entries.Add(new ManifestFile(name, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length));
        }

        var manifest = new ModelManifest(model, version, "Apache-2.0", entries, "1.0.0", null);
        WriteManifest(sourceDirectory, manifest);
        return manifest;
    }

    public static void WriteManifest(string directory, ModelManifest manifest)
    {
        File.WriteAllText(Path.Combine(directory, ModelManifestReader.ManifestFileName), ModelManifestReader.Serialize(manifest));
    }

    public static ModelManifest WithSignature(ModelManifest manifest, string? signature) =>
        manifest with { Signature = signature };
}

/// <summary>Test signature verifier: accepts exactly the signatures it was given.</summary>
public sealed class FakeManifestVerifier : IManifestVerifier
{
    private readonly HashSet<string> _accepted;

    public FakeManifestVerifier(params string[] accepted)
    {
        _accepted = new HashSet<string>(accepted, StringComparer.Ordinal);
    }

    public bool Verify(ModelManifest manifest, string signature) => _accepted.Contains(signature);
}
