using System.Diagnostics;
using System.Text;
using Bazaar.Client.Calcinato.Abstractions;

namespace Bazaar.Client.Calcinato.Pty.Tests;

/// <summary>Collects a session's output in the background so tests can wait for text in it.</summary>
internal sealed class PtyTranscript : IAsyncDisposable
{
    private readonly MemoryStream _bytes = new();
    private readonly Task _pump;

    public PtyTranscript(IPtySession session)
    {
        _pump = Task.Run(async () =>
        {
            await foreach (var chunk in session.Output.ReadAllAsync())
            {
                lock (_bytes)
                {
                    _bytes.Write(chunk);
                }
            }
        });
    }

    public string Text
    {
        get
        {
            lock (_bytes)
            {
                return Encoding.UTF8.GetString(_bytes.ToArray());
            }
        }
    }

    /// <summary>Polls the transcript until it contains <paramref name="text"/> or the timeout passes.</summary>
    public async Task<bool> WaitForAsync(string text, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (Text.Contains(text, StringComparison.Ordinal))
            {
                return true;
            }

            await Task.Delay(50);
        }

        return Text.Contains(text, StringComparison.Ordinal);
    }

    /// <summary>Polls the transcript until it matches <paramref name="pattern"/> or the timeout passes.</summary>
    public async Task<bool> WaitForMatchAsync(string pattern, TimeSpan timeout)
    {
        var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (regex.IsMatch(Text))
            {
                return true;
            }

            await Task.Delay(50);
        }

        return regex.IsMatch(Text);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The pump is left behind; the session that fed it is already disposed.
        }
    }
}
