using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bazaar.Server.VmEgress.Rules;

namespace Bazaar.Server.VmEgress.Enforcers;

/// <summary>
/// <see cref="IEgressEnforcer"/> for a Windows host: Hyper-V extended port ACLs on the agent's VM adapters (doc 16 WP9, layer 1).
/// The rules come from <see cref="HyperVAclRules"/>. Per VM, one constant script (<see cref="HyperVEgressScripts.Apply"/>) makes
/// the adapter's ACLs exactly the generated set, and a second constant script reads them back; the result is compared with the
/// generated rules and any difference throws, so success is never reported on trust.
/// </summary>
/// <remarks>
/// Ordering guarantee (fail closed): inside the apply script the deny-all rules are added before any other rule is removed, so a
/// replace never passes through a state with no rules, and rules that already match stay in place. A first apply to a VM that has
/// no rules yet adds the deny rules before the allow rules for the same reason. UNVERIFIED on a live host until the NUC
/// Category=Hyperv run. Calls for one agent are serialised so an apply and a block cannot interleave.
/// </remarks>
public sealed partial class HyperVEgressEnforcer : IEgressEnforcer
{
    private readonly IEgressTargetResolver _resolver;
    private readonly IHostCommandRunner _runner;
    private readonly KeyedGate _gate = new();

