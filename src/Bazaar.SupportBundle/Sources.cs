using System.Text;
using Bazaar.Diagnostics.Capture;

namespace Bazaar.SupportBundle;

/// <summary>Writes <c>client/info.txt</c>: version, device and loaded modules. No host name, user name or address (doc 43 section 4.1).</summary>
public sealed class ClientInfoSource(ClientInfo info) : ISupportBundleSource
{
    /// <inheritdoc />
    public string Name => "client-info";

    /// <inheritdoc />
    public IReadOnlyList<SupportBundleFile> Collect()
    {
        var sb = new StringBuilder();
        sb.Append("version: ").AppendLine(info.AppVersion);
        sb.Append("os: ").AppendLine(info.Device.OsDescription);
        sb.Append("runtime: ").AppendLine(info.Device.RuntimeDescription);
        sb.Append("architecture: ").AppendLine(info.Device.Architecture);
        sb.AppendLine("modules:");
        foreach (var module in info.Modules)
        {
            sb.Append("  ").Append(module.Name).Append(' ').AppendLine(module.Version);
        }

        return [new SupportBundleFile("client/info.txt", sb.ToString())];
    }
}

/// <summary>
/// Includes a file from disk, or nothing when it does not exist. For a log, the newest lines are kept by the builder's
/// size limit. The file is opened with shared read, so a running client can still write to it.
/// </summary>
public sealed class FileSource(string name, string diskPath, string zipPath) : ISupportBundleSource
{
    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public IReadOnlyList<SupportBundleFile> Collect()
    {
        if (!File.Exists(diskPath))
        {
            return [];
        }

        using var stream = new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return [new SupportBundleFile(zipPath, reader.ReadToEnd())];
    }
}
