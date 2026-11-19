namespace Bazaar.Server.Blobs;

/// <summary>
/// Settings the blob store reads. They are bound from the same <c>Bazaar:Http</c> keys the HTTP surface validates
/// (<c>BlobRoot</c>, <c>MaxActiveUploadsPerPrincipal</c>, <c>UploadTtlHours</c>), so operators keep one section.
/// </summary>
public sealed class BlobStoreOptions
{
    /// <summary>Configuration section the keys are read from.</summary>
    public const string SectionName = "Bazaar:Http";

    /// <summary>Directory of the blob store. Operator-set only, never taken from a request. Null means the per-user data folder.</summary>
    public string? BlobRoot { get; set; }

    /// <summary>Unfinished uploads one principal may hold.</summary>
    public int MaxActiveUploadsPerPrincipal { get; set; } = 8;

    /// <summary>Hours after which an unfinished upload is discarded.</summary>
    public int UploadTtlHours { get; set; } = 24;

    /// <summary>The directory the store uses: <see cref="BlobRoot"/> or the per-user default.</summary>
    public string ResolveBlobRoot()
    {
        if (!string.IsNullOrWhiteSpace(BlobRoot)) return Path.GetFullPath(BlobRoot);
        var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(data)) throw new InvalidOperationException("No per-user data folder; set Bazaar:Http:BlobRoot.");
        return Path.Combine(data, "Bazaar", "blobs");
    }
}
