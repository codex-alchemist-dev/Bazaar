using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Portability.Sandbox.Tests.Adversarial;

/// <summary>
/// Replaces the parser host with a shell script, through the confinement seam (the plan's command is ours). The
/// script plays a parser child that has been exploited and now lies over the protocol.
/// </summary>
internal sealed class HostileChild : IParserConfinement
{
    private readonly string _script;
    private readonly bool _viaLinuxProvider;

    public HostileChild(string script, bool viaLinuxProvider = false)
    {
        _script = script;
        _viaLinuxProvider = viaLinuxProvider;
    }

    public string Name => "hostile-child";

    public int Disposed;
    public int Killed;
    public List<IReadOnlyList<string>> Prepared { get; } = [];

    public ConfinementPlan Prepare(ConfinementLimits limits, LaunchCommand command)
    {
        Prepared.Add(command.Arguments);
        var fake = new LaunchCommand("/bin/sh", ["-c", _script]);
        if (_viaLinuxProvider)
        {
            var inner = new LinuxParserConfinement().Prepare(limits, fake);
            return inner with { AttachAfterStart = p => new CountingGuard(this, inner.AttachAfterStart!(p)) };
        }
        return new ConfinementPlan(fake, ["process-group-kill", "no-network"], [])
        {
            AttachAfterStart = p => new CountingGuard(this, new TreeGuard(p)),
        };
    }

    public static ParserSandboxRunner Runner(HostileChild child, Func<ParserSandboxOptions, ParserSandboxOptions>? tune = null)
    {
        var options = new ParserSandboxOptions { RequiredConfinement = [], JobTimeout = TimeSpan.FromSeconds(20), FrameGapTimeout = TimeSpan.FromSeconds(2) };
        if (tune is not null) options = tune(options);
        return new ParserSandboxRunner(options, child);
    }

    public static byte[] Frame(string json) => Frame(Encoding.UTF8.GetBytes(json));

    public static byte[] Frame(byte[] body)
    {
        var bytes = new byte[body.Length + 4];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), body.Length);
        body.CopyTo(bytes, 4);
        return bytes;
    }

    public static string RecordJson(string body = "b") => JsonSerializer.Serialize(new
    {
        type = "record",
        record = new { sourceKey = "k", kind = "document", title = "t", body, attributes = new Dictionary<string, string>() },
    });

    public const string DoneZero = "{\"type\":\"done\",\"count\":0}";

    /// <summary>Writes bytes to a temp file and returns a script fragment that sends them on stdout.</summary>
    public static string Send(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "bazaar-hostile-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, bytes);
        return $"cat '{path}'; rm -f '{path}';";
    }

    public static bool ProcessWithArgumentExists(string marker)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var found = Directory.EnumerateDirectories("/proc").Any(d =>
            {
                try
                {
                    return File.ReadAllText(Path.Combine(d, "cmdline")).Contains(marker, StringComparison.Ordinal);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            });
            if (!found) return false;
            Thread.Sleep(100);
        }
        return true;
    }

    /// <summary>Test clean-up: ends every process whose command line holds <paramref name="marker"/>.</summary>
    public static void KillProcessesWithArgument(string marker)
    {
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            try
            {
                if (int.TryParse(Path.GetFileName(directory), out var pid)
                    && File.ReadAllText(Path.Combine(directory, "cmdline")).Contains(marker, StringComparison.Ordinal))
                {
                    using var process = Process.GetProcessById(pid);
                    process.Kill();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Gone already.
            }
        }
    }

    private sealed class TreeGuard : IProcessGuard
    {
        private readonly Process _process;

        public TreeGuard(Process process)
        {
            _process = process;
        }

        public void KillTree()
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Gone already.
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class CountingGuard : IProcessGuard
    {
        private readonly HostileChild _owner;
        private readonly IProcessGuard _inner;

        public CountingGuard(HostileChild owner, IProcessGuard inner)
        {
            _owner = owner;
            _inner = inner;
        }

        public void KillTree()
        {
            Interlocked.Increment(ref _owner.Killed);
            _inner.KillTree();
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _owner.Disposed);
            _inner.Dispose();
        }
    }
}
