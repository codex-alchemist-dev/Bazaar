using Bazaar.Archive;
using Bazaar.Community;
using Bazaar.Conduit;
using Bazaar.Core;
using Bazaar.Fun;
using Bazaar.Roles;
using Bazaar.GitHub;
using Bazaar.RyuseiFuda.Kanban;
using Bazaar.RyuseiFuda.Orrery;
using Bazaar.RyuseiFuda.Roadmap;
using Bazaar.Server.AccountApproval;
using Bazaar.Server.AccountApproval.OwnerQueue;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.BackupPolicy;
using Bazaar.Server.BrowserPool;
using Bazaar.Server.BrowserPool.Audit;
using Bazaar.Server.BrowserPool.Display;
using Bazaar.Server.BrowserPool.Egress;
using Bazaar.Server.BrowserPool.Gate;
using Bazaar.Server.BrowserPool.Image;
using Bazaar.Server.BrowserPool.LinuxHost;
using Bazaar.Server.Calendar;
using Bazaar.Server.Community.GitHub;
using Bazaar.Server.Company;
using Bazaar.Server.CompanyBuild;
using Bazaar.Server.CompanyMemory;
using Bazaar.Server.Components.Backup;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Constellation;
using Bazaar.Server.Council;
using Bazaar.Server.Garnet;
using Bazaar.Server.Gateway.Adapters.Anthropic;
using Bazaar.Server.Gateway.Adapters.Gemini;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.AnthropicDirect;
using Bazaar.Server.Gateway.Catalogue;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Gateway.Retention;
using Bazaar.Server.Gc;
using Bazaar.Server.Health;
using Bazaar.Server.Gc.Skimmer;
using Bazaar.Server.GcMetadata;
using Bazaar.Server.GcReactions;
using Bazaar.Server.Hive;
using Bazaar.Server.Interrupts;
using Bazaar.Server.Kodo;
using Bazaar.Server.Host.Components.Audit;
using Bazaar.Server.Host.Components.ReadAudit;
using Bazaar.Server.Host.Components.Settings;
using Bazaar.Server.Identity.Kek;
using Bazaar.Server.Mail;
using Bazaar.Server.SignIn;
using Bazaar.Server.MailPolicy;
using Bazaar.Server.Memory.GitVault;
using Bazaar.Server.Mirror;
using Bazaar.Server.Modules;
using Bazaar.Server.Org;
using Bazaar.Server.Presence;
using Bazaar.Server.Retention;
using Bazaar.Server.Setup.Host;
using Bazaar.Server.Analytics;
using Bazaar.Server.Analytics.Git;
using Bazaar.Server.Analytics.Postgres;
using Bazaar.Server.ThinkingGc;
using Bazaar.Server.Skimmer;
using Bazaar.Server.TalentPool.Component;
using Bazaar.Server.Valkey;
using Bazaar.Server.Vault;
using Bazaar.Server.SoulProfile;
using Bazaar.Server.Walkthrough;
using Bazaar.Server.VmDensity;
using Bazaar.Server.VmDensity.Vms;
using Bazaar.Server.VmDisplay;
using Bazaar.Server.VmDisplay.AgentTools;
using Bazaar.Server.VmDisplay.AgentTools.Vms;
using Bazaar.Server.VmDisplay.Audit;
using Bazaar.Server.VmDisplay.Gate;
using Bazaar.Server.VmDisplay.GuestChannel;
using Bazaar.Server.VmDisplay.HyperV;
using Bazaar.Server.VmDisplay.Kvm;
using Bazaar.Server.WebAdmin;
using Bazaar.Server.Wire.Discovery;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Every Bazaar Component this host ships (docs/design/BAZAAR-COMPONENTS.md). The service graph registers them and the migration
/// runner orders their SQL namespaces from the same list, so the two never disagree about which components exist.
/// </summary>
public static class HostComponents
{
    public static IReadOnlyList<IBazaarComponent> All { get; } =
    [
        new AuditComponent(),
        new ReadAuditComponent(),
        new SettingsComponent(),
        new SetupComponent(),
        new WalkthroughComponent(),
        new SoulProfileComponent(),
        new FirstRunAdaptersComponent(),
        new RetentionComponent(),
        new ArchiveComponent(),
        new SkimmerComponent(),
        new GcSkimmerComponent(),
        new MirrorComponent(),
        new GatewayRetentionComponent(),
        new CatalogueComponent(),
        new KekComponent(),
        new GcComponent(),
        new GcMetadataComponent(),
        new ThinkingGcComponent(),
        new GcReactionsComponent(),
        new CalendarComponent(),
        new InterruptsComponent(),
        new OrgComponent(),
        new CompanyBuildComponent(),
        new FunComponent(),
        new KanbanComponent(),
        new RyuseiScheduleComponent(),
        new RoadmapComponent(),
        new CommunityComponent(),
        new RolesComponent(),
        new SignInComponent(),
        new WebAdminComponent(),
        new CommunityGitHubComponent(),
        new ConstellationComponent(),
        new BackupComponent(),
        new VaultComponent(),
        new MemoryComponent(),
        new HiveComponent(),
        new HealthServerComponent(),
        new AnalyticsComponent(),
        new AnalyticsPostgresComponent(),
        new AnalyticsGitComponent(),
        new AccountApprovalComponent(),
        new OwnerQueueComponent(),
        new CouncilComponent(),
        new CompanyMemoryComponent(),
        new TalentPoolComponent(),
        new AgentTurnsComponent(),
        new MailComponent(),
        new ModuleManagerComponent(),
        new BackupPolicyComponent(),
        new MailPolicyComponent(),
        new ConduitComponent(),
        new WireDiscoveryComponent(),
        new RemoteComponent(),
        new GitHubComponent(),
        new OpenAiChatAdapterComponent(),
        new AnthropicMessagesAdapterComponent(),
        new GeminiGenerateContentAdapterComponent(),
        new FreeLlmApiComponent(),
        new AnthropicDirectComponent(),
        new Bazaar.Server.Providers.ClaudeCode.ClaudeCodeComponent(),
        new Bazaar.Server.Providers.ClaudeCode.Windows.ClaudeCodeWindowsComponent(),
        new ValkeyComponent(),
        new GarnetComponent(),
        new KodoServerComponent(),
        new PresenceComponent(),
        new Bazaar.Server.Engines.EnginesComponent(),
        new Bazaar.Server.EngineGcRecorder.EngineGcRecorderComponent(),
        new Bazaar.Server.EngineRelay.EngineRelayComponent(),
        new Bazaar.Server.EngineTool.EngineToolComponent(),
        new Bazaar.Server.Presence.Sources.PresenceSourcesComponent(),
        // Screenshot and input for agent VMs, the density governor and the shared browser pool (docs/design/59). Every port the host
        // does not supply yet keeps the module's own fail-closed default, so none of this does anything until the operator and the
        // missing host ports are in place. The pool's Linux host and image check register nothing off Linux, and Hyper-V answers
        // "not supported" off Windows; the KVM adapter is limited to Linux here.
        new VmDisplayComponent(),
        new VmDisplayGateComponent(),
        // The guest source's only adapter: IDisplayGuestExec over the channel directory. No channel is registered yet, so every guest call fails.
        new VmDisplayGuestChannelComponent(),
        new VmDisplayAuditComponent(),
        new VmDisplayAgentToolsComponent(),
        // Off unless Bazaar:VmDisplayAgentTargets:Enabled is true; until then it registers nothing and no screen tool is offered. It starts before
        // the tools component whatever its place in this list (the kernel orders by dependencies, then id).
        new VmAgentDisplayTargetsComponent(),
        new HyperVComponent(),
        new OsLimitedComponent(new KvmComponent(), OperatingSystem.IsLinux),
        new VmDensityComponent(),
        // Gives the governor its VM directory and control over the VM module. Every VM counts as busy until something can read exec, terminal
        // and browser activity, so it saves nothing; nothing in the host calls the governor yet either.
        new VmDensityVmsComponent(),
        new BrowserPoolComponent(),
        new BrowserPoolDisplayComponent(),
        new BrowserPoolLinuxHostComponent(),
        new BrowserPoolImageComponent(),
        new BrowserPoolGateComponent(),
        new BrowserPoolAuditComponent(),
        new BrowserPoolEgressComponent(),
    ];

    /// <summary>
    /// Registers a component only where its platform exists, for adapters that do not check the operating system themselves. The
    /// manifest is unchanged, so the graph, the migration order and the module manager still see the component everywhere.
    /// </summary>
    private sealed class OsLimitedComponent(IBazaarComponent inner, Func<bool> supported) : IBazaarComponent
    {
        public ComponentManifest Manifest => inner.Manifest;

        public void AddServices(IServiceCollection services, IConfiguration configuration)
        {
            if (supported())
            {
                inner.AddServices(services, configuration);
            }
        }
    }
}
