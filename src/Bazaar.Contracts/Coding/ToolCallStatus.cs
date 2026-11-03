using System.Text.Json.Serialization;

namespace Bazaar.Contracts.Coding;

/// <summary>How a coding-agent tool call ended (docs/design/17-coding-agent-harness.md, tables).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ToolCallStatus>))]
public enum ToolCallStatus
{
    /// <summary>The tool finished normally.</summary>
    [JsonStringEnumMemberName("succeeded")]
    Succeeded,

    /// <summary>The tool finished with an error.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>The tool was killed when its timeout expired.</summary>
    [JsonStringEnumMemberName("timed_out")]
    TimedOut,

    /// <summary>The outcome could not be determined, such as after a host or guest crash.</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    /// <summary>Policy refused the call before it ran.</summary>
    [JsonStringEnumMemberName("refused")]
    Refused,
}
