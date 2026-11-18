using Bazaar.Diagnostics.Capture;
using Bazaar.Diagnostics.Fingerprinting;
using Bazaar.Diagnostics.Redaction;

namespace Bazaar.Diagnostics.Tests.Capture;

public class ClientCaptureTests
{
    private static readonly DateTimeOffset _at = new(2026, 11, 13, 9, 14, 2, TimeSpan.Zero);

    private static ClientInfo FixedInfo(string osDescription = "Linux 6.18") =>
        new(
            "0.1.0+a1b2c3d",
            new DeviceInfo(osDescription, ".NET 11.0.0", "x64"),
            [new ModuleInfo("Bazaar.Client.Core", "0.1.0"), new ModuleInfo("Bazaar.Diagnostics", "0.1.0")]);

    private static ClientCapture Capture(IFieldRedactor? redactor = null, IClientInfoProvider? info = null, Func<string, bool>? isApp = null) =>
        new(redactor ?? new Redactor(), info ?? new FixedClientInfoProvider(FixedInfo()), isApp);

    private static Exception Thrown(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("The action did not throw.");
    }

    /// <summary>Every text field of a record, joined, so a test can look for a value anywhere in it.</summary>
    private static string AllText(CaptureRecord record)
    {
        var parts = new List<string?>
        {
            record.ErrorCode,
            record.ExceptionType,
            record.Message,
            record.NormalisedTopFrame,
            record.Fingerprint,
            record.Client.AppVersion,
            record.Client.Device.OsDescription,
            record.Client.Device.RuntimeDescription,
            record.Client.Device.Architecture,
        };
        foreach (var frame in record.Stack)
        {
            parts.Add(frame.Module);
            parts.Add(frame.Type);
            parts.Add(frame.Method);
        }

        foreach (var entry in record.Logs)
        {
            parts.Add(entry.Level);
            parts.Add(entry.Category);
            parts.Add(entry.Message);
        }

        foreach (var module in record.Client.Modules)
        {
            parts.Add(module.Name);
            parts.Add(module.Version);
        }

        return string.Join('\n', parts);
    }

    private sealed class FixedClientInfoProvider(ClientInfo info) : IClientInfoProvider
    {
        public ClientInfo Current() => info;
    }

    private sealed class FakeRedactor(Func<RedactionClass, string?, RedactionResult> redact) : IFieldRedactor
    {
        public RedactionResult Redact(RedactionClass fieldClass, string? input) => redact(fieldClass, input);
    }

    [Fact]
    public void Record_carries_exception_type_code_message_and_is_not_held_on_the_clean_path()
    {
        var ex = Thrown(() => throw new InvalidOperationException("the list is empty"));

        var record = Capture().Capture(ex, "BZ-GC-0042", [], _at);

        Assert.Equal(typeof(InvalidOperationException).FullName, record.ExceptionType);
        Assert.Equal("BZ-GC-0042", record.ErrorCode);
        Assert.Equal("the list is empty", record.Message);
        Assert.Equal(_at, record.CapturedAt);
        Assert.False(record.Held);
    }

    [Fact]
    public void Unhandled_exception_has_a_null_code()
    {
        var ex = Thrown(() => throw new InvalidOperationException("no code"));

        var record = Capture().Capture(ex, null, [], _at);

        Assert.Null(record.ErrorCode);
    }

    [Fact]
    public void Throwing_app_frame_on_top_is_flagged_at_index_zero()
    {
        var ex = Thrown(() => throw new InvalidOperationException("boom"));

        var record = Capture().Capture(ex, null, [], _at);

        Assert.NotEmpty(record.Stack);
        Assert.True(record.Stack[0].App);
        Assert.Equal(0, record.FlaggedFrame);
        Assert.Equal("Bazaar.Diagnostics.Tests", record.Stack[0].Module);
    }

    [Fact]
    public void Framework_frame_on_top_is_skipped_and_the_first_app_frame_is_flagged()
    {
        var ex = Thrown(() => int.Parse("not a number", System.Globalization.CultureInfo.InvariantCulture));

        var record = Capture().Capture(ex, null, [], _at);

        Assert.NotNull(record.FlaggedFrame);
        var flagged = record.FlaggedFrame.Value;
        Assert.True(flagged > 0);
        Assert.False(record.Stack[0].App);
        Assert.True(record.Stack[flagged].App);
        for (var i = 0; i < flagged; i++)
        {
            Assert.False(record.Stack[i].App);
        }
    }

