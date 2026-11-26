namespace Bazaar.ModulePermissions;

/// <summary>
/// The ten module permission classes (doc 48 section 1), as they are written in a package manifest. This is the one permission
/// vocabulary: a permission is <c>class</c> or <c>class:scope</c>, and no other class name exists.
/// </summary>
public static class ModulePermissionClasses
{
    /// <summary>Read data of one component. Scope: the component, for example <c>data.read:gc</c>.</summary>
    public const string DataRead = "data.read";

    /// <summary>Write data of one component. Scope: the component.</summary>
    public const string DataWrite = "data.write";

    /// <summary>Post to a GC. The one class that may stand with no scope.</summary>
    public const string GcPost = "gc.post";

    /// <summary>Reach a network host. Scope: one host name or IP literal.</summary>
    public const string Network = "network";

    /// <summary>Use a filesystem path. Scope: a path or named root.</summary>
    public const string FileSystem = "fs";

    /// <summary>Read a secret. Scope: the secret name.</summary>
    public const string Secret = "secret";

    /// <summary>Show a UI pane. Scope: the pane.</summary>
    public const string UiPane = "ui.pane";

    /// <summary>Run a background job. Scope: the job.</summary>
    public const string BackgroundJob = "job";

    /// <summary>Start a process. Scope: the program.</summary>
    public const string ProcessSpawn = "process";

    /// <summary>Use a device capability. Scope: the capability, for example <c>device:notify</c>.</summary>
    public const string Device = "device";

    /// <summary>Every class, in the order doc 48 lists them.</summary>
    public static IReadOnlyList<string> All { get; } =
        [DataRead, DataWrite, GcPost, Network, FileSystem, Secret, UiPane, BackgroundJob, ProcessSpawn, Device];

    /// <summary>True when <paramref name="value"/> is exactly one of the ten class names.</summary>
    public static bool IsClass(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);
}
