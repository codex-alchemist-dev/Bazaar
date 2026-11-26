using System.Text.Json;
using Bazaar.Server.ModuleHost;

namespace Bazaar.ModulePermissions.HostServices.Tests;

public class GuardedModuleHostServicesTests
{
    private const string _module = "bazaar.weather";
    private const string _other = "bazaar.other";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Json(string text) => JsonElement.Parse(text);

    private sealed class ListSink : IModulePermissionDenialSink
    {
        public List<ModulePermissionDenial> Denials { get; } = [];

        public void Record(ModulePermissionDenial denial) => Denials.Add(denial);
    }

    /// <summary>The real services of one module. Every call that reaches them is written down.</summary>
    private sealed class RealServices(string moduleId) : IModuleHostServices, IModuleData, IModuleGcPoster, IModulePaneRegistry, IModuleJobScheduler, IModuleDeviceAccess
    {
        public List<string> Calls { get; } = [];

        public string? TicketJobName { get; set; }

        public bool NoJobs { get; set; }

        public string ModuleId { get; set; } = moduleId;

        public IModuleData Data => this;

        public IModuleGcPoster Gc => this;

        public IModulePaneRegistry Panes => this;

        public IModuleJobScheduler Jobs => NoJobs ? null! : this;

        public IModuleDeviceAccess Devices => this;

        public ValueTask<ModuleDataPage> ReadAsync(ModuleDataReadRequest request, CancellationToken ct = default)
        {
            Calls.Add($"read {request.Component}/{request.Collection}");
            return ValueTask.FromResult(new ModuleDataPage([], null));
        }

        public ValueTask<ModuleDataWriteResult> WriteAsync(ModuleDataWriteRequest request, CancellationToken ct = default)
        {
            Calls.Add($"write {request.Component}/{request.Collection}/{request.Key}");
            return ValueTask.FromResult(new ModuleDataWriteResult(true, "1"));
        }

        public ValueTask<ModuleGcPostResult> PostAsync(ModuleGcPost post, CancellationToken ct = default)
        {
            Calls.Add($"post {post.ClientMsgId}");
            return ValueTask.FromResult(new ModuleGcPostResult(1, false));
        }

        public ValueTask RegisterAsync(ModulePaneDeclaration pane, CancellationToken ct = default)
        {
            Calls.Add($"pane+ {pane.PaneId}");
            return ValueTask.CompletedTask;
        }

        public ValueTask UnregisterAsync(string paneId, CancellationToken ct = default)
        {
            Calls.Add($"pane- {paneId}");
            return ValueTask.CompletedTask;
        }

        public ValueTask<ModuleJobTicket> ScheduleAsync(ModuleJobRequest request, CancellationToken ct = default)
        {
            Calls.Add($"job+ {request.JobName}");
            return ValueTask.FromResult(new ModuleJobTicket(TicketJobName ?? request.JobName, Guid.NewGuid()));
        }

        public ValueTask<bool> CancelAsync(ModuleJobTicket ticket, CancellationToken ct = default)
        {
            Calls.Add($"job- {ticket.JobName}");
            return ValueTask.FromResult(true);
        }

        public ValueTask<ModuleDeviceResult> InvokeAsync(ModuleDeviceCall request, CancellationToken ct = default)
        {
            Calls.Add($"device {request.Capability}");
            return ValueTask.FromResult(new ModuleDeviceResult(true));
        }
    }

    private sealed class RealFactory : IModuleHostServicesFactory
    {
        public Dictionary<string, RealServices> Made { get; } = [];

        public Func<string, IModuleHostServices?>? Override { get; set; }

        public IModuleHostServices CreateFor(string moduleId)
        {
            if (Override is not null)
            {
                return Override(moduleId)!;
            }

            var real = new RealServices(moduleId);
            Made[moduleId] = real;
            return real;
        }
    }

    private sealed class Rig
    {
        public Rig(params string[] permissions)
        {
            Gate = new ModulePermissionGate(Sink, TimeProvider.System);
            Gate.Activate(_module, permissions);
            Services = new GuardedModuleHostServicesFactory(Factory, Gate).CreateFor(_module);
        }

