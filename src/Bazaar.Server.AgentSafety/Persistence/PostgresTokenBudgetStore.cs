using Bazaar.Contracts.Numerics;
using Bazaar.Server.Security.Budgets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.AgentSafety.Persistence;

/// <summary>
/// Postgres <see cref="ITokenBudgetStore"/> over the <c>agent_token_spend</c> table (agent_safety migrations 0001, 0005 and
/// 0006), with the large tier of <see cref="PostgresLargeTokenAmountStore"/> for amounts beyond the <c>numeric</c> limits.
/// Each spend is one row. Every instant is the caller's instant, never the database clock, so the service's
/// <see cref="TimeProvider"/> stays the only source of time. Works under the runtime role (<c>bazaar_app</c>).
/// </summary>
/// <remarks>
/// Instants are stored at microsecond precision, the precision Postgres keeps, so <see cref="Append"/> cuts them to
/// microseconds before writing. A zero-token spend is not written: it counts for nothing, and the table requires
/// <c>tokens &gt; 0</c>.
/// <para>
/// <b>Amounts.</b> Two tiers, one representation per spend (doc 48, DECIDED 11.4). An amount within
/// <see cref="TokenAmountBounds.PostgresNumeric"/> is stored inline in <c>tokens</c>, unconstrained <c>numeric</c> (DECIDED
/// 11.3), sent as text cast to <c>numeric</c> and read back, sums included, as <c>::text</c>: the driver's
/// <see cref="decimal"/> and <see cref="System.Numerics.BigInteger"/> mappings cannot carry every scale. A larger amount is
/// staged in the large tier in its own transactions, then sealed in the transaction that inserts the spend row, which refers to
/// it through <c>tokens_large</c>. Nothing is clamped, wrapped or truncated.
/// </para>
/// <para>
/// <b>Window sums.</b> Inline rows are summed in SQL. Large rows in the week window are then added by the streamed adder of
/// <see cref="TokenAmountChunkCodec"/>, each read once; after each addition the running sum is compared with the largest
/// window limit by the streamed comparer, and once it is over that limit, every window that still has rows to add is over
/// its limit and no further chunk is read. A large row with more integer digits than that limit is over it from its header
/// alone. If the SQL sum overflows <c>numeric</c>, the inline rows are added by the streamed adder too. A window sum is
/// materialised for the shared decision only when its digits fit <see cref="TokenBudgetOptions.InMemoryMaxDigits"/>; one
/// that does not, and one over every limit, counts as over its limit, so the call is denied. Intermediate sums are held in
/// memory while they fit that maximum and are staged in the large tier, then deleted, when they do not.
/// </para>
/// <para>
/// <b>Integrity.</b> A large row whose header is missing or unsealed, or whose chunks fail their structure or SHA-256 check
/// on a full read, is a data integrity finding: it is logged and the call is denied, never allowed and never thrown to the
/// caller of <see cref="TryConsume"/>. <see cref="LoadSince"/> throws <see cref="TokenAmountIntegrityException"/>.
/// </para>
/// <para>
/// <b>Concurrency.</b> <see cref="TryConsume"/> runs in one transaction: it takes a per-agent transaction-scoped advisory
/// lock (<c>pg_advisory_xact_lock(hashtext(agent))</c>) first, sums the agent's spends for the three window starts, inserts
/// only if the spend fits all three, then commits. A denial rolls back and writes nothing. Calls for one agent from any number
/// of processes queue on the lock, so admitted spend never exceeds a window; calls for other agents do not wait (a
/// <c>hashtext</c> collision between two agents only makes them share a lock, never weakens it). A large spend is staged on a
/// second pooled connection while the lock is held, and sealed under the lock.
/// </para>
/// </remarks>
public sealed partial class PostgresTokenBudgetStore : ITokenBudgetStore
{
    private const string _insert = "INSERT INTO agent_token_spend (agent_id, at, tokens) VALUES ($1, $2, $3::numeric)";
    private const string _insertLarge = "INSERT INTO agent_token_spend (agent_id, at, tokens_large) VALUES ($1, $2, $3)";
    private const string _loadSince =
        "SELECT s.at, s.tokens::text, s.tokens_large, l.integer_digits, l.fraction_digits, l.sha256, l.state " +
        "FROM agent_token_spend s LEFT JOIN token_amount_large l ON l.id = s.tokens_large " +
        "WHERE s.agent_id = $1 AND s.at > $2 ORDER BY s.at, s.id";
    private const string _lock = "SELECT pg_advisory_xact_lock(hashtext($1))";
    private const string _sums =
        "SELECT coalesce(sum(tokens) FILTER (WHERE at > $2), 0)::text, " +
        "coalesce(sum(tokens) FILTER (WHERE at > $3), 0)::text, " +
        "coalesce(sum(tokens), 0)::text " +
        "FROM agent_token_spend WHERE agent_id = $1 AND at > $4";
    private const string _largeRows =
        "SELECT s.at, s.tokens_large, l.integer_digits, l.fraction_digits, l.sha256, l.state " +
        "FROM agent_token_spend s LEFT JOIN token_amount_large l ON l.id = s.tokens_large " +
        "WHERE s.agent_id = $1 AND s.at > $2 AND s.tokens_large IS NOT NULL ORDER BY s.at DESC, s.id DESC";
    private const string _inlineRows =
        "SELECT at, tokens::text FROM agent_token_spend WHERE agent_id = $1 AND at > $2 AND tokens IS NOT NULL ORDER BY at DESC, id DESC";
    private const string _sumsSavepoint = "budget_sums";

