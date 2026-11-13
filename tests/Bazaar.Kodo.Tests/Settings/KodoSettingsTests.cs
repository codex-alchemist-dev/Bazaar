using Bazaar.Kodo.Settings;

namespace Bazaar.Kodo.Tests.Settings;

public sealed class KodoSettingsTests
{
    private static readonly int[] _DefaultBackoff = [1, 2, 5, 15, 60, 300];
    private static readonly int[] _TightBackoff = [1, 2, 10, 30];

    public static TheoryData<string> AllKeys()
    {
        var data = new TheoryData<string>();
        foreach (var definition in KodoSettingKey.All)
        {
            data.Add(definition.Key);
        }

        return data;
    }

    private static KodoSettingsResult Validate(params (string Key, string Value)[] pairs) =>
        KodoSettingsValidator.Validate(pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    [Fact]
    public void EveryKeyIsUniqueAndUnderTheKodoPrefix()
    {
        var keys = KodoSettingKey.All.Select(definition => definition.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key => Assert.StartsWith("kodo.", key, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void DefaultIsInsideTheRange(string key)
    {
        var definition = KodoSettingKey.Find(key)!;
        var result = Validate((key, definition.Default));

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(error => error.Message)));
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void DefaultIsCanonicalAndSatisfiesTheFloor(string key)
    {
        var definition = KodoSettingKey.Find(key)!;
        var result = Validate((key, definition.Default));

        // The stored text must match the default exactly, so the catalogue round-trips.
        Assert.Equal(definition.Default, result.Settings.Values[key]);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void DefaultsMatchTheValuesStatedInDoc46()
    {
        var defaults = KodoSettings.Defaults;

        Assert.Equal(_DefaultBackoff, defaults.GetIntList(KodoSettingKey.RestartBackoffSeconds));
        Assert.Equal(5, defaults.GetInt(KodoSettingKey.CrashLoopStarts));
        Assert.Equal(10, defaults.GetInt(KodoSettingKey.CrashLoopWindowMinutes));
        Assert.Equal(8, defaults.GetInt(KodoSettingKey.PoolLocalConcurrency));
        Assert.Equal(6, defaults.GetInt(KodoSettingKey.PoolDbConcurrency));
        Assert.Equal(4, defaults.GetInt(KodoSettingKey.PoolExternalConcurrency));
        Assert.Equal(2, defaults.GetInt(KodoSettingKey.PoolSlowConcurrency));
        Assert.Equal(2000, defaults.GetInt(KodoSettingKey.ProbeTimeoutMs));
        Assert.Equal(5, defaults.GetInt(KodoSettingKey.BreakerFailures));
        Assert.Equal(60, defaults.GetInt(KodoSettingKey.BreakerCooldownSeconds));
        Assert.Equal(300, defaults.GetInt(KodoSettingKey.StabilityWindowSeconds));
        Assert.Equal(6, defaults.GetInt(KodoSettingKey.FlapLimitTransitions));
        Assert.Equal(30, defaults.GetInt(KodoSettingKey.FlapLimitWindowMinutes));
        Assert.Equal(5, defaults.GetInt(KodoSettingKey.IntegrityScanIntervalMinutes));
        Assert.Equal(10, defaults.GetInt(KodoSettingKey.IntegrityProbationMinutes));
        Assert.Equal(1000, defaults.GetInt(KodoSettingKey.JournalCheckpointRecords));
        Assert.Equal(15, defaults.GetInt(KodoSettingKey.JournalCheckpointMinutes));
        Assert.Equal(512, defaults.GetInt(KodoSettingKey.JournalSpillMaxMb));
        Assert.Equal(99.5m, defaults.GetDecimal(KodoSettingKey.SloServerAvailabilityTarget));
        Assert.Equal(15, defaults.GetInt(KodoSettingKey.AlertEscalationCriticalMinutes));
    }

    [Fact]
    public void EmptyOverridesYieldTheDefaults()
    {
        var result = KodoSettingsValidator.Validate(new Dictionary<string, string>());

        Assert.True(result.IsValid);
        Assert.Equal(KodoSettings.Defaults.Values.Count, result.Settings.Values.Count);
        Assert.Equal(5, result.Settings.GetInt(KodoSettingKey.BreakerFailures));
    }

    [Theory]
    [InlineData(KodoSettingKey.CrashLoopStarts, "3")]
    [InlineData(KodoSettingKey.CrashLoopWindowMinutes, "15")]
    [InlineData(KodoSettingKey.BreakerFailures, "3")]
    [InlineData(KodoSettingKey.StabilityWindowSeconds, "600")]
    [InlineData(KodoSettingKey.FlapLimitTransitions, "4")]
    [InlineData(KodoSettingKey.FlapLimitWindowMinutes, "60")]
    [InlineData(KodoSettingKey.IntegrityScanIntervalMinutes, "1")]
    [InlineData(KodoSettingKey.AlertEscalationCriticalMinutes, "5")]
    [InlineData(KodoSettingKey.SloServerAvailabilityTarget, "99.9")]
    public void TighteningOverrideIsAccepted(string key, string value)
    {
        var result = Validate((key, value));

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(error => error.Message)));
        Assert.Equal(value, result.Settings.Values[key]);
    }

    [Theory]
    [InlineData(KodoSettingKey.CrashLoopStarts, "6")]
    [InlineData(KodoSettingKey.CrashLoopWindowMinutes, "9")]
    [InlineData(KodoSettingKey.BreakerFailures, "6")]
    [InlineData(KodoSettingKey.StabilityWindowSeconds, "59")]
    [InlineData(KodoSettingKey.FlapLimitTransitions, "7")]
    [InlineData(KodoSettingKey.FlapLimitWindowMinutes, "29")]
    [InlineData(KodoSettingKey.IntegrityScanIntervalMinutes, "6")]
    [InlineData(KodoSettingKey.AlertEscalationCriticalMinutes, "16")]
    [InlineData(KodoSettingKey.SloServerAvailabilityTarget, "94.9")]
    public void LooseningOverrideBelowOrAboveTheFloorIsRejected(string key, string value)
    {
        var result = Validate((key, value));

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal(key, error.Key);
        Assert.Contains("floor", error.Message, StringComparison.Ordinal);
        // A rejected override never changes the stored value.
        Assert.Equal(KodoSettings.Defaults.Values[key], result.Settings.Values[key]);
    }

    [Theory]
    [InlineData(KodoSettingKey.CrashLoopStarts, "1")]
    [InlineData(KodoSettingKey.BreakerFailures, "0")]
    [InlineData(KodoSettingKey.ProbeTimeoutMs, "50")]
    [InlineData(KodoSettingKey.ProbeTimeoutMs, "60001")]
    [InlineData(KodoSettingKey.JournalSpillMaxMb, "15")]
    [InlineData(KodoSettingKey.SloServerAvailabilityTarget, "100.5")]
    [InlineData(KodoSettingKey.SloServerAvailabilityTarget, "-1")]
    [InlineData(KodoSettingKey.PoolLocalConcurrency, "0")]
    [InlineData(KodoSettingKey.FlapLimitTransitions, "1")]
    public void OutOfRangeValueIsRejected(string key, string value)
    {
        var result = Validate((key, value));

        Assert.False(result.IsValid);
        Assert.Equal(key, Assert.Single(result.Errors).Key);
    }

    [Theory]
    [InlineData(KodoSettingKey.CrashLoopStarts, "5.5")]
    [InlineData(KodoSettingKey.CrashLoopStarts, "five")]
    [InlineData(KodoSettingKey.CrashLoopStarts, "")]
    [InlineData(KodoSettingKey.SloServerAvailabilityTarget, "abc")]
    [InlineData(KodoSettingKey.RestartBackoffSeconds, "")]
    [InlineData(KodoSettingKey.RestartBackoffSeconds, "1,,2")]
    [InlineData(KodoSettingKey.RestartBackoffSeconds, "1,x,5")]
    public void UnparseableValueIsRejected(string key, string value)
    {
        var result = Validate((key, value));

        Assert.False(result.IsValid);
        Assert.Equal(key, Assert.Single(result.Errors).Key);
    }

    [Fact]
    public void UnknownKeyIsRejected()
    {
        var result = Validate(("kodo.not_a_setting", "1"));

        Assert.False(result.IsValid);
        Assert.Equal("kodo.not_a_setting", Assert.Single(result.Errors).Key);
    }

    [Fact]
    public void ListValueIsParsedAndCanonicalised()
    {
        var result = Validate((KodoSettingKey.RestartBackoffSeconds, " 1, 2 ,10,30 "));

        Assert.True(result.IsValid);
        Assert.Equal("1,2,10,30", result.Settings.Values[KodoSettingKey.RestartBackoffSeconds]);
        Assert.Equal(_TightBackoff, result.Settings.GetIntList(KodoSettingKey.RestartBackoffSeconds));
    }

    [Fact]
    public void BadOverrideDoesNotStopTheValidOnesAndErrorsAreOrdered()
    {
        var result = Validate(
            (KodoSettingKey.StabilityWindowSeconds, "120"),
            (KodoSettingKey.BreakerFailures, "99"),
            (KodoSettingKey.CrashLoopStarts, "3"));

        Assert.False(result.IsValid);
        Assert.Equal(KodoSettingKey.BreakerFailures, Assert.Single(result.Errors).Key);
        Assert.Equal("120", result.Settings.Values[KodoSettingKey.StabilityWindowSeconds]);
        Assert.Equal("3", result.Settings.Values[KodoSettingKey.CrashLoopStarts]);
        Assert.Equal("5", result.Settings.Values[KodoSettingKey.BreakerFailures]);
    }

    [Fact]
    public void ValidationDoesNotChangeTheDefaults()
    {
        _ = Validate((KodoSettingKey.BreakerFailures, "2"));

        Assert.Equal(5, KodoSettings.Defaults.GetInt(KodoSettingKey.BreakerFailures));
    }

    [Fact]
    public void ReadingAKeyWithTheWrongTypeThrows()
    {
        var defaults = KodoSettings.Defaults;

        Assert.Throws<InvalidOperationException>(() => defaults.GetDecimal(KodoSettingKey.CrashLoopStarts));
    }

    [Fact]
    public void ReadingAnUnknownKeyThrows()
    {
        Assert.Throws<ArgumentException>(() => KodoSettings.Defaults.GetInt("kodo.not_a_setting"));
    }
}
