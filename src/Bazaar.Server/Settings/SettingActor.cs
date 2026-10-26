namespace Bazaar.Server.Settings;

/// <summary>Who is trying to change a setting: a principal id and the roles it holds.</summary>
public sealed record SettingActor(string Id, SettingRole Roles)
{
    /// <summary>True when the actor holds at least one of <paramref name="role"/>'s flags.</summary>
    public bool Has(SettingRole role) => (Roles & role) != SettingRole.None;
}
