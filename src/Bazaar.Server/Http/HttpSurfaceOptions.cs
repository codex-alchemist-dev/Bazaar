using Microsoft.Extensions.Options;

namespace Bazaar.Server.Http;

/// <summary>
/// Settings of the authenticated HTTP surface (doc 02 WP10). The rate bucket here is separate from the wire's
/// (<c>WireOptions.RequestsPerSecond</c>), so socket traffic never spends it and the reverse.
/// </summary>
public sealed class HttpSurfaceOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Bazaar:Http";

    /// <summary>Largest blob the operator may allow (hard ceiling for <see cref="MaxBlobBytes"/>).</summary>
    public const long BlobCeilingBytes = 1024L * 1024 * 1024;

    /// <summary>Directory of the blob store. Operator-set only, never taken from a request. Null means the per-user data folder.</summary>
    public string? BlobRoot { get; set; }

    /// <summary>Largest blob in bytes (<c>gc.blob_max_bytes</c>, default 25 MiB).</summary>
    public long MaxBlobBytes { get; set; } = 26_214_400;

    /// <summary>Largest single chunk in bytes.</summary>
    public long MaxChunkBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>Unfinished uploads one principal may hold.</summary>
    public int MaxActiveUploadsPerPrincipal { get; set; } = 8;

    /// <summary>Hours after which an unfinished upload is discarded.</summary>
    public int UploadTtlHours { get; set; } = 24;

    /// <summary>Requests per second one principal may sustain on the HTTP surface.</summary>
    public int RequestsPerSecond { get; set; } = 20;

    /// <summary>Requests one principal may burst above the sustained rate.</summary>
    public int RequestBurst { get; set; } = 40;

    /// <summary>Most search hits one request returns.</summary>
    public int SearchMaxResults { get; set; } = 50;

    /// <summary>The directory the store uses: <see cref="BlobRoot"/> or the per-user default.</summary>
    public string ResolveBlobRoot()
    {
        if (!string.IsNullOrWhiteSpace(BlobRoot)) return Path.GetFullPath(BlobRoot);
        var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(data)) throw new InvalidOperationException("No per-user data folder; set Bazaar:Http:BlobRoot.");
        return Path.Combine(data, "Bazaar", "blobs");
    }
}

/// <summary>Rejects HTTP settings that make no sense or exceed the ceilings.</summary>
public sealed class HttpSurfaceOptionsValidator : IValidateOptions<HttpSurfaceOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, HttpSurfaceOptions o)
    {
        var errors = new List<string>();
        if (o.MaxBlobBytes < 1 || o.MaxBlobBytes > HttpSurfaceOptions.BlobCeilingBytes) errors.Add("MaxBlobBytes must be between 1 and 1 GiB.");
        if (o.MaxChunkBytes < 1 || o.MaxChunkBytes > 16L * 1024 * 1024) errors.Add("MaxChunkBytes must be between 1 and 16 MiB.");
        if (o.MaxActiveUploadsPerPrincipal < 1 || o.UploadTtlHours < 1) errors.Add("Upload limits must be positive.");
        if (o.RequestsPerSecond < 1 || o.RequestBurst < 1 || o.SearchMaxResults < 1) errors.Add("Rate and page settings must be positive.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