        public ListSink Sink { get; } = new();

        public ModulePermissionGate Gate { get; }

        public RealFactory Factory { get; } = new();

        public IModuleHostServices Services { get; }

        public List<string> Calls => Factory.Made[_module].Calls;
    }

    private static async Task<ModulePermissionDeniedException> Refused(Func<ValueTask> call) =>
        await Assert.ThrowsAsync<ModulePermissionDeniedException>(async () => await call());

    // ---- nothing granted ----

    [Fact]
    public async Task A_module_with_no_permissions_reaches_no_real_service_and_every_refusal_is_audited()
    {
        var rig = new Rig();
        var s = rig.Services;

        await Refused(async () => await s.Data.ReadAsync(new ModuleDataReadRequest("kanban", "cards"), Ct));
        await Refused(async () => await s.Data.WriteAsync(new ModuleDataWriteRequest("kanban", "cards", "1", Json("{}")), Ct));
        await Refused(async () => await s.Gc.PostAsync(new ModuleGcPost(Guid.NewGuid(), "m1", "hello"), Ct));
        await Refused(async () => await s.Panes.RegisterAsync(new ModulePaneDeclaration("weather/forecast", "Forecast"), Ct));
        await Refused(async () => await s.Panes.UnregisterAsync("weather/forecast", Ct));
        await Refused(async () => await s.Jobs.ScheduleAsync(new ModuleJobRequest("weather/refresh", "k1"), Ct));
        await Refused(async () => await s.Jobs.CancelAsync(new ModuleJobTicket("weather/refresh", Guid.NewGuid()), Ct));
        await Refused(async () => await s.Devices.InvokeAsync(new ModuleDeviceCall("notify"), Ct));

        Assert.Empty(rig.Calls);
        Assert.Equal(8, rig.Sink.Denials.Count);
        Assert.All(rig.Sink.Denials, d => Assert.Equal(ModuleAccessDecision.DeniedNotGranted, d.Decision));
        Assert.All(rig.Sink.Denials, d => Assert.Equal(_module, d.ModuleId));
    }

    [Fact]
    public async Task Every_granted_service_reaches_the_real_one()
    {
        var rig = new Rig("data.read:kanban", "data.write:kanban", "gc.post", "ui.pane:weather/*", "job:weather/refresh", "device:notify");
        var s = rig.Services;

        await s.Data.ReadAsync(new ModuleDataReadRequest("kanban", "cards"), Ct);
        await s.Data.WriteAsync(new ModuleDataWriteRequest("kanban", "cards", "1", Json("{}")), Ct);
        await s.Gc.PostAsync(new ModuleGcPost(Guid.NewGuid(), "m1", "hello"), Ct);
        await s.Panes.RegisterAsync(new ModulePaneDeclaration("weather/forecast", "Forecast"), Ct);
        await s.Panes.UnregisterAsync("weather/forecast", Ct);
        var ticket = await s.Jobs.ScheduleAsync(new ModuleJobRequest("weather/refresh", "k1"), Ct);
        Assert.True(await s.Jobs.CancelAsync(ticket, Ct));
        await s.Devices.InvokeAsync(new ModuleDeviceCall("notify"), Ct);

        Assert.Equal(
            ["read kanban/cards", "write kanban/cards/1", "post m1", "pane+ weather/forecast", "pane- weather/forecast", "job+ weather/refresh", "job- weather/refresh", "device notify"],
            rig.Calls);
        Assert.Empty(rig.Sink.Denials);
        Assert.Equal(_module, s.ModuleId);
    }

    // ---- data ----

