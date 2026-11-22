namespace Bazaar.ModulePermissions.Http;

/// <summary>
/// Checks each request of a module before it leaves: an absolute <c>http</c> or <c>https</c> address with no user name or
/// password in it, to a host the module holds a <c>network</c> permission for. A <c>Host</c> header that names another
/// host is refused, so a granted host cannot front for an ungranted one on a shared address.
/// </summary>
public sealed class ModuleHttpGuard(IModulePermissionGate gate, string moduleId) : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Check(request);
        return base.SendAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Check(request);
        return base.Send(request, cancellationToken);
    }

    private void Check(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ModuleNetworkRefusedException(string.Empty, "A module request needs an absolute http or https address.");
        }

        // IdnHost is the ASCII form that is resolved and dialled; brackets are not part of an IPv6 host.
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.IdnHost.Trim('[', ']') : uri.IdnHost;
        if (uri.UserInfo.Length != 0)
        {
            throw new ModuleNetworkRefusedException(host, "A module request must not carry a user name or password in its address.");
        }

        if (request.Headers.Host is { } header)
        {
            var expected = uri.IsDefaultPort ? uri.IdnHost : $"{uri.IdnHost}:{uri.Port}";
            if (!string.Equals(header, uri.IdnHost, StringComparison.OrdinalIgnoreCase) && !string.Equals(header, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new ModuleNetworkRefusedException(host, "The Host header names a different host than the address.");
            }
        }

        gate.Demand(moduleId, new ModuleAccess(ModulePermissionClasses.Network, host));
    }
}
