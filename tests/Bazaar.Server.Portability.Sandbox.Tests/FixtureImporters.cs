using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Bazaar.Server.Portability;

namespace Bazaar.Server.Portability.Sandbox.Tests;

// Importers that misbehave on purpose. The tests load this assembly into the parser child (ImporterAssemblies), so the
// bad code runs only in the child, exactly as an importer module would.

internal static class Fixture
{
    public static SourceRecord Record(string key, string body) =>
        new(key, "document", key, body, new Dictionary<string, string>());
}

public sealed class HangImporter : IPortabilityImporter
{
    public string Id => "test.hang";
    public string Version => "1.0.0";
    public DetectResult Detect(ImportSource source) => new(0.5, "hang");

    public IReadOnlyList<SourceRecord> Parse(ImportSource source)
    {
        Thread.Sleep(Timeout.Infinite);
        return [];
    }
}

public sealed class HangInDetectImporter : IPortabilityImporter
{
    public string Id => "test.hangdetect";
    public string Version => "1.0.0";

    public DetectResult Detect(ImportSource source)
    {
        Thread.Sleep(Timeout.Infinite);
        return new DetectResult(1, "never");
    }

    public IReadOnlyList<SourceRecord> Parse(ImportSource source) => [];
}

public sealed class CrashImporter : IPortabilityImporter
{
    public string Id => "test.crash";
    public string Version => "1.0.0";
    public DetectResult Detect(ImportSource source) => new(0.5, "crash");

    public IReadOnlyList<SourceRecord> Parse(ImportSource source)
    {
        Environment.FailFast("test crash");
        return [];
    }
}

public sealed class ThrowImporter : IPortabilityImporter
{
    public string Id => "test.throw";
    public string Version => "1.0.0";
    public DetectResult Detect(ImportSource source) => new(0.5, "throw");
    public IReadOnlyList<SourceRecord> Parse(ImportSource source) => throw new InvalidOperationException("bad file");
}

/// <summary>Many records of 2 MiB each: each is under the record limit, the total is over a small output cap.</summary>
public sealed class HugeOutputImporter : IPortabilityImporter
{
    public string Id => "test.huge";
    public string Version => "1.0.0";
    public DetectResult Detect(ImportSource source) => new(0.5, "huge");

    public IReadOnlyList<SourceRecord> Parse(ImportSource source) =>
        [.. Enumerable.Range(0, 20).Select(i => Fixture.Record("r" + i, new string('x', 2 * 1024 * 1024)))];
}

/// <summary>One record over a small record limit.</summary>
public sealed class OversizeRecordImporter : IPortabilityImporter
{
    public string Id => "test.oversize";
    public string Version => "1.0.0";
    public DetectResult Detect(ImportSource source) => new(0.5, "oversize");
    public IReadOnlyList<SourceRecord> Parse(ImportSource source) => [Fixture.Record("big", new string('y', 3 * 1024 * 1024))];
}

/// <summary>Allocates until the memory ceiling stops it.</summary>
public sealed class MemoryHogImporter : IPortabilityImporter
{
    public string Id => "test.hog";
    public string Version => "1.0.0";
    public DetectResult Detect(ImportSource source) => new(0.5, "hog");

    public IReadOnlyList<SourceRecord> Parse(ImportSource source)
    {
        var keep = new List<byte[]>();
        while (true)
        {
            var block = new byte[16 * 1024 * 1024];
            Array.Fill(block, (byte)1);
            keep.Add(block);
        }
    }
}

/// <summary>Takes a few seconds to produce its one record.</summary>
public sealed class SlowImporter : IPortabilityImporter
{
    public string Id => "test.slow";
    public string Version => "1.0.0";
    public DetectResult Detect(ImportSource source) => new(0.5, "slow");

    public IReadOnlyList<SourceRecord> Parse(ImportSource source)
    {
        Thread.Sleep(TimeSpan.FromSeconds(4));
        return [Fixture.Record("slow", "late")];
    }
}