    [Fact]
    public async Task A_read_grant_does_not_allow_a_write_and_a_write_grant_does_not_allow_a_read()
    {
        var reader = new Rig("data.read:kanban");
        await reader.Services.Data.ReadAsync(new ModuleDataReadRequest("kanban", "cards"), Ct);
        var e = await Refused(async () => await reader.Services.Data.WriteAsync(new ModuleDataWriteRequest("kanban", "cards", "1", Json("{}")), Ct));
        Assert.Equal(ModulePermissionClasses.DataWrite, e.Access.Class);
        Assert.Equal(["read kanban/cards"], reader.Calls);

        var writer = new Rig("data.write:kanban");
        await Refused(async () => await writer.Services.Data.ReadAsync(new ModuleDataReadRequest("kanban", "cards"), Ct));
        await writer.Services.Data.WriteAsync(new ModuleDataWriteRequest("kanban", "cards", "1", Json("{}")), Ct);
        Assert.Equal(["write kanban/cards/1"], writer.Calls);
    }

    [Theory]
    [InlineData("vault")]
    [InlineData("Kanban")]
    [InlineData("kanban ")]
    [InlineData("kanban/")]
    [InlineData("kanban/cards")]
    [InlineData("kanban/../vault")]
    [InlineData("kanban\0")]
    [InlineData("kanban​")]
    [InlineData("*")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_data_grant_covers_its_own_component_only(string? component)
    {
        var rig = new Rig("data.read:kanban", "data.write:kanban");

        await Refused(async () => await rig.Services.Data.ReadAsync(new ModuleDataReadRequest(component!, "cards"), Ct));
        await Refused(async () => await rig.Services.Data.WriteAsync(new ModuleDataWriteRequest(component!, "cards", "1", Json("{}")), Ct));

        Assert.Empty(rig.Calls);
        Assert.Equal(2, rig.Sink.Denials.Count);
    }

    [Fact]
    public async Task The_collection_and_key_of_a_request_never_widen_the_component()
    {
        var rig = new Rig("data.read:kanban");

        // The grant is on the component. What the module writes in the other fields is the component's to validate.
        await rig.Services.Data.ReadAsync(new ModuleDataReadRequest("kanban", "../vault/notes", "../../x"), Ct);
        await Refused(async () => await rig.Services.Data.ReadAsync(new ModuleDataReadRequest("vault", "notes"), Ct));

        Assert.Equal(["read kanban/../vault/notes"], rig.Calls);
    }

    // ---- gc.post ----

    [Fact]
    public async Task A_scoped_gc_post_grant_does_not_cover_the_unscoped_post()
    {
        var rig = new Rig("gc.post:general");

        await Refused(async () => await rig.Services.Gc.PostAsync(new ModuleGcPost(Guid.NewGuid(), "m1", "hello"), Ct));

        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task Reading_gc_history_does_not_allow_posting()
    {
        var rig = new Rig("data.read:gc", "data.write:gc");

        await Refused(async () => await rig.Services.Gc.PostAsync(new ModuleGcPost(Guid.NewGuid(), "m1", "hello"), Ct));

        Assert.Empty(rig.Calls);
    }

    // ---- panes, jobs, devices ----

    [Theory]
    [InlineData("weather")]
    [InlineData("weather/")]
    [InlineData("weather-admin/forecast")]
    [InlineData("weather/../admin/pane")]
    [InlineData("weather//forecast")]
    [InlineData("weather\\forecast")]
    [InlineData("weather/*")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_pane_prefix_grant_covers_whole_segments_below_it_only(string? paneId)
    {
        var rig = new Rig("ui.pane:weather/*");

        await Refused(async () => await rig.Services.Panes.RegisterAsync(new ModulePaneDeclaration(paneId!, "Title"), Ct));
        await Refused(async () => await rig.Services.Panes.UnregisterAsync(paneId!, Ct));

        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task A_job_grant_covers_scheduling_and_cancelling_that_job_only()
    {
        var rig = new Rig("job:weather/refresh");
        var s = rig.Services;

        await Refused(async () => await s.Jobs.ScheduleAsync(new ModuleJobRequest("weather/purge", "k1"), Ct));
        await Refused(async () => await s.Jobs.CancelAsync(new ModuleJobTicket("weather/purge", Guid.NewGuid()), Ct));
        await Refused(async () => await s.Jobs.CancelAsync(new ModuleJobTicket("backup/nightly", Guid.NewGuid()), Ct));

        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task A_ticket_for_another_job_from_the_real_scheduler_is_not_handed_to_the_module()
    {
        var rig = new Rig("job:weather/refresh");
        rig.Factory.Made[_module].TicketJobName = "backup/nightly";

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await rig.Services.Jobs.ScheduleAsync(new ModuleJobRequest("weather/refresh", "k1"), Ct));
    }

    [Theory]
    [InlineData("camera")]
    [InlineData("notify/all")]
    [InlineData("Notify")]
    [InlineData("notify\n")]
    [InlineData(null)]
    public async Task A_device_grant_covers_its_own_capability_only(string? capability)
    {
        var rig = new Rig("device:notify");

        await Refused(async () => await rig.Services.Devices.InvokeAsync(new ModuleDeviceCall(capability!), Ct));

        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task One_class_never_stands_in_for_another()
    {
        // The same scope text under every other class: none of them opens the job scheduler.
        var rig = new Rig("data.read:notify", "data.write:notify", "ui.pane:notify", "device:notify", "fs:notify", "secret:notify", "process:notify", "gc.post");

        await Refused(async () => await rig.Services.Jobs.ScheduleAsync(new ModuleJobRequest("notify", "k1"), Ct));

        Assert.Empty(rig.Calls);
    }

    // ---- lifetime and identity ----

    [Fact]
    public async Task Services_handed_out_earlier_are_refused_once_the_module_is_deactivated()
    {
        var rig = new Rig("data.read:kanban", "gc.post");
        await rig.Services.Data.ReadAsync(new ModuleDataReadRequest("kanban", "cards"), Ct);

        Assert.True(rig.Gate.Deactivate(_module));

        var e = await Refused(async () => await rig.Services.Data.ReadAsync(new ModuleDataReadRequest("kanban", "cards"), Ct));
        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, e.Decision);
        await Refused(async () => await rig.Services.Gc.PostAsync(new ModuleGcPost(Guid.NewGuid(), "m1", "hello"), Ct));
        Assert.Equal(["read kanban/cards"], rig.Calls);
    }

    [Fact]
    public async Task Services_follow_the_set_in_force_now_not_the_one_at_creation()
    {
        var rig = new Rig("data.read:kanban", "device:notify");
        rig.Gate.Deactivate(_module);
        rig.Gate.Activate(_module, ["data.read:kanban"]);

        await rig.Services.Data.ReadAsync(new ModuleDataReadRequest("kanban", "cards"), Ct);
        await Refused(async () => await rig.Services.Devices.InvokeAsync(new ModuleDeviceCall("notify"), Ct));

        Assert.Equal(["read kanban/cards"], rig.Calls);
    }

    [Fact]
    public async Task Services_made_for_a_module_that_is_not_running_refuse_everything()
    {
        var rig = new Rig("gc.post");
        var stranger = new GuardedModuleHostServicesFactory(rig.Factory, rig.Gate).CreateFor(_other);

        var e = await Refused(async () => await stranger.Gc.PostAsync(new ModuleGcPost(Guid.NewGuid(), "m1", "hello"), Ct));

        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, e.Decision);
        Assert.Empty(rig.Factory.Made[_other].Calls);
    }

    [Fact]
    public async Task One_modules_grants_never_serve_another_module()
    {
        var rig = new Rig("device:notify");
        rig.Gate.Activate(_other, ["data.read:kanban"]);
        var other = new GuardedModuleHostServicesFactory(rig.Factory, rig.Gate).CreateFor(_other);

        await Refused(async () => await other.Devices.InvokeAsync(new ModuleDeviceCall("notify"), Ct));
        await Refused(async () => await rig.Services.Data.ReadAsync(new ModuleDataReadRequest("kanban", "cards"), Ct));

        Assert.Empty(rig.Calls);
        Assert.Empty(rig.Factory.Made[_other].Calls);
    }

    [Fact]
    public void Services_the_real_factory_made_for_another_module_are_refused()
    {
        var rig = new Rig("gc.post");
        rig.Factory.Override = _ => new RealServices(_other);

        Assert.Throws<InvalidOperationException>(() => new GuardedModuleHostServicesFactory(rig.Factory, rig.Gate).CreateFor(_module));
    }

    [Fact]
    public void A_missing_real_service_fails_at_creation_not_at_first_use()
    {
        var rig = new Rig("gc.post");
        rig.Factory.Override = id => new RealServices(id) { NoJobs = true };
        var factory = new GuardedModuleHostServicesFactory(rig.Factory, rig.Gate);

        Assert.Throws<InvalidOperationException>(() => factory.CreateFor(_module));

        rig.Factory.Override = _ => null;
        Assert.Throws<InvalidOperationException>(() => factory.CreateFor(_module));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_module_id_is_required(string? moduleId)
    {
        var rig = new Rig();

        Assert.ThrowsAny<ArgumentException>(() => new GuardedModuleHostServicesFactory(rig.Factory, rig.Gate).CreateFor(moduleId!));
    }

    [Fact]
    public async Task A_null_request_is_an_argument_error_and_reaches_nothing()
    {
        var rig = new Rig("data.read:kanban", "data.write:kanban", "gc.post", "ui.pane:a", "job:a", "device:a");
        var s = rig.Services;

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await s.Data.ReadAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await s.Data.WriteAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await s.Gc.PostAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await s.Panes.RegisterAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await s.Jobs.ScheduleAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await s.Jobs.CancelAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await s.Devices.InvokeAsync(null!, Ct));

        Assert.Empty(rig.Calls);
    }

    // ---- router ----

    private sealed class Source(string component, bool supportsWrite) : IModuleDataSource
    {
        public List<string> Calls { get; } = [];

        public string Component { get; } = component;

        public bool SupportsWrite { get; } = supportsWrite;

        public ValueTask<ModuleDataPage> ReadAsync(ModuleDataReadRequest request, CancellationToken ct = default)
        {
            Calls.Add("read");
            return ValueTask.FromResult(new ModuleDataPage([], null));
        }

        public ValueTask<ModuleDataWriteResult> WriteAsync(ModuleDataWriteRequest request, CancellationToken ct = default)
        {
            Calls.Add("write");
            return ValueTask.FromResult(new ModuleDataWriteResult(true, "1"));
        }
    }

    [Fact]
    public async Task The_router_sends_a_request_to_the_source_of_exactly_that_component()
    {
        var kanban = new Source("kanban", supportsWrite: true);
        var vault = new Source("vault", supportsWrite: false);
        var router = new ModuleDataRouter([kanban, vault]);

        await router.ReadAsync(new ModuleDataReadRequest("vault", "notes"), Ct);
        await router.WriteAsync(new ModuleDataWriteRequest("kanban", "cards", "1", Json("{}")), Ct);

        Assert.Equal(["read"], vault.Calls);
        Assert.Equal(["write"], kanban.Calls);
    }

    [Fact]
    public async Task The_router_refuses_a_write_to_a_read_only_component_and_an_unknown_component()
    {
        var vault = new Source("vault", supportsWrite: false);
        var router = new ModuleDataRouter([vault]);

        await Assert.ThrowsAsync<ModuleDataUnavailableException>(async () => await router.WriteAsync(new ModuleDataWriteRequest("vault", "notes", "1", Json("{}")), Ct));
        await Assert.ThrowsAsync<ModuleDataUnavailableException>(async () => await router.ReadAsync(new ModuleDataReadRequest("Vault", "notes"), Ct));
        await Assert.ThrowsAsync<ModuleDataUnavailableException>(async () => await router.ReadAsync(new ModuleDataReadRequest(null!, "notes"), Ct));

        Assert.Empty(vault.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("a*")]
    [InlineData("a\n")]
    public void The_router_refuses_a_source_whose_component_no_permission_can_name(string component)
    {
        Assert.Throws<ArgumentException>(() => new ModuleDataRouter([new Source(component, supportsWrite: false)]));
    }

    [Fact]
    public void The_router_refuses_two_sources_for_one_component()
    {
        Assert.Throws<ArgumentException>(() => new ModuleDataRouter([new Source("vault", false), new Source("vault", true)]));
    }
}
