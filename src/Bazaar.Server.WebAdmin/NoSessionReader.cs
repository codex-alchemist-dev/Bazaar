using Microsoft.AspNetCore.Http;

namespace Bazaar.Server.WebAdmin;

/// <summary>Default reader until the passkey sign-in module registers one: nobody is signed in, so every panel route answers 401.</summary>
public sealed class NoSessionReader : IAdminSessionReader
{
    public ValueTask<AdminSession?> ReadAsync(HttpContext context, CancellationToken ct = default) => ValueTask.FromResult<AdminSession?>(null);
}
