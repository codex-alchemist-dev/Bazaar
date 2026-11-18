using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bazaar.Vms.Sockets;

/// <summary>
/// AF_VSOCK through libc. .NET refuses the family: <c>SocketAddress</c> throws PlatformNotSupportedException for it on Unix, so the sockets are
/// plain file descriptors here and <see cref="VsockStream"/> reads and writes them. This file is also compiled into the guest agent.
/// </summary>
internal static partial class LinuxVsock
{
    /// <summary>AF_VSOCK.</summary>
    public const int Family = 40;

    /// <summary>VMADDR_CID_ANY: bind to every CID.</summary>
    public const uint AnyCid = 0xFFFFFFFF;

    /// <summary>VMADDR_CID_HOST: the host, as a guest sees it.</summary>
    public const uint HostCid = 2;

    /// <summary>Size of <c>sockaddr_vm</c> in bytes.</summary>
    public const int AddressSize = 16;

    private const int _sockStream = 1;
    private const int _sockCloexec = 0x80000;
    private const int _sockNonblock = 0x800;
    private const int _shutRdwr = 2;
    private const short _pollIn = 0x1;
    private const short _pollErr = 0x8;
    private const short _pollHup = 0x10;
    private const int _eintr = 4;
    private const int _eagain = 11;
    private const int _enomem = 12;
    private const int _emfile = 24;
    private const int _enfile = 23;
    private const int _eproto = 71;
    private const int _econnaborted = 103;
    private const int _enobufs = 105;
    private const int _msgNosignal = 0x4000;

