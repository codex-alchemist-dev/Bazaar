using System.Security.Cryptography;
using Bazaar.Server.Identity;
using Bazaar.Server.Identity.BrowserProfiles;

namespace Bazaar.Server.Tests.Identity.BrowserProfiles;

/// <summary>Shared fixtures for the browser profile tests: a random test key, a store and cookie builders.</summary>
internal static class BrowserProfileTestKit
{
    public const string AgentId = "agent-alpha";
    public const string ServiceA = "alpha-service.test";
    public const string ServiceB = "beta-service.test";

    public static InMemoryBrowserProfileStore NewStore() =>
        new(new AesGcmProfileEncryptor(RandomNumberGenerator.GetBytes(32)));

    public static BrowserCookie Cookie(string domain, string name = "session", string value = "s3cret-value") =>
        new(name, value, domain, "/", DateTimeOffset.UtcNow.AddDays(1));

    public static BrowserLease Lease(string service, string agentId = AgentId) =>
        new(new LeaseId(Guid.NewGuid()), agentId, service, DateTimeOffset.UtcNow.AddMinutes(15));
}
