namespace Bazaar.Server.Providers;

/// <summary>
/// The outcome of choosing one role's provider at startup.
/// <paramref name="Provider"/> is null when the setting is <c>none</c>, or when <c>auto</c> found no usable candidate.
/// <paramref name="Statuses"/> holds one probe result per candidate, in candidate order.
/// </summary>
public sealed record ProviderSelection(ProviderRole Role, string Requested, IProvider? Provider, IReadOnlyList<ProviderStatus> Statuses)
{
    /// <summary>One line for the startup log and the status page, for example <c>provider search: postgres_fts (requested auto)</c>.</summary>
    public string ToLogLine()
    {
        var name = $"provider {Role.SettingName()}";
        if (Requested == ProviderSelector.None)
        {
            return $"{name}: none (disabled by setting)";
        }

        var unusable = Statuses
            .Where(s => !s.Usable)
            .Select(s => $"{s.Kind} ({s.Reason ?? "unusable"})");
        var detail = string.Join("; ", unusable);
        var suffix = detail.Length == 0 ? string.Empty : $"; unusable: {detail}";

        return Provider is null
            ? $"{name}: no usable provider (requested {Requested}){suffix}"
            : $"{name}: {Provider.Kind} selected (requested {Requested}){suffix}";
    }
}

/// <summary>Thrown when an explicitly named provider is not registered for this OS or is not usable. There is no silent fallback.</summary>
public sealed class ProviderSelectionException : Exception
{
    public ProviderSelectionException(ProviderRole role, string requested, string reason)
        : base($"Provider {role.SettingName()} cannot use '{requested}': {reason}")
    {
        Role = role;
        Requested = requested;
    }

    public ProviderRole Role { get; }

    public string Requested { get; }
}