    /// <summary>Creates the enforcer.</summary>
    public HyperVEgressEnforcer(IEgressTargetResolver resolver, IHostCommandRunner runner)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(runner);
        _resolver = resolver;
        _runner = runner;
    }

    /// <inheritdoc />
    public async Task ApplyAsync(string agentId, EgressRules rules, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(rules);
        var acls = HyperVAclRules.Generate(rules);
        using var _ = await _gate.EnterAsync(agentId, ct).ConfigureAwait(false);
        var vms = await ResolveVmsAsync(agentId, ct).ConfigureAwait(false);
        foreach (var vm in vms)
        {
            await ReplaceAsync(vm, acls, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task BlockAllAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var acls = HyperVAclRules.GenerateBlockAll();
        using var _ = await _gate.EnterAsync(agentId, ct).ConfigureAwait(false);
        var vms = await ResolveVmsAsync(agentId, ct).ConfigureAwait(false);

        // The kill switch tries every VM even when one fails, then reports all failures together.
        List<Exception>? failures = null;
        foreach (var vm in vms)
        {
            try
            {
                await ReplaceAsync(vm, acls, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                (failures ??= []).Add(e);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("Blocking the agent's VM adapters did not complete on every VM.", failures);
        }
    }

    internal static bool IsValidVmName(string? name) => name is not null && VmNamePattern().IsMatch(name);

    private async Task<IReadOnlyList<string>> ResolveVmsAsync(string agentId, CancellationToken ct)
    {
        var target = await _resolver.ResolveAsync(agentId, ct).ConfigureAwait(false);
        if (target.VmNames is not { Count: > 0 })
        {
            throw new EgressEnforcementException("The agent has no VM to enforce egress on.");
        }

        // Every name is checked before any command runs, so one bad name changes nothing.
        foreach (var name in target.VmNames)
        {
            if (!IsValidVmName(name))
            {
                throw new EgressEnforcementException("A VM name has characters that are not allowed.");
            }
        }

        return target.VmNames;
    }

    private async Task ReplaceAsync(string vm, IReadOnlyList<HyperVAclRule> acls, CancellationToken ct)
    {
        var apply = await RunAsync(HyperVEgressScripts.Apply, vm, RulesJson(acls), ct).ConfigureAwait(false);
        Require(apply, "applying the extended ACLs");

        var read = await RunAsync(HyperVEgressScripts.ReadBack, vm, null, ct).ConfigureAwait(false);
        Require(read, "reading the extended ACLs back");
        var actual = ParseReadBack(read.StandardOutput);
        var expected = acls.Select(Key).Order(StringComparer.Ordinal).ToList();
        if (!expected.SequenceEqual(actual))
        {
            throw new EgressEnforcementException(
                $"The extended ACLs on the VM do not match the generated rules (expected {expected.Count}, found {actual.Count}). Egress is not confirmed in place.");
        }
    }

    private Task<HostCommandResult> RunAsync(string script, string vm, string? rulesJson, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string> { [_paramsKey] = ParametersJson(vm, rulesJson) };
        return _runner.RunAsync(
            new HostCommand(HostTool.PowerShell, ["-NoProfile", "-NonInteractive", "-Command", "-"], script + "\n", parameters),
            ct);
    }

    private static void Require(HostCommandResult result, string step)
    {
        if (result.ExitCode != 0)
        {
            var error = result.StandardError.Trim();
            throw new EgressEnforcementException($"PowerShell failed while {step} (exit code {result.ExitCode}): {(error.Length <= 200 ? error : error[..200])}");
        }
    }

    private static List<string> ParseReadBack(string output)
    {
        try
        {
            using var doc = JsonDocument.Parse(output.Trim().Length == 0 ? "[]" : output);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new EgressEnforcementException("The ACL read-back was not a JSON array.");
            }

            var keys = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                keys.Add(Key(
                    Text(item, "Action"),
                    Text(item, "Direction"),
                    Text(item, "RemoteIPAddress"),
                    Text(item, "Protocol"),
                    Text(item, "RemotePort"),
                    Text(item, "Weight"),
                    Text(item, "Stateful")));
            }

            keys.Sort(StringComparer.Ordinal);
            return keys;
        }
        catch (JsonException e)
        {
            throw new EgressEnforcementException("The ACL read-back was not valid JSON.", e);
        }
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : string.Empty;

    private static string Key(HyperVAclRule r) => Key(
        r.Action,
        r.Direction,
        r.RemoteAddress,
        r.Protocol ?? string.Empty,
        r.RemotePort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        r.Weight.ToString(CultureInfo.InvariantCulture),
        r.Stateful ? "true" : "false");

    // Same normalisation as Get-AclKey in the apply script: wildcards for protocol and port are empty, text is case-folded.
    private static string Key(string action, string direction, string address, string protocol, string port, string weight, string stateful)
    {
        static string Wild(string v) => v.Trim() is "" or "*" ? string.Empty : v.Trim().Equals("Any", StringComparison.OrdinalIgnoreCase) ? string.Empty : v.Trim();
        var w = int.TryParse(weight, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n.ToString(CultureInfo.InvariantCulture) : weight.Trim();
        return string.Join('|', action.Trim(), direction.Trim(), address.Trim(), Wild(protocol), Wild(port), w, stateful.Trim()).ToLowerInvariant();
    }

    private static string ParametersJson(string vm, string? rulesJson)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("VmName", vm);
            if (rulesJson is not null)
            {
                w.WriteString("Rules", rulesJson);
            }

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    internal static string RulesJson(IReadOnlyList<HyperVAclRule> acls)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartArray();
            foreach (var acl in acls)
            {
                w.WriteStartObject();
                w.WriteString("Action", acl.Action);
                w.WriteString("Direction", acl.Direction);
                w.WriteString("RemoteIPAddress", acl.RemoteAddress);
                if (acl.Protocol is null)
                {
                    w.WriteNull("Protocol");
                }
                else
                {
                    w.WriteString("Protocol", acl.Protocol);
                }

                if (acl.RemotePort is { } port)
                {
                    w.WriteNumber("RemotePort", port);
                }
                else
                {
                    w.WriteNull("RemotePort");
                }

                w.WriteNumber("Weight", acl.Weight);
                w.WriteBoolean("Stateful", acl.Stateful);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private const string _paramsKey = HyperVEgressScripts.ParametersVariable;

    // \A and \z, not ^ and $, so that a trailing newline does not pass. Same shape as the VM name rule in the display adapter.
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex VmNamePattern();
}
