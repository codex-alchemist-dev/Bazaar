using System.Text;
using Bazaar.Client.OfflineSync;
using Bazaar.OfflineSync;

namespace Bazaar.Client.OfflineSync.Tests;

/// <summary>A fresh directory for one test's queue, removed on dispose.</summary>
public sealed class TempQueueDirectory : IDisposable
{
    /// <summary>The directory path.</summary>
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "bazaar-offline-tests", Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

/// <summary>Builders and assertions shared by the offline sync tests.</summary>
public static class OfflineTestData
{
    /// <summary>The device used by the default queue.</summary>
    public static readonly Guid Device = Guid.Parse("6f1d0a52-8f7e-4c3b-9a71-2d5e4b8c9f01");

    /// <summary>A second device, for the wrong-device checks.</summary>
    public static readonly Guid OtherDevice = Guid.Parse("0b9e4c21-3a5d-4f6e-8b7c-1d2e3f4a5b6c");

    /// <summary>A key provider with a key of the given seed byte.</summary>
    public static InMemoryKeyProvider Key(byte seed = 7) => new(Enumerable.Repeat(seed, 32).ToArray());

    /// <summary>Builds a GC message write with the given sequence and payload text.</summary>
    public static OfflineWrite Write(Guid deviceId, long sequence, string payload = "payload") =>
        new(
            Guid.NewGuid(),
            deviceId,
            sequence,
            OfflineDataClass.GcMessage,
            "gc/" + payload,
            null,
            Encoding.UTF8.GetBytes(payload),
            new Hlc(1_000 + sequence, 0, deviceId));

    /// <summary>Asserts that two writes carry the same fields.</summary>
    public static void AssertSameWrite(OfflineWrite expected, OfflineWrite actual)
    {
        Assert.Equal(expected.WriteId, actual.WriteId);
        Assert.Equal(expected.DeviceId, actual.DeviceId);
        Assert.Equal(expected.DeviceSequence, actual.DeviceSequence);
        Assert.Equal(expected.DataClass, actual.DataClass);
        Assert.Equal(expected.Target, actual.Target);
        Assert.Equal(expected.BaseVersion, actual.BaseVersion);
        Assert.Equal(expected.Payload.ToArray(), actual.Payload.ToArray());
        Assert.Equal(expected.Hlc, actual.Hlc);
    }
}
