namespace Bazaar.Server.Subagents;

/// <summary>
/// The doc 17 harness policy layer in front of the runtime (doc 06 owns depth and concurrency limits and enforces them in
/// its own subagent runner behind <see cref="IRuntimeSubagentPort"/>). Every refusal is fail closed and happens before
/// anything starts; the spawn row is written before the runtime is called.
/// </summary>
public sealed class SubagentSpawner : ISubagentSpawner
{
    private readonly SubagentOptions _options;
    private readonly IRuntimeSubagentPort _runtime;
    private readonly ISubagentWorktreeAllocator _allocator;
    private readonly ISubagentTrace _trace;
    private readonly TimeProvider _time;

    // Writer reservations and busy worktrees sit behind one lock; spawns are rare and every critical section is short.
    private readonly object _gate = new();
    private readonly Dictionary<string, ChainState> _chains = new(StringComparer.Ordinal);
    private readonly HashSet<string> _busyWorktrees = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the spawner; throws when <paramref name="options"/> is invalid.</summary>
    public SubagentSpawner(
        SubagentOptions options,
        IRuntimeSubagentPort runtime,
        ISubagentWorktreeAllocator allocator,
        ISubagentTrace trace,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureValid();
        _options = options;
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<SubagentHandle> SpawnAsync(ParentContext parent, SubagentSpec spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = Validate(parent, spec);
        var subagentId = Guid.NewGuid().ToString("N");

        var reservation = plan.Worktree == WorktreeRequest.Own ? Reserve(parent, plan, subagentId) : null;
        WorktreeLease? lease = null;
        try
        {
            lease = await AllocateAsync(parent, plan, subagentId, cancellationToken).ConfigureAwait(false);

            try
            {
                await _trace.AppendAsync(Entry(SubagentTraceKind.Spawned, parent, plan, subagentId), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new SubagentRejectedException(SubagentRejection.TraceUnavailable, "The spawn record could not be written; spawn denied.", ex);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            await ReleaseAllAsync(parent.ChainId, lease, reservation).ConfigureAwait(false);
            throw;
        }

        var request = new RuntimeSubagentCall(
            subagentId, parent.ChainId, parent.ParentId, parent.Depth, spec.Task, plan.Tools, plan.ModelId, lease, plan.References);
        var cts = new CancellationTokenSource();
        var completion = Task.Run(() => RunAsync(parent, plan, subagentId, request, lease, reservation, cts), CancellationToken.None);
        return new SubagentHandle(subagentId, parent.ChainId, plan.ChildDepth, completion, cts.Cancel);
    }

    private async Task<SubagentResult> RunAsync(
        ParentContext parent,
        Plan plan,
        string subagentId,
        RuntimeSubagentCall request,
        WorktreeLease? lease,
        Reservation? reservation,
        CancellationTokenSource cts)
    {
        try
        {
            SubagentResult result;
            try
            {
                result = await _runtime.RunAsync(request, cts.Token).ConfigureAwait(false)
                    ?? Failed("The runner returned no result.");
            }
            catch (OperationCanceledException)
            {
                result = new SubagentResult(SubagentStatus.Cancelled, UntrustedText.Empty, SubagentUsage.None);
            }
            catch (SubagentRejectedException ex)
            {
                // The runtime refused (doc 06 depth, concurrency or tool limit); the limits are its, not ours.
                result = Failed($"The runtime refused the subagent ({ex.Reason}).");
            }
            catch (Exception)
            {
                // The exception text is host-side detail; the parent sees a generic failure.
                result = Failed("The subagent run failed.");
            }

            try
            {
                var finished = Entry(SubagentTraceKind.Finished, parent, plan, subagentId) with
                {
                    Status = result.Status,
                    Usage = result.Usage,
                };
                await _trace.AppendAsync(finished, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Fail closed: output nobody recorded is not handed to the parent.
                result = Failed("The finish record could not be written; result withheld.");
            }

            return result;
        }
        finally
        {
            await ReleaseAllAsync(parent.ChainId, lease, reservation).ConfigureAwait(false);
            cts.Dispose();
        }
    }

    private static SubagentResult Failed(string summary) =>
        new(SubagentStatus.Failed, new UntrustedText(summary), SubagentUsage.None);

    private SubagentTraceEntry Entry(SubagentTraceKind kind, ParentContext parent, Plan plan, string subagentId) =>
        new(kind, subagentId, parent.ParentId, parent.ChainId, plan.ChildDepth, plan.ModelId, plan.SortedTools, plan.Worktree, plan.Paths, _time.GetUtcNow());

    private async Task<WorktreeLease?> AllocateAsync(ParentContext parent, Plan plan, string subagentId, CancellationToken ct)
    {
        if (plan.Worktree == WorktreeRequest.None)
        {
            return null;
        }

        var readOnly = plan.Worktree == WorktreeRequest.ReadOnly;
        WorktreeLease lease;
        try
        {
            lease = await _allocator.AllocateAsync(new WorktreeAllocation(subagentId, parent.ParentId, readOnly, plan.Paths), ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The allocator returned no lease.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SubagentRejectedException(SubagentRejection.WorktreeUnavailable, "A worktree could not be allocated.", ex);
        }

        if (!readOnly)
        {
            bool taken;
            lock (_gate)
            {
                taken = !_busyWorktrees.Add(lease.Path);
            }

            if (taken)
            {
                // Another writer holds this path. Do not release the lease here: the allocator gave out a shared path,
                // and releasing it could free the other writer's worktree.
                throw new SubagentRejectedException(SubagentRejection.WorktreeInUse, "The worktree is already held by another writer.");
            }
        }

        return lease;
    }

    private Plan Validate(ParentContext parent, SubagentSpec spec)
    {
        if (parent is null || spec is null)
        {
            throw Reject(SubagentRejection.InvalidSpec, "Parent context and spec are required.");
        }

        if (string.IsNullOrWhiteSpace(parent.ParentId) || string.IsNullOrWhiteSpace(parent.ChainId)
            || parent.ToolAllowlist is null || parent.ModelWhitelist is null
            || parent.Depth < 0 || parent.Depth > _options.RuntimeMaxDepth)
        {
            throw Reject(SubagentRejection.InvalidSpec, "The parent context is malformed.");
        }

        if (spec.ParentSessionId != parent.ParentId)
        {
            throw Reject(SubagentRejection.InvalidSpec, "The spec names a different parent session.");
        }

        if (string.IsNullOrWhiteSpace(spec.Task) || string.IsNullOrWhiteSpace(spec.ModelId)
            || spec.ToolAllowlist is null || !Enum.IsDefined(spec.Worktree))
        {
            throw Reject(SubagentRejection.InvalidSpec, "Task, model, tool allowlist and worktree request are required.");
        }

        var childDepth = parent.Depth + 1;
        if (childDepth > _options.MaxDepth)
        {
            throw Reject(SubagentRejection.DepthExceeded, $"Depth {childDepth} exceeds the limit of {_options.MaxDepth}.");
        }

        if (!parent.ModelWhitelist.Contains(spec.ModelId))
        {
            throw Reject(SubagentRejection.ModelNotAllowed, "The model is not in the parent's whitelist.");
        }

        var parentTools = new HashSet<string>(parent.ToolAllowlist, StringComparer.Ordinal);
        var tools = new HashSet<string>(spec.ToolAllowlist, StringComparer.Ordinal);
        var extra = tools.Where(t => !parentTools.Contains(t)).Order(StringComparer.Ordinal).ToArray();
        if (extra.Length > 0)
        {
            throw Reject(SubagentRejection.ToolsNotSubset, "Tools not in the parent's allowlist: " + string.Join(", ", extra));
        }

        var authority = tools.Where(_options.AuthorityToolNames.Contains).Order(StringComparer.Ordinal).ToArray();
        if (authority.Length > 0)
        {
            throw Reject(SubagentRejection.AuthorityToolRequested, "Children never hold merge or approval tools: " + string.Join(", ", authority));
        }

        if (spec.Worktree != WorktreeRequest.Own)
        {
            var writes = tools.Where(_options.WriteToolNames.Contains).Order(StringComparer.Ordinal).ToArray();
            if (writes.Length > 0)
            {
                throw Reject(SubagentRejection.WriteToolsForReadOnly, "Read-only children cannot have write tools: " + string.Join(", ", writes));
            }
        }

        // A child at the depth limit cannot spawn: the spawn tool is removed, not just refused later.
        if (childDepth >= _options.MaxDepth)
        {
            tools.RemoveWhere(t => string.Equals(t, _options.SpawnToolName, StringComparison.OrdinalIgnoreCase));
        }

        var paths = NormalisePaths(spec);
        if (spec.Worktree == WorktreeRequest.Own && _options.DisjointWriters && paths.Count == 0)
        {
            throw Reject(SubagentRejection.PathsOverlap, "Disjoint writers must declare the paths they will touch.");
        }

        var references = (spec.References ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).ToArray();
        return new Plan(childDepth, spec.ModelId, tools, tools.Order(StringComparer.Ordinal).ToArray(), spec.Worktree, paths, references);
    }

    private static List<string> NormalisePaths(SubagentSpec spec)
    {
        if (spec.DeclaredPaths is null || spec.DeclaredPaths.Count == 0)
        {
            return [];
        }

        if (spec.Worktree != WorktreeRequest.Own)
        {
            throw Reject(SubagentRejection.InvalidSpec, "Only writers declare paths.");
        }

        var result = new List<string>();
        foreach (var raw in spec.DeclaredPaths)
        {
            var p = (raw ?? string.Empty).Trim().Replace('\\', '/');
            while (p.StartsWith("./", StringComparison.Ordinal))
            {
                p = p[2..];
            }

            p = p.TrimEnd('/');
            var bad = p.Length == 0 || p[0] == '/' || p.Contains(':') || p.Any(char.IsControl)
                || p.Split('/').Any(s => s.Length == 0 || s == "." || s == "..");
            if (bad)
            {
                throw Reject(SubagentRejection.PathsOverlap, $"Unsafe or empty declared path: '{raw}'.");
            }

            result.Add(p);
        }

        return result;
    }

    private static bool Overlaps(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase)
        || b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase);

    private static SubagentRejectedException Reject(SubagentRejection reason, string message) => new(reason, message);

    private Reservation Reserve(ParentContext parent, Plan plan, string subagentId)
    {
        lock (_gate)
        {
            var chain = GetChain(parent.ChainId);
            foreach (var other in chain.Writers)
            {
                if (!_options.DisjointWriters)
                {
                    if (other.ParentId == parent.ParentId)
                    {
                        throw Reject(SubagentRejection.WriterBusy, "This parent already has a running writer; disjoint writers are off.");
                    }
                }
                else if (plan.Paths.Any(p => other.Paths.Any(q => Overlaps(p, q))))
                {
                    throw Reject(SubagentRejection.PathsOverlap, "Declared paths overlap a running writer.");
                }
            }

            var reservation = new Reservation(subagentId, parent.ParentId, plan.Paths);
            chain.Writers.Add(reservation);
            return reservation;
        }
    }

    private ChainState GetChain(string chainId)
    {
        if (!_chains.TryGetValue(chainId, out var chain))
        {
            chain = new ChainState();
            _chains[chainId] = chain;
        }

        return chain;
    }

    private async Task ReleaseAllAsync(string chainId, WorktreeLease? lease, Reservation? reservation)
    {
        try
        {
            if (lease is not null)
            {
                if (!lease.ReadOnly)
                {
                    lock (_gate)
                    {
                        _busyWorktrees.Remove(lease.Path);
                    }
                }

                try
                {
                    await _allocator.ReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A failing release must not keep the slot; the allocator owns its own cleanup and audit.
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_chains.TryGetValue(chainId, out var chain))
                {
                    if (reservation is not null)
                    {
                        chain.Writers.Remove(reservation);
                    }

                    Trim(chainId);
                }
            }
        }
    }

    private void Trim(string chainId)
    {
        if (_chains.TryGetValue(chainId, out var chain) && chain.Writers.Count == 0)
        {
            _chains.Remove(chainId);
        }
    }

    private sealed record Plan(
        int ChildDepth,
        string ModelId,
        IReadOnlySet<string> Tools,
        IReadOnlyList<string> SortedTools,
        WorktreeRequest Worktree,
        IReadOnlyList<string> Paths,
        IReadOnlyList<string> References);

    private sealed record Reservation(string SubagentId, string ParentId, IReadOnlyList<string> Paths);

    private sealed class ChainState
    {
        public List<Reservation> Writers { get; } = [];
    }
}