    /// <summary>Writes a <c>sockaddr_vm</c>. Linux is little-endian on every CPU .NET supports.</summary>
    public static void EncodeAddress(uint cid, uint port, Span<byte> destination)
    {
        destination[..AddressSize].Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(destination, Family);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], port);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], cid);
    }

    /// <summary>Reads the CID and port from a <c>sockaddr_vm</c>. False when it is not an AF_VSOCK address.</summary>
    public static bool TryDecodeAddress(ReadOnlySpan<byte> source, out uint cid, out uint port)
    {
        cid = 0;
        port = 0;
        if (source.Length < AddressSize || BinaryPrimitives.ReadUInt16LittleEndian(source) != Family)
        {
            return false;
        }

        port = BinaryPrimitives.ReadUInt32LittleEndian(source[4..]);
        cid = BinaryPrimitives.ReadUInt32LittleEndian(source[8..]);
        return true;
    }

    /// <summary>True when this machine can create an AF_VSOCK socket: Linux with the vsock transport loaded.</summary>
    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            using var handle = new FdHandle(socket(Family, _sockStream | _sockCloexec, 0));
            return !handle.IsInvalid;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Binds and listens on <paramref name="port"/> for every CID.</summary>
    /// <exception cref="IOException">A libc call failed.</exception>
    public static FdHandle Listen(uint port, int backlog)
    {
        // Non-blocking, so an accept that loses a race with a client that gave up returns instead of blocking the loop.
        var handle = Create(_sockNonblock);
        try
        {
            Span<byte> address = stackalloc byte[AddressSize];
            EncodeAddress(AnyCid, port, address);
            Check(bind(handle, address, AddressSize), "bind");
            Check(listen(handle, backlog), "listen");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Connects out to <paramref name="cid"/> and <paramref name="port"/>. Blocks until connected or refused.</summary>
    /// <exception cref="IOException">The connection failed.</exception>
    public static FdHandle Connect(uint cid, uint port)
    {
        var handle = Create(0);
        try
        {
            Span<byte> address = stackalloc byte[AddressSize];
            EncodeAddress(cid, port, address);
            int result;
            do
            {
                result = connect(handle, address, AddressSize);
            }
            while (result < 0 && Marshal.GetLastPInvokeError() == _eintr);
            Check(result, "connect");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for a connection. Returns null on timeout, so the caller can check its cancellation and wait again.
    /// </summary>
    /// <exception cref="IOException">The listener failed.</exception>
    public static (FdHandle Handle, uint PeerCid)? Accept(FdHandle listener, int timeoutMs)
    {
        if (!WaitReadable(listener, timeoutMs))
        {
            return null;
        }

        Span<byte> address = stackalloc byte[AddressSize];
        uint length = AddressSize;
        int fd;
        do
        {
            fd = accept4(listener, address, ref length, _sockCloexec);
        }
        while (fd < 0 && Marshal.GetLastPInvokeError() == _eintr);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            switch (errno)
            {
                case _eagain:
                case _econnaborted:
                case _eproto:
                    // The client left before we got to it. Nothing to accept; the caller waits again.
                    return null;
                case _emfile:
                case _enfile:
                case _enomem:
                case _enobufs:
                    // Out of descriptors or memory is transient. Pause so the still-pending connection does not spin the loop.
                    Thread.Sleep(50);
                    return null;
                default:
                    throw new IOException($"vsock accept failed with errno {errno}.");
            }
        }

        var accepted = new FdHandle(fd);
        return TryDecodeAddress(address[..(int)Math.Min(length, AddressSize)], out var cid, out _)
            ? (accepted, cid)
            : throw CloseAndFail(accepted, "accept returned an address that is not AF_VSOCK");
    }

    /// <summary>Waits until the descriptor has data, an error or a hang-up. False on timeout.</summary>
    public static bool WaitReadable(FdHandle handle, int timeoutMs)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var poll = new PollFd { Fd = (int)handle.DangerousGetHandle(), Events = _pollIn };
            int ready;
            do
            {
                ready = poll_(ref poll, 1, timeoutMs);
            }
            while (ready < 0 && Marshal.GetLastPInvokeError() == _eintr);
            Check(ready, "poll");
            return ready > 0 && (poll.Revents & (_pollIn | _pollErr | _pollHup)) != 0;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>Reads into <paramref name="buffer"/>. Returns 0 at end of stream.</summary>
    public static int Receive(FdHandle handle, Span<byte> buffer)
    {
        nint count;
        do
        {
            count = recv(handle, buffer, (nuint)buffer.Length, 0);
        }
        while (count < 0 && Marshal.GetLastPInvokeError() == _eintr);
        Check((int)count, "recv");
        return (int)count;
    }

    /// <summary>Writes all of <paramref name="buffer"/>.</summary>
    public static void SendAll(FdHandle handle, ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            nint count;
            do
            {
                count = send(handle, buffer, (nuint)buffer.Length, _msgNosignal);
            }
            while (count < 0 && Marshal.GetLastPInvokeError() == _eintr);
            Check((int)count, "send");
            buffer = buffer[(int)count..];
        }
    }

    /// <summary>Shuts both directions down, which wakes a reader blocked on the descriptor.</summary>
    public static void Shutdown(FdHandle handle)
    {
        if (!handle.IsClosed)
        {
            // The result is ignored on purpose: shutting down an already-dead peer fails, and the handle is disposed next.
            _ = shutdown(handle, _shutRdwr);
        }
    }

    /// <summary>
    /// Wraps an already-open descriptor, for tests that stand in a Unix socket pair for a vsock connection.
    /// The handle owns the descriptor.
    /// </summary>
    public static FdHandle Wrap(int fd) => new(fd);

    private static FdHandle Create(int extraFlags)
    {
        var handle = new FdHandle(socket(Family, _sockStream | _sockCloexec | extraFlags, 0));
        if (handle.IsInvalid)
        {
            var errno = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException($"AF_VSOCK is not available on this machine (socket failed with errno {errno}).");
        }

        return handle;
    }

    private static void Check(int result, string call)
    {
        if (result < 0)
        {
            throw new IOException($"vsock {call} failed with errno {Marshal.GetLastPInvokeError()}.");
        }
    }

    private static IOException CloseAndFail(FdHandle handle, string message)
    {
        handle.Dispose();
        return new IOException(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int socket(int domain, int type, int protocol);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int bind(FdHandle fd, Span<byte> address, uint length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int listen(FdHandle fd, int backlog);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int connect(FdHandle fd, Span<byte> address, uint length);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int accept4(FdHandle fd, Span<byte> address, ref uint length, int flags);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int poll_(ref PollFd fds, nuint count, int timeoutMs);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint recv(FdHandle fd, Span<byte> buffer, nuint length, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial nint send(FdHandle fd, ReadOnlySpan<byte> buffer, nuint length, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int shutdown(FdHandle fd, int how);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int close_(int fd);

    /// <summary>A file descriptor that closes itself. Passing it to a libc call keeps it open for the duration of the call.</summary>
    internal sealed class FdHandle : SafeHandleMinusOneIsInvalid
    {
        /// <summary>Wraps a descriptor.</summary>
        public FdHandle(int fd) : base(ownsHandle: true)
        {
            SetHandle(fd);
        }

        /// <inheritdoc />
        protected override bool ReleaseHandle() => close_((int)handle) == 0;
    }
}
