using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.DevSandbox.Linux;

/// <summary>The helper's policy file, schema version 1 (mirrors <c>HelperPolicy</c> in the helper project; a test parses this output with the helper's parser).</summary>
internal sealed class LinuxHardeningPolicyFile
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("readPaths")]
    public List<string> ReadPaths { get; set; } = [];

    [JsonPropertyName("writePaths")]
    public List<string> WritePaths { get; set; } = [];

    [JsonPropertyName("extraSyscalls")]
    public List<string> ExtraSyscalls { get; set; } = [];

    [JsonPropertyName("path")]
    public string Path { get; set; } = "/usr/bin:/bin";

    [JsonPropertyName("addressSpaceBytes")]
    public long? AddressSpaceBytes { get; set; }
}

[JsonSerializable(typeof(LinuxHardeningPolicyFile))]
internal sealed partial class LinuxHardeningJsonContext : JsonSerializerContext
{
}

/// <summary>Builds the least-privilege policy for one launch: read and execute only the executable's directory and the system libraries; read and write only the work directory.</summary>
internal static class LinuxHardeningPolicy
{
    internal static LinuxHardeningPolicyFile Create(LinuxConfinementOptions options, LinuxLaunchCommand command, string workDirectory, long addressSpaceBytes)
    {
        var read = new List<string>();
        var exeDirectory = System.IO.Path.GetDirectoryName(command.Executable);
        if (!string.IsNullOrEmpty(exeDirectory))
        {
            read.Add(exeDirectory);
        }

        read.AddRange(LinuxConfinementOptions.SystemReadPaths);
        if (command.AdditionalReadPaths is not null)
        {
            read.AddRange(command.AdditionalReadPaths);
        }

        return new LinuxHardeningPolicyFile
        {
            ReadPaths = read.Distinct(StringComparer.Ordinal).ToList(),
            WritePaths = [workDirectory],
            Path = options.Path,
            AddressSpaceBytes = addressSpaceBytes,
        };
    }

    internal static string Serialize(LinuxHardeningPolicyFile policy) =>
        JsonSerializer.Serialize(policy, LinuxHardeningJsonContext.Default.LinuxHardeningPolicyFile);
}
