using System.Security.Cryptography;
using System.Text.Json;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Tests.Security.Approvals;

/// <summary>Signs approval decisions the way an owner's device does, for tests.</summary>
internal static class Signing
{
    public const string OwnerId = "owner";
    public const string DeviceId = "device-1";

    /// <summary>A fresh P-256 device key.</summary>
    public static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>The DER SubjectPublicKeyInfo a device registers.</summary>
    public static byte[] PublicKey(ECDsa key) => key.ExportSubjectPublicKeyInfo();

    /// <summary>Signs the decision message exactly as <see cref="ApprovalSignature.Message"/> lays it out.</summary>
    public static byte[] Sign(
        ECDsa key,
        Guid approvalId,
        ApprovalDecision decision,
        byte[] argsHash,
        DateTimeOffset expiresAt,
        string deviceId = DeviceId) =>
        key.SignData(
            ApprovalSignature.Message(approvalId, decision, argsHash, expiresAt, deviceId),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>A key registry holding one active device key for the owner.</summary>
    public static InMemoryOwnerKeyRegistry RegistryWith(ECDsa key)
    {
        var registry = new InMemoryOwnerKeyRegistry();
        registry.Register(new OwnerDeviceKey(OwnerId, DeviceId, PublicKey(key)));
        return registry;
    }
}

/// <summary>Fixed clock and dispatch contexts shared by the approval tests.</summary>
internal static class ApprovalTestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 26, 6, 0, 0, TimeSpan.Zero);

    public static readonly Guid Session = Guid.Parse("0196a6f0-0000-7000-8000-000000000201");

    /// <summary>The test's cancellation token, passed to every store and service call.</summary>
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Asserts a value is present and returns it, for use inside expressions.</summary>
    public static T Present<T>(T? value) where T : class
    {
        Assert.NotNull(value);
        return value!;
    }

    /// <summary>A clean actor call. The defaults form one fixed, valid call.</summary>
    public static DispatchContext Context(
        string agent = "charles",
        string tool = "repo.merge",
        string? target = "codex-alchemist-dev/Bazaar#12",
        string args = """{"number":12,"method":"squash"}""",
        DateTimeOffset? now = null) =>
        new(agent, Session, SessionRoles.Actor, tool, "github", target, Parse(args), SessionTaint.Clean, now ?? Now);

    /// <summary>Parses JSON and returns a cloned root element that outlives the document.</summary>
    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
