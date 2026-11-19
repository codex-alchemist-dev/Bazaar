using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Bazaar.Server.Portability.Sandbox.Tests;

// Doc 44 section 8.3: Detect and Parse run in a child process. Every failure below must end the job cleanly.
public class ParserSandboxTests
{
    private static readonly string _fixtures = typeof(HangImporter).Assembly.Location;

    private static ParserSandboxRunner Runner(
        Func<ParserSandboxOptions, ParserSandboxOptions>? tune = null,
        IParserSignalSink? sink = null,
        IParserConfinement? confinement = null)
    {
        var options = new ParserSandboxOptions { ImporterAssemblies = [_fixtures] };
        if (tune is not null) options = tune(options);
        return new ParserSandboxRunner(options, confinement, sink);
    }

    private static ImportSource Source(string name = "in.txt", string text = "hello") => new(name, Encoding.UTF8.GetBytes(text));

    private static ParserSandboxException Fails(Func<object> action)
    {
        var error = Assert.Throws<ParserSandboxException>(action);
        return error;
    }

    [Fact]
    public void A_real_importer_parses_in_the_child_and_returns_records()
    {
        var runner = Runner();

        var records = runner.Parse("bazaar.import.txt", Source("note.txt", "line one\nline two"));

        var record = Assert.Single(records);
        Assert.Equal("note", record.Title);
        Assert.Equal("line one\nline two", record.Body);
        Assert.Equal("txt", record.Attributes["format"]);
    }

    [Fact]
    public void Detect_runs_in_the_child_and_matches_the_in_process_result()
    {
        var runner = Runner();
        var source = Source("note.txt", "plain");

        var sandboxed = runner.Detect("bazaar.import.txt", source);
        var direct = new PlainTextImporter().Detect(source);

        Assert.Equal(direct, sandboxed);
    }

    [Fact]
    public void The_child_is_a_different_process()
    {
        var runner = Runner();

        var info = runner.ListImporters();

        Assert.Contains(info, i => i.Id == "bazaar.import.docx");
        Assert.Contains(info, i => i.Id == "test.hang");
        Assert.NotNull(runner.LastRun);
        Assert.Contains("wall-clock-timeout", runner.LastRun!.Applied);
    }

    [Fact]
    public void The_pipeline_runs_unchanged_over_sandboxed_importers()
    {
        var runner = Runner();
        var importers = runner.CreateImporters().Where(i => i.Id.StartsWith("bazaar.import.", StringComparison.Ordinal)).ToList();
        var pipeline = new ImportPipeline(importers);

        var result = pipeline.Run(Source("note.md", "# Title\nbody"), new HashSet<string>());

        Assert.Equal(ImportStatus.Ready, result.Status);
        Assert.Equal("bazaar.import.md", result.ImporterId);
        Assert.Equal("Title", Assert.Single(result.Preview!.New).Title);
    }

    [Fact]
    public void Output_printed_by_an_importer_does_not_corrupt_the_frames()
    {
        var runner = Runner();

        var records = runner.Parse("test.chatty", Source());

        Assert.Equal("ok", Assert.Single(records).Body);
    }

