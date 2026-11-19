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
    public void A_hanging_parse_is_killed_at_the_job_timeout_and_the_job_fails()
    {
        var runner = Runner(o => o with { JobTimeout = TimeSpan.FromSeconds(1) });
        var started = Stopwatch.StartNew();

        var error = Fails(() => runner.Parse("test.hang", Source()));

        Assert.Equal(ParserFailureKind.JobTimeout, error.Kind);
        Assert.Contains("job", error.Message, StringComparison.Ordinal);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), "the kill must not wait for the hang");
    }

    [Fact]
    public void A_hanging_detect_is_killed_too()
    {
        var runner = Runner(o => o with { JobTimeout = TimeSpan.FromSeconds(1) });

        var error = Assert.Throws<ParserSandboxException>(() => runner.Detect("test.hangdetect", Source()));

        Assert.Equal(ParserFailureKind.JobTimeout, error.Kind);
    }

    [Fact]
    public void The_wait_for_the_first_frame_uses_the_job_timeout_not_the_frame_gap_timeout()
    {
        // The slow importer needs about 4 s before its first frame. A 1 s frame-gap timeout must not apply to that wait.
        var records = Runner(o => o with { FrameGapTimeout = TimeSpan.FromSeconds(1) }).Parse("test.slow", Source());
        Assert.Equal("late", Assert.Single(records).Body);

        var error = Assert.Throws<ParserSandboxException>(() =>
            Runner(o => o with { JobTimeout = TimeSpan.FromSeconds(1) }).Parse("test.slow", Source()));
        Assert.Equal(ParserFailureKind.JobTimeout, error.Kind);
    }

    [Fact]
    public void A_job_timeout_does_not_feed_the_crash_loop()
    {
        var sink = new RecordingSink();
        var runner = Runner(o => o with { JobTimeout = TimeSpan.FromSeconds(1), CrashLoopThreshold = 1 }, sink);

        Assert.Equal(ParserFailureKind.JobTimeout, Assert.Throws<ParserSandboxException>(() => runner.Parse("test.hang", Source())).Kind);

        Assert.False(runner.CrashTracker.IsOpen("test.hang"));
        Assert.Empty(sink.Signals);
    }

    [Fact]
    public void The_timeout_kills_the_whole_process_tree()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the /proc scan is Linux only");
        var marker = "7" + Random.Shared.Next(100000, 999999) + ".5";
        var runner = Runner(o => o with { JobTimeout = TimeSpan.FromSeconds(2) });

        var error = Assert.Throws<ParserSandboxException>(() => runner.Parse("test.spawn", Source(marker)));

        Assert.Equal(ParserFailureKind.JobTimeout, error.Kind);
        Assert.False(ProcessWithArgumentExists(marker), "the grandchild must be killed with the tree");
    }

    [Fact]
    public async Task A_descendant_that_escapes_the_kill_and_holds_the_pipes_cannot_hang_the_server()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/bin/sh") && (File.Exists("/usr/bin/setsid") || File.Exists("/bin/setsid")), "needs sh and setsid");
        var marker = "8" + Random.Shared.Next(100000, 999999) + ".5";
        var runner = Runner(o => o with { JobTimeout = TimeSpan.FromSeconds(2) });
        try
        {
            var job = Task.Run(() => Assert.Throws<ParserSandboxException>(() => runner.Parse("test.escape", Source(marker))), TestContext.Current.CancellationToken);

            // Without the bounded waits this never completes, and WaitAsync fails the test with a timeout.
            var error = await job.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            Assert.Equal(ParserFailureKind.JobTimeout, error.Kind);
        }
        finally
        {
            KillProcessesWithArgument(marker);
        }
    }

    [Fact]
    public void An_incomplete_record_frame_is_a_protocol_violation()
    {
        var runner = Runner();

        var error = Fails(() => runner.Parse("test.nullattrs", Source()));

        Assert.Equal(ParserFailureKind.ProtocolViolation, error.Kind);
    }

    [Fact]
    public void A_detect_confidence_outside_zero_to_one_is_a_protocol_violation()
    {
        var error = Assert.Throws<ParserSandboxException>(() => Runner().Detect("test.badconfidence", Source()));

        Assert.Equal(ParserFailureKind.ProtocolViolation, error.Kind);
    }

    [Fact]
    public void An_oversize_or_control_character_source_name_is_refused_before_a_child_starts()
    {
        var runner = Runner();

        var long_ = Assert.Throws<ParserSandboxException>(() => runner.Parse("bazaar.import.txt", Source(new string('n', ParserSandboxRunner.MaxSourceNameBytes + 1))));
        var control = Assert.Throws<ParserSandboxException>(() => runner.Parse("bazaar.import.txt", Source("a\nb.txt")));

        Assert.Equal(ParserFailureKind.LimitExceeded, long_.Kind);
        Assert.Equal(ParserFailureKind.LimitExceeded, control.Kind);
        Assert.Null(runner.LastRun);
    }

    // Stand-in hosts: /bin/sh runs a script that prints prepared frames and exits with a chosen code.
    private static string StandInHost(string shellBody, params ParserMessage[] frames)
    {
        var dir = Directory.CreateTempSubdirectory("bazaar-standin-").FullName;
        using (var stream = File.Create(Path.Combine(dir, "frames.bin")))
        {
            foreach (var frame in frames) ParserFrames.Write(stream, frame);
        }
        var script = Path.Combine(dir, "host.sh");
        File.WriteAllText(script, shellBody.Replace("{frames}", Path.Combine(dir, "frames.bin"), StringComparison.Ordinal) + "\n");
        return script;
    }

    private static ParserSandboxRunner StandInRunner(string script, Func<ParserSandboxOptions, ParserSandboxOptions>? tune = null, IParserSignalSink? sink = null) =>
        Runner(o => (tune?.Invoke(o) ?? o) with { DotnetPath = "/bin/sh", HostAssemblyPath = script }, sink);

    [Fact]
    public void A_structured_bad_request_report_with_no_earlier_frames_is_a_bad_request_not_a_crash()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "uses /bin/sh as a stand-in host");
        var script = StandInHost(
            "cat '{frames}'; exit 2",
            new ParserMessage { Type = ParserMessageTypes.Error, ErrorKind = ParserErrorKinds.BadRequest, Message = "bad request header" });
        var sink = new RecordingSink();
        var runner = StandInRunner(script, o => o with { CrashLoopThreshold = 1 }, sink);

        var error = Fails(() => runner.Parse("bazaar.import.txt", Source()));

        Assert.Equal(ParserFailureKind.BadRequest, error.Kind);
        Assert.False(runner.CrashTracker.IsOpen("bazaar.import.txt"));
        Assert.Empty(sink.Signals);
    }

    [Fact]
    public void Exit_code_2_without_a_structured_report_is_a_crash()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "uses /bin/sh as a stand-in host");
        var runner = StandInRunner(StandInHost("exit 2"));

        var error = Fails(() => runner.Parse("bazaar.import.txt", Source()));

        Assert.Equal(ParserFailureKind.Crash, error.Kind);
    }

    [Fact]
    public void A_bad_request_report_after_other_frames_is_a_protocol_violation()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "uses /bin/sh as a stand-in host");
        var record = new SourceRecord("k", "document", "t", "b", new Dictionary<string, string>());
        var script = StandInHost(
            "cat '{frames}'; exit 2",
            new ParserMessage { Type = ParserMessageTypes.Record, Record = record },
            new ParserMessage { Type = ParserMessageTypes.Error, ErrorKind = ParserErrorKinds.BadRequest, Message = "late" });

        var error = Fails(() => StandInRunner(script).Parse("bazaar.import.txt", Source()));

        Assert.Equal(ParserFailureKind.ProtocolViolation, error.Kind);
    }

    [Fact]
    public void Out_of_memory_text_on_stderr_is_not_trusted_and_the_job_is_a_crash()
    {
        var runner = Runner();

        var error = Fails(() => runner.Parse("test.fakeoom", Source()));

        Assert.Equal(ParserFailureKind.Crash, error.Kind);
    }

    [Fact]
    public void A_child_killed_by_sigkill_that_the_server_did_not_cause_is_a_limit_failure()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "uses /bin/sh as a stand-in host");
        var runner = StandInRunner(StandInHost("kill -9 $$"));

        var error = Fails(() => runner.Parse("bazaar.import.txt", Source()));

        Assert.Equal(ParserFailureKind.LimitExceeded, error.Kind);
    }

    [Fact]
    public void Control_characters_and_length_in_child_text_are_stripped_from_messages()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "uses /bin/sh as a stand-in host");
        var evil = "\u001b[31mred\u0007\nline2" + new string('x', 5000);
        var errorScript = StandInHost(
            "cat '{frames}'; exit 0",
            new ParserMessage { Type = ParserMessageTypes.Error, ErrorKind = ParserErrorKinds.Parser, Message = evil });
        var stderrScript = StandInHost("printf 'bad\\033[31mtext\\a\\n' >&2; exit 1");

        var fromFrame = Fails(() => StandInRunner(errorScript).Parse("bazaar.import.txt", Source()));
        var fromStderr = Fails(() => StandInRunner(stderrScript).Parse("bazaar.import.txt", Source()));

        Assert.Equal(ParserFailureKind.ParserError, fromFrame.Kind);
        Assert.DoesNotContain(fromFrame.Message, char.IsControl);
        Assert.True(fromFrame.Message.Length <= 1024);
        Assert.Equal(ParserFailureKind.Crash, fromStderr.Kind);
        Assert.Contains("badtext".Substring(0, 3), fromStderr.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fromStderr.Message, char.IsControl);
    }

    [Fact]
    public void The_server_bounds_a_record_by_every_field_not_just_the_body()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "uses /bin/sh as a stand-in host");
        var fat = new SourceRecord("k", "document", "t", "b", new Dictionary<string, string> { ["a"] = new string('v', 5000) });
        var script = StandInHost(
            "cat '{frames}'; exit 0",
            new ParserMessage { Type = ParserMessageTypes.Record, Record = fat },
            new ParserMessage { Type = ParserMessageTypes.Done, Count = 1 });
        var runner = StandInRunner(script, o => o with { Limits = new ImportLimits(maxRecordBytes: 1024) });

        var error = Fails(() => runner.Parse("bazaar.import.txt", Source()));

        Assert.Equal(ParserFailureKind.LimitExceeded, error.Kind);
    }

    [Fact]
    public void The_child_also_bounds_a_record_by_every_field()
    {
        var runner = Runner(o => o with { Limits = new ImportLimits(maxRecordBytes: 1024 * 1024) });

        var error = Fails(() => runner.Parse("test.bigtitle", Source()));

        Assert.Equal(ParserFailureKind.LimitExceeded, error.Kind);
    }

    [Fact]
    public void One_source_failing_repeatedly_cannot_open_the_circuit_but_is_refused_after_the_cap()
    {
        var sink = new RecordingSink();
        var runner = Runner(o => o with { CrashLoopThreshold = 2, SourceFailureCap = 3 }, sink);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(ParserFailureKind.Crash, Assert.Throws<ParserSandboxException>(() => runner.Parse("test.crash", Source("same.txt", "same"))).Kind);
        }

        Assert.False(runner.CrashTracker.IsOpen("test.crash"));
        Assert.Empty(sink.Signals);
        var refused = Assert.Throws<ParserSandboxException>(() => runner.Parse("test.crash", Source("same.txt", "same")));
        Assert.Equal(ParserFailureKind.CrashLoop, refused.Kind);
        Assert.Contains("this source", refused.Message, StringComparison.Ordinal);
        // A different source is still tried.
        Assert.Equal(ParserFailureKind.Crash, Assert.Throws<ParserSandboxException>(() => runner.Parse("test.crash", Source("other.txt", "other"))).Kind);
    }

    [Fact]
    public void The_linux_guard_does_not_signal_the_group_of_a_child_that_has_exited()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/usr/bin/setsid"), "needs Linux setsid");
        var marker = "8" + Random.Shared.Next(100000, 999999) + ".5";
        var info = new ProcessStartInfo("/usr/bin/setsid") { UseShellExecute = false };
        info.ArgumentList.Add("sh");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add($"sleep {marker} & exit 0");
        using var leader = Process.Start(info)!;
        leader.WaitForExit();
        try
        {
            using var guard = LinuxParserConfinement.CreateGuard(leader, ownsGroup: true);

            guard.KillTree();

            Assert.True(ProcessWithArgumentExists(marker), "an exited leader's pid may be reused, so its group must not be signalled");
        }
        finally
        {
            using var cleanup = Process.Start(new ProcessStartInfo("pkill", ["-f", marker]) { UseShellExecute = false });
            cleanup?.WaitForExit();
        }
    }

    [Fact]
    public void A_provider_that_fails_to_attach_ends_the_job_cleanly_and_is_not_a_crash()
    {
        var runner = Runner(o => o with { CrashLoopThreshold = 1 }, confinement: new ThrowingAttachConfinement());

        var error = Fails(() => runner.Parse("bazaar.import.txt", Source()));

        Assert.Equal(ParserFailureKind.ConfinementUnavailable, error.Kind);
        Assert.False(runner.CrashTracker.IsOpen("bazaar.import.txt"));
    }

    [Fact]
    public void The_default_confinement_on_linux_is_the_linux_provider()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux default");

        Assert.IsType<LinuxParserConfinement>(ParserConfinements.CreateDefault());
    }

    [Fact]
    public void The_default_output_cap_is_256_mib()
    {
        Assert.Equal(256L * 1024 * 1024, new ParserSandboxOptions().MaxOutputBytes);
    }

    private sealed class ThrowingAttachConfinement : IParserConfinement
    {
        public string Name => "throwing";

        public ConfinementPlan Prepare(ConfinementLimits limits, LaunchCommand command) =>
            new(command, [], [])
            {
                AttachAfterStart = _ => throw new InvalidProgramException("attach failed"),
            };
    }

    private static void KillProcessesWithArgument(string marker)
    {
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            try
            {
                if (int.TryParse(Path.GetFileName(dir), System.Globalization.CultureInfo.InvariantCulture, out var pid)
                    && pid != Environment.ProcessId
                    && File.ReadAllText(Path.Combine(dir, "cmdline")).Contains(marker, StringComparison.Ordinal))
                {
                    using var process = System.Diagnostics.Process.GetProcessById(pid);
                    process.Kill();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Gone already.
            }
        }
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

        Assert.Equal(ParserFailureKind.LimitExceeded, error.Kind);
    }

    [Fact]
    public void Repeated_crashes_raise_parser_crash_loop_once_and_then_refuse_jobs()
    {
        var sink = new RecordingSink();
        var runner = Runner(o => o with { CrashLoopThreshold = 3 }, sink);

        // Three failures from two distinct sources (the same source twice stays under its cap of three).
        foreach (var name in new[] { "one.txt", "two.txt", "one.txt" })
        {
            Assert.Equal(ParserFailureKind.Crash, Assert.Throws<ParserSandboxException>(() => runner.Parse("test.crash", Source(name, name))).Kind);
        }

        var signal = Assert.Single(sink.Signals);
        Assert.Equal(ParserSignals.CrashLoop, signal.Name);
        Assert.Equal("test.crash", signal.ImporterId);
        var refused = Assert.Throws<ParserSandboxException>(() => runner.Parse("test.crash", Source("three.txt", "three")));
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
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParserSandboxOptions { FrameGapTimeout = TimeSpan.Zero }.Validate());
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