    // One statement: the spends go, and with them the large amounts they own (header and chunks).
    private const string _prune =
        "WITH gone AS (DELETE FROM agent_token_spend WHERE at <= $1 RETURNING tokens_large), " +
        "owned AS (SELECT tokens_large AS id FROM gone WHERE tokens_large IS NOT NULL), " +
        "chunks AS (DELETE FROM token_amount_large_chunk WHERE amount_id IN (SELECT id FROM owned)) " +
        "DELETE FROM token_amount_large WHERE id IN (SELECT id FROM owned)";

    private static readonly TokenBudgetWindow[] _windows = [TokenBudgetWindow.Hour, TokenBudgetWindow.Day, TokenBudgetWindow.Week];

    private readonly NpgsqlDataSource _dataSource;
    private readonly TokenBudgetOptions _options;
    private readonly PostgresLargeTokenAmountStore _large;
    private readonly ILogger<PostgresTokenBudgetStore> _logger;

    /// <param name="dataSource">The shared data source.</param>
    /// <param name="options">The budget options; only <see cref="TokenBudgetOptions.InMemoryMaxDigits"/> is used, by
    /// <see cref="LoadSince"/>. <see cref="TryConsume"/> uses the limits it is given. Defaults to the shipped options.</param>
    /// <param name="largeAmounts">The large tier. Defaults to one over <paramref name="dataSource"/>.</param>
    /// <param name="logger">Receives data integrity findings.</param>
    public PostgresTokenBudgetStore(
        NpgsqlDataSource dataSource,
        TokenBudgetOptions? options = null,
        PostgresLargeTokenAmountStore? largeAmounts = null,
        ILogger<PostgresTokenBudgetStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _options = options ?? new TokenBudgetOptions();
        _large = largeAmounts ?? new PostgresLargeTokenAmountStore(dataSource);
        _logger = logger ?? NullLogger<PostgresTokenBudgetStore>.Instance;
    }

    /// <summary>The large tier this store writes amounts beyond the numeric limits to.</summary>
    public PostgresLargeTokenAmountStore LargeAmounts => _large;

