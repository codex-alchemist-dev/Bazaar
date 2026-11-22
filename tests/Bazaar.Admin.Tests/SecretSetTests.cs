using Bazaar.Admin;
using Bazaar.Server.Setup;

namespace Bazaar.Admin.Tests;

/// <summary>A value source that returns a fixed value and counts the reads, so a test can show whether a value was asked for at all.</summary>
internal sealed class FakeSecretValueSource(string? value) : ISecretValueSource
{
    public int Reads { get; private set; }

    public string? Read(string prompt)
    {
        Reads++;
        return value;
    }
}

/// <summary>A store that keeps values in memory, so a test can see what was written. It can be set to fail with a message that names the value.</summary>
internal sealed class MemorySetupSecretWriter(Exception? failure = null) : ISetupSecretWriter
{
    public bool StoreAvailable => true;

    public Dictionary<string, string> Stored { get; } = new(StringComparer.Ordinal);

    public Task WriteAsync(string reference, string value, CancellationToken cancellationToken = default)
    {
        if (failure is not null)
        {
            throw failure;
        }

        Stored[reference] = value;
        return Task.CompletedTask;
    }
}

/// <summary>
/// <c>bazaar-admin secret set</c> (doc 45 section 1.6). The canary is a fake secret placed in every input path that could carry it
/// (the argument, the value source, the store's failure text). It must reach no output, no error and no exception text.
/// </summary>
public sealed class SecretSetTests
{
    private const string _canary = AdminFixtures.Canary;
    private const string _reference = "secret://bazaar/providers/anthropic/key";

    /// <summary>The one line the unavailable store prints. Pinned here so a change to the wording is a deliberate test change.</summary>
    private const string _secretStoreUnavailableLine =
        "secret set is unavailable: this build has no secret store (doc 14 ISecretStore); the Host adapter provides it. Nothing was read or stored.";

    private static async Task<CliRun> Run(ISetupSecretWriter? writer, ISecretValueSource source, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await AdminCommandLine.RunAsync(
            args,
            stdout,
            stderr,
            secretInput: source,
            secretWriter: writer,
            cancellationToken: TestContext.Current.CancellationToken);
        return new CliRun(code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task A_value_in_the_store_is_stored_under_the_reference_and_never_shown()
    {
        var writer = new MemorySetupSecretWriter();
        var source = new FakeSecretValueSource(_canary);

        var run = await Run(writer, source, "secret", "set", _reference);

        Assert.Equal((int)SetupExitCode.Ok, run.ExitCode);
        Assert.Equal(_canary, writer.Stored[_reference]);
        Assert.Equal(1, source.Reads);
        Assert.DoesNotContain(_canary, run.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_store_the_command_reports_held_and_never_reads_the_value()
    {
        var source = new FakeSecretValueSource(_canary);

        // No writer is given, so the placeholder answers: this build has no secret store.
        var run = await Run(null, source, "secret", "set", _reference);

        Assert.Equal((int)SetupExitCode.Held, run.ExitCode);
        Assert.Equal(0, source.Reads);
        Assert.Contains(_secretStoreUnavailableLine, run.Stderr);
        Assert.DoesNotContain(_canary, run.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_that_is_not_a_secret_reference_is_refused_before_any_value_is_read()
    {
        var writer = new MemorySetupSecretWriter();
        var source = new FakeSecretValueSource(_canary);

        var run = await Run(writer, source, "secret", "set", "providers/anthropic/key");

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Equal(0, source.Reads);
        Assert.Empty(writer.Stored);
        Assert.Contains("secret://bazaar/", run.Stderr);
    }

    [Fact]
    public async Task A_value_typed_on_the_command_line_is_refused_and_not_echoed()
    {
        var writer = new MemorySetupSecretWriter();
        var source = new FakeSecretValueSource(_canary);

        var run = await Run(writer, source, "secret", "set", _reference, _canary);

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Equal(0, source.Reads);
        Assert.Empty(writer.Stored);
        Assert.DoesNotContain(_canary, run.All, StringComparison.Ordinal);
        Assert.Contains("never taken from the command line", run.Stderr);
    }

    [Fact]
    public async Task A_value_given_as_an_equals_form_is_refused_and_not_echoed()
    {
        var writer = new MemorySetupSecretWriter();
        var run = await Run(writer, new FakeSecretValueSource(_canary), "secret", "set", "--value=" + _canary);

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Empty(writer.Stored);
        Assert.DoesNotContain(_canary, run.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_value_is_refused_and_nothing_is_stored()
    {
        var writer = new MemorySetupSecretWriter();

        var run = await Run(writer, new FakeSecretValueSource(string.Empty), "secret", "set", _reference);

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.Empty(writer.Stored);
        Assert.Contains("No value was given", run.Stderr);
    }

    [Fact]
    public async Task A_store_failure_names_no_detail_even_when_the_detail_holds_the_value()
    {
        // The failure text carries the value on purpose: the command must not print it.
        var writer = new MemorySetupSecretWriter(new InvalidOperationException("the store refused " + _canary));

        var run = await Run(writer, new FakeSecretValueSource(_canary), "secret", "set", _reference);

        Assert.Equal((int)SetupExitCode.ApplyFailedRolledBack, run.ExitCode);
        Assert.Contains("nothing was stored", run.Stderr);
        Assert.DoesNotContain(_canary, run.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secret_help_names_the_command_and_says_the_value_is_never_an_argument()
    {
        var run = await AdminRun.Async("secret", "--help");

        Assert.Equal((int)SetupExitCode.Ok, run.ExitCode);
        Assert.Contains("secret set <reference>", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("never an argument", run.Stdout, StringComparison.Ordinal);
    }
}
