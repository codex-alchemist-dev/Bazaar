using Bazaar.Client.Core.Mobile;

namespace Bazaar.Client.Push.Tests;

/// <summary>The provider selector, driven by the install-scope setting <c>android.push_provider</c>.</summary>
public sealed class PushProviderSelectorTests
{
    [Fact]
    public void Default_setting_selects_unifiedpush()
    {
        var unified = new FakeUnifiedPushProvider();
        var selector = new PushProviderSelector([new FakeFcmPushProvider(), unified]);

        var selected = selector.Select(new AndroidSettings().PushProvider);

        Assert.Same(unified, selected);
    }

    [Fact]
    public void Fcm_setting_selects_fcm()
    {
        var fcm = new FakeFcmPushProvider();
        var selector = new PushProviderSelector([fcm, new FakeUnifiedPushProvider()]);

        var selected = selector.Select(AndroidSettingsWith(PushProviderKind.Fcm).PushProvider);

        Assert.Same(fcm, selected);
    }

    [Theory]
    [InlineData(PushProviderKind.UnifiedPush, "unifiedpush")]
    [InlineData(PushProviderKind.Fcm, "fcm")]
    public void Kinds_map_to_the_setting_values(PushProviderKind kind, string expected)
    {
        Assert.Equal(expected, PushProviderSelector.NameFor(kind));
    }

    [Fact]
    public void Selected_kind_with_no_registered_provider_is_an_error()
    {
        var selector = new PushProviderSelector([new FakeUnifiedPushProvider()]);

        var ex = Assert.Throws<InvalidOperationException>(() => selector.Select(PushProviderKind.Fcm));

        Assert.Contains("'fcm'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_providers_with_one_name_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new PushProviderSelector([new FakeFcmPushProvider(), new FakeFcmPushProvider()]));
    }

    [Fact]
    public void Undefined_kind_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PushProviderSelector.NameFor((PushProviderKind)99));
    }

    [Fact]
    public async Task Registration_goes_to_the_selected_provider_only()
    {
        var fcm = new FakeFcmPushProvider();
        var unified = new FakeUnifiedPushProvider();
        var selector = new PushProviderSelector([fcm, unified]);

        var token = await selector.Select(PushProviderKind.Fcm).RegisterAsync(CancellationToken.None);

        Assert.Equal("fake-fcm-installation-id", token);
        Assert.Equal(1, fcm.RegisterCalls);
        Assert.Equal(0, unified.RegisterCalls);
    }

    private static AndroidSettings AndroidSettingsWith(PushProviderKind kind) => new() { PushProvider = kind };
}
