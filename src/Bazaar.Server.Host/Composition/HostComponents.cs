using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Components;
using Bazaar.Server.Gateway.Adapters.Anthropic;
using Bazaar.Server.Gateway.Adapters.Gemini;
using Bazaar.Server.Gateway.Adapters.OpenAi;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Gc;
using Bazaar.Server.Host.Components.Audit;
using Bazaar.Server.Host.Components.Settings;
using Bazaar.Server.Kanban;
using Bazaar.Server.Vault;

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
        new GcComponent(),
        new KanbanComponent(),
        new VaultComponent(),
        new AgentTurnsComponent(),
        new OpenAiChatAdapterComponent(),
        new AnthropicMessagesAdapterComponent(),
        new GeminiGenerateContentAdapterComponent(),
        new FreeLlmApiComponent(),
    ];
}
