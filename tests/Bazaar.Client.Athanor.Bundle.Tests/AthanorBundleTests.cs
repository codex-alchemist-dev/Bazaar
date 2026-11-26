using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Bazaar.Client.Athanor.Bridge;
using Bazaar.Client.Athanor.Bundle;

namespace Bazaar.Client.Athanor.Bundle.Tests;

public sealed partial class AthanorBundleTests
{
    private static string Root => AthanorBundle.DefaultRoot;

    private static string BridgeJs => File.ReadAllText(Path.Combine(Root, AthanorBundle.BridgeScript));

    private static string HostHtml => File.ReadAllText(Path.Combine(Root, AthanorBundle.HostPage));

    private static bool MonacoPresent => File.Exists(Path.Combine(Root, "monaco", "vs", "loader.js"));

    [Fact]
    public void Lock_pins_the_release_licence_and_a_sha256()
    {
        Assert.Equal("0.57.0", AthanorBundle.MonacoVersion);
        Assert.Equal("MIT", AthanorBundle.MonacoLicense);
        Assert.Matches("^[0-9a-f]{64}$", AthanorBundle.PinnedBundleSha256);
    }

    [Fact]
    public void Licence_notice_ships_with_the_bundle()
    {
        string notice = File.ReadAllText(Path.Combine(Root, "LICENSE-monaco-editor.txt"));

        Assert.StartsWith("The MIT License (MIT)", notice, StringComparison.Ordinal);
        Assert.Contains("Microsoft Corporation", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Host_page_loads_only_its_own_files_under_a_strict_policy()
    {
        string html = HostHtml;

        Assert.Contains("Content-Security-Policy", html, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", html, StringComparison.Ordinal);
        Assert.Contains("connect-src 'none'", html, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", html, StringComparison.Ordinal);
        Assert.Contains("monaco/vs/loader.js", html, StringComparison.Ordinal);
        Assert.Contains("bridge.js", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("eval(")]
    [InlineData("new Function")]
    [InlineData("innerHTML")]
    [InlineData("outerHTML")]
    [InlineData("insertAdjacentHTML")]
    [InlineData("document.write")]
    [InlineData("setTimeout(\"")]
    [InlineData("fetch(")]
    [InlineData("XMLHttpRequest")]
    [InlineData("WebSocket")]
    [InlineData("importScripts")]
    [InlineData("http://")]
    [InlineData("https://")]
    public void Bridge_script_never_builds_code_or_reaches_the_network(string forbidden)
    {
        Assert.DoesNotContain(forbidden, BridgeJs, StringComparison.Ordinal);
    }

    [Fact]
    public void Bridge_script_handles_every_host_message_type_the_codec_can_send()
    {
        string js = BridgeJs;
        foreach (string type in Discriminators(typeof(HostMessage)))
        {
            Assert.True(
                Regex.IsMatch(js, $@"\b{type}\s*:"),
                $"bridge.js has no handler for host message '{type}'.");
        }
    }

    [Fact]
    public void Bridge_script_posts_only_message_types_the_codec_accepts()
    {
        HashSet<string> accepted = [.. Discriminators(typeof(EditorMessage))];
        var posted = SentTypes().ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(posted);
        Assert.Subset(accepted, posted);
    }

    [Fact]
    public void Bridge_script_never_claims_a_ctrl_chord()
    {
        Assert.Contains("event.ctrlKey", BridgeJs, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_reports_a_missing_bundle()
    {
        string empty = Directory.CreateTempSubdirectory("athanor-empty-").FullName;
        try
        {
            BundleVerification result = AthanorBundle.Verify(empty);

            Assert.False(result.Complete);
            Assert.Contains("missing", result.Problem, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public void Verify_accepts_the_vendored_bundle()
    {
        Assert.SkipUnless(MonacoPresent, "The Monaco bundle is not vendored (run scripts/fetch-monaco.ps1).");

        BundleVerification result = AthanorBundle.Verify();

        Assert.True(result.Complete, result.Problem);
        Assert.Equal(AthanorBundle.PinnedBundleSha256, AthanorBundle.ReadStamp());
    }

    [Fact]
    public void Verify_rejects_a_tampered_monaco_file()
    {
        Assert.SkipUnless(MonacoPresent, "The Monaco bundle is not vendored (run scripts/fetch-monaco.ps1).");
        string copy = Directory.CreateTempSubdirectory("athanor-tamper-").FullName;
        try
        {
            CopyDirectory(Root, copy);
            File.AppendAllText(Path.Combine(copy, "monaco", "vs", "loader.js"), "\n// tampered");

            BundleVerification result = AthanorBundle.Verify(copy);

            Assert.False(result.Complete);
            Assert.Contains("pinned hash", result.Problem, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    [Fact]
    public void Verify_ignores_files_outside_the_pinned_set()
    {
        Assert.SkipUnless(MonacoPresent, "The Monaco bundle is not vendored (run scripts/fetch-monaco.ps1).");
        string copy = Directory.CreateTempSubdirectory("athanor-extra-").FullName;
        try
        {
            CopyDirectory(Root, copy);
            File.WriteAllText(Path.Combine(copy, "monaco", "vs", "unrelated.txt"), "not part of the lock");

            Assert.True(AthanorBundle.Verify(copy).Complete);
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    [Fact]
    public void Bundle_hash_follows_the_documented_manifest_format()
    {
        string dir = Directory.CreateTempSubdirectory("athanor-hash-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "b.js"), "bee");
            File.WriteAllText(Path.Combine(dir, "sub", "a.js"), "ay");
            File.WriteAllText(Path.Combine(dir, "skip.txt"), "no");

            string actual = AthanorBundle.ComputeBundleSha256(dir, ["*.js"]);

            string manifest = $"b.js {Sha("bee")}\nsub/a.js {Sha("ay")}\n";
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))), actual);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("editor-*.js", "editor-BdtEMBbM.js", true)]
    [InlineData("editor-*.js", "editor/editor.worker.js", false)]
    [InlineData("html-*.js", "html.worker-C1TNvQ72.js", false)]
    [InlineData("assets/editor.worker-*.js", "assets/editor.worker-_vAIFJDs.js", true)]
    [InlineData("LOADER.JS", "loader.js", true)]
    public void Include_patterns_match_like_the_fetch_script(string pattern, string path, bool expected)
    {
        string dir = Directory.CreateTempSubdirectory("athanor-glob-").FullName;
        try
        {
            string full = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");

            string with = AthanorBundle.ComputeBundleSha256(dir, [pattern]);
            string none = AthanorBundle.ComputeBundleSha256(dir, ["no-such-file"]);

            Assert.Equal(expected, with != none);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static IEnumerable<string> Discriminators(Type baseType) =>
        baseType.GetCustomAttributes<JsonDerivedTypeAttribute>().Select(a => (string)a.TypeDiscriminator!);

    private static IEnumerable<string> SentTypes() =>
        SendTypeRegex().Matches(BridgeJs).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal);

    [GeneratedRegex(@"type:\s*'([A-Za-z]+)'\s*,\s*seq:")]
    private static partial Regex SendTypeRegex();

    private static void CopyDirectory(string from, string to)
    {
        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }

        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }
}
