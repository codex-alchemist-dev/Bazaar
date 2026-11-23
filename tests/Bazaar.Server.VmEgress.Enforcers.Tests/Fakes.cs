using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.Server.VmEgress.Rules;

namespace Bazaar.Server.VmEgress.Enforcers.Tests;

internal static class Samples
{
    public static EgressRules Rules(int port = 3128) => EgressRules.DenyAll(IPAddress.Parse("10.20.0.1"), port);
}

internal sealed class FakeResolver(EgressTarget target) : IEgressTargetResolver
{
    public List<string> Asked { get; } = [];

    public Task<EgressTarget> ResolveAsync(string agentId, CancellationToken ct = default)
    {
        Asked.Add(agentId);
        return Task.FromResult(target);
    }
}

/// <summary>Records every command and answers through a handler.</summary>
internal class FakeHostRunner : IHostCommandRunner
{
    private readonly object _gate = new();
    private int _running;

    public List<HostCommand> Commands { get; } = [];

    public int MaxConcurrent { get; private set; }

    public Func<HostCommand, HostCommandResult> Handler { get; set; } = _ => new HostCommandResult(0, string.Empty, string.Empty);

    public TimeSpan Delay { get; set; }

    public async Task<HostCommandResult> RunAsync(HostCommand command, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Commands.Add(command);
            _running++;
            MaxConcurrent = Math.Max(MaxConcurrent, _running);
        }

        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, ct);
            }

            ct.ThrowIfCancellationRequested();
            return Handler(command);
        }
        finally
        {
            lock (_gate)
            {
                _running--;
            }
        }
    }
}

/// <summary>
/// Simulates the Hyper-V side at the level of what the scripts promise: the apply script leaves exactly the rules it was given and
/// the read-back script prints them with string fields (wildcards as <c>*</c>, as a host may print them).
/// </summary>
internal sealed class HyperVSim : FakeHostRunner
{
    private readonly Dictionary<string, List<HyperVAclRule>> _acls = new(StringComparer.Ordinal);

    public Func<string, List<HyperVAclRule>, List<HyperVAclRule>> Tamper { get; set; } = (_, rules) => rules;

    public IReadOnlyList<HyperVAclRule> AclsOf(string vm) => _acls.TryGetValue(vm, out var l) ? l : [];

    public HyperVSim()
    {
        Handler = Handle;
    }

    private HostCommandResult Handle(HostCommand command)
    {
        using var p = JsonDocument.Parse(command.Environment![HyperVEgressScripts.ParametersVariable]);
        var vm = p.RootElement.GetProperty("VmName").GetString()!;
        if (command.StandardInput.TrimEnd() == HyperVEgressScripts.Apply)
        {
            using var rules = JsonDocument.Parse(p.RootElement.GetProperty("Rules").GetString()!);
            var list = rules.RootElement.EnumerateArray().Select(r => new HyperVAclRule(
                r.GetProperty("Action").GetString()!,
                r.GetProperty("Direction").GetString()!,
                r.GetProperty("RemoteIPAddress").GetString()!,
                r.GetProperty("Protocol").GetString(),
                r.GetProperty("RemotePort").ValueKind == JsonValueKind.Null ? null : r.GetProperty("RemotePort").GetInt32(),
                r.GetProperty("Weight").GetInt32(),
                r.GetProperty("Stateful").GetBoolean())).ToList();
            _acls[vm] = Tamper(vm, list);
            return new HostCommandResult(0, string.Empty, string.Empty);
        }

        Assert.Equal(HyperVEgressScripts.ReadBack, command.StandardInput.TrimEnd());
        var sb = new StringBuilder("[");
        sb.Append(string.Join(',', AclsOf(vm).Select(a =>
            $"{{\"Action\":\"{a.Action}\",\"Direction\":\"{a.Direction}\",\"RemoteIPAddress\":\"{a.RemoteAddress}\",\"Protocol\":\"{a.Protocol ?? "*"}\",\"RemotePort\":\"{(a.RemotePort?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "*")}\",\"Weight\":\"{a.Weight}\",\"Stateful\":\"{(a.Stateful ? "True" : "False")}\"}}")));
        sb.Append(']');
        return new HostCommandResult(0, sb.ToString(), string.Empty);
    }
}
