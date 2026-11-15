using Bazaar.Contracts.Kodo;

namespace Bazaar.Client.Kodo;

/// <summary>
/// Source of Kodo status snapshots. The server implements it over the Kodo read API (doc 46, section 15); tests use a fake.
/// </summary>
public interface IKodoStatusSource
{
    /// <summary>Returns the latest status the server sent for this caller.</summary>
    Task<KodoStatusDto> GetStatusAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Turns wire status into the header row. It refuses a status it cannot read and never guesses a state. A refused status
/// leaves the last good row in place, so the pane fails closed.
/// </summary>
public sealed class KodoStatusFeed(IKodoStatusSource source, Func<DateTimeOffset> clock)
{
    private readonly IKodoStatusSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly Func<DateTimeOffset> _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>The last row that rendered successfully, or null before the first good read.</summary>
    public KodoStatusRow? LastGood { get; private set; }

    /// <summary>Why the most recent refresh was refused, or null when it succeeded.</summary>
    public string? LastError { get; private set; }

    /// <summary>Reads the latest status and updates <see cref="LastGood"/> or <see cref="LastError"/>.</summary>
    public async Task<KodoStatusRow?> RefreshAsync(CancellationToken cancellationToken)
    {
        KodoStatusDto dto;
        try
        {
            dto = await _source.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            LastError = $"Kodo is unreachable: {ex.Message}";
            return LastGood;
        }

        if (!KodoStatusMapper.TryMap(dto, out var snapshot, out var error))
        {
            LastError = error;
            return LastGood;
        }

        LastError = null;
        LastGood = KodoStatusViewModel.Build(snapshot!, _clock());
        return LastGood;
    }
}