    /// <inheritdoc />
    /// <remarks>An amount beyond the numeric limits goes to the large tier: staged, then sealed with its spend row.</remarks>
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty or whitespace.</exception>
    public async Task Append(string agentId, DateTimeOffset at, TokenAmount tokens, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        if (tokens.IsZero)
        {
            return;
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await RecordAsync(conn, tx, agentId, TruncateToMicroseconds(at), tokens, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty or whitespace.</exception>
    /// <exception cref="TokenAmountIntegrityException">A large spend fails its integrity check.</exception>
    /// <exception cref="InvalidOperationException">A large spend has more digits than <see cref="TokenBudgetOptions.InMemoryMaxDigits"/>, so it cannot be returned as a <see cref="TokenAmount"/>.</exception>
    public async Task<IReadOnlyList<TokenBudgetSpend>> LoadSince(string agentId, DateTimeOffset since, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await LoadSinceAsync(conn, null, agentId, TruncateToMicroseconds(since), _options.InMemoryMaxDigits, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"A spend has more digits than agent.budget.in_memory_max_digits ({_options.InMemoryMaxDigits}); it is not materialised.");
    }

    /// <inheritdoc />
    public async Task PruneOlderThan(DateTimeOffset instant, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await using var cmd = _dataSource.CreateCommand(_prune);
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(instant) });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="limits"/> is null.</exception>
    public async Task<TokenBudgetDecision> TryConsume(string agentId, DateTimeOffset at, TokenAmount tokens, TokenBudgetOptions limits, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(limits);
        ct.ThrowIfCancellationRequested();

        // Before any database work: more integer digits than a limit can never fit, whatever has been spent. Nothing is stored.
        if (TokenBudgetEvaluator.RejectOversized(tokens, limits) is { } oversized)
        {
            return oversized;
        }

        // Window starts are computed from the truncated instant, so what is summed and what is inserted agree to the microsecond.
        var now = TruncateToMicroseconds(at);
        var weekStart = TokenBudgetEvaluator.WidestStart(now);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // 1. Serialise every consumer of this agent until commit or rollback.
        await using (var lockCmd = new NpgsqlCommand(_lock, conn, tx))
        {
            lockCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agentId });
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // 2. Tokens used in the hour, day and week windows: inline rows in SQL, then large rows by the streamed adder.
        TokenAmount?[] used;
        try
        {
            used = await WindowSumsAsync(conn, tx, agentId, now, weekStart, limits, ct).ConfigureAwait(false);
        }
        catch (TokenAmountIntegrityException ex)
        {
            LogIntegrityFinding(ex, agentId);
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return TokenBudgetEvaluator.Unrepresentable();
        }

        var decision = TokenBudgetEvaluator.Decide(used[0], used[1], used[2], tokens, limits, out var needed, out var neverFits);

        if (!decision.Allowed)
        {
            // Denied: read the week's spends (still under the lock) only to work out the retry time, then roll back. When
            // the retry time is already known (never fits, or a sum was not representable) nothing more is read.
            var retryAfter = TimeSpan.MaxValue;
            if (!neverFits && needed is not null)
            {
                try
                {
                    var spends = await LoadSinceAsync(conn, tx, agentId, weekStart, limits.InMemoryMaxDigits, ct).ConfigureAwait(false);
                    retryAfter = spends is null ? TimeSpan.MaxValue : TokenBudgetEvaluator.RetryAfter(spends, now, decision.Window!.Value, needed, neverFits);
                }
                catch (TokenAmountIntegrityException ex)
                {
                    LogIntegrityFinding(ex, agentId);
                }
            }

            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return decision with { RetryAfter = retryAfter };
        }

        // 3. It fits: record it, then 4. commit, which also releases the lock.
        if (tokens.IsZero)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return decision;
        }

        await RecordAsync(conn, tx, agentId, now, tokens, ct).ConfigureAwait(false);
        return decision;
    }

