using System.Buffers.Binary;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Kvm.Tests;

internal sealed class FakeTransport : IQmpTransport
{
    public List<(string Domain, string Json)> Calls { get; } = [];

    public Func<string, string> Respond { get; set; } = _ => "{\"return\":{}}";

    public Task<string> ExecuteAsync(string domain, string qmpJson, CancellationToken ct)
    {
        Calls.Add((domain, qmpJson));
        return Task.FromResult(Respond(qmpJson));
    }
}

internal sealed class FakeReader : IScreendumpFileReader
{
    public byte[] Content { get; set; } = TestImages.Png(640, 480);

    public bool FailRead { get; set; }

    public List<string> Read { get; } = [];

    public List<string> Deleted { get; } = [];

    public Task<byte[]> ReadAsync(string path, CancellationToken ct)
    {
        Read.Add(path);
        if (FailRead)
            throw new DisplayException(DisplayFailure.SourceFailed, "read failed");
        return Task.FromResult(Content);
    }

    public void Delete(string path) => Deleted.Add(path);
}

internal static class TestImages
{
    public static byte[] Png(int width, int height)
    {
        byte[] data = new byte[33];
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        sig.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 13);
        "IHDR"u8.CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), (uint)height);
        return data;
    }

    public static byte[] Ppm(int width, int height) =>
        [.. System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"), 1, 2, 3];
}

internal static class Targets
{
    public static DisplayTarget For(string backendRef) => new("agent-1", Guid.NewGuid(), DisplayGuestOs.Linux, backendRef);
}
