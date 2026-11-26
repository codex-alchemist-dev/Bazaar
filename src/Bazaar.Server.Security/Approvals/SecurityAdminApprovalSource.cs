using System.Text;
using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.Security.Approvals;

/// <summary>
/// <see cref="IAdminApprovalSource"/> over the owner approval queue (doc 51 section 5, doc 15). It lists what is waiting; it never
/// decides anything, and it shows no payload, hash or taint detail: the tool, its target and the asking agent only.
/// </summary>
public sealed class SecurityAdminApprovalSource(IPendingApprovalLister reader, TimeProvider time) : IAdminApprovalSource
{
    /// <summary>The most rows one read returns.</summary>
    public const int MaxRows = 200;

    private const int _maxTextLength = 200;

    public async Task<IReadOnlyList<AdminApproval>> ListPendingAsync(CancellationToken ct = default)
    {
        var rows = await reader.ListPendingAsync(time.GetUtcNow(), MaxRows, ct).ConfigureAwait(false);
        return [.. rows.OrderBy(r => r.RequestedAt).ThenBy(r => r.Id).Select(r => new AdminApproval(
            r.Id.ToString("D"),
            Clean(r.Target is { Length: > 0 } ? r.Tool + " " + r.Target : r.Tool),
            Clean(r.AgentId),
            r.RequestedAt))];
    }

    // Tool and target text can echo agent input, so control characters are removed and the length is capped.
    private static string Clean(string value)
    {
        var sb = new StringBuilder(Math.Min(value.Length, _maxTextLength));
        foreach (var c in value)
        {
            if (sb.Length >= _maxTextLength)
            {
                break;
            }

            sb.Append(char.IsControl(c) ? ' ' : c);
        }

        return sb.ToString();
    }
}
