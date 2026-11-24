using System.Threading.Channels;
using Bazaar.Server.VmDisplay.Abstractions;
using Bazaar.Server.VmDisplay.GuestChannel;
using Bazaar.Server.VmTransport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmDisplay.GuestChannel.Tests;

public class GuestChannelDisplayExecTests
{
    private static readonly Guid _vm = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeChannel : IGuestChannel
    {
        public List<ExecRequest> Execs { get; } = [];

        public List<string> Fetched { get; } = [];

        public Func<ExecRequest, CancellationToken, Task<ExecResult>> Exec { get; set; } = (_, _) => Task.FromResult(new ExecResult(0, false, false));

        public Func<string, Stream, CancellationToken, Task> Get { get; set; } = (_, _, _) => Task.CompletedTask;

        public Task<ExecResult> ExecAsync(ExecRequest req, Func<OutputChunk, Task> onOutput, CancellationToken ct)
        {
            Execs.Add(req);
            return Exec(req, ct);
        }

        public Task GetFileAsync(string guestPath, Stream sink, CancellationToken ct)
        {
            Fetched.Add(guestPath);
            return Get(guestPath, sink, ct);
        }

        public Task PutFileAsync(string guestPath, Stream content, string sha256, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteFileAsync(string relativePath, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<GuestDirectoryEntry>> ListFilesAsync(string relativeDir, CancellationToken ct) => throw new NotSupportedException();

        public Task<string?> ResolveRealPathAsync(string relativePath, CancellationToken ct) => throw new NotSupportedException();

        public Task<IGuestPty> OpenPtyAsync(int cols, int rows, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeDirectory : IGuestChannelDirectory
    {
        public Dictionary<Guid, IGuestChannel> Channels { get; } = [];

        public List<Guid> Asked { get; } = [];

        public Func<Guid, CancellationToken, ValueTask<IGuestChannel?>>? Override { get; set; }

        public ValueTask<IGuestChannel?> TryGetByVmAsync(Guid vmId, CancellationToken ct)
        {
            Asked.Add(vmId);
            if (Override is not null)
                return Override(vmId, ct);
            return ValueTask.FromResult(Channels.GetValueOrDefault(vmId));
        }

        public ValueTask<AgentGuest?> TryGetAsync(string agentId, Bazaar.Server.VmMounts.Abstractions.GuestOs os, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<AgentGuest>> ListAsync(string agentId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static GuestChannelDisplayExec Adapter(FakeDirectory? directory, FakeTimeProvider? time = null, GuestChannelDisplayOptions? options = null) =>
        new(options ?? new GuestChannelDisplayOptions(), time ?? new FakeTimeProvider(), directory);

    private static (FakeDirectory Directory, FakeChannel Channel) Wired()
    {
        var channel = new FakeChannel();
        var directory = new FakeDirectory();
        directory.Channels[_vm] = channel;
        return (directory, channel);
    }

    private static async Task<DisplayException> Fails(Task task)
    {
        var e = await Assert.ThrowsAsync<DisplayException>(() => task);
        return e;
    }

    [Fact]
    public void Supports_only_a_linux_vm_target_and_never_a_browser_session()
    {
        var adapter = Adapter(null);

        Assert.True(adapter.Supports(new DisplayTarget("a", _vm, DisplayGuestOs.Linux, "vm")));
        Assert.True(adapter.Supports(new DisplayTarget("a", _vm, DisplayGuestOs.Linux, "vm", "")));
        Assert.False(adapter.Supports(new DisplayTarget("a", _vm, DisplayGuestOs.Windows, "vm")));
        Assert.False(adapter.Supports(new DisplayTarget("a", _vm, DisplayGuestOs.Linux, "vm", "session-1")));
        Assert.False(adapter.Supports(new DisplayTarget("a", Guid.Empty, DisplayGuestOs.Linux, "vm")));
    }

    [Fact]
    public async Task Exec_passes_the_argv_as_an_array_with_the_configured_directory_and_no_environment()
    {
        var (directory, channel) = Wired();
        channel.Exec = (_, _) => Task.FromResult(new ExecResult(7, false, false));
        string[] argv = ["grim", "-t", "png", "/run/bazaar/display/a b; rm -rf.png"];

        int exit = await Adapter(directory).ExecAsync(_vm, argv, TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(7, exit);
        var request = Assert.Single(channel.Execs);
        Assert.Equal(argv, request.Argv);
        Assert.Equal("/run/bazaar/display", request.Cwd);
        Assert.Empty(request.EnvAllowlist);
        Assert.Equal(TimeSpan.FromSeconds(10), request.Timeout);
        Assert.Equal([_vm], directory.Asked);
    }

    [Fact]
    public async Task Exec_cuts_a_long_timeout_to_the_configured_cap()
    {
        var (directory, channel) = Wired();
        var options = new GuestChannelDisplayOptions { MaxCallTimeoutSeconds = 20 };

        await Adapter(directory, options: options).ExecAsync(_vm, ["true"], TimeSpan.FromMinutes(5), Ct);

        Assert.Equal(TimeSpan.FromSeconds(20), channel.Execs[0].Timeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Exec_refuses_a_timeout_that_is_not_positive(int seconds)
    {
        var (directory, channel) = Wired();

        var e = await Fails(Adapter(directory).ExecAsync(_vm, ["true"], TimeSpan.FromSeconds(seconds), Ct));

        Assert.Equal(DisplayFailure.InvalidInput, e.Failure);
        Assert.Empty(channel.Execs);
        Assert.Empty(directory.Asked);
    }

    [Fact]
    public async Task Exec_refuses_an_empty_argv_or_one_with_a_nul()
    {
        var (directory, channel) = Wired();

        Assert.Equal(DisplayFailure.InvalidInput, (await Fails(Adapter(directory).ExecAsync(_vm, [], TimeSpan.FromSeconds(1), Ct))).Failure);
        Assert.Equal(DisplayFailure.InvalidInput, (await Fails(Adapter(directory).ExecAsync(_vm, [""], TimeSpan.FromSeconds(1), Ct))).Failure);
        Assert.Equal(DisplayFailure.InvalidInput, (await Fails(Adapter(directory).ExecAsync(_vm, ["rm", "a\0b"], TimeSpan.FromSeconds(1), Ct))).Failure);
        Assert.Empty(channel.Execs);
    }

    [Fact]
    public async Task A_different_vms_channel_is_never_used()
    {
        var (directory, own) = Wired();
        var other = new FakeChannel();
        var otherVm = Guid.NewGuid();
        directory.Channels[otherVm] = other;
        var unknownVm = Guid.NewGuid();
        var adapter = Adapter(directory);

        await adapter.ExecAsync(_vm, ["true"], TimeSpan.FromSeconds(1), Ct);
        await adapter.GetFileAsync(_vm, "/run/bazaar/display/x.png", new MemoryStream(), Ct);
        var exec = await Fails(adapter.ExecAsync(unknownVm, ["true"], TimeSpan.FromSeconds(1), Ct));
        var file = await Fails(adapter.GetFileAsync(unknownVm, "/run/bazaar/display/x.png", new MemoryStream(), Ct));

        Assert.Equal(DisplayFailure.SourceFailed, exec.Failure);
        Assert.Equal(DisplayFailure.SourceFailed, file.Failure);
        Assert.Single(own.Execs);
        Assert.Single(own.Fetched);
        Assert.Empty(other.Execs);
        Assert.Empty(other.Fetched);
        Assert.Equal([_vm, _vm, unknownVm, unknownVm], directory.Asked);
    }

    [Fact]
    public async Task No_directory_or_no_live_channel_fails_every_call()
    {
        var none = Adapter(null);
        var empty = Adapter(new FakeDirectory());

        foreach (var adapter in new[] { none, empty })
        {
            Assert.Equal(DisplayFailure.SourceFailed, (await Fails(adapter.ExecAsync(_vm, ["true"], TimeSpan.FromSeconds(1), Ct))).Failure);
            Assert.Equal(DisplayFailure.SourceFailed, (await Fails(adapter.GetFileAsync(_vm, "/run/bazaar/display/x.png", new MemoryStream(), Ct))).Failure);
        }
    }

    [Fact]
    public async Task A_throwing_lookup_fails_closed()
    {
        var directory = new FakeDirectory { Override = (_, _) => throw new InvalidOperationException("store down") };

        var e = await Fails(Adapter(directory).ExecAsync(_vm, ["true"], TimeSpan.FromSeconds(1), Ct));

        Assert.Equal(DisplayFailure.SourceFailed, e.Failure);
        Assert.IsType<InvalidOperationException>(e.InnerException);
    }

    [Fact]
    public async Task A_lookup_that_never_answers_times_out_and_fails()
    {
        var time = new FakeTimeProvider();
        var directory = new FakeDirectory
        {
            Override = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            },
        };
        var pending = Adapter(directory, time, new GuestChannelDisplayOptions { LookupTimeoutSeconds = 3 }).ExecAsync(_vm, ["true"], TimeSpan.FromSeconds(1), Ct);

        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(3));
        var e = await Fails(pending);

        Assert.Equal(DisplayFailure.SourceFailed, e.Failure);
        Assert.Contains("timed out", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_the_caller_during_the_lookup_propagates()
    {
        using var cts = new CancellationTokenSource();
        var directory = new FakeDirectory
        {
            Override = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            },
        };
        var pending = Adapter(directory).ExecAsync(_vm, ["true"], TimeSpan.FromSeconds(1), cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Cancelling_the_caller_during_the_exec_propagates_whether_the_channel_throws_or_reports_it()
    {
        foreach (bool reports in new[] { true, false })
        {
            var (directory, channel) = Wired();
            using var cts = new CancellationTokenSource();
            channel.Exec = async (_, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (reports)
                {
                    return new ExecResult(-1, false, true);
                }
                return new ExecResult(0, false, false);
            };
            var pending = Adapter(directory).ExecAsync(_vm, ["true"], TimeSpan.FromSeconds(1), cts.Token);

            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
    }

    [Fact]
    public async Task A_cancelled_result_the_caller_did_not_ask_for_is_a_source_failure()
    {
        var (directory, channel) = Wired();
        channel.Exec = (_, _) => Task.FromResult(new ExecResult(-1, false, true));

        var e = await Fails(Adapter(directory).ExecAsync(_vm, ["true"], TimeSpan.FromSeconds(1), Ct));

        Assert.Equal(DisplayFailure.SourceFailed, e.Failure);
    }

    [Fact]
    public async Task A_timed_out_exec_is_a_source_failure_even_with_a_zero_exit_code()
    {
        var (directory, channel) = Wired();
        channel.Exec = (_, _) => Task.FromResult(new ExecResult(0, true, false));

        var e = await Fails(Adapter(directory).ExecAsync(_vm, ["true"], TimeSpan.FromSeconds(1), Ct));

        Assert.Equal(DisplayFailure.SourceFailed, e.Failure);
        Assert.Contains("timed out", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Truncated_output_is_a_source_failure()
    {
        var (directory, channel) = Wired();
        channel.Exec = (_, _) => Task.FromResult(new ExecResult(0, false, false, OutputTruncated: true));

        var e = await Fails(Adapter(directory).ExecAsync(_vm, ["true"], TimeSpan.FromSeconds(1), Ct));

        Assert.Equal(DisplayFailure.SourceFailed, e.Failure);
    }

    [Fact]
    public async Task A_refused_exec_and_an_ended_channel_are_source_failures()
    {
        var (directory, channel) = Wired();
        channel.Exec = (_, _) => throw new GuestExecRefusedException("cwd_not_allowed", "outside the roots");

        var refused = await Fails(Adapter(directory).ExecAsync(_vm, ["true"], TimeSpan.FromSeconds(1), Ct));

        Assert.Equal(DisplayFailure.SourceFailed, refused.Failure);
        Assert.Contains("cwd_not_allowed", refused.Message, StringComparison.Ordinal);

        channel.Exec = (_, _) => throw new GuestChannelException("closed");
        var ended = await Fails(Adapter(directory).ExecAsync(_vm, ["true"], TimeSpan.FromSeconds(1), Ct));
        Assert.Equal(DisplayFailure.SourceFailed, ended.Failure);
    }

    [Fact]
    public async Task A_file_fetch_copies_the_bytes_into_the_sink()
    {
        var (directory, channel) = Wired();
        byte[] data = [0x89, 0x50, 0x00, 0xFF, 0x0A];
        channel.Get = async (_, sink, token) => await sink.WriteAsync(data, token);
        using var sink = new MemoryStream();

        await Adapter(directory).GetFileAsync(_vm, "/run/bazaar/display/f.png", sink, Ct);

        Assert.Equal(data, sink.ToArray());
        Assert.Equal(["/run/bazaar/display/f.png"], channel.Fetched);
    }

    [Fact]
    public async Task A_failed_file_fetch_is_a_source_failure_and_a_sink_refusal_keeps_its_reason()
    {
        var (directory, channel) = Wired();
        channel.Get = (_, _, _) => throw new GuestFileException("hash mismatch");
        var failed = await Fails(Adapter(directory).GetFileAsync(_vm, "/run/bazaar/display/f.png", new MemoryStream(), Ct));
        Assert.Equal(DisplayFailure.SourceFailed, failed.Failure);

        channel.Get = (_, _, _) => throw new GuestChannelException("closed");
        Assert.Equal(DisplayFailure.SourceFailed, (await Fails(Adapter(directory).GetFileAsync(_vm, "/run/bazaar/display/f.png", new MemoryStream(), Ct))).Failure);

        channel.Get = (_, _, _) => throw new DisplayException(DisplayFailure.FrameTooLarge, "too big");
        Assert.Equal(DisplayFailure.FrameTooLarge, (await Fails(Adapter(directory).GetFileAsync(_vm, "/run/bazaar/display/f.png", new MemoryStream(), Ct))).Failure);
    }

    [Fact]
    public async Task Cancelling_the_caller_during_a_file_fetch_propagates()
    {
        var (directory, channel) = Wired();
        using var cts = new CancellationTokenSource();
        channel.Get = async (_, _, token) => await Task.Delay(Timeout.InfiniteTimeSpan, token);
        var pending = Adapter(directory).GetFileAsync(_vm, "/run/bazaar/display/f.png", new MemoryStream(), cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Theory]
    [InlineData("/etc/shadow")]
    [InlineData("/run/bazaar/display/../../../etc/shadow")]
    [InlineData("/run/bazaar/display/")]
    [InlineData("/run/bazaar/display//x.png")]
    [InlineData("/run/bazaar/displayx/x.png")]
    [InlineData("run/bazaar/display/x.png")]
    [InlineData("")]
    public async Task A_file_outside_the_display_directory_is_never_fetched(string path)
    {
        var (directory, channel) = Wired();

        var e = await Fails(Adapter(directory).GetFileAsync(_vm, path, new MemoryStream(), Ct));

        Assert.Equal(DisplayFailure.InvalidInput, e.Failure);
        Assert.Empty(channel.Fetched);
        Assert.Empty(directory.Asked);
    }

    [Fact]
    public void Options_default_to_the_documented_values_and_reject_bad_ones()
    {
        var defaults = GuestChannelDisplayOptions.Bind(Config());

        Assert.Equal("/run/bazaar/display", defaults.ExecWorkingDirectory);
        Assert.Equal(5, defaults.LookupTimeoutSeconds);
        Assert.Equal(60, defaults.MaxCallTimeoutSeconds);
        Assert.Equal("/srv/d", GuestChannelDisplayOptions.Bind(Config(("ExecWorkingDirectory", "/srv/d"))).ExecWorkingDirectory);
        foreach (var bad in new[] { ("ExecWorkingDirectory", "relative"), ("ExecWorkingDirectory", "/"), ("ExecWorkingDirectory", "/a/../b"), ("ExecWorkingDirectory", "/a/"), ("LookupTimeoutSeconds", "0"), ("LookupTimeoutSeconds", "61"), ("MaxCallTimeoutSeconds", "601"), ("MaxCallTimeoutSeconds", "x") })
        {
            Assert.Throws<InvalidOperationException>(() => GuestChannelDisplayOptions.Bind(Config(bad)));
        }
    }

    [Fact]
    public async Task The_component_registers_the_adapter_and_loading_touches_no_channel()
    {
        var directory = new FakeDirectory();
        var services = new ServiceCollection();
        services.AddSingleton<IGuestChannelDirectory>(directory);
        new VmDisplayGuestChannelComponent().AddServices(services, Config());
        using var provider = services.BuildServiceProvider();

        var adapter = Assert.IsType<GuestChannelDisplayExec>(Assert.Single(provider.GetServices<IDisplayGuestExec>()));

        Assert.Empty(directory.Asked);
        Assert.Equal("vm-display-guest-channel", VmDisplayGuestChannelComponent.ManifestDefinition.Id);
        Assert.Equal(["vm-display"], VmDisplayGuestChannelComponent.ManifestDefinition.DependsOn);
        await Assert.ThrowsAsync<DisplayException>(() => adapter.ExecAsync(_vm, ["true"], TimeSpan.FromSeconds(1), Ct));
    }

    [Fact]
    public void The_component_works_without_a_channel_directory()
    {
        var services = new ServiceCollection();
        new VmDisplayGuestChannelComponent().AddServices(services, Config());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<GuestChannelDisplayExec>(Assert.Single(provider.GetServices<IDisplayGuestExec>()));
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => GuestChannelDisplayOptions.SectionName + ":" + v.Key, v => (string?)v.Value)).Build();
}
