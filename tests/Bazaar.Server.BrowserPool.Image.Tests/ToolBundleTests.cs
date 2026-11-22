using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.BrowserPool.LinuxHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.BrowserPool.Image.Tests;

public class ToolBundleTests
{
    private static readonly string _hashA = new('a', 64);
    private static readonly string _hashB = new('b', 64);

    private static string Json(string tools) => "{\"tools\":[" + tools + "]}";

    private static string Tool(string name = "bazaar-pool-spawn", string? file = null, string? hash = null, string size = "10") =>
        $"{{\"name\":\"{name}\",\"file\":\"{file ?? name}\",\"sha256\":\"{hash ?? _hashA}\",\"size\":{size}}}";

    [Fact]
    public void A_valid_manifest_parses_and_round_trips()
    {
        string text = Json(Tool() + "," + Tool("bazaar-pointer", hash: _hashB, size: "20"));

        Assert.True(PoolToolBundle.TryParse(text, out PoolToolBundle? b, out _));

        Assert.Equal(2, b!.Tools.Count);
        Assert.Equal(new PoolToolEntry("bazaar-pointer", "bazaar-pointer", _hashB, 20), b.Find("bazaar-pointer"));
        Assert.True(PoolToolBundle.TryParse(b.ToJson(), out PoolToolBundle? again, out _));
        Assert.Equal(b.ToJson(), again!.ToJson());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"tools\":[]}")]
    [InlineData("{\"tools\":[{}]}")]
    [InlineData("{\"tools\":null}")]
    [InlineData("{\"tools\":[null]}")]
    [InlineData("{\"tools\":[{\"name\":\"bazaar-pool-spawn\",\"file\":\"bazaar-pool-spawn\",\"sha256\":\"x\",\"size\":1,\"extra\":1}]}")]
    [InlineData("{\"tools\":[],\"extra\":1}")]
    [InlineData("{\"tools\":[{\"name\":\"bazaar-pool-spawn\",\"file\":\"bazaar-pool-spawn\",\"sha256\":\"aa\",\"size\":\"7\"}]}")]
    public void Malformed_manifests_are_refused(string text)
    {
        Assert.False(PoolToolBundle.TryParse(text, out PoolToolBundle? b, out string error));

        Assert.Null(b);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("rm", "rm", null, "10")]
    [InlineData("bazaar-pool-spawn", "other", null, "10")]
    [InlineData("bazaar-pool-spawn", null, "abc", "10")]
    [InlineData("bazaar-pool-spawn", null, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "10")]
    [InlineData("bazaar-pool-spawn", null, null, "0")]
    [InlineData("bazaar-pool-spawn", null, null, "-5")]
    public void Invalid_entries_are_refused(string name, string? file, string? hash, string size)
    {
        Assert.False(PoolToolBundle.TryParse(Json(Tool(name, file, hash, size)), out _, out _));
    }

    [Fact]
    public void A_duplicate_tool_is_refused()
    {
        Assert.False(PoolToolBundle.TryParse(Json(Tool() + "," + Tool()), out _, out string error));

        Assert.Contains("more than once", error);
    }

    [Fact]
    public void Duplicate_members_comments_and_trailing_commas_are_refused()
    {
        Assert.False(PoolToolBundle.TryParse("{\"tools\":[],\"tools\":[" + Tool() + "]}", out _, out _));
        Assert.False(PoolToolBundle.TryParse("{\"tools\":[" + Tool() + "]} // x", out _, out _));
        Assert.False(PoolToolBundle.TryParse("{\"tools\":[" + Tool() + ",]}", out _, out _));
    }

    [Fact]
    public void Create_refuses_invalid_entries()
    {
        Assert.Throws<ArgumentException>(() => PoolToolBundle.Create([new PoolToolEntry("sh", "sh", _hashA, 1)]));
        Assert.Throws<ArgumentException>(() => PoolToolBundle.Create([]));
    }

    [Fact]
    public void The_publish_script_manifest_shape_is_accepted()
    {
        // The exact layout scripts/ci/publish-pool-tools.sh writes.
        string text = "{\n  \"tools\": [\n    {\n      \"name\": \"bazaar-pool-spawn\",\n      \"file\": \"bazaar-pool-spawn\",\n      \"sha256\": \""
            + _hashA + "\",\n      \"size\": 1370312\n    },\n    {\n      \"name\": \"bazaar-pointer\",\n      \"file\": \"bazaar-pointer\",\n      \"sha256\": \""
            + _hashB + "\",\n      \"size\": 2204160\n    }\n  ]\n}\n";

        Assert.True(PoolToolBundle.TryParse(text, out PoolToolBundle? b, out _));
        Assert.Equal(text, b!.ToJson());
    }

    [Fact]
    public void FromDirectory_hashes_each_tool_through_the_port()
    {
        var fs = new FakeFileSystem();
        fs.Contents["/src/bazaar-pool-spawn"] = "spawn"u8.ToArray();
        fs.Contents["/src/bazaar-pointer"] = "pointer"u8.ToArray();

        PoolToolBundle b = PoolToolBundle.FromDirectory("/src/", fs);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("spawn"u8)), b.Find("bazaar-pool-spawn")!.Sha256);
        Assert.Equal(7, b.Find("bazaar-pointer")!.Size);
    }

    [Fact]
    public void FromDirectory_fails_on_a_missing_empty_or_oversize_file()
    {
        var fs = new FakeFileSystem();
        fs.Contents["/src/bazaar-pool-spawn"] = "x"u8.ToArray();
        Assert.Throws<InvalidOperationException>(() => PoolToolBundle.FromDirectory("/src", fs));

        fs.Contents["/src/bazaar-pointer"] = [];
        Assert.Throws<InvalidOperationException>(() => PoolToolBundle.FromDirectory("/src", fs));

        fs.Contents["/src/bazaar-pointer"] = "x"u8.ToArray();
        fs.Lengths["/src/bazaar-pointer"] = PoolToolBundle.MaxToolBytes + 1;
        Assert.Throws<InvalidOperationException>(() => PoolToolBundle.FromDirectory("/src", fs));
    }

    [Fact]
    public void The_physical_file_system_hashes_a_real_file()
    {
        string dir = Directory.CreateTempSubdirectory("pool-tools-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "bazaar-pool-spawn"), "spawn"u8.ToArray());
            File.WriteAllBytes(Path.Combine(dir, "bazaar-pointer"), "pointer"u8.ToArray());

            PoolToolBundle b = PoolToolBundle.FromDirectory(dir.Replace('\\', '/'), new PhysicalPoolImageFileSystem());

            Assert.Equal(5, b.Find("bazaar-pool-spawn")!.Size);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- options ----

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Tool_settings_default_to_no_install_and_no_hash_check()
    {
        PoolImageOptions o = PoolImageOptions.Bind(Config());

        Assert.Null(o.ToolSourceDirectory);
        Assert.Null(o.ToolManifestPath);
        Assert.Equal("/opt/bazaar/bin", o.ToolInstallDirectory);
    }

    [Fact]
    public void Tool_settings_bind()
    {
        PoolImageOptions o = PoolImageOptions.Bind(Config(
            ("Bazaar:BrowserPool:Image:ToolSourceDirectory", "/home/op/tools"),
            ("Bazaar:BrowserPool:Image:ToolInstallDirectory", "/opt/x/bin"),
            ("Bazaar:BrowserPool:Image:ToolManifestPath", "/opt/x/bin/pool-tools.json")));

        Assert.Equal("/home/op/tools", o.ToolSourceDirectory);
        Assert.Equal("/opt/x/bin", o.ToolInstallDirectory);
        Assert.Equal("/opt/x/bin/pool-tools.json", o.ToolManifestPath);
    }

    [Theory]
    [InlineData("ToolSourceDirectory", "relative/dir")]
    [InlineData("ToolSourceDirectory", "/a/../b")]
    [InlineData("ToolSourceDirectory", "/a/b; id")]
    [InlineData("ToolInstallDirectory", "/opt")]
    [InlineData("ToolInstallDirectory", "/opt/$HOME")]
    [InlineData("ToolManifestPath", "/etc/pool-tools.json")]
    [InlineData("ToolManifestPath", "/opt/bazaar/bin/other.json")]
    [InlineData("ToolManifestPath", "pool-tools.json")]
    public void Invalid_tool_settings_throw_without_echoing_them(string key, string value)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => PoolImageOptions.Bind(Config(($"Bazaar:BrowserPool:Image:{key}", value))));

        Assert.DoesNotContain(value, ex.Message);
    }

    // ---- script ----

    private static PoolToolBundle Bundle() =>
        PoolToolBundle.Create([new("bazaar-pool-spawn", "bazaar-pool-spawn", _hashA, 10), new("bazaar-pointer", "bazaar-pointer", _hashB, 20)]);

    private static string Render(PoolToolBundle? bundle, PoolImageOptions image) =>
        PoolImageScript.Render(image, new BrowserPoolOptions(), new LinuxPoolHostOptions(), bundle);

    [Fact]
    public void Without_a_bundle_the_script_is_the_unchanged_default()
    {
        Assert.Equal(PoolImageScript.Render(new PoolImageOptions(), new BrowserPoolOptions(), new LinuxPoolHostOptions()), Render(null, new PoolImageOptions()));
        Assert.DoesNotContain("sha256sum", Render(null, new PoolImageOptions()));
    }

    [Fact]
    public void With_a_bundle_the_script_checks_each_hash_before_installing()
    {
        string script = Render(Bundle(), new PoolImageOptions { ToolSourceDirectory = "/home/op/tools" });
        string plain = Render(null, new PoolImageOptions());

        string expectedTail =
            "\n# Guest tools: checked against the manifest hash, then installed root-owned.\n"
            + "mkdir -p /opt/bazaar/bin\nchown root:root /opt/bazaar/bin\nchmod 0755 /opt/bazaar/bin\n"
            + $"printf '%s  %s\\n' {_hashA} /home/op/tools/bazaar-pool-spawn | sha256sum -c -\n"
            + "install -o root -g root -m 0755 /home/op/tools/bazaar-pool-spawn /opt/bazaar/bin/bazaar-pool-spawn\n"
            + $"printf '%s  %s\\n' {_hashB} /home/op/tools/bazaar-pointer | sha256sum -c -\n"
            + "install -o root -g root -m 0755 /home/op/tools/bazaar-pointer /opt/bazaar/bin/bazaar-pointer\n";

        Assert.Equal(plain + expectedTail, script);
        Assert.True(script.IndexOf("sha256sum -c", StringComparison.Ordinal) < script.IndexOf("install -o", StringComparison.Ordinal));
        Assert.True(script.LastIndexOf("sha256sum -c", StringComparison.Ordinal) < script.LastIndexOf("install -o", StringComparison.Ordinal));
        Assert.DoesNotContain("curl", script);
        Assert.DoesNotContain("wget", script);
    }

    [Fact]
    public void The_script_writes_the_manifest_when_a_path_is_set()
    {
        var image = new PoolImageOptions { ToolSourceDirectory = "/t", ToolManifestPath = "/opt/bazaar/bin/pool-tools.json" };

        string script = Render(Bundle(), image);

        Assert.Contains("cat > /opt/bazaar/bin/pool-tools.json <<'BAZAAR_POOL_TOOL_MANIFEST'\n" + Bundle().ToJson() + "BAZAAR_POOL_TOOL_MANIFEST\n", script);
        Assert.EndsWith("chown root:root /opt/bazaar/bin/pool-tools.json\nchmod 0644 /opt/bazaar/bin/pool-tools.json\n", script);
    }

    [Fact]
    public void A_bundle_and_a_source_directory_must_come_together()
    {
        Assert.Throws<ArgumentException>(() => Render(Bundle(), new PoolImageOptions()));
        Assert.Throws<ArgumentException>(() => Render(null, new PoolImageOptions { ToolSourceDirectory = "/t" }));
    }

    [Theory]
    [InlineData("/t; rm -rf /", "/opt/bazaar/bin")]
    [InlineData("/t", "/opt/$(id)")]
    [InlineData("rel", "/opt/bazaar/bin")]
    public void Unsafe_tool_paths_are_refused(string source, string target)
    {
        Assert.Throws<ArgumentException>(() => Render(Bundle(), new PoolImageOptions { ToolSourceDirectory = source, ToolInstallDirectory = target }));
    }

    // ---- readiness ----

    private const string _manifestPath = "/opt/bazaar/bin/pool-tools.json";
    private const string _helper = "/opt/bazaar/bin/bazaar-pool-spawn";
    private const string _pointer = "/opt/bazaar/bin/bazaar-pointer";

    private static (PoolImageReadinessCheck Check, FakeFileSystem Fs) MakeCheck(Action<FakeFileSystem>? change = null)
    {
        var image = new PoolImageOptions { ToolSourceDirectory = "/t", ToolManifestPath = _manifestPath };
        var reqs = PoolImageRequirements.From(new BrowserPoolOptions(), new LinuxPoolHostOptions { PoolSpawnHelperPath = _helper }, image);
        var fs = FakeFileSystem.Ready(reqs);
        byte[] spawn = "spawn"u8.ToArray();
        byte[] pointer = "pointer"u8.ToArray();
        fs.Contents[_helper] = spawn;
        fs.Contents[_pointer] = pointer;
        fs.Texts[_manifestPath] = PoolToolBundle.Create(
        [
            new("bazaar-pool-spawn", "bazaar-pool-spawn", Convert.ToHexStringLower(SHA256.HashData(spawn)), spawn.Length),
            new("bazaar-pointer", "bazaar-pointer", Convert.ToHexStringLower(SHA256.HashData(pointer)), pointer.Length),
        ]).ToJson();
        change?.Invoke(fs);
        return (new PoolImageReadinessCheck(reqs, fs, image, new FakeTimeProvider()), fs);
    }

    private static async Task<IReadOnlyList<Bazaar.Server.BrowserPool.Abstractions.PoolReadinessFailure>> Run(PoolImageReadinessCheck c) =>
        await c.CheckAsync(CancellationToken.None);

    [Fact]
    public async Task Matching_hashes_pass()
    {
        var (check, _) = MakeCheck();

        Assert.Empty(await Run(check));
    }

    [Fact]
    public void Requirements_list_one_hash_entry_per_tool()
    {
        var image = new PoolImageOptions { ToolManifestPath = _manifestPath };
        var reqs = PoolImageRequirements.From(new BrowserPoolOptions(), new LinuxPoolHostOptions { PoolSpawnHelperPath = _helper }, image);

        Assert.Equal(["tool-hash:bazaar-pool-spawn", "tool-hash:bazaar-pointer"], reqs.Items.Where(i => i.Kind == PoolImageRequirementKind.ToolHash).Select(i => i.Id));
        Assert.DoesNotContain(PoolImageRequirements.From(new BrowserPoolOptions(), new LinuxPoolHostOptions()).Items, i => i.Kind == PoolImageRequirementKind.ToolHash);
    }

    [Fact]
    public async Task A_changed_tool_fails_by_id()
    {
        var (check, _) = MakeCheck(fs => fs.Contents[_helper] = "tampered"u8.ToArray());

        var f = Assert.Single(await Run(check));

        Assert.Equal("tool-hash:bazaar-pool-spawn", f.Id);
        Assert.DoesNotContain("tampered", f.Reason);
    }

    [Fact]
    public async Task A_missing_manifest_fails_closed()
    {
        var (check, _) = MakeCheck(fs => fs.Texts.Remove(_manifestPath));

        Assert.All(await Run(check), f => Assert.StartsWith("tool-hash:", f.Id));
        Assert.Equal(2, (await Run(check)).Count);
    }

    [Fact]
    public async Task A_malformed_manifest_fails_closed()
    {
        var (check, _) = MakeCheck(fs => fs.Texts[_manifestPath] = "{ nope");

        Assert.Equal(2, (await Run(check)).Count);
    }

    [Fact]
    public async Task A_manifest_without_the_tool_fails()
    {
        var (check, _) = MakeCheck(fs => fs.Texts[_manifestPath] = PoolToolBundle.Create([new("bazaar-pool-spawn", "bazaar-pool-spawn", new string('c', 64), 5)]).ToJson());

        Assert.Contains(await Run(check), f => f.Id == "tool-hash:bazaar-pointer");
    }

    [Fact]
    public async Task An_oversize_file_fails_closed()
    {
        var (check, _) = MakeCheck(fs => fs.Lengths[_pointer] = PoolToolBundle.MaxToolBytes + 1);

        Assert.Equal("tool-hash:bazaar-pointer", Assert.Single(await Run(check)).Id);
    }

    [Fact]
    public async Task A_read_error_fails_closed_without_detail()
    {
        var (check, _) = MakeCheck(fs => fs.Throwing.Add(_pointer));

        var f = Assert.Single(await Run(check));

        Assert.Equal("tool-hash:bazaar-pointer", f.Id);
        Assert.DoesNotContain("secret detail", f.Reason);
    }

    [Fact]
    public async Task Without_a_manifest_path_no_hash_is_checked()
    {
        var reqs = PoolImageRequirements.From(new BrowserPoolOptions(), new LinuxPoolHostOptions());
        var check = new PoolImageReadinessCheck(reqs, FakeFileSystem.Ready(reqs), new PoolImageOptions(), new FakeTimeProvider());

        Assert.Empty(await Run(check));
    }
}
