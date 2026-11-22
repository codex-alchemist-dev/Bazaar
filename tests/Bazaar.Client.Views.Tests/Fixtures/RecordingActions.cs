namespace Bazaar.Client.Views.Tests.Fixtures;

/// <summary>A fake head: records every action it is asked to run, and can be told to fail.</summary>
internal sealed class RecordingActions : IViewActions
{
    public List<string> Calls { get; } = [];

    public Exception? FailWith { get; set; }

    public Task NavigateAsync(string target) => Record($"navigate:{target}");

    public Task InvokeAsync(string command) => Record($"invoke:{command}");

    public Task OpenPaneAsync(string paneKind) => Record($"openPane:{paneKind}");

    public Task SetSettingAsync(string key, string value) => Record($"setSetting:{key}={value}");

    public Task InvokeAsync(string command, IReadOnlyDictionary<string, object> args) => Record($"invoke:{command}?{Join(args)}");

    public Task NavigateAsync(string target, IReadOnlyDictionary<string, object> args) => Record($"navigate:{target}?{Join(args)}");

    public Task OpenPaneAsync(string paneKind, IReadOnlyDictionary<string, object> args) => Record($"openPane:{paneKind}?{Join(args)}");

    private static string Join(IReadOnlyDictionary<string, object> args) =>
        string.Join("&", args.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => $"{a.Key}={Convert.ToString(a.Value, System.Globalization.CultureInfo.InvariantCulture)}"));

    private Task Record(string call)
    {
        Calls.Add(call);
        return FailWith is null ? Task.CompletedTask : Task.FromException(FailWith);
    }
}

/// <summary>A head that never overrode the argument overloads, so it keeps the interface defaults.</summary>
internal sealed class NoArgsActions : IViewActions
{
    public Task NavigateAsync(string target) => Task.CompletedTask;

    public Task InvokeAsync(string command) => Task.CompletedTask;

    public Task OpenPaneAsync(string paneKind) => Task.CompletedTask;

    public Task SetSettingAsync(string key, string value) => Task.CompletedTask;
}

/// <summary>A localiser that knows one key. An unknown key returns itself, as the contract asks.</summary>
internal sealed class FakeLocalizer : IViewLocalizer
{
    public string Text(string key) => key == "greeting" ? "Hello from the catalogue" : key;
}
