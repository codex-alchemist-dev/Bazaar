using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Bazaar.Server.Gc.Create;

/// <summary>A GC to create: its id, title, creator and members. The id is chosen by the caller so a retry finds the same row.</summary>
/// <param name="GcId">The GC id. Use <see cref="GcCreateService.DeterministicGcId"/> for a retry-safe id.</param>
/// <param name="Title">The display title. Trimmed, and at most <see cref="GcCreateService.MaxTitleLength"/> characters.</param>
/// <param name="CreatedBy">The creator's id, stored on the GC.</param>
/// <param name="Members">The members to add. Distinct, and at most the service's member cap.</param>
public sealed record GcCreateRequest(Guid GcId, string Title, Guid CreatedBy, IReadOnlyList<Guid> Members);

/// <summary>Raised when a create request breaks a rule. The message names the rule, never the title.</summary>
public sealed class GcCreateRefusedException(string message) : Exception(message);

/// <summary>
/// Creates a general GC with its members (doc 01, minimal create; doc 38 WP-R7 needs it for <c>new_gc</c> pings). The write is
/// idempotent on the GC id: a second call with the same id changes nothing, so a retried fire lands on the same GC. The member
/// cap bounds the fan-out of one create. Lines go through the GC log, not here.
/// </summary>
public sealed class GcCreateService
{
    /// <summary>The longest title stored, in characters.</summary>
    public const int MaxTitleLength = 120;

    /// <summary>The default member cap: the ping fan-out cap (8) plus the creator.</summary>
    public const int DefaultMaxMembers = 9;

    /// <summary>The GC type every trigger-created GC uses (doc 01 seed, <c>general</c>).</summary>
    public const string TypeKey = "general";

    private readonly NpgsqlDataSource _dataSource;
    private readonly int _maxMembers;

    public GcCreateService(NpgsqlDataSource dataSource, int maxMembers = DefaultMaxMembers)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (maxMembers < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMembers), maxMembers, "The member cap must be at least 1.");
        }

        _dataSource = dataSource;
        _maxMembers = maxMembers;
    }

    /// <summary>The member cap this service enforces.</summary>
    public int MaxMembers => _maxMembers;

    /// <summary>
    /// A GC id derived from a seed (for example a trigger and its fire instant), so the same seed always names the same GC.
    /// The bytes are a SHA-256 prefix, not a random id, which is the point.
    /// </summary>
    public static Guid DeterministicGcId(string seed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seed);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Creates the GC and its members if the id is new; otherwise does nothing. Returns true when the GC was created by this call.
    /// Throws <see cref="GcCreateRefusedException"/> for a rule break (blank title, duplicate or too many members, no members).
    /// </summary>
    public async Task<bool> EnsureAsync(GcCreateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var title = Validate(request);

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        const string insertGc = """
            INSERT INTO gc (gc_id, type_key, title, created_by)
            VALUES (@gc_id, @type_key, @title, @created_by)
            ON CONFLICT (gc_id) DO NOTHING
            """;
        await using (var cmd = new NpgsqlCommand(insertGc, conn, tx))
        {
            cmd.Parameters.AddWithValue("gc_id", request.GcId);
            cmd.Parameters.AddWithValue("type_key", TypeKey);
            cmd.Parameters.AddWithValue("title", title);
            cmd.Parameters.AddWithValue("created_by", request.CreatedBy);
            var created = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
            if (!created)
            {
                // The GC already exists from an earlier attempt; its members were written with it.
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }
        }

        // Members are written only when this call created the GC, so a plain insert is right. gc_member's key is
        // (gc_id, principal_id, joined_seq) and the table is append-only, so an ON CONFLICT on a narrower key would not match.
        const string insertMember = """
            INSERT INTO gc_member (gc_id, principal_id, role, joined_seq)
            VALUES (@gc_id, @principal_id, 'member', 0)
            """;
        foreach (var member in request.Members)
        {
            await using var cmd = new NpgsqlCommand(insertMember, conn, tx);
            cmd.Parameters.AddWithValue("gc_id", request.GcId);
            cmd.Parameters.AddWithValue("principal_id", member);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private string Validate(GcCreateRequest request)
    {
        if (request.GcId == Guid.Empty)
        {
            throw new GcCreateRefusedException("The GC id is empty.");
        }

        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            throw new GcCreateRefusedException("The GC title is blank.");
        }

        if (title.Length > MaxTitleLength)
        {
            title = title[..MaxTitleLength];
        }

        if (request.Members.Count == 0)
        {
            throw new GcCreateRefusedException("A GC needs at least one member.");
        }

        if (request.Members.Distinct().Count() != request.Members.Count)
        {
            throw new GcCreateRefusedException("A GC member is listed twice.");
        }

        if (request.Members.Count > _maxMembers)
        {
            throw new GcCreateRefusedException($"A GC takes at most {_maxMembers} members.");
        }

        return title;
    }
}
