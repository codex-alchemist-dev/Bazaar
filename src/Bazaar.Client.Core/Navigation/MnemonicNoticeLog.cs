namespace Bazaar.Client.Core.Navigation;

/// <summary>Writes each mnemonic notice once per client session, so a repeated clash does not repeat in the log.</summary>
public sealed class MnemonicNoticeLog
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Action<string> _write;

    public MnemonicNoticeLog(Action<string> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        _write = write;
    }

    /// <summary>Writes the notices not seen before. Returns how many were written.</summary>
    public int Record(IEnumerable<MnemonicNotice> notices)
    {
        ArgumentNullException.ThrowIfNull(notices);
        var written = 0;
        foreach (var notice in notices)
        {
            if (_seen.Add(notice.Key))
            {
                _write(notice.Message);
                written++;
            }
        }

        return written;
    }
}
