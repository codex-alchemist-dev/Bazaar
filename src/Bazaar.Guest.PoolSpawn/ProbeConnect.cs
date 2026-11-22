using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bazaar.Guest.PoolSpawn;

/// <summary>
/// What one connection attempt did. The numbers are the process exit codes of probe mode: 0 means the connection was made, 20 to 23
/// mean the network refused it in some way (the pool host counts those as "blocked"), and anything else, including the exit codes of
/// the tools in front of this one, means the probe could not run and is never read as a block.
/// </summary>
public enum ProbeOutcome
{
    /// <summary>The TCP handshake completed.</summary>
    Connected = 0,

    /// <summary>No answer within the timeout: the typical result of a drop rule.</summary>
    TimedOut = 20,

    /// <summary>The peer answered with a reset.</summary>
    Refused = 21,

    /// <summary>No route to the address, or the network is down.</summary>
    Unreachable = 22,

    /// <summary>The kernel refused to send (EPERM or EACCES, which an output-hook drop can cause).</summary>
    Denied = 23,

    /// <summary>Any other socket error: the probe learned nothing.</summary>
    Error = 70,
}

/// <summary>
/// One non-blocking TCP connect with a deadline, through the same libc P/Invoke pattern as <see cref="Posix"/> so the tool references
/// no networking library. It sends no data and closes at once. Nothing it does is printed. Linux only; the entry point refuses to run
/// elsewhere before it is reached.
/// </summary>
internal static partial class ProbeConnect
{
    private const int _afInet = 2;
    private const int _sockStream = 1;
    private const int _sockNonBlock = 0x800;
    private const int _sockCloExec = 0x80000;
    private const int _solSocket = 1;
    private const int _soError = 4;
    private const short _pollOut = 0x004;

    private const int _eperm = 1;
    private const int _eintr = 4;
    private const int _eacces = 13;
    private const int _enetdown = 100;
    private const int _enetunreach = 101;
    private const int _etimedout = 110;
    private const int _econnrefused = 111;
    private const int _ehostdown = 112;
    private const int _ehostunreach = 113;
    private const int _einprogress = 115;

    /// <summary>Tries to connect to the target. Never throws for a network condition.</summary>
    public static ProbeOutcome Run(ProbeArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var fd = CreateSocket(_afInet, _sockStream | _sockNonBlock | _sockCloExec, 0);
        if (fd < 0)
        {
            return ProbeOutcome.Error;
        }

        try
        {
            // struct sockaddr_in: family (host order), port (network order), address, eight bytes of padding.
            var address = new byte[16];
            address[0] = _afInet;
            address[2] = (byte)(arguments.Port >> 8);
            address[3] = (byte)(arguments.Port & 0xFF);
            arguments.Address.CopyTo(address, 4);

            if (Connect(fd, address, address.Length) == 0)
            {
                return ProbeOutcome.Connected;
            }

            var errno = Marshal.GetLastPInvokeError();
            if (errno != _einprogress)
            {
                return Classify(errno);
            }

            return WaitForConnect(fd, arguments.TimeoutMs);
        }
        finally
        {
            _ = CloseFd(fd);
        }
    }

    /// <summary>Maps a Linux errno to the outcome the pool host reads.</summary>
    public static ProbeOutcome Classify(int errno) => errno switch
    {
        0 => ProbeOutcome.Connected,
        _econnrefused => ProbeOutcome.Refused,
        _etimedout => ProbeOutcome.TimedOut,
        _enetunreach or _ehostunreach or _enetdown or _ehostdown => ProbeOutcome.Unreachable,
        _eperm or _eacces => ProbeOutcome.Denied,
        _ => ProbeOutcome.Error,
    };

    private static ProbeOutcome WaitForConnect(int fd, int timeoutMs)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeoutMs * (Stopwatch.Frequency / 1000.0));
        while (true)
        {
            var remaining = (int)Math.Ceiling((deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency);
            if (remaining <= 0)
            {
                return ProbeOutcome.TimedOut;
            }

            var poll = new PollFd { Fd = fd, Events = _pollOut };
            var ready = Poll(ref poll, 1, remaining);
            if (ready < 0)
            {
                if (Marshal.GetLastPInvokeError() == _eintr)
                {
                    continue;
                }

                return ProbeOutcome.Error;
            }

            if (ready == 0)
            {
                return ProbeOutcome.TimedOut;
            }

            var error = 0;
            var length = (uint)sizeof(int);
            return GetSockOpt(fd, _solSocket, _soError, ref error, ref length) < 0 ? ProbeOutcome.Error : Classify(error);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport("libc", EntryPoint = "socket", SetLastError = true)]
    private static partial int CreateSocket(int domain, int type, int protocol);

    [LibraryImport("libc", EntryPoint = "connect", SetLastError = true)]
    private static partial int Connect(int fd, byte[] address, int length);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int Poll(ref PollFd fds, nuint count, int timeoutMs);

    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static partial int GetSockOpt(int fd, int level, int name, ref int value, ref uint length);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseFd(int fd);
}
