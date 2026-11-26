using System.Collections.Concurrent;
using System.Globalization;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Tools;

namespace Bazaar.Server.CompanyKnowledge;

/// <summary>
/// Who proposed each staged candidate and when. It backs <see cref="ICompanyCandidateProposers"/>, so
/// <c>company/candidates/**</c> is readable by the proposing agent. The staging service records into it; <see cref="RebuildAsync"/> restores it
/// from the Vault (the <c>company.proposed_by</c> field), so a restart does not lose it. Until it is rebuilt every agent is denied
/// (fail closed); the owner and maintenance are unaffected.
/// </summary>
public sealed class CompanyCandidateRegistry : ICompanyCandidateProposers
{
    private readonly ConcurrentDictionary<string, (string Proposer, DateTimeOffset At)> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string? ProposerOf(VaultPath candidatePath)
    {
        ArgumentNullException.ThrowIfNull(candidatePath);
        return _entries.TryGetValue(candidatePath.Value, out var entry) && entry.Proposer.StartsWith("agent:", StringComparison.Ordinal)
            ? entry.Proposer["agent:".Length..]
            : null;
    }

    /// <summary>Records a staged candidate.</summary>
    public void Record(string path, string proposer, DateTimeOffset at) => _entries[path] = (proposer, at);

    /// <summary>How many candidates <paramref name="proposer"/> staged at or after <paramref name="since"/>.</summary>
    public int CountSince(string proposer, DateTimeOffset since) =>
        _entries.Values.Count(e => string.Equals(e.Proposer, proposer, StringComparison.Ordinal) && e.At >= since);

    /// <summary>Forgets a candidate that was admitted or rejected.</summary>
    public void Remove(string path) => _entries.TryRemove(path, out _);

    /// <summary>Reloads the registry from the candidates in the Vault. A candidate that does not parse is left out, so it stays unreadable to agents.</summary>
    public async Task RebuildAsync(IMemoryVault vault, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vault);
        foreach (var path in await vault.ListAsync("company/candidates/", ct).ConfigureAwait(false))
        {
            var text = await vault.ReadNoteAsync(path, ct).ConfigureAwait(false);
            if (text is null || CompanyNote.Parse(text).Note is not { } note || note.Company.ProposedBy is not { } proposer
                || note.Memory.Status != Memory.Notes.MemoryNoteStatus.Active)
            {
                continue;
            }

            var at = CompanyUlid.TimeOf(note.Memory.Id) ?? DateTimeOffset.MinValue;
            Record(path.Value, proposer, at);
        }
    }
}

/// <summary>ULID helpers: ids are 26 Crockford base32 characters, the first ten carrying the millisecond time.</summary>
internal static class CompanyUlid
{
    private const string _alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New(DateTimeOffset at)
    {
        Span<byte> random = stackalloc byte[10];
        System.Security.Cryptography.RandomNumberGenerator.Fill(random);
        UInt128 low = 0;
        foreach (var b in random)
        {
            low = (low << 8) | b;
        }

        var value = ((UInt128)(ulong)at.ToUnixTimeMilliseconds() << 80) | low;
        var chars = new char[26];
        for (var i = 25; i >= 0; i--)
        {
            chars[i] = _alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return new string(chars);
    }

    public static DateTimeOffset? TimeOf(string ulid)
    {
        if (ulid.Length != 26)
        {
            return null;
        }

        ulong millis = 0;
        foreach (var c in ulid.AsSpan(0, 10))
        {
            var digit = _alphabet.IndexOf(char.ToUpper(c, CultureInfo.InvariantCulture), StringComparison.Ordinal);
            if (digit < 0)
            {
                return null;
            }

            millis = (millis << 5) | (uint)digit;
        }

        return millis > 253402300799999 ? null : DateTimeOffset.FromUnixTimeMilliseconds((long)millis);
    }
}
