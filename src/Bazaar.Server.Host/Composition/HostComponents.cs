using Bazaar.Archive;
using Bazaar.Server.Community;
using Bazaar.Conduit;
using Bazaar.Core;
using Bazaar.Server.Fun;
using Bazaar.Server.GitHub;
using Bazaar.Roles;
using Bazaar.RyuseiFuda.Kanban;
using Bazaar.RyuseiFuda.Orrery;
using Bazaar.RyuseiFuda.Roadmap;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Company;
using Bazaar.Server.Calendar;
using Bazaar.Server.CompanyBuild;
using Bazaar.Server.Components.Backup;
using Bazaar.Server.Components.Remote;
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
using Bazaar.Server.GcReactions;
using Bazaar.Server.Hive;
using Bazaar.Server.Host.Components.Audit;
using Bazaar.Server.Host.Components.Settings;
using Bazaar.Server.Identity.Kek;
using Bazaar.Server.Mail;
using Bazaar.Server.Memory.GitVault;
using Bazaar.Server.Modules;
using Bazaar.Server.Mirror;
using Bazaar.Server.Org;
using Bazaar.Server.Retention;
using Bazaar.Server.Setup.Host;
using Bazaar.Server.TalentPool.Component;
using Bazaar.Server.Valkey;
using Bazaar.Server.Vault;
using Bazaar.Server.WebAdmin;
using Bazaar.Server.Wire.Discovery;

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
        new SettingsComponent(),
        new SetupComponent(),
        new RetentionComponent(),
        new ArchiveComponent(),
        new MirrorComponent(),
        new GatewayRetentionComponent(),
        new CatalogueComponent(),
        new KekComponent(),
        new GcComponent(),
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
        new WebAdminComponent(),
        new ConstellationComponent(),
        new BackupComponent(),
        new VaultComponent(),
        new MemoryComponent(),
        new HiveComponent(),
        new TalentPoolComponent(),
        new AgentTurnsComponent(),
        new MailComponent(),
        new ModuleManagerComponent(),
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
    ];
}
