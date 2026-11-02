using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Client.LocalInference.BlobCache;

/// <summary>
/// Identity of a compiled NPU blob: the model it was compiled from, the execution provider, the driver version and the
/// runtime version. Any field change is a different key, so the cache misses and the caller recompiles. A null driver
/// version means the provider has no driver (CPU).
/// </summary>
/// <remarks>
/// <see cref="ToString"/> is the canonical form. Each present value is written as its length and a colon followed by the
/// value, and a null driver is written as <c>-</c>. Length prefixes keep distinct keys from sharing a form, whatever
/// characters the values contain. <see cref="DirectoryName"/> is the SHA-256 of that form in lowercase hex, so the
/// on-disk name is always a safe single path segment.
/// </remarks>
public sealed record BlobCacheKey
{
    /// <summary>Creates a key. Model, provider and runtime are required; a non-null driver version must not be blank.</summary>
    public BlobCacheKey(string modelSha256, string executionProvider, string? driverVersion, string runtimeVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeVersion);
        if (driverVersion is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(driverVersion);
        }

        ModelSha256 = modelSha256;
        ExecutionProvider = executionProvider;
        DriverVersion = driverVersion;
        RuntimeVersion = runtimeVersion;
    }

    /// <summary>SHA-256 of the model file the blob was compiled from.</summary>
    public string ModelSha256 { get; }

    /// <summary>Execution provider the blob was compiled for, for example OpenVINO or QNN.</summary>
    public string ExecutionProvider { get; }

    /// <summary>Driver version of the device, or null for CPU.</summary>
    public string? DriverVersion { get; }

    /// <summary>Version of the inference runtime that compiled the blob.</summary>
    public string RuntimeVersion { get; }

    /// <summary>Lowercase hex SHA-256 of <see cref="ToString"/>. Used as the entry folder name.</summary>
    public string DirectoryName => HashHex(ToString());

    /// <summary>Deterministic canonical form of the key. Equal keys always produce equal text.</summary>
    public override string ToString() =>
        string.Join('|', "v1", Field(ModelSha256), Field(ExecutionProvider), Field(DriverVersion), Field(RuntimeVersion));

    internal static string HashHex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Field(string? value) => value is null ? "-" : value.Length + ":" + value;
}
