using System.Runtime.InteropServices;

namespace Spike.S11.Pty;

// Minimal P/Invoke surface onto the system C library (glibc) for POSIX pseudo-terminals
// and posix_spawn. Written for this spike; no third-party code. libc is provided by the
// operating system and is not redistributed with Bazaar.
internal static partial class Native
{
    internal const int O_RDWR = 0x2;
    internal const int O_NOCTTY = 0x100;
    internal const int O_CLOEXEC = 0x80000;
    internal const nuint TIOCSWINSZ = 0x5414;
    internal const short POLLIN = 0x1;
    internal const short POLLERR = 0x8;
    internal const short POLLHUP = 0x10;
    internal const int WNOHANG = 1;
    internal const int SIGKILL = 9;
    internal const int EINTR = 4;
    internal const int EAGAIN = 11;
    // glibc spawn.h: POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSID
    internal const short SpawnFlags = 0x04 | 0x08 | 0x80;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Winsize
    {
        public ushort Row;
        public ushort Col;
        public ushort XPixel;
        public ushort YPixel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int posix_openpt(int flags);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int grantpt(int fd);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int unlockpt(int fd);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int ptsname_r(int fd, ref byte buf, nuint buflen);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int ioctl(int fd, nuint request, ref Winsize ws);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial nint read(int fd, ref byte buf, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial nint write(int fd, ref byte buf, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int poll(ref PollFd fds, nuint nfds, int timeout);

    // posix_spawn returns the error number directly (0 on success), not -1 with errno.
    [LibraryImport("libc")]
    internal static partial int posix_spawn(out int pid, nint path, nint fileActions, nint attrp, nint argv, nint envp);

    [LibraryImport("libc")]
    internal static partial int posix_spawn_file_actions_init(nint fileActions);

    [LibraryImport("libc")]
    internal static partial int posix_spawn_file_actions_destroy(nint fileActions);

    [LibraryImport("libc")]
    internal static partial int posix_spawn_file_actions_addopen(nint fileActions, int fd, nint path, int oflag, uint mode);

    [LibraryImport("libc")]
    internal static partial int posix_spawn_file_actions_adddup2(nint fileActions, int fd, int newFd);

    [LibraryImport("libc")]
    internal static partial int posix_spawnattr_init(nint attr);

    [LibraryImport("libc")]
    internal static partial int posix_spawnattr_destroy(nint attr);

    [LibraryImport("libc")]
    internal static partial int posix_spawnattr_setflags(nint attr, short flags);

    [LibraryImport("libc")]
    internal static partial int posix_spawnattr_setsigdefault(nint attr, nint sigset);

    [LibraryImport("libc")]
    internal static partial int posix_spawnattr_setsigmask(nint attr, nint sigset);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int waitpid(int pid, out int status, int options);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int kill(int pid, int sig);
}
