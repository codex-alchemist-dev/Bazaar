using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Identity.Mount;

/// <summary>A canary seeded into one agent's mounted folder.</summary>
/// <param name="ServiceName">The service the decoy imitates (a name from <c>identity.canary_services</c>).</param>
/// <param name="CanaryId">Random id embedded in the decoy; the caller records it in the audit event on a hit.</param>
/// <param name="FileName">The decoy's file name inside the agent's folder.</param>
public sealed record SeededCanary(string ServiceName, string CanaryId, string FileName);

/// <summary>
/// The result of a canary use. The caller must record the audit event (this type does not touch the audit log) and must
/// apply the hard taint it carries.
/// </summary>
/// <param name="AgentId">The agent whose mount held the decoy.</param>
/// <param name="ServiceName">The service the decoy imitated.</param>
/// <param name="CanaryId">The id of the seeded decoy, or <c>null</c> when this writer did not seed one for that service.</param>
/// <param name="Taint">Always <see cref="TaintState.Hard"/>.</param>
public sealed record CanaryHit(string AgentId, string ServiceName, string? CanaryId, TaintState Taint);

/// <summary>
/// Writes an agent's mounted folder under the allow-list (docs/design/14-identity-service-and-passport.md, "Mounted folder").
/// Every write is validated first. Once an agent's mount is detached, writes for that agent throw until the process
/// restarts and the caller rebuilds state.
/// </summary>
/// <remarks>
/// Detach state is held in memory. The caller that owns the agent's lifecycle must persist it and call
/// <see cref="Detach"/> again after a restart if the mount is still considered detached.
/// </remarks>
public sealed class MountedFolderWriter(IMountFileSystem fileSystem)
{
    private readonly IMountFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly object _gate = new();

    /// <summary>Agent id to the reason its mount was detached.</summary>
    private readonly Dictionary<string, string> _detached = new(StringComparer.Ordinal);

    /// <summary>Agent id to service name to the canary seeded for that service.</summary>
    private readonly Dictionary<string, Dictionary<string, SeededCanary>> _canaries = new(StringComparer.Ordinal);

    /// <summary>Writes metadata and cookie-profile entries for an agent. Canaries are seeded only through <see cref="SeedCanaries"/>.</summary>
    /// <exception cref="ArgumentException">An entry is refused by the allow-list, or it is a canary.</exception>
    /// <exception cref="InvalidOperationException">The agent's mount is detached.</exception>
    public void Materialize(string agentId, IEnumerable<MountEntry> entries)
    {
        MountNameRules.EnsureSafeAgentId(agentId);
        ArgumentNullException.ThrowIfNull(entries);

        // Validate the whole batch before the first write, so a refused entry leaves nothing behind.
        var batch = entries.ToArray();
        foreach (var entry in batch)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.Kind == MountedFileKind.Canary)
            {
                throw new ArgumentException("Canaries are seeded with SeedCanaries, not materialized.", nameof(entries));
            }

