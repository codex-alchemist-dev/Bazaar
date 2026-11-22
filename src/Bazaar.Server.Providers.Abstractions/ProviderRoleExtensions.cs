namespace Bazaar.Server.Providers;

/// <summary>Setting-name mapping for <see cref="ProviderRole"/>, used in log lines and in <c>host.readiness.required_roles</c> (doc 03).</summary>
public static class ProviderRoleExtensions
{
    /// <summary>Returns the role's segment in its setting key, for example <c>service_host</c>.</summary>
    public static string SettingName(this ProviderRole role) => role switch
    {
        ProviderRole.ServiceHost => "service_host",
        ProviderRole.Vm => "vm",
        ProviderRole.Cache => "cache",
        ProviderRole.Search => "search",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown provider role."),
    };
}
