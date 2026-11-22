namespace Bazaar.Client.Kodo;

/// <summary>The Kodo permission names of doc 46 section 14.5 that the Status and Fleet views read.</summary>
public static class KodoPermissions
{
    public const string ViewStatus = "kodo.view.status";
    public const string ViewFleet = "kodo.view.fleet";
    public const string ControlRestart = "kodo.control.restart";
    public const string ControlIsolate = "kodo.control.isolate";
    public const string ControlRollback = "kodo.control.rollback";
    public const string IncidentExit = "kodo.incident.exit";

    /// <summary>The permission a control needs before the pane shows it.</summary>
    public static string For(KodoControl control) => control switch
    {
        KodoControl.Restart => ControlRestart,
        KodoControl.Isolate => ControlIsolate,
        KodoControl.RollBack => ControlRollback,
        KodoControl.IncidentExit => IncidentExit,
        _ => throw new ArgumentOutOfRangeException(nameof(control), control, null),
    };
}

/// <summary>
/// What the signed-in viewer's role grants (doc 46 section 14.5). The head implements it over the real role grants; this
/// project never evaluates roles. The server still checks every action, so this only decides what is shown.
/// </summary>
public interface IKodoPermissions
{
    /// <summary>True when the viewer holds <paramref name="permission"/>.</summary>
    bool Can(string permission);
}

/// <summary>Grants every permission. For tests and an owner-only install.</summary>
public sealed class KodoAllPermissions : IKodoPermissions
{
    /// <inheritdoc />
    public bool Can(string permission) => true;
}

/// <summary>Grants nothing. The default for a viewer with no role (doc 46 section 14.5).</summary>
public sealed class KodoNoPermissions : IKodoPermissions
{
    /// <inheritdoc />
    public bool Can(string permission) => false;
}
