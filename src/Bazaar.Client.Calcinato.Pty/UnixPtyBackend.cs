using System.Collections;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Bazaar.Client.Calcinato.Abstractions;

namespace Bazaar.Client.Calcinato.Pty;

/// <summary>
/// Local shells on a Unix pseudo terminal: <c>posix_openpt</c>, the slave opened by the child as its controlling
/// terminal in a new session (<c>POSIX_SPAWN_SETSID</c>), and <c>TIOCSWINSZ</c> for resize. Supported on Linux only;
/// the approach follows the S11 Linux harness (docs/spikes/S11.md).
/// </summary>
public sealed class UnixPtyBackend : IPtyBackend
{
    /// <summary>True on Linux. Other Unix systems are not covered by the spike and return false.</summary>
    public bool IsSupported => OperatingSystem.IsLinux();

    /// <inheritdoc />
    public IPtySession Open(TerminalSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("The Unix PTY backend runs on Linux only.");
        }

        var shell = ShellResolver.Resolve(spec.Shell, isWindows: false);
        var cwd = string.IsNullOrWhiteSpace(spec.WorkingDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : spec.WorkingDirectory;
        var env = EnvironmentFilter.Build(
            InheritedEnvironment(),
            spec.Environment,
            StringComparer.Ordinal);
        if (!env.ContainsKey("TERM"))
        {
            env["TERM"] = "xterm-256color";
        }

        var master = UnixNative.posix_openpt(UnixNative.O_RDWR | UnixNative.O_NOCTTY | UnixNative.O_CLOEXEC);
        if (master < 0)
        {
            throw Errno("posix_openpt");
        }

        try
        {
            if (UnixNative.grantpt(master) != 0 || UnixNative.unlockpt(master) != 0)
            {
                throw Errno("grantpt/unlockpt");
            }

            var slave = SlavePath(master);
            SetSize(master, spec.Cols, spec.Rows);
            var pid = Spawn(shell, cwd, slave, env);
            return new UnixPtySession(master, pid);
        }
        catch
        {
            UnixNative.close(master);
            throw;
        }
    }

