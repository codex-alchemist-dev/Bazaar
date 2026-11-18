using System.Security.Cryptography;
using Bazaar.Contracts.Modules;
using Bazaar.Server.Install.Trust;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Modules.Trust;

/// <summary>Signs module packages with a fresh P-256 publisher key and a verifier that trusts it. Shared by the trust and install tests.</summary>
internal static class ModuleSigningTestHelper
{
    public static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    public const string KeyId = "publisher-key-2";
    public const string Digest = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    public static (EcdsaModuleSignatureVerifier Verifier, ECDsa Key) Setup()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var store = new InMemoryTrustedKeyStore();
        store.Add(new TrustedModuleKey(KeyId, "publisher-2", key, Now.AddYears(-1), Now.AddYears(1), false));
        return (new EcdsaModuleSignatureVerifier(store, new FakeTimeProvider(Now)), key);
    }

    /// <summary>A package signed by <paramref name="key"/>. Permissions default to a single <c>storage:local</c>.</summary>
    public static ModulePackage Sign(HostRequirements host, ECDsa key, IEnumerable<string>? permissions = null, string moduleId = "bazaar.sandbox")
    {
        var declared = new HashSet<string>(permissions ?? ["storage:local"], StringComparer.Ordinal);
        var unsigned = new ModulePackage(moduleId, "0.1.0", declared, Digest, KeyId, host, []);
        var signature = key.SignData(ModuleCanonicalForm.Build(unsigned), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return unsigned with { Signature = signature };
    }
}
