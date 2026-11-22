using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Setup.FirstRun.Postgres;

/// <summary>The fixed markers that make the first-run seed idempotent (doc 45 section 1.15).</summary>
public static class FirstRunMarkers
{
    /// <summary>The marker on the seeded default agent (<c>agent.first_run_marker</c>).</summary>
    public const string DefaultAgent = "first-run:default-agent";

    /// <summary>The id of the seeded General GC: a SHA-256 prefix of a fixed seed, so every run names the same GC.</summary>
    public static Guid GeneralGcId { get; } = new(SHA256.HashData(Encoding.UTF8.GetBytes("bazaar:first-run:general-gc")).AsSpan(0, 16));
}
