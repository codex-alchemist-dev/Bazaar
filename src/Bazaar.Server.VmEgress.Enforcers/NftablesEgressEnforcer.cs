using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Bazaar.Server.VmEgress.Rules;

namespace Bazaar.Server.VmEgress.Enforcers;

/// <summary>
/// <see cref="IEgressEnforcer"/> for a Linux host: one nftables <c>bridge</c> table per agent (doc 16 WP9, layer 1), from
/// <see cref="NftablesRuleset"/>, loaded with <c>nft -f -</c> (ruleset on standard input, argv fixed) through an
/// <see cref="IRootCommandRunner"/>. After the load it lists the table and compares it with the generated text; a difference
/// or any failure throws.
/// </summary>
/// <remarks>
/// Ordering guarantee: the generated ruleset is <c>add table</c>, <c>delete table</c>, then the table body, and nft applies one
/// <c>-f</c> file as a single transaction, so the old rules are replaced atomically and there is no moment with the guest
/// unfiltered. The comparison is line by line after folding white space and the named priority <c>filter</c> to <c>-200</c>
/// (nft prints the bridge hook priority by name); that normalisation is UNVERIFIED until the NUC run. One guest interface per
/// agent is supported (<see cref="EgressTarget.GuestInterface"/>). Calls for one agent are serialised.
/// </remarks>
public sealed partial class NftablesEgressEnforcer : IEgressEnforcer
{
    private const string _tablePrefix = "bazaar_egress_";

    private readonly IEgressTargetResolver _resolver;
    private readonly IRootCommandRunner _root;
    private readonly KeyedGate _gate = new();

    /// <summary>Creates the enforcer.</summary>
    public NftablesEgressEnforcer(IEgressTargetResolver resolver, IRootCommandRunner root)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(root);
        _resolver = resolver;
        _root = root;
    }

    /// <inheritdoc />
    public async Task ApplyAsync(string agentId, EgressRules rules, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(rules);
        using var _ = await _gate.EnterAsync(agentId, ct).ConfigureAwait(false);
        var (tag, iface) = await ResolveAsync(agentId, ct).ConfigureAwait(false);
        await LoadAndVerifyAsync(tag, NftablesRuleset.Generate(tag, iface, rules), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task BlockAllAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        using var _ = await _gate.EnterAsync(agentId, ct).ConfigureAwait(false);
        var (tag, iface) = await ResolveAsync(agentId, ct).ConfigureAwait(false);
        await LoadAndVerifyAsync(tag, NftablesRuleset.GenerateBlockAll(tag, iface), ct).ConfigureAwait(false);
    }

    /// <summary>The table tag for an agent: the id itself when it is a plain identifier, otherwise <c>h</c> plus 31 hex digits of its SHA-256.</summary>
    internal static string TagFor(string agentId)
    {
        if (TagPattern().IsMatch(agentId))
        {
            return agentId;
        }

        return "h" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(agentId)))[..31];
    }

    internal static string Normalize(string text)
    {
        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = Whitespace().Replace(raw.Trim(), " ");
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("add table ", StringComparison.Ordinal) || line.StartsWith("delete table ", StringComparison.Ordinal))
            {
                continue;
            }

            lines.Add(line.Replace("priority filter", "priority -200", StringComparison.Ordinal));
        }

        return string.Join('\n', lines);
    }

    private async Task<(string Tag, string Interface)> ResolveAsync(string agentId, CancellationToken ct)
    {
        var target = await _resolver.ResolveAsync(agentId, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(target.GuestInterface))
        {
            throw new EgressEnforcementException("The agent has no guest interface to enforce egress on.");
        }

        if (!InterfacePattern().IsMatch(target.GuestInterface))
        {
            throw new ArgumentException("Interface name must be 1 to 15 letters, digits, dot, dash or underscore.");
        }

        return (TagFor(agentId), target.GuestInterface);
    }

    private async Task LoadAndVerifyAsync(string tag, string ruleset, CancellationToken ct)
    {
        var load = await _root.RunAsync(new RootCommand(["-f", "-"], ruleset), ct).ConfigureAwait(false);
        Require(load, "loading the ruleset");

        var list = await _root.RunAsync(new RootCommand(["list", "table", "bridge", _tablePrefix + tag], string.Empty), ct).ConfigureAwait(false);
        Require(list, "listing the table back");
        if (Normalize(ruleset) != Normalize(list.StandardOutput))
        {
            throw new EgressEnforcementException("The nftables table does not match the generated ruleset. Egress is not confirmed in place.");
        }
    }

    private static void Require(HostCommandResult result, string step)
    {
        if (result.ExitCode != 0)
        {
            var error = result.StandardError.Trim();
            throw new EgressEnforcementException($"nft failed while {step} (exit code {result.ExitCode}): {(error.Length <= 200 ? error : error[..200])}");
        }
    }

    // \A and \z, not ^ and $: the generators' own patterns accept a trailing newline, so this module checks again.
    [GeneratedRegex(@"\A[A-Za-z0-9_]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_.-]{1,15}\z", RegexOptions.CultureInvariant)]
    private static partial Regex InterfacePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
