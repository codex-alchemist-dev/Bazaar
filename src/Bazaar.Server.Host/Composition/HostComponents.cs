using Bazaar.Archive;
using Bazaar.Server.Gc.Skimmer;
using Bazaar.Server.Skimmer;
using Bazaar.Community;
using Bazaar.Conduit;
using Bazaar.Core;
using Bazaar.Fun;
using Bazaar.Roles;
using Bazaar.GitHub;
using Bazaar.RyuseiFuda.Kanban;
using Bazaar.RyuseiFuda.Orrery;
using Bazaar.RyuseiFuda.Roadmap;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.BackupPolicy;
using Bazaar.Server.BrowserPool;
using Bazaar.Server.BrowserPool.Display;
using Bazaar.Server.BrowserPool.Image;
using Bazaar.Server.BrowserPool.LinuxHost;
using Bazaar.Server.Calendar;
using Bazaar.Server.CompanyBuild;
using Bazaar.Server.CompanyMemory;
using Bazaar.Server.Components.Backup;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Council;
using Bazaar.Server.Constellation;
using Bazaar.Server.Garnet;
using Bazaar.Server.Gateway.Adapters.Anthropic;
using Bazaar.Server.Gateway.Adapters.Gemini;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.AnthropicDirect;
using Bazaar.Server.Gateway.Catalogue;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Gateway.Retention;
using Bazaar.Server.Gc;
using Bazaar.Server.GcMetadata;
using Bazaar.Server.GcReactions;
using Bazaar.Server.Hive;
using Bazaar.Server.Host.Components.Audit;
using Bazaar.Server.Host.Components.ReadAudit;
using Bazaar.Server.Host.Components.Settings;
using Bazaar.Server.Identity.Kek;
using Bazaar.Server.Mail;
using Bazaar.Server.MailPolicy;
using Bazaar.Server.SignIn;
using Bazaar.Server.Memory.GitVault;
using Bazaar.Server.Modules;
using Bazaar.Server.Mirror;
using Bazaar.Server.Org;
using Bazaar.Server.Retention;
using Bazaar.Server.Setup.Host;
using Bazaar.Server.ThinkingGc;
using Bazaar.Server.TalentPool.Component;
using Bazaar.Server.Valkey;
using Bazaar.Server.Vault;
using Bazaar.Server.VmDensity;
using Bazaar.Server.VmDisplay;
using Bazaar.Server.VmDisplay.AgentTools;
using Bazaar.Server.VmDisplay.Audit;
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
        new ConstellationComponent(),
        new BackupComponent(),
        new VaultComponent(),
        new MemoryComponent(),
        new HiveComponent(),
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
        // Screenshot and input for agent VMs, the density governor and the shared browser pool (docs/design/59). Every port the host
        // does not supply yet keeps the module's own fail-closed default, so none of this does anything until the operator and the
        // missing host ports are in place. The pool's Linux host and image check register nothing off Linux, and Hyper-V answers
        // "not supported" off Windows; the KVM adapter is limited to Linux here.
        new VmDisplayComponent(),
        new VmDisplayAuditComponent(),
        new VmDisplayAgentToolsComponent(),
        new HyperVComponent(),
        new OsLimitedComponent(new KvmComponent(), OperatingSystem.IsLinux),
        new VmDensityComponent(),
        new BrowserPoolComponent(),
        new BrowserPoolDisplayComponent(),
        new BrowserPoolLinuxHostComponent(),
        new BrowserPoolImageComponent(),
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
