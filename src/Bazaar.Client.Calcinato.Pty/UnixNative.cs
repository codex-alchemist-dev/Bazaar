using System.Runtime.InteropServices;

namespace Bazaar.Client.Calcinato.Pty;

/// <summary>
/// libc declarations for the Unix PTY backend (glibc on Linux). Source-generated P/Invoke. Constants are the Linux
/// values; the backend is Linux-only (see <see cref="UnixPtyBackend.IsSupported"/>).
/// </summary>
internal static unsafe partial class UnixNative
{
    internal const int O_RDWR = 0x2;
    internal const int O_NOCTTY = 0x100;
    internal const int O_CLOEXEC = 0x80000;
    internal const nuint TIOCSWINSZ = 0x5414;

    internal const short POSIX_SPAWN_SETSIGDEF = 0x04;
    internal const short POSIX_SPAWN_SETSIGMASK = 0x08;
    internal const short POSIX_SPAWN_SETSID = 0x80;

    internal const short POLLIN = 0x1;
    internal const int SIGHUP = 1;
    internal const int SIGKILL = 9;
    internal const int EINTR = 4;
    internal const int EAGAIN = 11;

    /// <summary>Size of glibc's <c>sigset_t</c> (1024 bits).</summary>
    internal const int SigsetBytes = 128;

    /// <summary>Generous upper bound for glibc's <c>posix_spawnattr_t</c> (336 bytes) and <c>posix_spawn_file_actions_t</c> (80 bytes).</summary>
    internal const int SpawnAttrBytes = 512;

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_openpt(int flags);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int grantpt(int fd);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int unlockpt(int fd);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int ptsname_r(int fd, byte* buffer, nuint length);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int ioctl(int fd, nuint request, WinSize* size);

    [LibraryImport("libc")]
    internal static partial void close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial nint read(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial nint write(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int poll(PollFd* fds, nuint count, int timeoutMilliseconds);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial void kill(int pid, int signal);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int waitpid(int pid, int* status, int options);

    /// <summary>Returns 0 or an error number (it does not set errno).</summary>
    [LibraryImport("libc")]
    internal static partial int posix_spawn(int* pid, nint path, nint fileActions, nint attributes, nint argv, nint envp);

    [LibraryImport("libc")]
    internal static partial int posix_spawnattr_init(nint attributes);

    [LibraryImport("libc")]
    internal static partial void posix_spawnattr_destroy(nint attributes);

    [LibraryImport("libc")]
    internal static partial int posix_spawnattr_setflags(nint attributes, short flags);

    [LibraryImport("libc")]
    internal static partial int posix_spawnattr_setsigdefault(nint attributes, nint set);

    [LibraryImport("libc")]
    internal static partial int posix_spawnattr_setsigmask(nint attributes, nint set);

    [LibraryImport("libc")]
    internal static partial int posix_spawn_file_actions_init(nint actions);

    [LibraryImport("libc")]
    internal static partial void posix_spawn_file_actions_destroy(nint actions);

    [LibraryImport("libc")]
    internal static partial int posix_spawn_file_actions_addopen(nint actions, int fd, nint path, int oflag, uint mode);

    [LibraryImport("libc")]
    internal static partial int posix_spawn_file_actions_adddup2(nint actions, int fd, int newFd);

    [LibraryImport("libc")]
    internal static partial int posix_spawn_file_actions_addchdir_np(nint actions, nint path);

    [LibraryImport("libc")]
    internal static partial int sigfillset(nint set);

    [LibraryImport("libc")]
    internal static partial int sigemptyset(nint set);
}

/// <summary>struct winsize.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WinSize
{
    public ushort Rows;
    public ushort Columns;
    public ushort XPixels;
    public ushort YPixels;
}

/// <summary>struct pollfd.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PollFd
{
    public int Fd;
    public short Events;
    public short Revents;
}
