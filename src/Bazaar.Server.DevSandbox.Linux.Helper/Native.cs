using System.Runtime.InteropServices;

namespace Bazaar.Server.DevSandbox.Linux.Helper;

/// <summary>
/// Raw libc entry points called through function pointers. They are resolved once, before any restriction is applied, so the calls made
/// after Landlock and seccomp need no marshalling stub, no new assembly and no file access.
/// </summary>
internal static unsafe class Native
{
    internal const int PrSetNoNewPrivs = 38;
    internal const int PrGetNoNewPrivs = 39;
    internal const int ORdonly = 0;
    internal const int OPath = 0x200000;
    internal const int OCloexec = 0x80000;
    internal const int RlimitCore = 4;
    internal const int RlimitAs = 9;
    internal const uint CloseRangeCloexec = 4;

    private static nint _syscall;
    private static nint _prctl;
    private static nint _open;
    private static nint _close;
    private static nint _setrlimit;
    private static nint _execve;
    private static nint _write;
    private static nint _exit;
    private static nint _socket;

    /// <summary>Resolves every function; returns an error text, or null when all were found.</summary>
    internal static string? Initialize()
    {
        nint lib;
        if (!NativeLibrary.TryLoad("libc.so.6", out lib) && !NativeLibrary.TryLoad("libc", out lib))
        {
            return "cannot load libc";
        }

        if (!Resolve(lib, "syscall", out _syscall) || !Resolve(lib, "prctl", out _prctl) || !Resolve(lib, "open", out _open)
            || !Resolve(lib, "close", out _close) || !Resolve(lib, "setrlimit", out _setrlimit) || !Resolve(lib, "execve", out _execve)
            || !Resolve(lib, "write", out _write) || !Resolve(lib, "_exit", out _exit) || !Resolve(lib, "socket", out _socket))
        {
            return "libc is missing a required function";
        }

        return null;
    }

    private static bool Resolve(nint lib, string name, out nint address) => NativeLibrary.TryGetExport(lib, name, out address);

    /// <summary>The errno of the last failed call. Read it immediately after the call.</summary>
    internal static int Errno => Marshal.GetLastSystemError();

    internal static long Syscall(long number, long a1 = 0, long a2 = 0, long a3 = 0, long a4 = 0, long a5 = 0) =>
        ((delegate* unmanaged<long, long, long, long, long, long, long>)_syscall)(number, a1, a2, a3, a4, a5);

    internal static int Prctl(int option, ulong a2 = 0, ulong a3 = 0, ulong a4 = 0, ulong a5 = 0) =>
        ((delegate* unmanaged<int, ulong, ulong, ulong, ulong, int>)_prctl)(option, a2, a3, a4, a5);

    internal static int Open(byte* path, int flags) => ((delegate* unmanaged<byte*, int, int>)_open)(path, flags);

    internal static int Close(int fd) => ((delegate* unmanaged<int, int>)_close)(fd);

    internal static int SetRlimit(int resource, ulong soft, ulong hard)
    {
        var limit = stackalloc ulong[2];
        limit[0] = soft;
        limit[1] = hard;
        return ((delegate* unmanaged<int, ulong*, int>)_setrlimit)(resource, limit);
    }

    internal static int Execve(byte* path, byte** argv, byte** envp) =>
        ((delegate* unmanaged<byte*, byte**, byte**, int>)_execve)(path, argv, envp);

    internal static nint Write(int fd, byte* buffer, nuint count) =>
        ((delegate* unmanaged<int, byte*, nuint, nint>)_write)(fd, buffer, count);

    internal static int Socket(int domain, int type, int protocol) =>
        ((delegate* unmanaged<int, int, int, int>)_socket)(domain, type, protocol);

    internal static void Exit(int code)
    {
        ((delegate* unmanaged<int, void>)_exit)(code);
    }

    /// <summary>Writes a UTF-8 text to fd 2 using only the raw write call (safe under the seccomp filter).</summary>
    internal static void WriteError(byte[] message)
    {
        fixed (byte* p = message)
        {
            _ = Write(2, p, (nuint)message.Length);
        }
    }
}
