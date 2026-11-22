using System.Security.Cryptography;
using Bazaar.Kodo.Manifest;
using Bazaar.Kodo.Runtime;
using Bazaar.Kodo.Tests.Runtime;

namespace Bazaar.Kodo.Tests.Manifest;

internal static class ManifestTestData
{
    public static ModuleProbeCatalogue Catalogue() =>
        new(["gc.notify_listener", "gc.append_latency", "gc.event_gaps", "gc.shard."]);

    public static ModuleManifest Valid() => new(
        "gc",
        "Group chat",
        "first-party",
        "0.1.0+a1b2c3d",
        1,
        new ModuleManifestDependencies(["audit"], ["kanban"]),
        new ModuleManifestHealth("builtin", "builtin", new ModuleManifestSelfTest(2000, 30000, "daily")),
        ["gc.notify_listener", "gc.append_latency", "gc.event_gaps"],
        ["runbooks/gc-notify-restart.json"],
        ["db.read:gc", "db.write:gc", "kodo.observe"],
        null);

    public static ModuleManifest Sign(ModuleManifest manifest, RSA? key = null, string keyId = RuntimeTestSigning.KeyId, string alg = RsaPkcs1Sha256Verifier.AlgorithmName)
    {
        string signature = RuntimeTestSigning.Sign(key ?? RuntimeTestSigning.PrimaryKey, ModuleManifestCodec.CanonicalBytes(manifest));
        return manifest with { Signature = new ModuleManifestSignature(keyId, alg, signature) };
    }

    public static ModuleManifestLoader Loader(RuntimeTrustRoot? trust = null) =>
        new(trust ?? RuntimeTestSigning.TrustRoot(), Catalogue());

    public static ModuleLoadDecision Evaluate(ModuleManifest signed, IReadOnlySet<string>? approved = null) =>
        Loader().Evaluate(ModuleManifestCodec.Encode(signed), RuntimeTestSigning.Now, approved);

    public static ModuleLoadDecision EvaluateSigned(ModuleManifest unsigned, IReadOnlySet<string>? approved = null) =>
        Evaluate(Sign(unsigned), approved);
}
