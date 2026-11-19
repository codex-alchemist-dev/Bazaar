using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Bazaar.Contracts.Releases;

namespace Bazaar.Client.Updater.Linux.Tests;

/// <summary>The updater on a real temp directory: real tar extraction, real symlink switch. The signature step is faked here and real in <see cref="RealGpgTests"/>.</summary>
[SupportedOSPlatform("linux")]
public sealed class RealFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-updater-" + Guid.NewGuid().ToString("N"));

    public RealFileSystemTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private (LinuxUpdater Updater, Rig Rig) Wire(string installed)
    {
        var rig = new Rig(installed);
        rig.Settings = rig.Settings with { InstallRoot = Path.Combine(_root, "install") };
        Directory.CreateDirectory(rig.Settings.InstallRoot);
        return (new LinuxUpdater(rig.Settings, rig.Source, new PhysicalUpdateFileSystem(), rig.Gpg, new FixedClock()), rig);
    }

    private string Install => Path.Combine(_root, "install");

    [LinuxFact]
    public async Task Extraction_and_the_symlink_switch_work_on_a_real_disk()
    {
        var (updater, rig) = Wire("0.1.0");
        rig.Publish("0.2.0");

        var result = await updater.ApplyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStatus.Installed, result.Status);
        var current = new FileInfo(Path.Combine(Install, "current"));
        Assert.Equal("releases/0.2.0", current.LinkTarget);
        Assert.Equal("0.2.0", File.ReadAllText(Path.Combine(Install, "current", "Bazaar"), Encoding.UTF8));
        Assert.Equal("core.dll", new FileInfo(Path.Combine(Install, "releases", "0.2.0", "lib", "core-link.dll")).LinkTarget);
        Assert.True(File.GetUnixFileMode(Path.Combine(Install, "current", "Bazaar")).HasFlag(UnixFileMode.UserExecute));
        Assert.False(File.GetUnixFileMode(Path.Combine(Install, "current", "lib", "core.dll")).HasFlag(UnixFileMode.UserExecute));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(Install), p => p.Contains("staging", StringComparison.Ordinal) || p.Contains(".tmp-", StringComparison.Ordinal));
    }

    [LinuxFact]
    public async Task Current_always_resolves_while_it_is_switched_between_releases()
    {
        var (_, rig) = Wire("0.1.0");
        var fs = new PhysicalUpdateFileSystem();
        rig.Publish("0.2.0");
        Assert.Equal(UpdateStatus.Installed, (await new LinuxUpdater(rig.Settings, rig.Source, fs, rig.Gpg, new FixedClock()).ApplyAsync(TestContext.Current.CancellationToken)).Status);

        string exe = Path.Combine(Install, "current", "Bazaar");
        int misses = 0;
        int reads = 0;
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    Interlocked.Increment(ref reads);
                    if (File.ReadAllText(exe).Length == 0)
                    {
                        Interlocked.Increment(ref misses);
                    }
                }
                catch (IOException)
                {
                    Interlocked.Increment(ref misses);
                }
            }
        });

        string installed = "0.2.0";
        for (int i = 3; i < 13; i++)
        {
            string version = "0." + i + ".0";
            rig.Settings = rig.Settings with { InstalledVersion = installed };
            rig.Publish(version);
            var updater = new LinuxUpdater(rig.Settings, rig.Source, fs, rig.Gpg, new FixedClock());
            Assert.Equal(UpdateStatus.Installed, (await updater.ApplyAsync(TestContext.Current.CancellationToken)).Status);
            Assert.Equal(UpdateStatus.RolledBack, updater.Rollback().Status);
            Assert.Equal(UpdateStatus.RolledBack, updater.Rollback().Status);
            installed = version;
        }

        await stop.CancelAsync();
        await reader;
        Assert.True(reads > 0);
        Assert.Equal(0, misses);
    }

    [LinuxFact]
    public async Task A_hostile_tarball_writes_nothing_outside_the_release_on_a_real_disk()
    {
        var (updater, rig) = Wire("0.1.0");
        string outside = Path.Combine(_root, "escape.txt");
        rig.Publish("0.2.0", tarball: TarBuilder.Build(TarBuilder.File("ok"), TarBuilder.File("../../../escape.txt", "pwned")));

        var result = await updater.ApplyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStatus.ExtractionRejected, result.Status);
        Assert.False(File.Exists(outside));
        Assert.False(File.Exists(Path.Combine(Install, "escape.txt")));
        Assert.False(File.Exists(Path.Combine(Install, "current")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(Install, "releases")));
    }

    [LinuxFact]
    public async Task A_link_chain_cannot_write_outside_on_a_real_disk()
    {
        var (updater, rig) = Wire("0.1.0");
        string target = Path.Combine(_root, "victim");
        Directory.CreateDirectory(target);
        rig.Publish("0.2.0", tarball: TarBuilder.Build(TarBuilder.Link("hop", "."), TarBuilder.Link("hop2", "hop"), TarBuilder.File("hop2/x", "pwned")));

        var result = await updater.ApplyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateStatus.ExtractionRejected, result.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [LinuxFact]
    public async Task The_physical_file_system_refuses_to_overwrite_and_replaces_links_atomically()
    {
        var fs = new PhysicalUpdateFileSystem();
        string dir = Path.Combine(_root, "fs");
        fs.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Path.Combine(dir, "a"));
        Directory.CreateDirectory(Path.Combine(dir, "b"));
        fs.CreateSymbolicLink(Path.Combine(dir, "cur"), "a");
        fs.CreateSymbolicLink(Path.Combine(dir, "cur.tmp"), "b");

        fs.RenameOver(Path.Combine(dir, "cur.tmp"), Path.Combine(dir, "cur"));

        Assert.Equal("b", fs.ReadLink(Path.Combine(dir, "cur")));
        Assert.Null(fs.ReadLink(Path.Combine(dir, "a")));
        Assert.True(fs.DirectoryExists(Path.Combine(dir, "cur")));
        Assert.Equal(2, fs.EnumerateDirectories(dir).Count);
        await using (fs.CreateFile(Path.Combine(dir, "f")))
        {
        }

        Assert.ThrowsAny<IOException>(() => fs.CreateFile(Path.Combine(dir, "f")));
        fs.DeleteDirectory(dir);
        Assert.False(Directory.Exists(dir));
    }
}

