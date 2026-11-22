using System.Buffers.Binary;
using System.Text;

namespace Bazaar.Server.DataVolume.Linux.Tests;

internal sealed record RecordedCall(string FileName, string[] Arguments, byte[] StandardInput)
{
    public string Line => FileName + " " + string.Join(' ', Arguments);
}

/// <summary>Records every command. <see cref="Respond"/> overrides the default answers; returning null keeps the default.</summary>
internal sealed class FakeCommandRunner : ICommandRunner
{
    public const string Uuid = "0a1b2c3d-4e5f-4a6b-8c7d-1234567890ab";

    public List<RecordedCall> Calls { get; } = [];

    public Func<RecordedCall, CommandResult?>? Respond { get; set; }

    public bool MappingActive { get; set; }

    public bool LoopAttached { get; set; }

    public ValueTask<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, ReadOnlyMemory<byte> standardInput, CancellationToken cancellationToken)
    {
        var call = new RecordedCall(fileName, [.. arguments], standardInput.ToArray());
        Calls.Add(call);
        return ValueTask.FromResult(Respond?.Invoke(call) ?? Default(call));
    }

    public bool Ran(string line) => Calls.Any(c => c.Line == line);

    private CommandResult Default(RecordedCall call)
    {
        var line = call.Line;
        if (line.StartsWith("losetup --find --show", StringComparison.Ordinal))
            return Ok("/dev/loop7\n");
        if (line.StartsWith("losetup --associated", StringComparison.Ordinal))
            return Ok(LoopAttached ? "/dev/loop7: [64769]:12 (" + call.Arguments[1] + ")\n" : string.Empty);
        if (line.StartsWith("cryptsetup luksUUID", StringComparison.Ordinal))
            return Ok(Uuid + "\n");
        if (line.StartsWith("cryptsetup status", StringComparison.Ordinal))
            return new CommandResult(MappingActive ? 0 : 4, string.Empty, string.Empty);
        if (call.FileName == "blkid")
            return new CommandResult(2, string.Empty, string.Empty);
        return Ok(string.Empty);
    }

    public static CommandResult Ok(string output) => new(0, output, string.Empty);

    public static CommandResult Fail(int code, string error) => new(code, string.Empty, error);
}

internal sealed class FakeLinuxFiles : ILinuxVolumeFiles
{
    public Dictionary<string, string> Texts { get; } = [];

    public Dictionary<string, string[]> Directories { get; } = [];

    public Dictionary<string, byte[]> Prefixes { get; } = [];

    public HashSet<string> ExistingFiles { get; } = [];

    public HashSet<string> EmptyDirectories { get; } = [];

    public List<string> Created { get; } = [];

    public List<string> Deleted { get; } = [];

    public long Length { get; set; } = 64L * 1024 * 1024;

    public string ResolvePath(string path) => path;

    public string? ReadAllText(string path) => Texts.GetValueOrDefault(path);

    public IReadOnlyList<string> ListDirectory(string path) => Directories.GetValueOrDefault(path) ?? [];

    public byte[]? ReadPrefix(string path, int count) =>
        Prefixes.TryGetValue(path, out var bytes) ? bytes.AsSpan(0, Math.Min(count, bytes.Length)).ToArray() : null;

    public bool FileExists(string path) => ExistingFiles.Contains(path);

    public bool DirectoryExists(string path) => EmptyDirectories.Contains(path);

    public bool IsDirectoryEmpty(string path) => EmptyDirectories.Contains(path);

    public long FileLength(string path) => Length;

    public void CreateExclusiveFile(string path, UnixFileMode mode)
    {
        Created.Add(path);
        ExistingFiles.Add(path);
    }

    public void DeleteFile(string path)
    {
        Deleted.Add(path);
        ExistingFiles.Remove(path);
    }
}

internal static class HeaderBuilder
{
    public const string Json =
        """{"keyslots":{"0":{"type":"luks2","key_size":64}},"segments":{"0":{"type":"crypt","encryption":"aes-xts-plain64","sector_size":512}}}""";

    /// <summary>Builds a LUKS2 header: 4096-byte binary part, then the JSON area padded with NULs to <paramref name="size"/>.</summary>
    public static byte[] Build(string json = Json, string uuid = FakeCommandRunner.Uuid, ulong size = 16384, ushort version = 2, bool goodMagic = true)
    {
        var bytes = new byte[16384];
        if (goodMagic)
            new byte[] { (byte)'L', (byte)'U', (byte)'K', (byte)'S', 0xba, 0xbe }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), version);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), size);
        Encoding.ASCII.GetBytes(uuid).CopyTo(bytes, 168);
        var body = Encoding.UTF8.GetBytes(json);
        body.AsSpan(0, Math.Min(body.Length, bytes.Length - 4096)).CopyTo(bytes.AsSpan(4096));
        return bytes;
    }
}
