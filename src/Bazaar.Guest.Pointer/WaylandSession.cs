using System.Net.Sockets;

namespace Bazaar.Guest.Pointer;

/// <summary>A failure the tool reports as one line on stderr and a non-zero exit.</summary>
internal sealed class PointerToolException(string message) : Exception(message);

/// <summary>
/// Runs one <see cref="PointerCommand"/> against a compositor over its UNIX socket: registry, bind the wlroots virtual pointer
/// manager, send the planned requests, then a <c>wl_display.sync</c> round trip so the compositor has processed them.
/// </summary>
internal static class WaylandSession
{
    /// <summary>The wlr-virtual-pointer-unstable-v1 manager interface.</summary>
    public const string ManagerInterface = "zwlr_virtual_pointer_manager_v1";

    // Object ids the client allocates, in order of use (ids 2 and up belong to the client).
    private const uint _registryId = 2;
    private const uint _firstSyncId = 3;
    private const uint _managerId = 4;
    private const uint _pointerId = 5;
    private const uint _lastSyncId = 6;

    private const int _timeoutMs = 5000;

    /// <summary>The socket path for the two environment values, or throws when they cannot name a socket.</summary>
    public static string SocketPath(string? runtimeDir, string? display)
    {
        if (string.IsNullOrEmpty(runtimeDir) || !Path.IsPathRooted(runtimeDir))
            throw new PointerToolException("XDG_RUNTIME_DIR is not set to an absolute path");
        string name = string.IsNullOrEmpty(display) ? "wayland-0" : display;
        if (name.Contains('/', StringComparison.Ordinal) || name is "." or "..")
            throw new PointerToolException("WAYLAND_DISPLAY must be a socket name");
        return Path.Combine(runtimeDir, name);
    }

    /// <summary>Connects to <paramref name="socketPath"/> and runs <paramref name="command"/>.</summary>
    /// <exception cref="PointerToolException">The compositor is unreachable, refuses, errors or lacks the pointer manager.</exception>
    public static void Run(string socketPath, PointerCommand command)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
        {
            SendTimeout = _timeoutMs,
            ReceiveTimeout = _timeoutMs,
        };
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
        }
        catch (SocketException)
        {
            throw new PointerToolException("cannot connect to the Wayland socket");
        }

        try
        {
            Execute(socket, command);
        }
        catch (SocketException)
        {
            throw new PointerToolException("the Wayland connection failed");
        }
        catch (InvalidDataException e)
        {
            throw new PointerToolException(e.Message);
        }
    }

    private static void Execute(Socket socket, PointerCommand command)
    {
        var decoder = new WaylandDecoder();
        var encoder = new WaylandEncoder();

        encoder.GetRegistry(_registryId);
        encoder.Sync(_firstSyncId);
        SendAll(socket, encoder.Take());

        var globals = new List<(uint Name, string Interface, uint Version)>();
        WaitForDone(socket, decoder, _firstSyncId, e =>
        {
            if (e.ObjectId == _registryId && e.Opcode == WaylandDecoder.RegistryGlobal)
            {
                uint name = e.Word(0);
                string iface = WaylandDecoder.ReadString(e.Payload, 4, out int next);
                if (e.Payload.Length - next < 4)
                    throw new InvalidDataException("The compositor sent a malformed global.");
                globals.Add((name, iface, Native.ReadWord(e.Payload.AsSpan(next))));
            }
        });

        (uint Name, string Interface, uint Version) manager = globals.FirstOrDefault(g => g.Interface == ManagerInterface);
        if (manager.Interface is null)
            throw new PointerToolException($"the compositor does not offer {ManagerInterface}");

        // Version 1 has everything used here; a newer manager still serves it.
        encoder.Bind(_registryId, manager.Name, ManagerInterface, 1, _managerId);
        encoder.CreateVirtualPointer(_managerId, _pointerId);
        SendAll(socket, encoder.Take());

        foreach (PlannedWrite write in PointerPlanner.Plan(command, _pointerId, (uint)Environment.TickCount64))
        {
            SendAll(socket, write.Bytes);
            if (write.DelayAfterMs > 0)
                Thread.Sleep(write.DelayAfterMs);
        }

        encoder.DestroyPointer(_pointerId);
        encoder.Sync(_lastSyncId);
        SendAll(socket, encoder.Take());
        WaitForDone(socket, decoder, _lastSyncId, _ => { });
    }

    // Reads events until the callback with the given id is done. A wl_display.error ends the run with its message.
    private static void WaitForDone(Socket socket, WaylandDecoder decoder, uint callbackId, Action<WaylandEvent> onEvent)
    {
        byte[] buffer = new byte[4096];
        while (true)
        {
            while (decoder.TryRead(out WaylandEvent e))
            {
                if (e.ObjectId == WaylandEncoder.DisplayId && e.Opcode == WaylandDecoder.DisplayError)
                {
                    string message = e.Payload.Length >= 12 ? WaylandDecoder.ReadString(e.Payload, 8, out _) : "unknown error";
                    throw new PointerToolException($"the compositor reported an error: {message}");
                }
                if (e.ObjectId == callbackId && e.Opcode == WaylandDecoder.CallbackDone)
                    return;
                onEvent(e);
            }

            int read = socket.Receive(buffer);
            if (read == 0)
                throw new PointerToolException("the compositor closed the connection");
            decoder.Feed(buffer.AsSpan(0, read));
        }
    }

    private static void SendAll(Socket socket, byte[] bytes)
    {
        int sent = 0;
        while (sent < bytes.Length)
            sent += socket.Send(bytes, sent, bytes.Length - sent, SocketFlags.None);
    }
}
