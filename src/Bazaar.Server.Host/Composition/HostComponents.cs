using Bazaar.Community;
using Bazaar.Core;
using Bazaar.Fun;
using Bazaar.RyuseiFuda.Kanban;
using Bazaar.RyuseiFuda.Orrery;
using Bazaar.RyuseiFuda.Roadmap;
using Bazaar.Server.Agents.Turns;
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
using Bazaar.Server.Vault;
using Bazaar.Server.Vms.Android;
using Bazaar.Server.Vms.Devices;
using Bazaar.Server.Vms.MicroVm;

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
        new KekComponent(),
        new GcComponent(),
        new CompanyComponent(),
        new CompanyBuildComponent(),
        new FunComponent(),
        new KanbanComponent(),
        new RyuseiScheduleComponent(),
        new RoadmapComponent(),
        new CommunityComponent(),
        new ConstellationComponent(),
        new BackupComponent(),
        new VaultComponent(),
        new MemoryComponent(),
        new AgentTurnsComponent(),
        new MailComponent(),
        new ModuleManagerComponent(),
        new DeviceProvidersComponent(),
        new AndroidComponent(),
        new MicroVmComponent(),
        new RemoteComponent(),
        new OpenAiChatAdapterComponent(),
        new AnthropicMessagesAdapterComponent(),
        new GeminiGenerateContentAdapterComponent(),
        new FreeLlmApiComponent(),
        new AnthropicDirectComponent(),
    ];
}