/// <summary>Real <c>gpg</c> against a real key pair generated in a temp home. Skipped where gpg is not installed.</summary>
[SupportedOSPlatform("linux")]
public sealed class RealGpgTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-gpg-" + Guid.NewGuid().ToString("N"));

    public RealGpgTests() => Directory.CreateDirectory(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static bool GpgAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("gpg", "--version") { RedirectStandardOutput = true, UseShellExecute = false });
            p!.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string Gpg(string home, params string[] args)
    {
        var info = new ProcessStartInfo("gpg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in new[] { "--batch", "--no-tty", "--homedir", home }.Concat(args))
        {
            info.ArgumentList.Add(a);
        }

        using var p = Process.Start(info)!;
        string output = p.StandardOutput.ReadToEnd();
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"gpg {string.Join(' ', args)} failed: {error}");
        return output;
    }

    /// <summary>Makes a key, exports its public half, signs the tarball and returns (fingerprint, key file, signature file).</summary>
    private (string Fingerprint, string KeyFile, string SigFile) MakeKeyAndSign(string name, string tarballPath)
    {
        string home = Path.Combine(_root, name + "-home");
        Directory.CreateDirectory(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Gpg(home, "--passphrase", string.Empty, "--pinentry-mode", "loopback", "--quick-generate-key", name + " <" + name + "@example.invalid>", "ed25519", "sign", "never");
        string listing = Gpg(home, "--with-colons", "--fingerprint", "--list-keys");
        string fingerprint = GpgSignatureVerifier.PrimaryFingerprints(listing).Single();
        string keyFile = Path.Combine(_root, name + ".asc");
        File.WriteAllText(keyFile, Gpg(home, "--armor", "--export", fingerprint));
        string sigFile = Path.Combine(_root, name + ".tar.gz.asc");
        File.WriteAllText(sigFile, Gpg(home, "--pinentry-mode", "loopback", "--passphrase", string.Empty, "--armor", "--detach-sign", "--local-user", fingerprint, "--output", "-", tarballPath));
        return (fingerprint, keyFile, sigFile);
    }

    private (LinuxUpdater Updater, string Install) WireReal(byte[] tarball, string fingerprint, string keyFile, byte[] signature, string manifestFingerprint)
    {
        string install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);
        var rig = new Rig();
        rig.Settings = rig.Settings with { InstallRoot = install, PinnedKeyFingerprint = fingerprint, PinnedKeyPath = keyFile };
        rig.Publish("0.2.0", tarball: tarball, fingerprint: manifestFingerprint, signature: signature);
        return (new LinuxUpdater(rig.Settings, rig.Source, new PhysicalUpdateFileSystem(), new SystemProcessRunner(), new FixedClock()), install);
    }

    [LinuxFact]
    public async Task A_real_signature_by_the_pinned_key_installs_and_a_real_wrong_key_is_refused()
    {
        Assert.SkipUnless(GpgAvailable(), "gpg is not installed");
        byte[] tarball = TarBuilder.Release("0.2.0");
        string tarPath = Path.Combine(_root, "release.tar.gz");
        await System.IO.File.WriteAllBytesAsync(tarPath, tarball);
        var good = MakeKeyAndSign("company", tarPath);
        var other = MakeKeyAndSign("intruder", tarPath);

        // Signed by the pinned key.
        var ok = WireReal(tarball, good.Fingerprint, good.KeyFile, await System.IO.File.ReadAllBytesAsync(good.SigFile), good.Fingerprint);
        var installed = await ok.Updater.ApplyAsync(TestContext.Current.CancellationToken);
        Assert.True(installed.Status == UpdateStatus.Installed, installed.Status + ": " + installed.Detail);
        Assert.Equal("releases/0.2.0", new FileInfo(Path.Combine(ok.Install, "current")).LinkTarget);

        // Signed by a different key while the manifest claims the pinned one.
        Directory.Delete(ok.Install, recursive: true);
        var wrongSigner = WireReal(tarball, good.Fingerprint, good.KeyFile, await System.IO.File.ReadAllBytesAsync(other.SigFile), good.Fingerprint);
        var refused = await wrongSigner.Updater.ApplyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateStatus.SignatureInvalid, refused.Status);
        Assert.False(System.IO.File.Exists(Path.Combine(wrongSigner.Install, "current")));

        // A signature over different bytes.
        Directory.Delete(wrongSigner.Install, recursive: true);
        string otherTar = Path.Combine(_root, "other.tar.gz");
        await System.IO.File.WriteAllBytesAsync(otherTar, TarBuilder.Release("different"));
        var sigOverOther = MakeKeyAndSign("company2", otherTar);
        var tampered = WireReal(tarball, sigOverOther.Fingerprint, sigOverOther.KeyFile, await System.IO.File.ReadAllBytesAsync(sigOverOther.SigFile), sigOverOther.Fingerprint);
        Assert.Equal(UpdateStatus.SignatureInvalid, (await tampered.Updater.ApplyAsync(TestContext.Current.CancellationToken)).Status);

        // A key file that is not the pinned key.
        Directory.Delete(tampered.Install, recursive: true);
        var wrongKeyFile = WireReal(tarball, good.Fingerprint, other.KeyFile, await System.IO.File.ReadAllBytesAsync(other.SigFile), good.Fingerprint);
        Assert.Equal(UpdateStatus.FingerprintMismatch, (await wrongKeyFile.Updater.ApplyAsync(TestContext.Current.CancellationToken)).Status);
    }

    [LinuxFact]
    public async Task A_missing_executable_is_reported_by_the_real_runner()
    {
        var runner = new SystemProcessRunner();

        await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(new ProcessRequest("/nonexistent/gpg", [], new Dictionary<string, string>(), TimeSpan.FromSeconds(5)), TestContext.Current.CancellationToken));
    }

    [LinuxFact]
    public async Task A_process_that_outlives_its_timeout_is_killed()
    {
        var runner = new SystemProcessRunner();

        await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync(new ProcessRequest("sleep", ["30"], new Dictionary<string, string>(), TimeSpan.FromMilliseconds(300)), TestContext.Current.CancellationToken));
    }

    [LinuxFact]
    public async Task Arguments_and_environment_reach_the_child_without_a_shell()
    {
        var runner = new SystemProcessRunner();

        var result = await runner.RunAsync(
            new ProcessRequest("printenv", ["BAZAAR_TEST_VALUE"], new Dictionary<string, string> { ["BAZAAR_TEST_VALUE"] = "a b;c" }, TimeSpan.FromSeconds(10)),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("a b;c\n", result.StandardOutput);
    }
}
