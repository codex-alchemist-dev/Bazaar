using System.Text.Json.Serialization;

namespace Bazaar.Server.Modules.Updates;

/// <summary>Wire shape of an update policy document. Internal: callers use <see cref="UpdatePolicyDocument"/>.</summary>
internal sealed class UpdatePolicyDocumentDto
{
    public required int SchemaVersion { get; init; }

    public required List<UpdatePolicyEntryDto> Policies { get; init; }
}

internal sealed class UpdatePolicyEntryDto
{
    public UpdatePolicyScopeDto? Scope { get; init; }

    public UpdatePolicyConditionsDto? Conditions { get; init; }

    public List<MaintenanceWindowDto>? Windows { get; init; }

    public required string Action { get; init; }
}

internal sealed class UpdatePolicyScopeDto
{
    public string? ModuleId { get; init; }

    public string? Category { get; init; }

    public string? VersionType { get; init; }

    public string? Channel { get; init; }
}

internal sealed class UpdatePolicyConditionsDto
{
    public bool? IsSecurity { get; init; }

    public bool? MatrixPassed { get; init; }
}

internal sealed class MaintenanceWindowDto
{
    public required List<string> Days { get; init; }

    public required string Start { get; init; }

    public required string End { get; init; }

    public required MaintenanceZoneDto Zone { get; init; }
}

internal sealed class MaintenanceZoneDto
{
    public string? TimeZone { get; init; }

    public string? UtcOffset { get; init; }
}

/// <summary>Source-generated JSON metadata for the policy document. No reflection-based serialization.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    WriteIndented = true)]
[JsonSerializable(typeof(UpdatePolicyDocumentDto))]
internal sealed partial class UpdatePolicyDocumentJsonContext : JsonSerializerContext;