            MountAllowList.EnsureEntryAllowed(entry);
        }

        lock (_gate)
        {
            EnsureNotDetached(agentId);
            foreach (var entry in batch)
            {
                WriteEntry(agentId, entry);
            }
        }
    }

    /// <summary>
    /// Seeds one decoy per service. Each decoy has clearly fake content and a random id. A previous decoy for the same
    /// service is replaced.
    /// </summary>
    /// <exception cref="InvalidOperationException">The agent's mount is detached.</exception>
    public IReadOnlyList<SeededCanary> SeedCanaries(string agentId, IEnumerable<string> canaryServices)
    {
        MountNameRules.EnsureSafeAgentId(agentId);
        ArgumentNullException.ThrowIfNull(canaryServices);

        var services = canaryServices.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var service in services)
        {
            MountNameRules.EnsureSafeServiceName(service);
        }

        lock (_gate)
        {
            EnsureNotDetached(agentId);

            if (!_canaries.TryGetValue(agentId, out var seeded))
            {
                seeded = new Dictionary<string, SeededCanary>(StringComparer.Ordinal);
                _canaries[agentId] = seeded;
            }

            var result = new List<SeededCanary>(services.Length);
            foreach (var service in services)
            {
                if (seeded.TryGetValue(service, out var previous))
                {
                    _fileSystem.DeleteFile(PathFor(agentId, previous.FileName));
                }

                var id = RandomNumberGenerator.GetHexString(32, lowercase: true);
                var fileName = $"canary-{id}.session.json";
                var content = Encoding.UTF8.GetBytes(
                    $"{{\"service\":\"{service}\",\"canary\":true,\"id\":\"{id}\"," +
                    $"\"cookies\":[{{\"name\":\"CANARY_NOT_A_CREDENTIAL\",\"value\":\"FAKE-{id}\"}}]}}");

                WriteEntry(agentId, new MountEntry(fileName, MountedFileKind.Canary, null, content));

                var canary = new SeededCanary(service, id, fileName);
                seeded[service] = canary;
                result.Add(canary);
            }

            return result;
        }
    }

    /// <summary>
    /// Marks the agent's mount detached and deletes every file it holds. The mount is marked first, so a failure
    /// partway through still refuses writes, and calling this again finishes the cleanup.
    /// </summary>
    /// <exception cref="ArgumentException">The reason is empty.</exception>
    public void Detach(string agentId, string reason)
    {
        MountNameRules.EnsureSafeAgentId(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_gate)
        {
            _detached.TryAdd(agentId, reason);

            foreach (var path in _fileSystem.ListFiles(agentId))
            {
                _fileSystem.DeleteFile(path);
            }

            _fileSystem.Detach(agentId);
        }
    }

    /// <summary>Whether the agent's mount is detached.</summary>
    public bool IsDetached(string agentId)
    {
        MountNameRules.EnsureSafeAgentId(agentId);
        lock (_gate)
        {
            return _detached.ContainsKey(agentId);
        }
    }

    /// <summary>
    /// Applies a taint change. Hard taint detaches the mount. Soft and clean taint leave it as it is.
    /// </summary>
    /// <returns>Whether the mount is detached after this call.</returns>
    public bool OnTaint(string agentId, TaintState state)
    {
        MountNameRules.EnsureSafeAgentId(agentId);
        if (state == TaintState.Hard)
        {
            Detach(agentId, "hard taint");
        }

        return IsDetached(agentId);
    }

    /// <summary>
    /// Handles a decoy being used. The result carries hard taint, and this writer also detaches the agent's mount so
    /// the response does not depend on the caller acting first. The caller records the audit event and holds the
    /// agent's sessions for review.
    /// </summary>
    public CanaryHit ReportCanaryUse(string agentId, string serviceName)
    {
        MountNameRules.EnsureSafeAgentId(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        lock (_gate)
        {
            string? canaryId = null;
            if (_canaries.TryGetValue(agentId, out var seeded) && seeded.TryGetValue(serviceName, out var canary))
            {
                canaryId = canary.CanaryId;
            }

            Detach(agentId, $"canary used for service '{serviceName}'");
            return new CanaryHit(agentId, serviceName, canaryId, TaintState.Hard);
        }
    }

    private void WriteEntry(string agentId, MountEntry entry)
    {
        // Validation runs again immediately before each write, so no path bypasses the allow-list.
        MountAllowList.EnsureEntryAllowed(entry);
        _fileSystem.WriteFile(PathFor(agentId, entry.FileName), entry.Content);
    }

    private void EnsureNotDetached(string agentId)
    {
        if (_detached.TryGetValue(agentId, out var reason))
        {
            throw new InvalidOperationException($"The mount for agent '{agentId}' is detached ({reason}); writes are refused.");
        }
    }

    private static string PathFor(string agentId, string fileName) => $"{agentId}/{fileName}";
}
