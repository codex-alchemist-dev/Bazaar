using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Modules.Releases;

/// <summary>
/// Deterministic ring membership. An install id maps to a fixed bucket through SHA-256, so the same install stays in
/// or out of a ring on every server and every run. The ring percentage is a setting passed in by the caller.
/// Membership is nested: an install in a smaller percentage is also in every larger one.
/// </summary>
/// <remarks>
/// XxHash64 (System.IO.Hashing) is not in <c>Directory.Packages.props</c>, so this uses SHA-256 from the BCL.
/// </remarks>
public static class RingBucket
{
    /// <summary>Bucket count. Each bucket is 0.01 percent, so a whole-percent setting has exact steps.</summary>
    public const int BucketCount = 10_000;

    /// <summary>The bucket (0 to <see cref="BucketCount"/> minus 1) of an install id.</summary>
    public static int BucketOf(string installId)
    {
        ArgumentException.ThrowIfNullOrEmpty(installId);

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(installId), digest);
        var value = BinaryPrimitives.ReadUInt64BigEndian(digest);
        return (int)(value % BucketCount);
    }

    /// <summary>
    /// True when the install is inside a ring that covers <paramref name="percent"/> of installs.
    /// 0 is never in, and 100 is always in.
    /// </summary>
    public static bool IsInRing(string installId, int percent)
    {
        ArgumentException.ThrowIfNullOrEmpty(installId);
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);

        return BucketOf(installId) < percent * (BucketCount / 100);
    }
}
