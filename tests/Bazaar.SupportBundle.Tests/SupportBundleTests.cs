using System.IO.Compression;
using System.Text;
using Bazaar.Diagnostics.Capture;

namespace Bazaar.SupportBundle.Tests;

public class SupportBundleTests
{
    private sealed class FakeSource(string name, params SupportBundleFile[] files) : ISupportBundleSource
    {
        public string Name => name;

        public IReadOnlyList<SupportBundleFile> Collect() => files;
    }

    private sealed class ThrowingSource : ISupportBundleSource
    {
        public string Name => "boom";

        public IReadOnlyList<SupportBundleFile> Collect() => throw new InvalidOperationException(@"C:\Users\alice\secret.txt");
    }

    private static byte[] Build(SupportBundleBuilder builder, params ISupportBundleSource[] sources)
    {
        using var ms = new MemoryStream();
        builder.Build(sources, ms);
        return ms.ToArray();
    }

    private static Dictionary<string, string> Read(byte[] zip)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        });
    }

    [Fact]
    public void Secrets_paths_and_exact_values_never_reach_the_zip()
    {
        var log = "2026-01-01 WARN token=abc123supersecret path C:\\Users\\alice\\docs host DESKTOP-ALICE mail a@b.example\nBearer abcdef.ghi";
        var bytes = Build(new SupportBundleBuilder(["DESKTOP-ALICE"]), new FakeSource("log", new SupportBundleFile("logs/client.log", log)));

        var all = string.Join('\n', Read(bytes).Values);
        Assert.DoesNotContain("abc123supersecret", all);
        Assert.DoesNotContain("alice", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DESKTOP-ALICE", all);
        Assert.DoesNotContain("a@b.example", all);
        Assert.DoesNotContain("abcdef.ghi", all);
    }

    [Fact]
    public void Same_inputs_give_the_same_bytes()
    {
        var source = new FakeSource("a", new SupportBundleFile("x.txt", "one\ntwo"), new SupportBundleFile("a/y.txt", "three"));

        Assert.Equal(Build(new SupportBundleBuilder(), source), Build(new SupportBundleBuilder(), source));
    }

    [Fact]
    public void A_throwing_source_costs_only_its_own_files_and_is_listed_without_its_message()
    {
        var bytes = Build(new SupportBundleBuilder(), new ThrowingSource(), new FakeSource("ok", new SupportBundleFile("ok.txt", "fine")));

        var files = Read(bytes);
        Assert.Equal("fine", files["ok.txt"]);
        Assert.Contains("source 'boom' failed: InvalidOperationException", files["manifest.json"]);
        Assert.DoesNotContain("alice", files["manifest.json"]);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("/abs.txt")]
    [InlineData("C:/x.txt")]
    [InlineData("a\\b.txt")]
    [InlineData("manifest.json")]
    [InlineData("")]
    public void Unsafe_paths_are_refused(string path)
    {
        var files = Read(Build(new SupportBundleBuilder(), new FakeSource("s", new SupportBundleFile(path, "x"))));

        Assert.Equal(["manifest.json"], files.Keys.ToArray());
        Assert.Contains("refused path", files["manifest.json"]);
    }

    [Fact]
    public void A_large_file_keeps_its_newest_lines_and_the_total_limit_leaves_files_out()
    {
        var lines = string.Join('\n', Enumerable.Range(0, 1000).Select(i => $"line {i:D4}"));
        var builder = new SupportBundleBuilder(limits: new SupportBundleLimits(MaxFileBytes: 200, MaxTotalBytes: 250));
        var files = Read(Build(builder, new FakeSource("s", new SupportBundleFile("a.log", lines), new SupportBundleFile("b.log", lines))));

        Assert.Contains("line 0999", files["a.log"]);
        Assert.DoesNotContain("line 0000", files["a.log"]);
        Assert.False(files.ContainsKey("b.log"));
        Assert.Contains("left out 'b.log'", files["manifest.json"]);
    }

    [Fact]
    public void Client_info_source_lists_version_and_modules_only()
    {
        var info = new ClientInfo("0.1.0+abc", new DeviceInfo("Windows 11", ".NET 11.0.0", "x64"), [new ModuleInfo("Panes", "1.2.3")]);
        var files = Read(Build(new SupportBundleBuilder(), new ClientInfoSource(info)));

        Assert.Contains("version: 0.1.0+abc", files["client/info.txt"]);
        Assert.Contains("Panes 1.2.3", files["client/info.txt"]);
    }

    [Fact]
    public void File_source_reads_a_log_another_process_still_has_open_and_skips_a_missing_one()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = ClientDataPaths.LogFile(dir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            writer.WriteLine("hello");

            Assert.Equal("hello\n", new FileSource("log", path, "logs/client.log").Collect().Single().Text.ReplaceLineEndings("\n"));
            Assert.Empty(new FileSource("log", Path.Combine(dir, "none.log"), "logs/none.log").Collect());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class SelfCheckTests
{
    private sealed class Check(string id, Func<SelfCheckResult> run) : ISelfCheck
    {
        public string Id => id;

        public SelfCheckResult Run() => run();
    }

    [Fact]
    public void A_throwing_check_fails_without_stopping_the_others_and_without_its_message()
    {
        var report = SelfCheckRunner.Run("0.1.0", [
            new Check("b", () => new SelfCheckResult("b", true, "ok")),
            new Check("a", () => throw new IOException(@"C:\Users\bob\x")),
        ]);

        Assert.False(report.Passed);
        Assert.Equal(["a", "b"], report.Checks.Select(c => c.Id).ToArray());
        Assert.Equal("threw IOException", report.Checks[0].Reason);
    }

    [Fact]
    public void Report_passes_only_when_it_has_checks_and_all_passed_and_json_is_stable()
    {
        Assert.False(SelfCheckRunner.Run("v", []).Passed);

        var report = SelfCheckRunner.Run("v", [new Check("a", () => new SelfCheckResult("a", true, "ok"))]);
        Assert.True(report.Passed);
        var json = SelfCheckRunner.ToJson(report);
        Assert.Contains("\"schema\": \"bazaar.selfcheck/1\"", json);
        Assert.Contains("\"passed\": true", json);
        Assert.Equal(json, SelfCheckRunner.ToJson(report));
    }
}

public class ClientLogFileTests
{
    [Fact]
    public void Writes_reach_both_the_inner_writer_and_a_timestamped_file_line_and_errors_go_to_the_file_only()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var path = ClientDataPaths.LogFile(dir);
            var inner = new StringWriter();
            using (var log = ClientLogFile.Open(path, inner, new FixedClock()))
            {
                log.WriteLine("module skipped");
                log.Error("exploded");
            }

            Assert.Equal("module skipped" + Environment.NewLine, inner.ToString());
            var lines = File.ReadAllLines(path);
            Assert.Equal(["2026-11-22T17:00:00Z WARN module skipped", "2026-11-22T17:00:00Z ERROR exploded"], lines);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Data_dir_uses_the_override_when_set()
    {
        Assert.Equal("X:\\clean", ClientDataPaths.DataDir(_ => "X:\\clean"));
        Assert.EndsWith("Bazaar", ClientDataPaths.DataDir(_ => null));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 22, 17, 0, 0, TimeSpan.Zero);
    }
}
