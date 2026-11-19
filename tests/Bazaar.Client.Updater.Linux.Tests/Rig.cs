using System.Security.Cryptography;
using System.Text;
using Bazaar.Contracts.Releases;

namespace Bazaar.Client.Updater.Linux.Tests;

internal sealed class FixedClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
}

internal sealed class FakeSource : IReleaseSource
{
    public ReleaseManifest? Manifest { get; set; }

    public Exception? ManifestError { get; set; }

    public Dictionary<string, byte[]> Files { get; } = [];

    public List<string> Opened { get; } = [];

    public int ManifestReads { get; private set; }

    public Task<ReleaseManifest?> GetManifestAsync(CancellationToken ct)
    {
        ManifestReads++;
        return ManifestError is null ? Task.FromResult(Manifest) : throw ManifestError;
    }

    public Task<Stream> OpenAsync(string url, CancellationToken ct)
    {
        Opened.Add(url);
        return Files.TryGetValue(url, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes))
            : throw new IOException("404 " + url);
    }
}

/// <summary>Plays the part of gpg: an import, a key listing and a verify, each with a scripted answer.</summary>
internal sealed class FakeGpg : IProcessRunner
{
    private readonly string _fingerprint;

    public FakeGpg(string fingerprint) => _fingerprint = fingerprint;

    public List<ProcessRequest> Calls { get; } = [];

    public Exception? Throws { get; set; }

    public int ImportExit { get; set; }

    public string? ListOutput { get; set; }

    public int VerifyExit { get; set; }

    public string? VerifyOutput { get; set; }

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct)
    {
        Calls.Add(request);
        if (Throws is not null)
        {
            throw Throws;
        }

        if (request.Arguments.Contains("--import"))
        {
            return Task.FromResult(new ProcessResult(ImportExit, string.Empty, string.Empty));
        }

        if (request.Arguments.Contains("--list-keys"))
        {
            string listing = ListOutput
                ?? $"tru::1:1:1:0:3:1:5\npub:-:255:22:AAAA:1:::-:::scESC:::::ed25519:::0:\nfpr:::::::::{_fingerprint.ToUpperInvariant()}:\nsub:-:255:18:BBBB:1:::::e:::::cv25519::\nfpr:::::::::{new string('B', 40)}:\n";
            return Task.FromResult(new ProcessResult(0, listing, string.Empty));
        }

        string verify = VerifyOutput
            ?? $"[GNUPG:] NEWSIG\n[GNUPG:] GOODSIG AAAA Bazaar\n[GNUPG:] VALIDSIG {_fingerprint.ToUpperInvariant()} 2026-11-19 1 0 4 0 22 10 00 {_fingerprint.ToUpperInvariant()}\n[GNUPG:] TRUST_UNDEFINED 0 pgp\n";
        return Task.FromResult(new ProcessResult(VerifyExit, verify, string.Empty));
    }
}

internal sealed class FakeCompat : IClientCompatibility
{
    public string RunningProtocol { get; set; } = "1.0";

    public string ServerProtocol { get; set; } = "1.0";

    public bool ServerAcceptsPreviousMajor { get; set; } = true;

    public string ServerSchemaVersion { get; set; } = "1";
}

/// <summary>An updater wired to in-memory fakes, with helpers to publish releases.</summary>
internal sealed class Rig
{
    public static readonly string Fingerprint = new('a', 40);

    public const string Root = "/opt/bazaar";

    public Rig(string installed = "0.1.0", string channel = ReleaseChannels.Stable, bool owner = false)
    {
        Fs.CreateDirectory(Root, UnixFileMode.UserRead);
        Settings = new LinuxUpdaterSettings
        {
            InstallRoot = Root,
            InstalledVersion = installed,
            Architecture = ReleaseArtifact.ArchLinuxX64,
            PinnedKeyFingerprint = Fingerprint,
            PinnedKeyPath = "/etc/bazaar/company-key.asc",
            Channel = channel,
            IsOwnerInstall = owner,
        };
        Gpg = new FakeGpg(Fingerprint);
    }

    public InMemoryFs Fs { get; } = new();

    public FakeSource Source { get; } = new();

    public FakeGpg Gpg { get; }

    public FakeCompat Compat { get; } = new();

    public LinuxUpdaterSettings Settings { get; set; }

    public LinuxUpdater Updater => new(Settings, Source, Fs, Gpg, new FixedClock(), Compat);

    public static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Publishes a release whose files are consistent unless the arguments say otherwise.</summary>
    public ReleaseManifest Publish(
        string version,
        byte[]? tarball = null,
        string channel = ReleaseChannels.Stable,
        string? fingerprint = null,
        string? inlineSha = null,
        string? shaFileText = null,
        long? declaredSize = null,
        byte[]? servedTarball = null,
        byte[]? signature = null,
        string architecture = ReleaseArtifact.ArchLinuxX64,
        string format = ReleaseArtifact.FormatTarGz,
        string minProtocol = "1.0",
        string maxProtocol = "1.0",
        string schema = "1")
    {
        tarball ??= TarBuilder.Release(version);
        string baseUrl = $"https://company.example/updates/Bazaar-{version}-{architecture}";
        string hash = Hex(tarball);
        var artifact = new ReleaseArtifact(
            ReleaseArtifact.OsLinux, architecture, format, baseUrl + ".tar.gz", declaredSize ?? tarball.Length,
            inlineSha ?? hash, baseUrl + ".tar.gz.sha256", baseUrl + ".tar.gz.asc");
        Source.Files[artifact.Url] = servedTarball ?? tarball;
        Source.Files[artifact.Sha256Url] = Encoding.ASCII.GetBytes(shaFileText ?? $"{hash}  Bazaar-{version}.tar.gz\n");
        Source.Files[artifact.SignatureUrl] = signature ?? Encoding.ASCII.GetBytes("-----BEGIN PGP SIGNATURE-----");
        var manifest = new ReleaseManifest(channel, version, minProtocol, maxProtocol, schema, fingerprint ?? Fingerprint, [artifact]);
        Source.Manifest = manifest;
        return manifest;
    }

    public string? Current => Fs.ReadLink(Root + "/current");

    public string? Previous => Fs.ReadLink(Root + "/previous");
}