    [Fact]
    public void Without_any_app_module_the_throwing_frame_is_flagged()
    {
        var ex = Thrown(() => throw new InvalidOperationException("boom"));

        var record = Capture(isApp: _ => false).Capture(ex, null, [], _at);

        Assert.NotEmpty(record.Stack);
        Assert.All(record.Stack, frame => Assert.False(frame.App));
        Assert.Equal(0, record.FlaggedFrame);
    }

    [Fact]
    public void Never_thrown_exception_has_no_stack_no_flag_and_no_top_frame()
    {
        var record = Capture().Capture(new InvalidOperationException("never thrown"), null, [], _at);

        Assert.Empty(record.Stack);
        Assert.Null(record.FlaggedFrame);
        Assert.Null(record.NormalisedTopFrame);
    }

    [Fact]
    public void Top_frame_is_given_in_the_fingerprint_normalised_form()
    {
        var ex = Thrown(() => throw new InvalidOperationException("boom"));

        var record = Capture().Capture(ex, null, [], _at);

        var top = record.Stack[0];
        var expected = Fingerprint.FrameKey(new FingerprintFrame(top.Module, top.Type, top.Method, top.App));
        Assert.Equal(expected, record.NormalisedTopFrame);
    }

    [Fact]
    public void Fingerprint_is_the_shared_fp1_function_over_the_redacted_fields()
    {
        var ex = Thrown(() => throw new InvalidOperationException("boom 42"));

        var record = Capture().Capture(ex, "BZ-GC-0042", [], _at);

        var expected = Fingerprint.Compute(new FingerprintInput(
            record.ErrorCode,
            record.ExceptionType,
            record.Stack.Select(frame => new FingerprintFrame(frame.Module, frame.Type, frame.Method, frame.App)).ToArray(),
            record.Message));
        Assert.Equal(expected, record.Fingerprint);
        Assert.StartsWith("fp1:", record.Fingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_failure_gives_the_same_record_on_repeat()
    {
        var ex = Thrown(() => throw new InvalidOperationException("boom"));
        var capture = Capture();

        var first = capture.Capture(ex, "BZ-GC-0042", [], _at);
        var second = capture.Capture(ex, "BZ-GC-0042", [], _at);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.Stack, second.Stack);
        Assert.Equal(first.FlaggedFrame, second.FlaggedFrame);
        Assert.Equal(first.Message, second.Message);
    }

    [Fact]
    public void Exact_runtime_value_is_removed_from_the_message()
    {
        var secret = "session-" + Guid.NewGuid().ToString("N");
        var ex = Thrown(() => throw new InvalidOperationException("token " + secret + " was rejected"));

        var record = Capture(new Redactor([secret])).Capture(ex, null, [], _at);

        Assert.DoesNotContain(secret, AllText(record), StringComparison.Ordinal);
        Assert.Contains(RedactionMarkers.Exact, record.Message, StringComparison.Ordinal);
        Assert.False(record.Held);
    }

    [Fact]
    public void Log_entries_are_copied_in_order_and_redacted()
    {
        var secret = "tok-" + Guid.NewGuid().ToString("N");
        var logs = new[]
        {
            new CaptureLogEntry(_at.AddSeconds(-2), "Warning", "Bazaar.Client.Core.Wire", "reconnect with " + secret),
            new CaptureLogEntry(_at.AddSeconds(-1), "Information", "Bazaar.Client.Core.Wire", "connected"),
        };
        var ex = Thrown(() => throw new InvalidOperationException("boom"));

        var record = Capture(new Redactor([secret])).Capture(ex, null, logs, _at);

        Assert.Equal(2, record.Logs.Count);
        Assert.Equal(_at.AddSeconds(-2), record.Logs[0].Time);
        Assert.Equal("Warning", record.Logs[0].Level);
        Assert.Equal("connected", record.Logs[1].Message);
        Assert.DoesNotContain(secret, AllText(record), StringComparison.Ordinal);
    }

    [Fact]
    public void Device_and_module_information_come_from_the_provider_and_are_redacted()
    {
        var secret = "machine-" + Guid.NewGuid().ToString("N");
        var info = new FixedClientInfoProvider(FixedInfo("Windows 10 on " + secret));
        var ex = Thrown(() => throw new InvalidOperationException("boom"));

        var record = Capture(new Redactor([secret]), info).Capture(ex, null, [], _at);

        Assert.Equal("0.1.0+a1b2c3d", record.Client.AppVersion);
        Assert.Equal(".NET 11.0.0", record.Client.Device.RuntimeDescription);
        Assert.Equal("x64", record.Client.Device.Architecture);
        Assert.Equal(2, record.Client.Modules.Count);
        Assert.Equal("Bazaar.Client.Core", record.Client.Modules[0].Name);
        Assert.DoesNotContain(secret, AllText(record), StringComparison.Ordinal);
    }

    [Fact]
    public void Redaction_throwing_stores_the_placeholder_and_holds_the_record()
    {
        var secret = "raw-" + Guid.NewGuid().ToString("N");
        var redactor = new FakeRedactor((_, _) => throw new InvalidOperationException("rules failed"));
        var ex = Thrown(() => throw new InvalidOperationException("contains " + secret));

        var record = Capture(redactor).Capture(ex, null, [], _at);

        Assert.Equal(RedactionMarkers.Failed, record.Message);
        Assert.True(record.Held);
        Assert.DoesNotContain(secret, AllText(record), StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_status_drops_the_text_even_when_the_redactor_returns_it()
    {
        var secret = "raw-" + Guid.NewGuid().ToString("N");
        var redactor = new FakeRedactor((_, text) => new RedactionResult(text ?? string.Empty, RedactionCounts.None, RedactionStatus.Failed));
        var ex = Thrown(() => throw new InvalidOperationException("contains " + secret));

        var record = Capture(redactor).Capture(ex, null, [], _at);

        Assert.Equal(RedactionMarkers.Failed, record.Message);
        Assert.True(record.Held);
        Assert.DoesNotContain(secret, AllText(record), StringComparison.Ordinal);
    }

    [Fact]
    public void Residual_status_stores_the_residual_marker_and_holds_the_record()
    {
        var secret = "raw-" + Guid.NewGuid().ToString("N");
        var redactor = new FakeRedactor((_, text) => new RedactionResult(text ?? string.Empty, RedactionCounts.None, RedactionStatus.Residual));
        var ex = Thrown(() => throw new InvalidOperationException("contains " + secret));

        var record = Capture(redactor).Capture(ex, null, [], _at);

        Assert.Equal(RedactionMarkers.Residual, record.Message);
        Assert.True(record.Held);
        Assert.DoesNotContain(secret, AllText(record), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_in_one_field_does_not_leak_the_others_and_holds_the_record()
    {
        var secret = "raw-" + Guid.NewGuid().ToString("N");
        var redactor = new FakeRedactor((fieldClass, text) => fieldClass == RedactionClass.FreeText
            ? throw new InvalidOperationException("message rules failed")
            : new RedactionResult(text ?? string.Empty, RedactionCounts.None, RedactionStatus.Ok));
        var logs = new[] { new CaptureLogEntry(_at, "Warning", "Bazaar", "log " + secret) };
        var ex = Thrown(() => throw new InvalidOperationException("contains " + secret));

        var record = Capture(redactor).Capture(ex, null, logs, _at);

        Assert.Equal(RedactionMarkers.Failed, record.Message);
        Assert.True(record.Held);
        Assert.Equal(RedactionMarkers.Failed, record.Logs[0].Message);
        Assert.DoesNotContain(secret, AllText(record), StringComparison.Ordinal);
    }

    [Fact]
    public void Ring_buffer_snapshot_feeds_the_capture_and_drops_are_not_visible_in_the_record()
    {
        var buffer = new RingBuffer<CaptureLogEntry>(2);
        for (var i = 0; i < 4; i++)
        {
            buffer.Add(new CaptureLogEntry(_at.AddSeconds(i), "Information", "Bazaar", "line " + i));
        }

        var ex = Thrown(() => throw new InvalidOperationException("boom"));
        var record = Capture().Capture(ex, null, buffer.Snapshot(), _at);

        Assert.Equal(2, buffer.DroppedCount);
        Assert.Equal(["line 2", "line 3"], record.Logs.Select(entry => entry.Message).ToArray());
    }

    [Fact]
    public void Runtime_provider_sorts_modules_and_reports_the_given_version()
    {
        var provider = new RuntimeClientInfoProvider(
            "0.1.0+a1b2c3d",
            [new ModuleInfo("Bazaar.Diagnostics", "0.1.0"), new ModuleInfo("Bazaar.Client.Core", "0.1.0")]);

        var info = provider.Current();

        Assert.Equal("0.1.0+a1b2c3d", info.AppVersion);
        Assert.Equal(["Bazaar.Client.Core", "Bazaar.Diagnostics"], info.Modules.Select(module => module.Name).ToArray());
        Assert.False(string.IsNullOrWhiteSpace(info.Device.OsDescription));
        Assert.Equal(info.Device.Architecture, info.Device.Architecture.ToLowerInvariant());
        Assert.StartsWith(".NET", info.Device.RuntimeDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_provider_refuses_an_empty_version()
    {
        Assert.Throws<ArgumentException>(() => new RuntimeClientInfoProvider("", []));
    }
}
