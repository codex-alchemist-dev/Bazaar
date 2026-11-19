namespace Bazaar.Server.Agents.Turns.Tools;

/// <summary>
/// One tool offered to agents, under the exact name a model calls it by. Components register one of these per tool. The host
/// collects them into the <see cref="ToolRegistry"/> that <see cref="AgentTurnsComponent"/> provides, so a component never needs
/// the registry itself and a tool is offered only when its component registered it (doc 06, Tool dispatch). A tool a model
/// should see also needs a <c>ModelToolDefinition</c> with the same name, which <see cref="ToolCatalogue"/> collects.
/// </summary>
public sealed record ToolRegistration(string Name, ITool Tool);
