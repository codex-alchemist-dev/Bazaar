using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Gc;

/// <summary>The text stored in the <c>author_kind</c> and <c>source_trust</c> columns (the same names the wire uses).</summary>
internal static class GcEnumText
{
    public static string ToText(AuthorKind kind) => kind switch
    {
        AuthorKind.Owner => "owner",
        AuthorKind.Human => "human",
        AuthorKind.Agent => "agent",
        AuthorKind.System => "system",
        AuthorKind.ExternalBridge => "external_bridge",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static AuthorKind ParseAuthorKind(string text) => text switch
    {
        "owner" => AuthorKind.Owner,
        "human" => AuthorKind.Human,
        "agent" => AuthorKind.Agent,
        "system" => AuthorKind.System,
        "external_bridge" => AuthorKind.ExternalBridge,
        _ => throw new InvalidDataException($"Unknown author_kind '{text}'."),
    };

    public static string ToText(SourceTrust trust) => trust switch
    {
        SourceTrust.Owner => "owner",
        SourceTrust.System => "system",
        SourceTrust.MemberHuman => "member-human",
        SourceTrust.Agent => "agent",
        SourceTrust.External => "external",
        SourceTrust.Quarantined => "quarantined",
        _ => throw new ArgumentOutOfRangeException(nameof(trust), trust, null),
    };

    public static SourceTrust ParseSourceTrust(string text) => text switch
    {
        "owner" => SourceTrust.Owner,
        "system" => SourceTrust.System,
        "member-human" => SourceTrust.MemberHuman,
        "agent" => SourceTrust.Agent,
        "external" => SourceTrust.External,
        "quarantined" => SourceTrust.Quarantined,
        _ => throw new InvalidDataException($"Unknown source_trust '{text}'."),
    };
}
