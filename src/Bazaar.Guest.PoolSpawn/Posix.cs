using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Bazaar.Guest.PoolSpawn;

/// <summary>The libc calls the tool uses. Linux only; the entry point refuses to run elsewhere before any of them is reached.</summary>
internal sealed partial class Posix : IFdOps
{
    private const int _readOnly = 0;
    private const int _writeOnly = 1;
    private const int _closeOnExec = 0x80000; // O_CLOEXEC on Linux (the same value on x86-64 and arm64)
    private const int _noFollowX86 = 0x20000; // O_NOFOLLOW on x86-64
    private const int _noFollowArm = 0x8000; // O_NOFOLLOW on arm and arm64
    private const int _setFd = 2; // F_SETFD
    private const int _dupFdCloExec = 1030; // F_DUPFD_CLOEXEC
    private const int _enoent = 2;

    /// <summary>Opens a FIFO read-only (blocks until a writer exists). Returns the descriptor, close-on-exec.</summary>
    public static int OpenForRead(string path) => Open(path, _readOnly);

    /// <summary>Opens a FIFO write-only (blocks until a reader exists). Returns the descriptor, close-on-exec.</summary>
    public static int OpenForWrite(string path) => Open(path, _writeOnly);

    /// <inheritdoc />
    public int DuplicateAtLeast(int fd, int minimum)
    {
        var result = Fcntl(fd, _dupFdCloExec, minimum);
        return result < 0 ? throw Fail("fcntl") : result;
    }

    /// <inheritdoc />
    public void Duplicate2(int source, int target)
    {
        if (Dup2(source, target) < 0)
        {
            throw Fail("dup2");
        }

        // dup2 clears close-on-exec on a new descriptor, but be explicit: the browser must still hold 3 and 4 after execve.
        if (Fcntl(target, _setFd, 0) < 0)
        {
            throw Fail("fcntl");
        }
    }

    /// <inheritdoc />
    public void Close(int fd)
    {
        if (CloseFd(fd) < 0)
        {
            throw Fail("close");
        }
    }

    /// <summary>Replaces this process with the program. Returns only on failure, with the errno.</summary>
    public static int Exec(string program, IReadOnlyList<string> argv, IReadOnlyList<string> environment)
    {
        var argvBlock = Allocate(argv);
        var envBlock = Allocate(environment);
        var programPtr = Marshal.StringToCoTaskMemUTF8(program);
        try
        {
            _ = ExecVe(programPtr, argvBlock, envBlock);
            return Marshal.GetLastPInvokeError();
        }
        finally
        {
            // Only reached when execve failed.
            Marshal.FreeCoTaskMem(programPtr);
            Free(argvBlock);
            Free(envBlock);
        }
    }

    /// <summary>True when an exec errno means "no such program".</summary>
    public static bool IsNotFound(int errno) => errno == _enoent;

    private static nint[] Allocate(IReadOnlyList<string> values)
    {
        var block = new nint[values.Count + 1];
        for (var i = 0; i < values.Count; i++)
        {
            block[i] = Marshal.StringToCoTaskMemUTF8(values[i]);
        }

        return block;
    }

    private static void Free(nint[] block)
    {
        foreach (var p in block)
        {
            if (p != 0)
            {
                Marshal.FreeCoTaskMem(p);
            }
        }
    }

    private static int Open(string path, int access)
    {
        var noFollow = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? _noFollowArm : _noFollowX86;
        var fd = OpenFile(path, access | _closeOnExec | noFollow);
        return fd < 0 ? throw Fail("open") : fd;
    }

    private static Win32Exception Fail(string call) => new(Marshal.GetLastPInvokeError(), call + " failed");

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenFile(string path, int flags);

    [LibraryImport("libc", EntryPoint = "dup2", SetLastError = true)]
    private static partial int Dup2(int oldFd, int newFd);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int Fcntl(int fd, int command, int argument);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseFd(int fd);

    [LibraryImport("libc", EntryPoint = "execve", SetLastError = true)]
    private static partial int ExecVe(nint path, nint[] argv, nint[] envp);
}