    /// <summary>
    /// Inserts one spend in <paramref name="tx"/> and commits: inline when it fits <c>numeric</c>, otherwise staged in the large
    /// tier and sealed in the same transaction as the row that refers to it.
    /// </summary>
    private async Task RecordAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string agentId, DateTimeOffset at, TokenAmount tokens, CancellationToken ct)
    {
        if (tokens.Fits(TokenAmountBounds.PostgresNumeric))
        {
            await using (var insert = new NpgsqlCommand(_insert, conn, tx))
            {
                insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agentId });
                insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = at });
                insert.Parameters.Add(AmountParameter(tokens));
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return;
        }

        // Staged in its own transactions on another connection; sealed with the spend row, or deleted on any failure.
        await using var staged = await _large.StageAsync(tokens, ct).ConfigureAwait(false);
        await PostgresLargeTokenAmountStore.SealAsync(conn, tx, staged.Id, ct).ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand(_insertLarge, conn, tx))
        {
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agentId });
            insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = at });
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = staged.Id });
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        staged.Keep();
    }

    /// <summary>The used tokens per window (hour, day, week); null where the sum is over every limit or cannot be held.</summary>
    private async Task<TokenAmount?[]> WindowSumsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string agentId, DateTimeOffset now, DateTimeOffset weekStart,
        TokenBudgetOptions limits, CancellationToken ct)
    {
        TokenAmount?[] inline = [TokenAmount.Zero, TokenAmount.Zero, TokenAmount.Zero];
        var inlineOverflow = false;

        // A savepoint, so a numeric overflow in the sum aborts only the sum, not the locked transaction.
        await tx.SaveAsync(_sumsSavepoint, ct).ConfigureAwait(false);
        try
        {
            await using var sumCmd = new NpgsqlCommand(_sums, conn, tx);
            sumCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agentId });
            sumCmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TokenBudgetEvaluator.StartOf(TokenBudgetWindow.Hour, now) });
            sumCmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TokenBudgetEvaluator.StartOf(TokenBudgetWindow.Day, now) });
            sumCmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = weekStart });
            await using var reader = await sumCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            inline = [ReadSum(reader, 0), ReadSum(reader, 1), ReadSum(reader, 2)];
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.NumericValueOutOfRange)
        {
            // The inline sum is beyond numeric (only spends written around TryConsume, or a limit beyond numeric, get here).
            // It is not an allow and not an error: the inline rows are added by the streamed adder below instead.
            await tx.RollbackAsync(_sumsSavepoint, ct).ConfigureAwait(false);
            inlineOverflow = true;
        }

        var addends = new List<Addend>();
        await using (var largeCmd = new NpgsqlCommand(_largeRows, conn, tx))
        {
            largeCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agentId });
            largeCmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = weekStart });
            await using var reader = await largeCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                addends.Add(new Addend(reader.GetFieldValue<DateTimeOffset>(0), TokenAmount.Zero, ReadLargeHeader(reader, 1)));
            }
        }

        if (inlineOverflow)
        {
            inline = [TokenAmount.Zero, TokenAmount.Zero, TokenAmount.Zero];
            await using var inlineCmd = new NpgsqlCommand(_inlineRows, conn, tx);
            inlineCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agentId });
            inlineCmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = weekStart });
            await using var reader = await inlineCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                addends.Add(new Addend(reader.GetFieldValue<DateTimeOffset>(0), ReadStoredAmount(reader, 1), null));
            }
        }

        if (addends.Count == 0)
        {
            return inline;
        }

        // Newest first, so each window's rows are a prefix of the next window's.
        addends.Sort((left, right) => right.At.CompareTo(left.At));
        return await AddStreamedAsync(conn, tx, inline, addends, now, limits, ct).ConfigureAwait(false);
    }

    /// <summary>Adds the rows to the inline sums with the streamed adder, reading each large row once (see the type remarks).</summary>
    private async Task<TokenAmount?[]> AddStreamedAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, TokenAmount?[] inline, List<Addend> addends, DateTimeOffset now,
        TokenBudgetOptions limits, CancellationToken ct)
    {
        var codec = _large.Codec;
        var inMemory = limits.InMemoryMaxDigits;
        var largestLimit = TokenAmount.Max(limits.HourlyTokens, TokenAmount.Max(limits.DailyTokens, limits.WeeklyTokens));
        var largestLimitChunks = await InMemoryChunkedTokenAmount.FromAsync(largestLimit, codec, ct).ConfigureAwait(false);

        await using var scratch = new ScratchSpace(_large, conn, tx, inMemory);
        ITokenAmountChunkReader partial = await InMemoryChunkedTokenAmount.FromAsync(TokenAmount.Zero, codec, ct).ConfigureAwait(false);
        var overEveryLimit = false;
        var next = 0;
        var used = new TokenAmount?[_windows.Length];

        for (var i = 0; i < _windows.Length; i++)
        {
            var cutoff = TokenBudgetEvaluator.StartOf(_windows[i], now);
            for (; next < addends.Count && addends[next].At > cutoff; next++)
            {
                var addend = addends[next];
                if (overEveryLimit)
                {
                    continue;
                }

                if (addend.Digits.IntegerDigits > largestLimit.IntegerDigitCount)
                {
                    // Over the largest limit from its header alone: no chunk of it is read.
                    overEveryLimit = true;
                    continue;
                }

                ITokenAmountChunkReader operand = addend.Large is { } large
                    ? PostgresTokenAmountChunkReader.Within(conn, tx, large.Id, large.Digits, large.Sha256, codec.Pool)
                    : await InMemoryChunkedTokenAmount.FromAsync(addend.Inline, codec, ct).ConfigureAwait(false);
                var sum = await scratch.AddAsync(partial, operand, ct).ConfigureAwait(false);
                await scratch.ReleaseAsync(partial).ConfigureAwait(false);
                partial = sum;
                overEveryLimit = await codec.CompareAsync(partial, largestLimitChunks, ct).ConfigureAwait(false) > 0;
            }

            if (overEveryLimit || inline[i] is not { } start)
            {
                used[i] = null;
                continue;
            }

            var startChunks = await InMemoryChunkedTokenAmount.FromAsync(start, codec, ct).ConfigureAwait(false);
            var total = await scratch.AddAsync(startChunks, partial, ct).ConfigureAwait(false);
            used[i] = await codec.TryReadAsync(total, inMemory, ct).ConfigureAwait(false);
            await scratch.ReleaseAsync(total).ConfigureAwait(false);
        }

        return used;
    }

    /// <summary>Spends after <paramref name="since"/>, oldest first; null when a large one does not fit <paramref name="inMemoryMaxDigits"/>.</summary>
    private async Task<IReadOnlyList<TokenBudgetSpend>?> LoadSinceAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string agentId, DateTimeOffset since, int inMemoryMaxDigits, CancellationToken ct)
    {
        var rows = new List<(DateTimeOffset At, TokenAmount Inline, LargeRow? Large)>();
        await using (var cmd = new NpgsqlCommand(_loadSince, conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agentId });
            cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = since });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var at = reader.GetFieldValue<DateTimeOffset>(0);
                rows.Add(reader.IsDBNull(1) ? (at, TokenAmount.Zero, ReadLargeHeader(reader, 2)) : (at, ReadStoredAmount(reader, 1), null));
            }
        }

        // Large amounts are read after the row reader is closed: one command at a time per connection.
        var spends = new List<TokenBudgetSpend>(rows.Count);
        foreach (var (at, inline, large) in rows)
        {
            if (large is not { } l)
            {
                spends.Add(new TokenBudgetSpend(at, inline));
                continue;
            }

            var reader = PostgresTokenAmountChunkReader.Within(conn, tx, l.Id, l.Digits, l.Sha256, _large.Codec.Pool);
            if (await _large.Codec.TryReadAsync(reader, inMemoryMaxDigits, ct).ConfigureAwait(false) is not { } amount)
            {
                return null;
            }

            spends.Add(new TokenBudgetSpend(at, amount));
        }

        return spends;
    }

    /// <summary>The large amount a spend row refers to, from the joined header columns starting at <paramref name="ordinal"/>.</summary>
    /// <exception cref="TokenAmountIntegrityException">The header is missing or not sealed.</exception>
    private static LargeRow ReadLargeHeader(NpgsqlDataReader reader, int ordinal)
    {
        var id = reader.GetGuid(ordinal);
        if (reader.IsDBNull(ordinal + 4) || reader.GetString(ordinal + 4) != "sealed")
        {
            throw new TokenAmountIntegrityException($"A spend refers to large token amount {id}, which is missing or not sealed.");
        }

        return new LargeRow(
            id,
            new TokenAmountDigitCounts(reader.GetInt64(ordinal + 1), reader.GetInt64(ordinal + 2)),
            reader.GetFieldValue<byte[]>(ordinal + 3));
    }

    /// <summary>An amount as a text parameter; the SQL casts it to <c>numeric</c>.</summary>
    private static NpgsqlParameter<string> AmountParameter(TokenAmount tokens) =>
        new() { TypedValue = tokens.ToString(), NpgsqlDbType = NpgsqlDbType.Text };

    /// <summary>A window sum read as text: null (over every limit) when it does not parse within the numeric bounds.</summary>
    private static TokenAmount? ReadSum(NpgsqlDataReader reader, int ordinal) =>
        TokenAmount.TryParse(reader.GetString(ordinal), out var sum) ? sum : null;

    /// <summary>One stored inline spend read as text. The column's CHECK and the numeric limits mean it always parses.</summary>
    private static TokenAmount ReadStoredAmount(NpgsqlDataReader reader, int ordinal) => TokenAmount.Parse(reader.GetString(ordinal));

    /// <summary>Cuts an instant to whole microseconds, the precision Postgres stores, so a write and its read-back agree.</summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset instant) => PostgresLargeTokenAmountStore.TruncateToMicroseconds(instant);

    [LoggerMessage(Level = LogLevel.Error, Message = "Data integrity finding: a large token amount of agent {AgentId} failed its check; the budget call was denied.")]
    private partial void LogIntegrityFinding(Exception exception, string agentId);

    private readonly record struct LargeRow(Guid Id, TokenAmountDigitCounts Digits, byte[] Sha256);

    /// <summary>One row to add by the streamed adder: an inline amount (when the SQL sum overflowed) or a large one.</summary>
    private readonly record struct Addend(DateTimeOffset At, TokenAmount Inline, LargeRow? Large)
    {
        public TokenAmountDigitCounts Digits => Large is { } large ? large.Digits : TokenAmountDigitCounts.Of(Inline);
    }

    /// <summary>
    /// Where intermediate sums live: in memory while the sum's digit bound fits the in-memory maximum, otherwise staged in the
    /// large tier (never sealed) and deleted when released or when the space is disposed.
    /// </summary>
    private sealed class ScratchSpace(PostgresLargeTokenAmountStore large, NpgsqlConnection conn, NpgsqlTransaction tx, int inMemoryMaxDigits) : IAsyncDisposable
    {
        private readonly Dictionary<ITokenAmountChunkReader, PostgresStagedTokenAmount> _staged = [];

        public async Task<ITokenAmountChunkReader> AddAsync(ITokenAmountChunkReader left, ITokenAmountChunkReader right, CancellationToken ct)
        {
            if (TokenAmountChunkCodec.SumBound(left.Digits, right.Digits).TotalAtMost(inMemoryMaxDigits))
            {
                var sum = new InMemoryChunkedTokenAmount();
                await large.Codec.AddAsync(left, right, sum, ct).ConfigureAwait(false);
                return sum;
            }

            var staged = await large.BeginStagingAsync(ct).ConfigureAwait(false);
            try
            {
                await large.Codec.AddAsync(left, right, staged, ct).ConfigureAwait(false);
            }
            catch
            {
                await staged.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            // Read inside the locked transaction: the staged chunks were committed on the staging connection.
            var reader = staged.OpenReader(conn, tx);
            _staged.Add(reader, staged);
            return reader;
        }

        public async Task ReleaseAsync(ITokenAmountChunkReader amount)
        {
            if (_staged.Remove(amount, out var staged))
            {
                await staged.DisposeAsync().ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var staged in _staged.Values)
            {
                await staged.DisposeAsync().ConfigureAwait(false);
            }

            _staged.Clear();
        }
    }
}
