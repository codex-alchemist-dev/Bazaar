using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.DataVolume.Linux;

/// <summary>One line of <c>/proc/self/mountinfo</c>, reduced to what the provider needs.</summary>
internal sealed record MountInfoEntry(string MajorMinor, string Root, string MountPoint);

/// <summary>Pure parser over mountinfo text, so it is tested without a device. Lines of the wrong shape are skipped.</summary>
internal static class MountInfoParser
{
    public static IEnumerable<MountInfoEntry> Parse(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split(' ');
            if (fields.Length < 6 || !fields[2].Contains(':', StringComparison.Ordinal))
                continue;
            yield return new MountInfoEntry(fields[2], Unescape(fields[3]), Unescape(fields[4]));
        }
    }

    /// <summary>The visible mount at exactly <paramref name="path"/>: the last entry wins because a later mount shadows an earlier one.</summary>
    public static MountInfoEntry? FindMountAt(string text, string path)
    {
        var wanted = LinuxPaths.Normalise(path);
        MountInfoEntry? found = null;
        foreach (var entry in Parse(text))
        {
            if (string.Equals(entry.MountPoint, wanted, StringComparison.Ordinal))
                found = entry;
        }

        return found;
    }

    /// <summary>Decodes the kernel's octal escapes (<c>\040</c> for a space, <c>\134</c> for a backslash). Anything else stays as it is.</summary>
    public static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
            return value;
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && IsOctal(value, i + 1))
            {
                builder.Append((char)Convert.ToInt32(value.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                builder.Append(value[i]);
            }
        }

        return builder.ToString();
    }

    private static bool IsOctal(string value, int start)
    {
        if (start + 3 > value.Length)
            return false;
        for (var i = start; i < start + 3; i++)
        {
            if (value[i] is < '0' or > '7')
                return false;
        }

        return true;
    }
}

internal static class DmUuid
{
    private const string _luks2Prefix = "CRYPT-LUKS2-";

    /// <summary>
    /// Reads a device-mapper uuid. A LUKS2 mapping is <c>CRYPT-LUKS2-&lt;32 hex&gt;-&lt;name&gt;</c>; the 32 hex digits are the LUKS UUID
    /// without dashes. Returns the UUID in canonical dashed lowercase form, or null for LUKS1, plain, LVM or anything else.
    /// </summary>
    public static string? TryGetLuks2Uuid(string? dmUuid)
    {
        if (dmUuid is null)
            return null;
        var text = dmUuid.Trim();
        if (!text.StartsWith(_luks2Prefix, StringComparison.Ordinal) || text.Length < _luks2Prefix.Length + 33)
            return null;
        var hex = text.AsSpan(_luks2Prefix.Length, 32);
        foreach (var c in hex)
        {
            if (!char.IsAsciiHexDigit(c))
                return null;
        }

        if (text[_luks2Prefix.Length + 32] != '-')
            return null;
        return Guid.ParseExact(hex, "N").ToString("D", CultureInfo.InvariantCulture);
    }
}

/// <summary>Reads the facts the guard needs from a LUKS2 binary header and its JSON area, with every length bounded.</summary>
internal static class Luks2Header
{
    /// <summary>The size of the binary header; the JSON area starts right after it.</summary>
    public const int BinaryHeaderSize = 4096;

    /// <summary>The LUKS2 specification allows header sizes of 16 KiB up to 4 MiB. Anything else is not read.</summary>
    public const int MaxHeaderSize = 4 * 1024 * 1024;

    private const int _minHeaderSize = 16 * 1024;
    private const int _uuidOffset = 168;
    private const int _uuidLength = 40;
    private static readonly byte[] _magic = [(byte)'L', (byte)'U', (byte)'K', (byte)'S', 0xba, 0xbe];

    /// <summary>Validates magic, version and size from the first 4096 bytes and returns the total header size to read, or null.</summary>
    public static int? TryGetHeaderSize(ReadOnlySpan<byte> first)
    {
        if (first.Length < BinaryHeaderSize || !first[.._magic.Length].SequenceEqual(_magic))
            return null;
        if (BinaryPrimitives.ReadUInt16BigEndian(first[6..]) != 2)
            return null;
        var size = BinaryPrimitives.ReadUInt64BigEndian(first[8..]);
        if (size < _minHeaderSize || size > MaxHeaderSize || size % BinaryHeaderSize != 0)
            return null;
        return (int)size;
    }

    /// <summary>The LUKS UUID from the binary header, canonical dashed lowercase, or null.</summary>
    public static string? ReadUuid(ReadOnlySpan<byte> header)
    {
        if (TryGetHeaderSize(header) is null)
            return null;
        var field = header.Slice(_uuidOffset, _uuidLength);
        var end = field.IndexOf((byte)0);
        var text = Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
        return Guid.TryParseExact(text, "D", out var guid) ? guid.ToString("D", CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// <see cref="DataVolumeCiphers.XtsAes256"/> when every data segment is <c>crypt</c> with <c>aes-xts-plain64</c> and every key
    /// slot has a 64-byte (512-bit) key; null for anything else, including a truncated or malformed header.
    /// </summary>
    public static string? ReadCipher(ReadOnlySpan<byte> header)
    {
        try
        {
            if (TryGetHeaderSize(header) is not { } size || header.Length < size)
                return null;
            var json = header[BinaryHeaderSize..size];
            var end = json.IndexOf((byte)0);
            if (end >= 0)
                json = json[..end];
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("keyslots", out var keyslots) || keyslots.ValueKind != JsonValueKind.Object)
                return null;

            var segmentCount = 0;
            foreach (var segment in segments.EnumerateObject())
            {
                if (segment.Value.ValueKind != JsonValueKind.Object
                    || GetString(segment.Value, "type") != "crypt"
                    || GetString(segment.Value, "encryption") != "aes-xts-plain64")
                    return null;
                segmentCount++;
            }

            var slotCount = 0;
            foreach (var slot in keyslots.EnumerateObject())
            {
                if (slot.Value.ValueKind != JsonValueKind.Object
                    || GetString(slot.Value, "type") != "luks2"
                    || !slot.Value.TryGetProperty("key_size", out var keySize)
                    || keySize.ValueKind != JsonValueKind.Number
                    || !keySize.TryGetInt32(out var bytes) || bytes != 64)
                    return null;
                slotCount++;
            }

            return segmentCount > 0 && slotCount > 0 ? DataVolumeCiphers.XtsAes256 : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

internal static class LosetupOutput
{
    /// <summary>Reads <c>losetup -j &lt;file&gt;</c> output (<c>/dev/loop0: [64769]:12 (/path)</c>) into the loop device paths.</summary>
    public static IReadOnlyList<string> ParseDevices(string output)
    {
        var devices = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && IsLoopDevice(line[..colon]))
                devices.Add(line[..colon]);
        }

        return devices;
    }

    /// <summary>True for <c>/dev/loopN</c>.</summary>
    public static bool IsLoopDevice(string path) =>
        path.StartsWith("/dev/loop", StringComparison.Ordinal) && path.Length > 9 && path.AsSpan(9).IndexOfAnyExceptInRange('0', '9') < 0;
}