    [Fact]
    public void A_hanging_parse_is_killed_at_the_record_timeout_and_the_job_fails()
    {
        var runner = Runner(o => o with { RecordTimeout = TimeSpan.FromSeconds(1) });
        var started = Stopwatch.StartNew();

        var error = Fails(() => runner.Parse("test.hang", Source()));

        Assert.Equal(ParserFailureKind.Timeout, error.Kind);
        Assert.Contains("record", error.Message, StringComparison.Ordinal);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), "the kill must not wait for the hang");
    }

    [Fact]
    public void A_hanging_detect_is_killed_too()
    {
        var runner = Runner(o => o with { RecordTimeout = TimeSpan.FromSeconds(1) });

        var error = Assert.Throws<ParserSandboxException>(() => runner.Detect("test.hangdetect", Source()));

        Assert.Equal(ParserFailureKind.Timeout, error.Kind);
    }

    [Fact]
    public void The_job_timeout_ends_a_job_even_when_each_record_is_in_time()
    {
        var runner = Runner(o => o with { JobTimeout = TimeSpan.FromSeconds(1) });

        var error = Fails(() => runner.Parse("test.hang", Source()));

        Assert.Equal(ParserFailureKind.Timeout, error.Kind);
        Assert.Contains("job", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_slow_record_fails_the_record_timeout_but_not_a_longer_one()
    {
        Assert.Equal(
            ParserFailureKind.Timeout,
            Assert.Throws<ParserSandboxException>(() => Runner(o => o with { RecordTimeout = TimeSpan.FromSeconds(1) }).Parse("test.slow", Source())).Kind);

        var records = Runner(o => o with { RecordTimeout = TimeSpan.FromSeconds(30) }).Parse("test.slow", Source());
        Assert.Equal("late", Assert.Single(records).Body);
    }

    [Fact]
    public void The_timeout_kills_the_whole_process_tree()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the /proc scan is Linux only");
        var marker = "7" + Random.Shared.Next(100000, 999999) + ".5";
        var runner = Runner(o => o with { RecordTimeout = TimeSpan.FromSeconds(2) });

        var error = Assert.Throws<ParserSandboxException>(() => runner.Parse("test.spawn", Source(marker)));

        Assert.Equal(ParserFailureKind.Timeout, error.Kind);
        Assert.False(ProcessWithArgumentExists(marker), "the grandchild must be killed with the tree");
    }

    private static bool ProcessWithArgumentExists(string marker)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var found = Directory.EnumerateDirectories("/proc")
                .Select(d => Path.Combine(d, "cmdline"))
                .Any(f =>
                {
                    try
                    {
                        return File.ReadAllText(f).Contains(marker, StringComparison.Ordinal);
                    }
                    catch (IOException)
                    {
                        return false;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        return false;
                    }
                });
            if (!found) return false;
            Thread.Sleep(100);
        }
        return true;
    }

    [Fact]
    public void A_crashing_parser_fails_the_job_with_the_exit_code()
    {
        var runner = Runner();

        var error = Fails(() => runner.Parse("test.crash", Source()));

        Assert.Equal(ParserFailureKind.Crash, error.Kind);
        Assert.Contains("exit code", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parser_that_throws_reports_a_parser_error_and_does_not_count_as_a_crash()
    {
        var runner = Runner(o => o with { CrashLoopThreshold = 1 });

        var error = Fails(() => runner.Parse("test.throw", Source()));

        Assert.Equal(ParserFailureKind.ParserError, error.Kind);
        Assert.Contains("bad file", error.Message, StringComparison.Ordinal);
        Assert.False(runner.CrashTracker.IsOpen("test.throw"));
    }

    [Fact]
    public void An_unknown_importer_is_a_parser_error()
    {
        var error = Fails(() => Runner().Parse("test.nothing", Source()));

        Assert.Equal(ParserFailureKind.ParserError, error.Kind);
    }

    [Fact]
    public void Huge_output_is_cut_off_at_the_output_cap_and_the_child_is_killed()
    {
        var runner = Runner(o => o with { MaxOutputBytes = 8 * 1024 * 1024 });

        var error = Fails(() => runner.Parse("test.huge", Source()));

        Assert.Equal(ParserFailureKind.OutputTooLarge, error.Kind);
    }

    [Fact]
    public void A_record_over_the_record_limit_is_refused_by_the_child()
    {
        var runner = Runner(o => o with { Limits = new ImportLimits(maxRecordBytes: 1024 * 1024) });

        var error = Fails(() => runner.Parse("test.oversize", Source()));

        Assert.Equal(ParserFailureKind.LimitExceeded, error.Kind);
    }

    [Fact]
    public void A_source_over_the_size_limit_is_refused_by_the_child_before_parsing()
    {
        var runner = Runner(o => o with { Limits = new ImportLimits(maxSourceBytes: 10) });

        var error = Fails(() => runner.Parse("bazaar.import.txt", Source("big.txt", new string('a', 100))));

        Assert.Equal(ParserFailureKind.LimitExceeded, error.Kind);
    }

    [Fact]
    public void A_zip_bomb_fails_the_job_cleanly_with_a_limit_error()
    {
        var runner = Runner();

        var error = Fails(() => runner.Parse("bazaar.import.docx", new ImportSource("bomb.docx", MakeBomb())));

        Assert.Equal(ParserFailureKind.LimitExceeded, error.Kind);
        Assert.Contains("expands past the limit", error.Message, StringComparison.Ordinal);
        Assert.False(runner.CrashTracker.IsOpen("bazaar.import.docx"));
    }

    [Fact]
    public void A_zip_bomb_through_the_pipeline_is_a_failed_job_with_no_preview()
    {
        var runner = Runner();
        var docx = new SandboxedImporter(runner, "bazaar.import.docx", "1.0.0");
        var pipeline = new ImportPipeline([docx]);

        var result = pipeline.Run(new ImportSource("bomb.docx", MakeBomb()), new HashSet<string>(), preferredImporterId: "bazaar.import.docx");

        Assert.Equal(ImportStatus.Failed, result.Status);
        Assert.Null(result.Preview);
        Assert.Equal(string.Empty, result.MappingHash);
    }

    [Fact]
    public void A_crash_through_the_pipeline_is_a_failed_job_with_no_preview()
    {
        var runner = Runner();
        var pipeline = new ImportPipeline([new SandboxedImporter(runner, "test.crash", "1.0.0")]);

        var result = pipeline.Run(Source(), new HashSet<string>(), preferredImporterId: "test.crash");

        Assert.Equal(ImportStatus.Failed, result.Status);
        Assert.Null(result.Preview);
        Assert.Contains("ended without a result", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_memory_hog_is_stopped_by_the_memory_ceiling()
    {
        var runner = Runner(o => o with { MaxMemoryBytes = 128L * 1024 * 1024, JobTimeout = TimeSpan.FromSeconds(60) });

        var error = Fails(() => runner.Parse("test.hog", Source()));

        Assert.Equal(ParserFailureKind.Crash, error.Kind);
    }

    [Fact]
    public void Repeated_crashes_raise_parser_crash_loop_once_and_then_refuse_jobs()
    {
        var sink = new RecordingSink();
        var runner = Runner(o => o with { CrashLoopThreshold = 3 }, sink);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(ParserFailureKind.Crash, Assert.Throws<ParserSandboxException>(() => runner.Parse("test.crash", Source())).Kind);
        }

        var signal = Assert.Single(sink.Signals);
        Assert.Equal(ParserSignals.CrashLoop, signal.Name);
        Assert.Equal("test.crash", signal.ImporterId);
        var refused = Assert.Throws<ParserSandboxException>(() => runner.Parse("test.crash", Source()));
        Assert.Equal(ParserFailureKind.CrashLoop, refused.Kind);
        Assert.Single(sink.Signals);
        // Another importer is not affected.
        Assert.Equal("ok", Assert.Single(runner.Parse("test.chatty", Source())).Body);
    }

    [Fact]
    public void A_missing_host_fails_cleanly()
    {
        var runner = Runner(o => o with { DotnetPath = "/nonexistent/dotnet" });

        var error = Fails(() => runner.Parse("bazaar.import.txt", Source()));

        Assert.Equal(ParserFailureKind.HostUnavailable, error.Kind);
    }

    [Fact]
    public void Required_confinement_refuses_to_run_when_the_provider_cannot_apply_it()
    {
        var runner = Runner(o => o with { RequireFullConfinement = true }, confinement: new TreeKillOnlyConfinement());

        var error = Fails(() => runner.Parse("bazaar.import.txt", Source()));

        Assert.Equal(ParserFailureKind.ConfinementUnavailable, error.Kind);
    }

    [Fact]
    public void The_child_has_no_network_when_the_provider_reports_it_applied()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "network namespaces are applied by the Linux provider");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var runner = Runner();

        var records = runner.Parse("test.net", Source("p.txt", port.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Contains(LinuxParserConfinement.ItemNoNetwork, runner.LastRun!.Applied.Concat(runner.LastRun.NotApplied));
        if (runner.LastRun.Applied.Contains(LinuxParserConfinement.ItemNoNetwork))
        {
            Assert.Equal("blocked", Assert.Single(records).Body);
        }
    }

    [Fact]
    public void The_linux_provider_reports_honestly_what_it_does_not_confine()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "reports the Linux provider");
        var runner = Runner();

        _ = runner.Parse("test.chatty", Source());

        Assert.Contains(LinuxParserConfinement.ItemNoFileWrites, runner.LastRun!.NotApplied);
        Assert.Contains(LinuxParserConfinement.ItemFileReadScope, runner.LastRun.NotApplied);
        Assert.Contains(LinuxParserConfinement.ItemNoChildProcesses, runner.LastRun.NotApplied);
        Assert.Contains(LinuxParserConfinement.ItemCpuLimit, runner.LastRun.Applied);
    }

    [Fact]
    public void The_child_sees_no_server_environment()
    {
        Environment.SetEnvironmentVariable("BAZAAR_TEST_SECRET", "s3cret");
        try
        {
            var records = Runner().Parse("test.env", Source());

            Assert.Equal("none", Assert.Single(records).Body);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BAZAAR_TEST_SECRET", null);
        }
    }

    [Fact]
    public void Options_reject_values_below_their_floors()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParserSandboxOptions { RecordTimeout = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParserSandboxOptions { MaxMemoryBytes = 1024 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParserSandboxOptions { CrashLoopThreshold = 0 }.Validate());
        new ParserSandboxOptions().Validate();
    }

    private static byte[] MakeBomb()
    {
        const string ns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var types = zip.CreateEntry("[Content_Types].xml").Open()) types.Write(Encoding.UTF8.GetBytes("<Types/>"));
            using var document = new StreamWriter(zip.CreateEntry("word/document.xml", CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            document.Write($"<?xml version=\"1.0\"?><w:document xmlns:w=\"{ns}\"><w:body><w:p><w:r><w:t>");
            var chunk = new string('a', 1024 * 1024);
            for (var i = 0; i < 8; i++) document.Write(chunk);
            document.Write("</w:t></w:r></w:p></w:body></w:document>");
        }
        return buffer.ToArray();
    }

    private sealed class RecordingSink : IParserSignalSink
    {
        public List<ParserSignal> Signals { get; } = [];

        public void Raise(ParserSignal signal) => Signals.Add(signal);
    }
}
