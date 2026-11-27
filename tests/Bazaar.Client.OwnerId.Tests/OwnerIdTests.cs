using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bazaar.Client.OwnerId.Avalonia;

namespace Bazaar.Client.OwnerId.Tests;

public class OwnerIdTests
{
    private sealed class Store(string? initial = null) : IOwnerIdStore
    {
        public string? Value { get; private set; } = initial;
        public bool Fails { get; set; }
        public string? Load() => Value;
        public bool Save(string canonicalId) { if (Fails) { return false; } Value = canonicalId; return true; }
    }

    [Theory]
    [InlineData("0F0F0F0F-0000-4000-8000-000000000001", true, "0f0f0f0f-0000-4000-8000-000000000001")]
    [InlineData("  0f0f0f0f000040008000000000000001  ", true, "0f0f0f0f-0000-4000-8000-000000000001")]
    [InlineData("{0f0f0f0f-0000-4000-8000-000000000001}", true, "0f0f0f0f-0000-4000-8000-000000000001")]
    [InlineData("00000000-0000-0000-0000-000000000000", false, "")]
    [InlineData("owner", false, "")]
    [InlineData("", false, "")]
    [InlineData(null, false, "")]
    public void Text_is_normalised_or_refused(string? text, bool ok, string expected)
    {
        Assert.Equal(ok, OwnerIdText.TryNormalize(text, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Fact]
    public void Save_stores_the_canonical_id_only_when_valid()
    {
        var store = new Store();
        var vm = new OwnerIdPairingViewModel(store);
        Assert.False(vm.IsPaired);
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.Input = "not an id";
        vm.Save();
        Assert.Null(store.Value);
        Assert.Contains("not a valid", vm.Notice);

        vm.Input = "0F0F0F0F-0000-4000-8000-000000000001";
        Assert.True(vm.SaveCommand.CanExecute(null));
        vm.SaveCommand.Execute(null);
        Assert.Equal("0f0f0f0f-0000-4000-8000-000000000001", store.Value);
        Assert.True(vm.IsPaired);
        Assert.Contains("No key was created", vm.Notice);
    }

    [Fact]
    public void A_failing_store_is_reported_and_keeps_the_phone_unpaired()
    {
        var vm = new OwnerIdPairingViewModel(new Store { Fails = true }) { Input = "0f0f0f0f-0000-4000-8000-000000000001" };
        vm.Save();
        Assert.False(vm.IsPaired);
        Assert.Contains("could not be stored", vm.Notice);
    }

    [Fact]
    public void An_existing_id_shows_as_paired()
    {
        var vm = new OwnerIdPairingViewModel(new Store("0f0f0f0f-0000-4000-8000-000000000001"));
        Assert.True(vm.IsPaired);
        Assert.Contains("0f0f0f0f", vm.Status);
    }

    [AvaloniaFact]
    public void The_card_has_one_box_one_button_and_says_it_creates_no_key()
    {
        var store = new Store();
        var vm = new OwnerIdPairingViewModel(store);
        var window = new Window { Width = 400, Height = 500, DataContext = vm, Content = new OwnerIdPairingView() };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.Single(window.GetVisualDescendants().OfType<TextBox>());
        Assert.Single(window.GetVisualDescendants().OfType<Button>());
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "This stores your id only. It creates no approval key.");
        vm.Input = "0f0f0f0f-0000-4000-8000-000000000001";
        vm.Save();
        Assert.NotNull(store.Value);
    }
}
