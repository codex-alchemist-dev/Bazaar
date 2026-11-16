// Signs one packaged PostgreSQL bundle as a Bazaar module package (doc 48 section 1, doc 49 section 3). A file-based app:
//   dotnet run packaging/postgres/source/SignBundle.cs -- sign   --bundle <major dir> --module-id postgresql \
//       --version 18.7.0 --permission fs.dataRoot --key-id <id> --private-key <pem file> --out <json file>
//   dotnet run packaging/postgres/source/SignBundle.cs -- verify --package <json file> --public-key <pem file> --key-id <id> --publisher-id <id>
// The package is the canonical form of ModulePackage, signed with ECDSA P-256 (ModulePackageSigner). The content digest
// is the SHA-256 of the bundle's SHA256SUMS file, which lists every file's hash, so the signature covers the whole bundle.
// Signing covers SHA256SUMS, which lists every file of the bundle. A changed file fails sha256sum in
// test-source-bundle.sh, and a changed SHA256SUMS fails the signature. Verify with --bundle to check the digest too.
// The private key is read from a file the release step provides (a CI secret, or the owner's offline key). This app
// never creates or stores a key. Key id and publisher id are required arguments with no default. Doc 48 section 1 names the
// publisher id but does not define its format, so the format is whatever the release owner registers.
#:project ../../../src/Bazaar.Server/Bazaar.Server.csproj

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Contracts.Modules;
using Bazaar.Server.Modules.Trust;

static string Arg(string[] args, string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault()
    ?? throw new ArgumentException($"missing {name}");

static IReadOnlySet<string> Permissions(string[] args) =>
    new HashSet<string>(args.Select((a, i) => (a, i)).Where(t => t.a == "--permission").Select(t => args[t.i + 1]), StringComparer.Ordinal);

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: sign | verify (see the header of this file)");
    return 2;
}

if (args[0] == "sign")
{
    var bundle = Arg(args, "--bundle");
    var sums = Path.Combine(bundle, "SHA256SUMS");
    var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sums))).ToLowerInvariant();
    var manifest = File.ReadAllLines(Path.Combine(bundle, "bazaar-bundle.txt"));
    var version = Arg(args, "--version");

    using var key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(Arg(args, "--private-key")));

    var unsigned = new ModulePackage(
        Arg(args, "--module-id"),
        version,
        Permissions(args),
        digest,
        Arg(args, "--key-id"),
        new HostRequirements(HostRequirements.CurrentSchemaVersion, ["linux", "windows"], ["x64"], []),
        []);
    var signed = ModulePackageSigner.Sign(unsigned, key);

    var record = new JsonObject
    {
        ["moduleId"] = signed.ModuleId,
        ["version"] = signed.Version,
        ["permissions"] = new JsonArray(signed.DeclaredPermissions.Order(StringComparer.Ordinal).Select(p => (JsonNode)p).ToArray()),
        ["contentDigestSha256"] = signed.ContentDigestSha256Hex,
        ["signerKeyId"] = signed.SignerKeyId,
        ["host"] = new JsonObject
        {
            ["schema"] = signed.Host.SchemaVersion,
            ["os"] = new JsonArray(signed.Host.OperatingSystems.Select(v => (JsonNode)v).ToArray()),
            ["arch"] = new JsonArray(signed.Host.Architectures.Select(v => (JsonNode)v).ToArray()),
            ["features"] = new JsonArray(signed.Host.RequiredFeatures.Select(v => (JsonNode)v).ToArray()),
        },
        ["signature"] = Convert.ToBase64String(signed.Signature),
        ["manifestLines"] = manifest.Length,
    };
    File.WriteAllText(Arg(args, "--out"), record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"signed {signed.ModuleId} {signed.Version} (content digest {digest[..12]}...)");
    return 0;
}

if (args[0] == "verify")
{
    var record = JsonElement.Parse(File.ReadAllText(Arg(args, "--package")));
    var host = record.GetProperty("host");
    var package = new ModulePackage(
        record.GetProperty("moduleId").GetString()!,
        record.GetProperty("version").GetString()!,
        record.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal),
        record.GetProperty("contentDigestSha256").GetString()!,
        record.GetProperty("signerKeyId").GetString()!,
        new HostRequirements(
            host.GetProperty("schema").GetInt32(),
            host.GetProperty("os").EnumerateArray().Select(e => e.GetString()!).ToArray(),
            host.GetProperty("arch").EnumerateArray().Select(e => e.GetString()!).ToArray(),
            host.GetProperty("features").EnumerateArray().Select(e => e.GetString()!).ToArray()),
        Convert.FromBase64String(record.GetProperty("signature").GetString()!));

    var bundleArg = args.SkipWhile(a => a != "--bundle").Skip(1).FirstOrDefault();
    if (bundleArg is not null)
    {
        var actualDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(bundleArg, "SHA256SUMS")))).ToLowerInvariant();
        if (actualDigest != package.ContentDigestSha256Hex)
        {
            Console.WriteLine("BundleDigestMismatch");
            return 1;
        }
    }

    using var publicKey = ECDsa.Create();
    publicKey.ImportFromPem(File.ReadAllText(Arg(args, "--public-key")));
    var store = new InMemoryTrustedKeyStore();
    store.Add(new TrustedModuleKey(Arg(args, "--key-id"), Arg(args, "--publisher-id"), publicKey, DateTimeOffset.UnixEpoch, DateTimeOffset.MaxValue));
    var status = new EcdsaModuleSignatureVerifier(store, TimeProvider.System).Verify(package);
    Console.WriteLine(status);
    return status == ModuleSignatureStatus.Valid ? 0 : 1;
}

Console.Error.WriteLine($"unknown command: {args[0]}");
return 2;
