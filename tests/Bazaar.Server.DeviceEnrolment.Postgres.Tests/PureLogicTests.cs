using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bazaar.Server.DeviceEnrolment.Postgres.Tests;

/// <summary>SQL-independent logic: runs without a database.</summary>
public sealed partial class PureLogicTests
{
    [Fact]
    public void Every_device_class_has_a_distinct_lowercase_column_value_that_the_device_check_allows()
    {
        var allowed = new[] { "windows", "linux", "android", "terminal", "other" };
        var names = Enum.GetValues<DeviceClass>().Select(DeviceClassNames.Name).ToList();

        Assert.Equal(Enum.GetValues<DeviceClass>().Length, names.Distinct().Count());
        Assert.All(names, n => Assert.Contains(n, allowed));
    }

    [Fact]
    public void An_unknown_device_class_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DeviceClassNames.Name((DeviceClass)99));
    }

    [Fact]
    public void The_audit_payload_is_json_with_sorted_keys_and_ids_only()
    {
        var enrolment = Guid.NewGuid();
        var device = Guid.NewGuid();

        var payload = EnrolmentAuditRow.Payload(enrolment, device);

        using var doc = JsonDocument.Parse(payload);
        Assert.Equal(["device_id", "enrolment_id"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(device.ToString("D"), doc.RootElement.GetProperty("device_id").GetString());
        Assert.Equal(enrolment.ToString("D"), doc.RootElement.GetProperty("enrolment_id").GetString());
        Assert.DoesNotMatch(KeyMaterial(), payload.Replace(device.ToString("D"), "").Replace(enrolment.ToString("D"), ""));
    }

    [Fact]
    public void The_audit_hash_is_deterministic_and_covers_every_field()
    {
        var ts = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
        var prev = RandomNumberGenerator.GetBytes(32);
        var baseline = EnrolmentAuditRow.ComputeHash(5, ts, "actor", "kind", "{}", prev);

        Assert.Equal(baseline, EnrolmentAuditRow.ComputeHash(5, ts, "actor", "kind", "{}", prev));
        Assert.NotEqual(baseline, EnrolmentAuditRow.ComputeHash(6, ts, "actor", "kind", "{}", prev));
        Assert.NotEqual(baseline, EnrolmentAuditRow.ComputeHash(5, ts.AddTicks(10), "actor", "kind", "{}", prev));
        Assert.NotEqual(baseline, EnrolmentAuditRow.ComputeHash(5, ts, "actor2", "kind", "{}", prev));
        Assert.NotEqual(baseline, EnrolmentAuditRow.ComputeHash(5, ts, "actor", "kind2", "{}", prev));
        Assert.NotEqual(baseline, EnrolmentAuditRow.ComputeHash(5, ts, "actor", "kind", "{ }", prev));
        Assert.NotEqual(baseline, EnrolmentAuditRow.ComputeHash(5, ts, "actor", "kind", "{}", RandomNumberGenerator.GetBytes(32)));
        Assert.Equal(32, baseline.Length);
    }

    [Fact]
    public void Length_prefixing_keeps_adjacent_fields_apart()
    {
        var ts = DateTimeOffset.UnixEpoch;
        var prev = new byte[32];

        Assert.NotEqual(
            EnrolmentAuditRow.ComputeHash(1, ts, "ab", "c", "{}", prev),
            EnrolmentAuditRow.ComputeHash(1, ts, "a", "bc", "{}", prev));
    }

    [Fact]
    public void Timestamps_are_cut_to_microseconds_in_utc()
    {
        var ts = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.FromHours(2)).AddTicks(7);

        var cut = EnrolmentAuditRow.Truncate(ts);

        Assert.Equal(TimeSpan.Zero, cut.Offset);
        Assert.Equal(0, cut.UtcTicks % 10);
        Assert.Equal(ts.UtcTicks - 7, cut.UtcTicks);
    }

    [Fact]
    public void The_audit_lock_key_and_kind_match_the_servers()
    {
        Assert.Equal(0x4241554449544C47, EnrolmentAuditRow.AppendLockKey);
        Assert.Equal("approval_key.enrolled", EnrolmentAuditRow.Kind);
        Assert.Equal(new byte[32], EnrolmentAuditRow.GenesisHash);
    }

    [GeneratedRegex("(?i)public|private|spki|secret|token")]
    private static partial Regex KeyMaterial();
}
