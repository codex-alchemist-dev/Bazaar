using System.ComponentModel;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Contracts.Panes;
using Bazaar.Contracts.Vms;

namespace Bazaar.Client.Devices;

/// <summary>One provider row: what it is, whether the host probe passed and why not, its capability flags and its load.</summary>
public sealed record DeviceProviderRow(string Name, string Status, string Capabilities, string Load);

/// <summary>One running guest row.</summary>
public sealed record DeviceGuestRow(string Name, string State, string Id);

/// <summary>
/// Holds the Devices pane list (docs/design/62-device-vm-providers-android-ios-macos.md section 9). List only: no start,
/// stop or screen in v1. The pane is offered only when the server reports at least one enabled provider, so an install
/// with none, a non-owner principal (the server answers <c>not_found</c>) and a missing source all leave it hidden.
/// Data only: no Avalonia types.
/// </summary>
public sealed class DevicesPaneViewModel : IPaneViewModel, INotifyPropertyChanged
{
    private readonly IDeviceListSource? _source;
    private IReadOnlyList<DeviceProviderRow> _providers = [];
    private IReadOnlyList<DeviceGuestRow> _guests = [];

    public DevicesPaneViewModel(PaneDescriptor descriptor, IDeviceListSource? source = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        _source = source;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Kind { get; }

    public string Title => "Devices";

    /// <summary>Enabled providers, in the order the server sent them.</summary>
    public IReadOnlyList<DeviceProviderRow> Providers => _providers;

    /// <summary>Running device guests with their state.</summary>
    public IReadOnlyList<DeviceGuestRow> Guests => _guests;

    /// <summary>True when the pane should be offered: at least one provider is enabled.</summary>
    public bool IsOffered => _providers.Count > 0;

    /// <summary>
    /// Fetches the list and replaces what is shown. A refusal or a missing source clears the list, so the pane hides;
    /// cancellation and other failures propagate to the caller.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (_source is null)
        {
            Apply(null);
            return;
        }

        try
        {
            Apply(await _source.ListAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (WireRequestException)
        {
            Apply(null);
        }
    }

    /// <summary>Replaces the list from a reply, or clears it when <paramref name="reply"/> is null.</summary>
    public void Apply(DeviceListReply? reply)
    {
        _providers = reply is null ? [] : [.. reply.Providers.Select(ToRow)];
        _guests = reply is null ? [] : [.. reply.Guests.Select(g => new DeviceGuestRow($"{g.Kind} ({g.GuestOs})", g.State, g.VmId.ToString("N")[..8]))];

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Providers)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Guests)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOffered)));
    }

    private static DeviceProviderRow ToRow(DeviceProviderInfo p)
    {
        var status = p.Usable ? "usable" : string.IsNullOrWhiteSpace(p.Reason) ? "not usable" : $"not usable: {p.Reason}";
        List<string> flags = [p.Isolation, p.Headless ? "headless" : "headed"];
        if (p.Save)
        {
            flags.Add("save");
        }

        if (p.Checkpoint)
        {
            flags.Add("checkpoint");
        }

        if (p.Restore)
        {
            flags.Add("restore");
        }

        flags.Add(p.EgressEnforced ? "egress enforced" : "egress not enforced");
        var load = p.MaxConcurrentInstances is { } cap ? $"{p.Running} of {cap} running" : $"{p.Running} running";
        return new DeviceProviderRow($"{p.Kind} ({p.GuestOs})", status, string.Join(", ", flags), load);
    }
}
