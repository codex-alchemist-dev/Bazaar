using System.Text.Json.Serialization;

namespace Bazaar.Server.WebAdmin.Http;

/// <summary>Body of <c>GET /admin/api/session</c>.</summary>
public sealed record AdminSessionView(Guid PrincipalId, string Kind, IReadOnlyList<string> Areas, IReadOnlyList<string> Sections);

/// <summary>Body of a section whose port is not registered.</summary>
public sealed record AdminUnavailable(bool Available);

/// <summary>Body of <c>POST /admin/api/modules/preview</c>.</summary>
public sealed record AdminPreviewRequest(IReadOnlyList<string>? Selected);

/// <summary>Body of <c>PUT /admin/api/modules/selection</c>.</summary>
public sealed record AdminSelectionRequest(IReadOnlyList<string>? Selected, long? ExpectedRevision);

/// <summary>Source-generated JSON for every panel payload (no reflection), camelCase.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AdminSessionView))]
[JsonSerializable(typeof(AdminUnavailable))]
[JsonSerializable(typeof(AdminPreviewRequest))]
[JsonSerializable(typeof(AdminSelectionRequest))]
[JsonSerializable(typeof(AdminModuleCatalogue))]
[JsonSerializable(typeof(IReadOnlyList<AdminBuildRecord>))]
[JsonSerializable(typeof(AdminSelectionPreview))]
[JsonSerializable(typeof(AdminSelectionCommit))]
[JsonSerializable(typeof(AdminRebuildStart))]
[JsonSerializable(typeof(AdminCatalogueReport))]
[JsonSerializable(typeof(AdminStatusView))]
[JsonSerializable(typeof(AdminKodoView))]
[JsonSerializable(typeof(IReadOnlyList<AdminUser>))]
[JsonSerializable(typeof(IReadOnlyList<AdminDevice>))]
[JsonSerializable(typeof(AdminAuditPage))]
[JsonSerializable(typeof(IReadOnlyList<AdminApproval>))]
public sealed partial class AdminJson : JsonSerializerContext
{
}