    private static Dictionary<string, string> InheritedEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                result[name] = value;
            }
        }

        return result;
    }

    private static unsafe string SlavePath(int master)
    {
        var buffer = stackalloc byte[256];
        if (UnixNative.ptsname_r(master, buffer, 256) != 0)
        {
            throw Errno("ptsname_r");
        }

        return Marshal.PtrToStringUTF8((nint)buffer) ?? throw new InvalidOperationException("ptsname_r returned no path.");
    }

    internal static unsafe void SetSize(int fd, int cols, int rows)
    {
        var size = new WinSize
        {
            Columns = (ushort)Math.Clamp(cols, 1, ushort.MaxValue),
            Rows = (ushort)Math.Clamp(rows, 1, ushort.MaxValue),
        };
        if (UnixNative.ioctl(fd, UnixNative.TIOCSWINSZ, &size) != 0)
        {
            throw Errno("ioctl(TIOCSWINSZ)");
        }
    }

    private static unsafe int Spawn(string shell, string cwd, string slave, IReadOnlyDictionary<string, string> env)
    {
        var arena = new NativeArena();
        try
        {
            var attributes = arena.Alloc(UnixNative.SpawnAttrBytes);
            var actions = arena.Alloc(UnixNative.SpawnAttrBytes);
            var defaults = arena.Alloc(UnixNative.SigsetBytes);
            var emptyMask = arena.Alloc(UnixNative.SigsetBytes);

            Check(UnixNative.posix_spawnattr_init(attributes), "posix_spawnattr_init");
            Check(UnixNative.posix_spawn_file_actions_init(actions), "posix_spawn_file_actions_init");
            try
            {
                // Signal dispositions: every signal back to its default, and an empty mask. A .NET host ignores SIGPIPE,
                // and an ignored disposition survives exec, so the shell would otherwise inherit it.
                Check(UnixNative.sigfillset(defaults), "sigfillset");
                Check(UnixNative.sigemptyset(emptyMask), "sigemptyset");
                Check(UnixNative.posix_spawnattr_setflags(
                    attributes,
                    UnixNative.POSIX_SPAWN_SETSID | UnixNative.POSIX_SPAWN_SETSIGDEF | UnixNative.POSIX_SPAWN_SETSIGMASK),
                    "posix_spawnattr_setflags");
                Check(UnixNative.posix_spawnattr_setsigdefault(attributes, defaults), "posix_spawnattr_setsigdefault");
                Check(UnixNative.posix_spawnattr_setsigmask(attributes, emptyMask), "posix_spawnattr_setsigmask");

                // The child opens the slave without O_NOCTTY after setsid, so the slave becomes its controlling terminal.
                Check(UnixNative.posix_spawn_file_actions_addchdir_np(actions, arena.Utf8(cwd)), "addchdir_np");
                Check(UnixNative.posix_spawn_file_actions_addopen(actions, 0, arena.Utf8(slave), UnixNative.O_RDWR, 0), "addopen");
                Check(UnixNative.posix_spawn_file_actions_adddup2(actions, 0, 1), "adddup2 stdout");
                Check(UnixNative.posix_spawn_file_actions_adddup2(actions, 0, 2), "adddup2 stderr");

                var argv = arena.StringArray([shell]);
                var envp = arena.StringArray(env.Select(pair => $"{pair.Key}={pair.Value}").ToArray());

                int pid;
                var rc = UnixNative.posix_spawn(&pid, arena.Utf8(shell), actions, attributes, argv, envp);
                if (rc != 0)
                {
                    throw new InvalidOperationException($"posix_spawn failed with error {rc}.");
                }

                return pid;
            }
            finally
            {
                UnixNative.posix_spawn_file_actions_destroy(actions);
                UnixNative.posix_spawnattr_destroy(attributes);
            }
        }
        finally
        {
            arena.Dispose();
        }
    }

    private static void Check(int rc, string call)
    {
        if (rc != 0)
        {
            throw new InvalidOperationException($"{call} failed with error {rc}.");
        }
    }

    private static InvalidOperationException Errno(string call)
    {
        var error = Marshal.GetLastPInvokeError();
        return new InvalidOperationException($"{call} failed: {Marshal.GetPInvokeErrorMessage(error)} (errno {error}).");
    }

    /// <summary>Unmanaged memory owned for the duration of one spawn. Everything is freed on dispose.</summary>
    private sealed unsafe class NativeArena : IDisposable
    {
        private readonly List<nint> _nativeBlocks = [];
        private readonly List<nint> _coTaskBlocks = [];

        public nint Alloc(int bytes)
        {
            var block = (nint)NativeMemory.AllocZeroed((nuint)bytes);
            _nativeBlocks.Add(block);
            return block;
        }

        public nint Utf8(string value)
        {
            var pointer = Marshal.StringToCoTaskMemUTF8(value);
            _coTaskBlocks.Add(pointer);
            return pointer;
        }

        public nint StringArray(string[] values)
        {
            var array = (nint*)NativeMemory.AllocZeroed((nuint)(values.Length + 1) * (nuint)sizeof(nint));
            _nativeBlocks.Add((nint)array);
            for (var i = 0; i < values.Length; i++)
            {
                array[i] = Utf8(values[i]);
            }

            array[values.Length] = nint.Zero;
            return (nint)array;
        }

        public void Dispose()
        {
            foreach (var block in _nativeBlocks)
            {
                NativeMemory.Free((void*)block);
            }

            foreach (var block in _coTaskBlocks)
            {
                Marshal.FreeCoTaskMem(block);
            }

            _nativeBlocks.Clear();
            _coTaskBlocks.Clear();
        }
    }
}
