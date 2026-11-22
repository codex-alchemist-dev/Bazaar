using System.Text.Json;

namespace Bazaar.Server.ModuleHost;

// Host-service shapes for the permission classes that have no guard yet (doc 48 section 1, doc 40 module host entry).
// Every shape here is PROPOSED. A shape is what a hosted module is handed, and a permission guard is a decorator over the same
// interface: it holds the module id, asks IModulePermissionGate.Demand for one exact access, then calls the real service. The
// interfaces carry no module id, so the real service and its guarded copy have one shape.
//
// Access each guard demands (class, target), matching the written form of doc 48 section 1:
//   IModuleData.ReadAsync      data.read, target = Component
//   IModuleData.WriteAsync     data.write, target = Component
//   IModuleGcPoster.PostAsync  gc.post, no target (only an unscoped gc.post grant covers an access with no target)
//   IModulePaneRegistry        ui.pane, target = PaneId
//   IModuleJobScheduler        job, target = JobName
//   IModuleDeviceAccess        device, target = Capability

/// <summary>What the host hands one module: one guarded service per permission class that needs a host service.</summary>
public interface IModuleHostServices
{
    /// <summary>The module these services were made for.</summary>
    string ModuleId { get; }

    /// <summary>Read and write the data of named components (<c>data.read</c>, <c>data.write</c>).</summary>
    IModuleData Data { get; }

    /// <summary>Post to a GC (<c>gc.post</c>).</summary>
    IModuleGcPoster Gc { get; }

    /// <summary>Declare UI panes (<c>ui.pane</c>).</summary>
    IModulePaneRegistry Panes { get; }

    /// <summary>Schedule background jobs (<c>job</c>).</summary>
    IModuleJobScheduler Jobs { get; }

    /// <summary>Use device capabilities (<c>device</c>).</summary>
    IModuleDeviceAccess Devices { get; }
}

/// <summary>
/// Makes the guarded services of one running module. The module host calls it after it puts the module's approved set in force.
/// Implementations return services that fail closed once the module is deactivated.
/// </summary>
public interface IModuleHostServicesFactory
{
    /// <summary>Makes the services for <paramref name="moduleId"/>. The module must be running; otherwise every call is refused.</summary>
    IModuleHostServices CreateFor(string moduleId);
}

/// <summary>
/// Data of the components that offer it (GC history, Kanban, Vault, Constellation; doc 48 section 1 names the four). One
/// component, one <see cref="IModuleDataSource"/>; this facade routes by component name.
/// </summary>
public interface IModuleData
{
    /// <summary>Reads one record or one page of a collection. Needs <c>data.read:&lt;component&gt;</c>.</summary>
    ValueTask<ModuleDataPage> ReadAsync(ModuleDataReadRequest request, CancellationToken ct = default);

    /// <summary>Writes one record. Needs <c>data.write:&lt;component&gt;</c>.</summary>
    ValueTask<ModuleDataWriteResult> WriteAsync(ModuleDataWriteRequest request, CancellationToken ct = default);
}

/// <summary>
/// What a component implements so modules can reach its data. The component decides what its collections are. It never sees a
/// module id, and it must not assume the caller was checked: only the guard in front of <see cref="IModuleData"/> checks.
/// </summary>
public interface IModuleDataSource
{
    /// <summary>The component name, lower case (<c>gc</c>, <c>kanban</c>, <c>vault</c>, <c>constellation</c>). The scope of the grant.</summary>
    string Component { get; }

    /// <summary>False when the component has no module write path. A write to it is refused as unsupported.</summary>
    bool SupportsWrite { get; }

    /// <summary>Reads data.</summary>
    ValueTask<ModuleDataPage> ReadAsync(ModuleDataReadRequest request, CancellationToken ct = default);

    /// <summary>Writes data. Called only when <see cref="SupportsWrite"/> is true.</summary>
    ValueTask<ModuleDataWriteResult> WriteAsync(ModuleDataWriteRequest request, CancellationToken ct = default);
}

/// <summary>A read. A null <paramref name="Key"/> asks for a page of the collection.</summary>
/// <param name="Component">The component, as the grant scope names it.</param>
/// <param name="Collection">The component's own collection name.</param>
/// <param name="Key">One record key, or null for a page.</param>
/// <param name="Cursor">The <see cref="ModuleDataPage.NextCursor"/> of the previous page, or null for the first.</param>
/// <param name="Limit">The most records to return. The component may return fewer and has its own cap.</param>
public sealed record ModuleDataReadRequest(string Component, string Collection, string? Key = null, string? Cursor = null, int Limit = 100);

/// <summary>One page of records, each a JSON value in the component's own shape.</summary>
public sealed record ModuleDataPage(IReadOnlyList<JsonElement> Items, string? NextCursor);

