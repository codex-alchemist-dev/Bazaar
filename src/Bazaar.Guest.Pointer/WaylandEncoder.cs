using System.Buffers.Binary;
using System.Text;

namespace Bazaar.Guest.Pointer;

/// <summary>
/// Encodes the few Wayland requests this tool sends, as the wire bytes (pure: no socket). A message is a 32-bit object id, a
/// 32-bit word of <c>size &lt;&lt; 16 | opcode</c> (size counts the 8 header bytes), then 32-bit arguments in host byte order.
/// Strings are a length (with the NUL), the bytes, a NUL, padded to 4 bytes. No request here passes a file descriptor.
/// </summary>
internal sealed class WaylandEncoder
{
    /// <summary>The fixed id of <c>wl_display</c>.</summary>
    public const uint DisplayId = 1;

    // wl_display requests.
    private const ushort _displaySync = 0;
    private const ushort _displayGetRegistry = 1;

    // wl_registry request.
    private const ushort _registryBind = 0;

    // zwlr_virtual_pointer_manager_v1 request.
    private const ushort _managerCreateVirtualPointer = 0;

    // zwlr_virtual_pointer_v1 requests.
    private const ushort _pointerMotionAbsolute = 1;
    private const ushort _pointerButton = 2;
    private const ushort _pointerAxis = 3;
    private const ushort _pointerFrame = 4;
    private const ushort _pointerAxisSource = 5;
    private const ushort _pointerAxisDiscrete = 7;
    private const ushort _pointerDestroy = 8;

    private readonly List<byte> _bytes = [];

    /// <summary>Everything encoded so far.</summary>
    public byte[] ToArray() => [.. _bytes];

    /// <summary>Returns the encoded bytes and starts over.</summary>
    public byte[] Take()
    {
        byte[] result = [.. _bytes];
        _bytes.Clear();
        return result;
    }

    /// <summary>wl_display.get_registry(new_id registry).</summary>
    public void GetRegistry(uint registryId) => Message(DisplayId, _displayGetRegistry, registryId);

    /// <summary>wl_display.sync(new_id callback).</summary>
    public void Sync(uint callbackId) => Message(DisplayId, _displaySync, callbackId);

    /// <summary>wl_registry.bind(name, interface, version, new_id): the untyped new_id carries its interface and version.</summary>
    public void Bind(uint registryId, uint name, string iface, uint version, uint newId)
    {
        int start = Begin(registryId, _registryBind);
        Uint(name);
        Str(iface);
        Uint(version);
        Uint(newId);
        End(start);
    }

    /// <summary>zwlr_virtual_pointer_manager_v1.create_virtual_pointer(seat = null, new_id pointer).</summary>
    public void CreateVirtualPointer(uint managerId, uint pointerId) => Message(managerId, _managerCreateVirtualPointer, 0u, pointerId);

    /// <summary>zwlr_virtual_pointer_v1.motion_absolute(time, x, y, x_extent, y_extent).</summary>
    public void MotionAbsolute(uint pointerId, uint time, uint x, uint y, uint xExtent, uint yExtent) =>
        Message(pointerId, _pointerMotionAbsolute, time, x, y, xExtent, yExtent);

    /// <summary>zwlr_virtual_pointer_v1.button(time, button, state).</summary>
    public void Button(uint pointerId, uint time, uint button, bool pressed) => Message(pointerId, _pointerButton, time, button, pressed ? 1u : 0u);

    /// <summary>zwlr_virtual_pointer_v1.axis(time, axis, value) with the value as wl_fixed.</summary>
    public void Axis(uint pointerId, uint time, uint axis, int whole) => Message(pointerId, _pointerAxis, time, axis, unchecked((uint)ToFixed(whole)));

    /// <summary>zwlr_virtual_pointer_v1.axis_source(source).</summary>
    public void AxisSource(uint pointerId, uint source) => Message(pointerId, _pointerAxisSource, source);

    /// <summary>zwlr_virtual_pointer_v1.axis_discrete(time, axis, value as wl_fixed, discrete steps).</summary>
    public void AxisDiscrete(uint pointerId, uint time, uint axis, int whole, int steps) =>
        Message(pointerId, _pointerAxisDiscrete, time, axis, unchecked((uint)ToFixed(whole)), unchecked((uint)steps));

    /// <summary>zwlr_virtual_pointer_v1.frame.</summary>
    public void Frame(uint pointerId) => Message(pointerId, _pointerFrame);

    /// <summary>zwlr_virtual_pointer_v1.destroy.</summary>
    public void DestroyPointer(uint pointerId) => Message(pointerId, _pointerDestroy);

    /// <summary>The wl_fixed (24.8 signed) encoding of a whole number.</summary>
    public static int ToFixed(int whole) => checked(whole * 256);

    private void Message(uint objectId, ushort opcode, params ReadOnlySpan<uint> args)
    {
        int start = Begin(objectId, opcode);
        foreach (uint a in args)
            Uint(a);
        End(start);
    }

    // Writes the object id and the opcode half of the header word; End patches in the size.
    private int Begin(uint objectId, ushort opcode)
    {
        int start = _bytes.Count;
        Uint(objectId);
        Uint(opcode);
        return start;
    }

    private void End(int start)
    {
        int size = _bytes.Count - start;
        if (size > ushort.MaxValue)
            throw new InvalidOperationException("A Wayland message is limited to 65535 bytes.");
        Span<byte> word = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
            word[i] = _bytes[start + 4 + i];
        uint opcode = Native.ReadWord(word);
        Native.WriteWord(word, ((uint)size << 16) | opcode);
        for (int i = 0; i < 4; i++)
            _bytes[start + 4 + i] = word[i];
    }

    private void Uint(uint value)
    {
        Span<byte> word = stackalloc byte[4];
        Native.WriteWord(word, value);
        foreach (byte b in word)
            _bytes.Add(b);
    }

    private void Str(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        Uint((uint)utf8.Length + 1);
        _bytes.AddRange(utf8);
        _bytes.Add(0);
        while (_bytes.Count % 4 != 0)
            _bytes.Add(0);
    }
}

/// <summary>Wayland words are in the host's byte order.</summary>
internal static class Native
{
    public static uint ReadWord(ReadOnlySpan<byte> source) =>
        BitConverter.IsLittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(source) : BinaryPrimitives.ReadUInt32BigEndian(source);

    public static void WriteWord(Span<byte> destination, uint value)
    {
        if (BitConverter.IsLittleEndian)
            BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
        else
            BinaryPrimitives.WriteUInt32BigEndian(destination, value);
    }
}
