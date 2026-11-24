using System.Text.Json;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Providers.ClaudeCode;
using Bazaar.Server.Settings;
using Xunit;

namespace Bazaar.Server.Host.Tests;

public sealed class ClaudeCodeAcknowledgementStoreTests
{
    private sealed class MemorySettings : ISettingStore
    {
        public string Value { get; set; } = "";

        public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default) =>
            Task.FromResult(key is SettingKey<string> && Value.Length > 0 ? (T)(object)Value : key.Default);

        public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default)
        {
            var text = (string)(object)value!;
            var error = key.CheckValue(JsonSerializer.SerializeToElement(text));
            if (error is not null)
            {
                return Task.FromResult(new SettingWriteResult(key.Name, SettingWriteOutcome.Rejected, ApprovalTier.None, error));
            }

            Value = text;
            return Task.FromResult(new SettingWriteResult(key.Name, SettingWriteOutcome.Accepted, ApprovalTier.None, null));
        }

        public Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task First_acceptance_is_stored_and_later_ones_do_not_move_it()
    {
        var settings = new MemorySettings();
        var store = new SettingsClaudeCodeAcknowledgementStore(settings);
        Assert.Null(await store.GetAcknowledgedAtAsync(TestContext.Current.CancellationToken));

        var first = new DateTimeOffset(2026, 11, 22, 19, 0, 0, TimeSpan.Zero);
        await store.RecordAsync(first, TestContext.Current.CancellationToken);
        await store.RecordAsync(first.AddDays(1), TestContext.Current.CancellationToken);

        Assert.Equal(first, await store.GetAcknowledgedAtAsync(TestContext.Current.CancellationToken));
        Assert.Equal("2026-11-22T19:00:00.0000000Z", settings.Value);
    }

    [Fact]
    public void Key_rejects_text_that_is_not_a_time()
    {
        Assert.NotNull(ClaudeCodeSettings.TermsAcknowledgedAt.CheckValue(JsonSerializer.SerializeToElement("yesterday-ish")));
        Assert.Null(ClaudeCodeSettings.TermsAcknowledgedAt.CheckValue(JsonSerializer.SerializeToElement("")));
        Assert.Null(ClaudeCodeSettings.TermsAcknowledgedAt.CheckValue(JsonSerializer.SerializeToElement("2026-11-22T19:00:00Z")));
    }

    [Fact]
    public void Guided_setup_accepts_the_settings_backed_store()
    {
        var setup = new ClaudeCodeGuidedSetup(new SettingsClaudeCodeAcknowledgementStore(new MemorySettings()));
        Assert.NotNull(setup);
    }
}
