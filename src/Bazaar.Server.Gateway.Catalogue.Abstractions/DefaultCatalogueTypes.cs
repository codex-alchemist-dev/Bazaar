using System.Text.Json.Serialization;

namespace Bazaar.Server.Gateway.Catalogue;

/// <summary>Provider kinds of docs/design/05-model-gateway.md ("Provider kinds").</summary>
public enum DefaultCatalogueKind
{
    [JsonStringEnumMemberName("first_party")] FirstParty,
    [JsonStringEnumMemberName("aggregator")] Aggregator,
    [JsonStringEnumMemberName("gateway")] Gateway,
    [JsonStringEnumMemberName("reverse_proxy")] ReverseProxy,
    [JsonStringEnumMemberName("local")] Local,
}

/// <summary>Wire families of the default catalogue table in doc 05.</summary>
public enum DefaultCatalogueAdapter
{
    [JsonStringEnumMemberName("anthropic_messages")] AnthropicMessages,
    [JsonStringEnumMemberName("gemini_generate_content")] GeminiGenerateContent,
    [JsonStringEnumMemberName("openai_chat")] OpenAiChat,
}

/// <summary>Doc 05 verification marks: V primary page read, P partly, U unverified.</summary>
public enum DefaultCatalogueVerification
{
    V,
    P,
    U,
}

/// <summary>Whether a source has a free tier. <c>Unknown</c> until the owner or a raw-page check fills it.</summary>
public enum FreeTierStatus
{
    [JsonStringEnumMemberName("unknown")] Unknown,
    [JsonStringEnumMemberName("yes")] Yes,
    [JsonStringEnumMemberName("no")] No,
}

/// <summary>Terms-of-service risk of using a source's free tier. <c>Unknown</c> until checked.</summary>
public enum TermsRisk
{
    [JsonStringEnumMemberName("unknown")] Unknown,
    [JsonStringEnumMemberName("low")] Low,
    [JsonStringEnumMemberName("medium")] Medium,
    [JsonStringEnumMemberName("high")] High,
}

/// <summary>Whether a source trains on free-tier data. <c>Unknown</c> until checked.</summary>
public enum TrainsOnFreeData
{
    [JsonStringEnumMemberName("unknown")] Unknown,
    [JsonStringEnumMemberName("yes")] Yes,
    [JsonStringEnumMemberName("no")] No,
}

/// <summary>
/// One source of the shipped default catalogue (docs/design/05-model-gateway.md, "Default catalogue"). Secret-free by contract:
/// no key, token or credential field exists. <c>BaseUrl</c> is null where the doc says "to confirm", "account-specific" or
/// "operator-set". <c>VerificationNote</c> is the doc's Status text.
/// </summary>
public sealed record DefaultCatalogueEntry(
    string Id,
    string DisplayName,
    DefaultCatalogueAdapter Adapter,
    string? BaseUrl,
    DefaultCatalogueKind Kind,
    DefaultCatalogueVerification Verification,
    string VerificationNote,
    FreeTierStatus FreeTier,
    TermsRisk TermsRisk,
    TrainsOnFreeData TrainsOnFreeData);

/// <summary>The shipped default catalogue file: a schema version and the entries.</summary>
public sealed record DefaultCatalogue(int SchemaVersion, IReadOnlyList<DefaultCatalogueEntry> Entries);
