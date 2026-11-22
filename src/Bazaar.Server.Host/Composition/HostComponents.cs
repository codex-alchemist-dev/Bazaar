using Bazaar.Community;
using Bazaar.Core;
using Bazaar.Fun;
using Bazaar.Roles;
using Bazaar.GitHub;
using Bazaar.RyuseiFuda.Kanban;
using Bazaar.RyuseiFuda.Orrery;
using Bazaar.RyuseiFuda.Roadmap;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.AuditReader;
using Bazaar.Server.MemberRoster;
using Bazaar.Server.StatusProviders;
using Bazaar.Server.Company;
using Bazaar.Server.CompanyBuild;
using Bazaar.Server.Components.Backup;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Constellation;
using Bazaar.Server.Gateway.Adapters.Anthropic;
using Bazaar.Server.Gateway.Adapters.Gemini;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.AnthropicDirect;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Gc;
using Bazaar.Server.Host.Components.Audit;
using Bazaar.Server.Host.Components.Settings;
using Bazaar.Server.Identity.Kek;
using Bazaar.Server.Mail;
using Bazaar.Server.Memory.GitVault;
using Bazaar.Server.Modules;
using Bazaar.Server.Retention;
using Bazaar.Server.Vault;
using Bazaar.Server.WebAdmin;

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
        new RetentionComponent(),
        new KekComponent(),
        new GcComponent(),
        new CompanyComponent(),
        new CompanyBuildComponent(),
        new FunComponent(),
        new KanbanComponent(),
        new RyuseiScheduleComponent(),
        new RoadmapComponent(),
        new CommunityComponent(),
        new RolesComponent(),
        new WebAdminComponent(),
        new AuditReaderComponent(),
        new MemberRosterComponent(),
        new StatusProvidersComponent(),
        new ConstellationComponent(),
        new BackupComponent(),
        new VaultComponent(),
        new MemoryComponent(),
        new AgentTurnsComponent(),
        new MailComponent(),
        new ModuleManagerComponent(),
        new RemoteComponent(),
        new GitHubComponent(),
        new OpenAiChatAdapterComponent(),
        new AnthropicMessagesAdapterComponent(),
        new GeminiGenerateContentAdapterComponent(),
        new FreeLlmApiComponent(),
        new AnthropicDirectComponent(),
    ];
}
