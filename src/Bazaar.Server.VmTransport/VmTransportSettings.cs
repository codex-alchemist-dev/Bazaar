using System.Text.Json;
using Bazaar.Server.Settings;
using Bazaar.Vms.Sockets;

namespace Bazaar.Server.VmTransport;

/// <summary>
/// The guest transport's operator settings (doc 16 WP7). Install scope, operator-changed; the registry audits every change.
/// Nothing here is a security floor: peer identity comes from the hypervisor, not from the service GUID.
/// </summary>
public static class VmTransportSettings
{
    /// <summary>The setting name.</summary>
    public const string ServiceGuidName = "vm.transport.service_guid";

    /// <summary>
    /// The Hyper-V service GUID the host listener binds and Windows guests dial. Default is the fixed shipped GUID (vsock port 5000 in the
    /// Hyper-V service template). A new value must also be registered under <c>GuestCommunicationServices</c> on the host and given to the
    /// guest agent (<c>--hvsock-service-id</c>).
    /// </summary>
    public static readonly SettingKey<string> ServiceGuid = new(
        ServiceGuidName, HvSocketAddress.DefaultServiceId.ToString("D"), SettingScope.Install, SettingRole.Operator,
        "Hyper-V service GUID the guest transport listens on. Must be a non-empty GUID. Windows guests must be told the same value.",
        validate: CheckGuid);

    /// <summary>Every VM transport key.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } = [ServiceGuid];

    /// <summary>
    /// Resolves the service GUID from a stored value (null when unset). An unset value gives the default without a problem; a stored value
    /// that is invalid gives the default and the reason in <paramref name="problem"/>. Never throws.
    /// </summary>
    public static Guid ResolveServiceId(JsonElement? stored, out string? problem)
    {
        problem = null;
        if (stored is not { } json)
            return HvSocketAddress.DefaultServiceId;

        var reason = ServiceGuid.CheckValue(json);
        if (reason is not null)
        {
            problem = $"{reason} Using the default {HvSocketAddress.DefaultServiceId:D}.";
            return HvSocketAddress.DefaultServiceId;
        }

        return Guid.Parse(ServiceGuid.Read(json));
    }

    private static string? CheckGuid(string value) =>
        Guid.TryParse(value, out var guid) && guid != Guid.Empty
            ? null
            : $"'{ServiceGuidName}' must be a non-empty GUID; got '{value}'.";
}

/// <summary>The guest transport's contribution to the settings registry. The host registers it; Bazaar.Server cannot reference this project.</summary>
public sealed class VmTransportSettingContribution : ISettingContribution
{
    /// <inheritdoc />
    public IReadOnlyList<SettingDefinition> Settings => VmTransportSettings.All;
}