/// <summary>Starts a long-running grandchild, then hangs.</summary>
public sealed class SpawnImporter : IPortabilityImporter
{
    public string Id => "test.spawn";
    public string Version => "1.0.0";
    public DetectResult Detect(ImportSource source) => new(0.5, "spawn");

    public IReadOnlyList<SourceRecord> Parse(ImportSource source)
    {
        var marker = source.Name;
        var info = new ProcessStartInfo(File.Exists("/usr/bin/sleep") ? "/usr/bin/sleep" : "/bin/sleep") { UseShellExecute = false };
        info.ArgumentList.Add(marker);
        Process.Start(info);
        Thread.Sleep(Timeout.Infinite);
        return [];
    }
}

/// <summary>Tries a loopback connection to the port named in the file content; reports what happened.</summary>
public sealed class NetworkProbeImporter : IPortabilityImporter
{
    public string Id => "test.net";
    public string Version => "1.0.0";
    public DetectResult Detect(ImportSource source) => new(0.5, "net");

    public IReadOnlyList<SourceRecord> Parse(ImportSource source)
    {
        var port = int.Parse(System.Text.Encoding.UTF8.GetString(source.Content), System.Globalization.CultureInfo.InvariantCulture);
        string outcome;
        try
        {
            using var client = new TcpClient();
            client.ConnectAsync(IPAddress.Loopback, port).Wait(TimeSpan.FromSeconds(3));
            outcome = client.Connected ? "connected" : "blocked";
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            outcome = "blocked";
        }
        return [Fixture.Record("net", outcome)];
    }
}

/// <summary>Prints to stdout; the host must keep that out of the frame stream.</summary>
public sealed class ChattyImporter : IPortabilityImporter
{
    public string Id => "test.chatty";
    public string Version => "1.0.0";
    public DetectResult Detect(ImportSource source) => new(0.5, "chatty");

    public IReadOnlyList<SourceRecord> Parse(ImportSource source)
    {
        Console.WriteLine("this line must not corrupt the frames");
        return [Fixture.Record("c", "ok")];
    }
}

/// <summary>Reports whether a server environment variable leaked into the child.</summary>
public sealed class EnvProbeImporter : IPortabilityImporter
{
    public string Id => "test.env";
    public string Version => "1.0.0";
    public DetectResult Detect(ImportSource source) => new(0.5, "env");

    public IReadOnlyList<SourceRecord> Parse(ImportSource source) =>
        [Fixture.Record("env", Environment.GetEnvironmentVariable("BAZAAR_TEST_SECRET") ?? "none")];
}

/// <summary>Returns a record whose Attributes is missing on the wire.</summary>
public sealed class NullAttributesImporter : IPortabilityImporter
{
    public string Id => "test.nullattrs";
    public string Version => "1.0.0";
    public DetectResult Detect(ImportSource source) => new(0.5, "nullattrs");

    public IReadOnlyList<SourceRecord> Parse(ImportSource source) =>
        [new SourceRecord("k", "document", "t", "b", null!)];
}

/// <summary>Returns a detect confidence outside [0,1].</summary>
public sealed class BadConfidenceImporter : IPortabilityImporter
{
    public string Id => "test.badconfidence";
    public string Version => "1.0.0";
    public DetectResult Detect(ImportSource source) => new(5, "too sure");
    public IReadOnlyList<SourceRecord> Parse(ImportSource source) => [];
}

/// <summary>Allocates native (unmanaged) memory without limit, which the managed heap ceiling does not see.</summary>
public sealed class NativeHogImporter : IPortabilityImporter
{
    public string Id => "test.native";
    public string Version => "1.0.0";
    public DetectResult Detect(ImportSource source) => new(0.5, "native");

    public unsafe IReadOnlyList<SourceRecord> Parse(ImportSource source)
    {
        // 6 GiB in one block, then touch it: with an address-space data limit of memory + 1 GiB this must fail.
        var block = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc(checked((nuint)Math.Min(6L * 1024 * 1024 * 1024, (long)nuint.MaxValue)));
        for (long i = 0; i < 6L * 1024 * 1024 * 1024; i += 4096) block[i] = 1;
        return [Fixture.Record("native", "allocated")];
    }
}