/// <summary>A write of one record.</summary>
/// <param name="Component">The component, as the grant scope names it.</param>
/// <param name="Collection">The component's own collection name.</param>
/// <param name="Key">The record key.</param>
/// <param name="Value">The new value, in the component's own shape. The component validates it.</param>
/// <param name="ExpectedVersion">The version the module last read, or null for a create. A stale one is a conflict.</param>
public sealed record ModuleDataWriteRequest(string Component, string Collection, string Key, JsonElement Value, string? ExpectedVersion = null);

/// <summary>The outcome of a write. <paramref name="Version"/> is the new record version.</summary>
public sealed record ModuleDataWriteResult(bool Written, string? Version, string? Conflict = null);

/// <summary>
/// Posts into a GC as the module (<c>gc.post</c>). The author is the module's own system identity, never a person or agent. A
/// module that is handed only this cannot read the GC back (that is <c>data.read:gc</c>).
/// </summary>
public interface IModuleGcPoster
{
    /// <summary>Appends one message. Idempotent by <see cref="ModuleGcPost.ClientMsgId"/> within the GC.</summary>
    ValueTask<ModuleGcPostResult> PostAsync(ModuleGcPost post, CancellationToken ct = default);
}

/// <summary>A message a module posts. The host fixes the author and the source trust; the module cannot name either.</summary>
/// <param name="GcId">The GC to post into.</param>
/// <param name="ClientMsgId">Idempotency key, unique per module within the GC.</param>
/// <param name="Text">The message text.</param>
public sealed record ModuleGcPost(Guid GcId, string ClientMsgId, string Text);

/// <summary>The outcome of a post. <paramref name="Seq"/> is the sequence number of the event in the GC.</summary>
public sealed record ModuleGcPostResult(long Seq, bool Duplicate);

/// <summary>
/// Declares UI panes a module contributes. The server only records the declaration and tells connected clients which pane kinds
/// exist; a client decides whether to load one (doc 41b). The module never draws.
/// </summary>
public interface IModulePaneRegistry
{
    /// <summary>Declares or replaces the module's declaration of one pane. Needs <c>ui.pane:&lt;PaneId&gt;</c>.</summary>
    ValueTask RegisterAsync(ModulePaneDeclaration pane, CancellationToken ct = default);

    /// <summary>Removes one pane declaration of the module. Needs the same permission.</summary>
    ValueTask UnregisterAsync(string paneId, CancellationToken ct = default);
}

/// <summary>One pane declaration.</summary>
/// <param name="PaneId">The pane kind id, the scope of the grant, for example <c>weather/forecast</c>.</param>
/// <param name="Title">The title shown in the pane list.</param>
/// <param name="ViewDocument">The view document name the client loads for it (doc 41c), or null when the client module draws it.</param>
public sealed record ModulePaneDeclaration(string PaneId, string Title, string? ViewDocument = null);

/// <summary>
/// Schedules background work for the module on the host's <c>job</c> table (doc 03). The handler runs inside the host under the
/// module's own approved set, so a job cannot do what the module could not.
/// </summary>
public interface IModuleJobScheduler
{
    /// <summary>Queues one run of a named job. Needs <c>job:&lt;JobName&gt;</c>. Idempotent by <see cref="ModuleJobRequest.IdempotencyKey"/>.</summary>
    ValueTask<ModuleJobTicket> ScheduleAsync(ModuleJobRequest request, CancellationToken ct = default);

    /// <summary>Cancels a run that has not started. Needs the permission of the job the ticket belongs to.</summary>
    ValueTask<bool> CancelAsync(ModuleJobTicket ticket, CancellationToken ct = default);
}

/// <summary>A job run to queue.</summary>
/// <param name="JobName">The job, the scope of the grant, for example <c>weather/refresh</c>.</param>
/// <param name="IdempotencyKey">A repeat with the same key and job name returns the first ticket.</param>
/// <param name="RunAt">When it is due, or null for as soon as possible. The host clamps it to its own limits.</param>
/// <param name="Payload">Data for the handler, or null.</param>
public sealed record ModuleJobRequest(string JobName, string IdempotencyKey, DateTimeOffset? RunAt = null, JsonElement? Payload = null);

/// <summary>A queued run, named by its job and run id.</summary>
public sealed record ModuleJobTicket(string JobName, Guid RunId);

/// <summary>
/// Uses one capability of a connected device (<c>device</c>), for example <c>notify</c>. The server relays it to the owner's or
/// member's client; it never reaches a device the module was not granted.
/// </summary>
public interface IModuleDeviceAccess
{
    /// <summary>Calls one capability. Needs <c>device:&lt;Capability&gt;</c>.</summary>
    ValueTask<ModuleDeviceResult> InvokeAsync(ModuleDeviceCall request, CancellationToken ct = default);
}

/// <summary>One device call.</summary>
/// <param name="Capability">The capability, the scope of the grant, for example <c>notify</c>.</param>
/// <param name="Payload">Arguments in the capability's own shape, or null.</param>
public sealed record ModuleDeviceCall(string Capability, JsonElement? Payload = null);

/// <summary>The outcome of a device call. <paramref name="Delivered"/> is false when no device took it.</summary>
public sealed record ModuleDeviceResult(bool Delivered, JsonElement? Result = null);
