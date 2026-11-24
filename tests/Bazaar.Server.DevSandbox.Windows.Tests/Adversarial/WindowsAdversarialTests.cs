using Bazaar.Server.DevSandbox.Windows;

namespace Bazaar.Server.DevSandbox.Windows.Tests.Adversarial;

public sealed class WindowsAdversarialTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-win-adv-" + Guid.NewGuid().ToString("N"));

    public WindowsAdversarialTests()
    {
        Directory.CreateDirectory(_root);
    }

    private sealed class Plan(Func<WindowsLaunchCommand> create) : IWindowsSandboxLaunchPlan
    {
        public WindowsLaunchCommand Create(SandboxRunSpec spec, string workDirectory) => create();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private WindowsConfinementProvider Provider(FakeWindowsApi api, Func<WindowsLaunchCommand> command) => new(
        new WindowsConfinementOptions
        {
            WorkRoot = _root,
            SystemDirectory = "C:\\Windows\\System32",
            SystemRoot = "C:\\Windows",
            SelfTestTimeout = TimeSpan.FromMilliseconds(600),
        },
        new Plan(command),
        api);

    private static SandboxRunSpec Spec(string runId = "run-1") =>
        new(runId, "mod", "1.0.0", SandboxEngineKind.Vm, [], [], 1UL, 30, 128);

    [Theory]
    [InlineData("C:\\Windows\\System32\\cmd.exe.")]
    [InlineData("C:\\Windows\\System32\\cmd.exe ")]
    [InlineData("C:\\Windows\\System32\\CMD.EXE...")]
    [InlineData("C:\\a\\x.bat.")]
    [InlineData("C:\\a\\x.cmd ")]
    [InlineData("C:\\a\\x.exe:stream")]
    [InlineData("C:\\a\\x.exe::$DATA")]
    [InlineData("C:\\Windows\\System32\\CMD~1.EXE")]
    [InlineData("C:\\a\\x.exe\n")]
    [InlineData("C:\\a\\x.exe\r\n")]
    [InlineData("C:\\a\\x.exe\0")]
    [InlineData("C:\\a\\")]
    [InlineData("\\\\?\\C:\\a\\x.exe")]
    [InlineData("\\\\.\\C:\\a\\x.exe")]
    [InlineData("//server/share/x.exe")]
    [InlineData("C:/a/x.exe/../../y.exe")]
    [InlineData("C:\\a\\..\\x.exe")]
    [InlineData("C:\\a\\x.exe|y")]
    [InlineData("C:\\a\\x*.exe")]
    public async Task A_tricky_executable_path_is_refused_before_a_process_exists(string executable)
    {
        var api = new FakeWindowsApi();
        var p = Provider(api, () => new WindowsLaunchCommand(executable, []));
        Assert.True(p.Tested, p.UntestedReason);

        await Assert.ThrowsAnyAsync<Exception>(() => p.StartAsync(Spec(), TestContext.Current.CancellationToken));

        Assert.DoesNotContain("CreateProcessSuspended", api.Calls.Skip(api.Calls.Count - 6));
        Assert.True(api.Jobs[^1].Disposed);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Theory]
    [InlineData("run-1\n")]
    [InlineData("run-1\nx")]
    [InlineData("..")]
    [InlineData("a\\b")]
    [InlineData("RUN")]
    [InlineData("")]
    public async Task A_run_id_that_is_not_plain_is_refused_before_any_api_call(string runId)
    {
        var api = new FakeWindowsApi();
        var p = Provider(api, () => new WindowsLaunchCommand("C:\\Tools\\module.exe", []));
        Assert.True(p.Tested, p.UntestedReason);
        var mark = api.Calls.Count;

        await Assert.ThrowsAnyAsync<ArgumentException>(() => p.StartAsync(Spec(runId), TestContext.Current.CancellationToken));

        Assert.Equal(mark, api.Calls.Count);
    }

    [Fact]
    public async Task The_process_is_suspended_in_the_job_before_it_runs_and_has_no_capabilities_and_a_whitelisted_environment()
    {
        var api = new FakeWindowsApi();
        var p = Provider(api, () => new WindowsLaunchCommand("C:\\Tools\\module.exe", ["x"]));
        Assert.True(p.Tested, p.UntestedReason);
        var mark = api.Calls.Count;
        Environment.SetEnvironmentVariable("BAZAAR_ADV_SECRET", "leak");
        try
        {
            await using var process = await p.StartAsync(Spec(), TestContext.Current.CancellationToken);

            var calls = api.Calls.Skip(mark).ToArray();
            Assert.True(Array.IndexOf(calls, "CreateProcessSuspended") < Array.IndexOf(calls, "Assign"));
            Assert.True(Array.IndexOf(calls, "Assign") < Array.IndexOf(calls, "Resume"));
            var request = api.Requests[^1];
            Assert.Equal(WindowsConfinementMode.AppContainer, request.Mode);
            Assert.NotNull(request.AppContainerSid);
            var names = request.EnvironmentBlock.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(e => e[..e.IndexOf('=', StringComparison.Ordinal)]).Order().ToArray();
            Assert.Equal(["LOCALAPPDATA", "SystemRoot", "TEMP", "TMP"], names);
            Assert.DoesNotContain("BAZAAR_ADV_SECRET", request.EnvironmentBlock, StringComparison.Ordinal);
            var limits = api.Jobs[^1].QueryLimits();
            Assert.True(limits.KillOnJobClose);
            Assert.False(limits.BreakawayAllowed);
            Assert.True(limits.DieOnUnhandledException);
            Assert.True(limits.ActiveProcessLimit > 0);
            Assert.True(limits.ProcessMemoryBytes > 0);
            Assert.True(limits.JobMemoryBytes > 0);
            // Every JOB_OBJECT_UILIMIT_* bit: handles, read and write clipboard, system parameters, display settings, global atoms, desktop, exit windows.
            Assert.Equal(0xFFu, limits.UiRestrictions);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BAZAAR_ADV_SECRET", null);
        }
    }

    [Theory]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a\\", "a\\")]
    [InlineData("a b\\", "\"a b\\\\\"")]
    [InlineData("a\\\\\"b", "\"a\\\\\\\\\\\"b\"")]
    [InlineData("", "\"\"")]
    [InlineData("a\tb", "\"a\tb\"")]
    [InlineData("&calc", "&calc")]
    [InlineData("%PATH%", "%PATH%")]
    public void Classic_quoting_cases_split_back_to_the_same_argument(string argument, string expected)
    {
        Assert.Equal(expected, WindowsCommandLine.Quote(argument));
        Assert.Equal(argument, ParseOne(WindowsCommandLine.Quote(argument)));
    }

    [Fact]
    public void A_quote_in_the_program_name_or_a_nul_anywhere_is_refused()
    {
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Build("C:\\a\"b.exe", []));
        Assert.Throws<ArgumentException>(() => WindowsCommandLine.Build("C:\\a.exe", ["x\0y"]));
    }

    /// <summary>A small CommandLineToArgvW for one argument (the rules the quoting must satisfy).</summary>
    private static string ParseOne(string quoted)
    {
        var result = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < quoted.Length; i++)
        {
            var backslashes = 0;
            while (i < quoted.Length && quoted[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i < quoted.Length && quoted[i] == '"')
            {
                result.Append('\\', backslashes / 2);
                if (backslashes % 2 == 0)
                {
                    inQuotes = !inQuotes;
                }
                else
                {
                    result.Append('"');
                }
            }
            else
            {
                result.Append('\\', backslashes);
                if (i < quoted.Length)
                {
                    result.Append(quoted[i]);
                }
            }
        }

        Assert.False(inQuotes);
        return result.ToString();
    }
}
