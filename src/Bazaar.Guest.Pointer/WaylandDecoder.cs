using System.Text;

namespace Bazaar.Guest.Pointer;

/// <summary>One event from the compositor: the object it came from, its opcode and its raw argument bytes.</summary>
internal readonly record struct WaylandEvent(uint ObjectId, ushort Opcode, byte[] Payload)
{
    /// <summary>The 32-bit argument at <paramref name="index"/> (in words).</summary>
    public uint Word(int index) => Native.ReadWord(Payload.AsSpan(index * 4));
}

/// <summary>Reads Wayland events out of a byte stream that may split or join them arbitrarily.</summary>
internal sealed class WaylandDecoder
{
    /// <summary>wl_display.error(object, code, message).</summary>
    public const ushort DisplayError = 0;

    /// <summary>wl_registry.global(name, interface, version).</summary>
    public const ushort RegistryGlobal = 0;

    /// <summary>wl_callback.done(data).</summary>
    public const ushort CallbackDone = 0;

    private readonly List<byte> _pending = [];

    /// <summary>Adds received bytes.</summary>
    public void Feed(ReadOnlySpan<byte> data) => _pending.AddRange(data);

    /// <summary>The next complete event, or <see langword="false"/> when more bytes are needed.</summary>
    /// <exception cref="InvalidDataException">The compositor sent a message that is smaller than its own header.</exception>
    public bool TryRead(out WaylandEvent evt)
    {
        evt = default;
        if (_pending.Count < 8)
            return false;
        byte[] head = [.. _pending.Take(8)];
        uint id = Native.ReadWord(head);
        uint word = Native.ReadWord(head.AsSpan(4));
        int size = (int)(word >> 16);
        if (size < 8 || size % 4 != 0)
            throw new InvalidDataException("The compositor sent a malformed message.");
        if (_pending.Count < size)
            return false;
        byte[] payload = [.. _pending.Skip(8).Take(size - 8)];
        _pending.RemoveRange(0, size);
        evt = new WaylandEvent(id, (ushort)(word & 0xFFFF), payload);
        return true;
    }

    /// <summary>Reads a Wayland string argument at the byte offset <paramref name="offset"/> of <paramref name="payload"/>.</summary>
    /// <exception cref="InvalidDataException">The string does not fit the payload.</exception>
    public static string ReadString(byte[] payload, int offset, out int next)
    {
        if (offset < 0 || payload.Length - offset < 4)
            throw new InvalidDataException("The compositor sent a malformed string.");
        uint length = Native.ReadWord(payload.AsSpan(offset));
        int start = offset + 4;
        if (length == 0 || length > payload.Length - start)
            throw new InvalidDataException("The compositor sent a malformed string.");
        string value = Encoding.UTF8.GetString(payload, start, (int)length - 1);
        next = start + (((int)length + 3) & ~3);
        return value;
    }
}
