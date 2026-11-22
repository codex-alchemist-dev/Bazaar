using Bazaar.Server.ModuleHost;

namespace Bazaar.ModulePermissions.HostServices;

/// <summary>The guarded services of one module. Each property is a decorator that asks the gate before every call.</summary>
internal sealed class GuardedModuleHostServices : IModuleHostServices
{
    public GuardedModuleHostServices(string moduleId, IModuleHostServices real, IModulePermissionGate gate)
    {
        ModuleId = moduleId;
        Data = new GuardedModuleData(moduleId, Required(real.Data), gate);
        Gc = new GuardedModuleGcPoster(moduleId, Required(real.Gc), gate);
        Panes = new GuardedModulePaneRegistry(moduleId, Required(real.Panes), gate);
        Jobs = new GuardedModuleJobScheduler(moduleId, Required(real.Jobs), gate);
        Devices = new GuardedModuleDeviceAccess(moduleId, Required(real.Devices), gate);
    }

    public string ModuleId { get; }

    public IModuleData Data { get; }

    public IModuleGcPoster Gc { get; }

    public IModulePaneRegistry Panes { get; }

    public IModuleJobScheduler Jobs { get; }

    public IModuleDeviceAccess Devices { get; }

    private static T Required<T>(T? service)
        where T : class =>
        service ?? throw new InvalidOperationException($"The host service factory returned no {typeof(T).Name}.");
}

/// <summary><c>data.read:&lt;component&gt;</c> and <c>data.write:&lt;component&gt;</c>.</summary>
internal sealed class GuardedModuleData(string moduleId, IModuleData real, IModulePermissionGate gate) : IModuleData
{
    public ValueTask<ModuleDataPage> ReadAsync(ModuleDataReadRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        gate.Demand(moduleId, new ModuleAccess(ModulePermissionClasses.DataRead, request.Component));
        return real.ReadAsync(request, ct);
    }

    public ValueTask<ModuleDataWriteResult> WriteAsync(ModuleDataWriteRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A read grant never stands in for a write: the two classes are demanded apart.
        gate.Demand(moduleId, new ModuleAccess(ModulePermissionClasses.DataWrite, request.Component));
        return real.WriteAsync(request, ct);
    }
}

/// <summary><c>gc.post</c>, with no target: only an unscoped grant covers it.</summary>
internal sealed class GuardedModuleGcPoster(string moduleId, IModuleGcPoster real, IModulePermissionGate gate) : IModuleGcPoster
{
    public ValueTask<ModuleGcPostResult> PostAsync(ModuleGcPost post, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(post);
        gate.Demand(moduleId, new ModuleAccess(ModulePermissionClasses.GcPost));
        return real.PostAsync(post, ct);
    }
}

/// <summary><c>ui.pane:&lt;paneId&gt;</c>, for a declaration and for its removal.</summary>
internal sealed class GuardedModulePaneRegistry(string moduleId, IModulePaneRegistry real, IModulePermissionGate gate) : IModulePaneRegistry
{
    public ValueTask RegisterAsync(ModulePaneDeclaration pane, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pane);
        gate.Demand(moduleId, new ModuleAccess(ModulePermissionClasses.UiPane, pane.PaneId));
        return real.RegisterAsync(pane, ct);
    }

    public ValueTask UnregisterAsync(string paneId, CancellationToken ct = default)
    {
        gate.Demand(moduleId, new ModuleAccess(ModulePermissionClasses.UiPane, paneId));
        return real.UnregisterAsync(paneId, ct);
    }
}

/// <summary><c>job:&lt;jobName&gt;</c>, for a new run and for cancelling one.</summary>
internal sealed class GuardedModuleJobScheduler(string moduleId, IModuleJobScheduler real, IModulePermissionGate gate) : IModuleJobScheduler
{
    public ValueTask<ModuleJobTicket> ScheduleAsync(ModuleJobRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        gate.Demand(moduleId, new ModuleAccess(ModulePermissionClasses.BackgroundJob, request.JobName));
        return ScheduleCoreAsync(request, ct);
    }

    public ValueTask<bool> CancelAsync(ModuleJobTicket ticket, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        // The job name on a ticket is the caller's word. The real scheduler matches the run id to this module and that name.
        gate.Demand(moduleId, new ModuleAccess(ModulePermissionClasses.BackgroundJob, ticket.JobName));
        return real.CancelAsync(ticket, ct);
    }

    private async ValueTask<ModuleJobTicket> ScheduleCoreAsync(ModuleJobRequest request, CancellationToken ct)
    {
        var ticket = await real.ScheduleAsync(request, ct).ConfigureAwait(false);
        if (ticket is null || !string.Equals(ticket.JobName, request.JobName, StringComparison.Ordinal))
        {
            // A ticket for another job would let the module cancel under a name it was not checked for.
            throw new InvalidOperationException("The job scheduler returned a ticket for another job.");
        }

        return ticket;
    }
}

/// <summary><c>device:&lt;capability&gt;</c>.</summary>
internal sealed class GuardedModuleDeviceAccess(string moduleId, IModuleDeviceAccess real, IModulePermissionGate gate) : IModuleDeviceAccess
{
    public ValueTask<ModuleDeviceResult> InvokeAsync(ModuleDeviceCall request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        gate.Demand(moduleId, new ModuleAccess(ModulePermissionClasses.Device, request.Capability));
        return real.InvokeAsync(request, ct);
    }
}
