using Microsoft.AspNetCore.Http;

namespace Bazaar.Server.Http.Auth;

/// <summary>Carries the verified caller on an <see cref="HttpContext"/> from the authentication middleware to the routes.</summary>
public static class HttpContextAuthenticatedCallerExtensions
{
    private static readonly object _callerKey = new();

    /// <summary>The verified caller, or null when the request carried no valid, unrevoked access token.</summary>
    public static AuthenticatedCaller? GetAuthenticatedCaller(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(_callerKey, out var value) ? value as AuthenticatedCaller : null;
    }

    /// <summary>Records the verified caller for the rest of the request. Called by the authentication middleware only.</summary>
    public static void SetAuthenticatedCaller(this HttpContext context, AuthenticatedCaller caller)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(caller);
        context.Items[_callerKey] = caller;
    }
}
