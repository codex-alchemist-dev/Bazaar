using Bazaar.Server.DataVolume;

namespace Bazaar.Server.Setup.Storage.Tests;

public sealed class StorageVolumeStepTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SetupApplyContext _context = new(
        "hash", SetupEntryPath.File, "operator", null, null, new SetupStateSnapshot(new Dictionary<string, string>()), []);

    private sealed class FakeGuard(string providerKind = "dev-plain", InstanceClass instanceClass = InstanceClass.Development, DataVolumeRefusal? refusal = null)
        : IDataVolumeGuard
    {
        public int Calls { get; private set; }

        public ValueTask<DataVolumeVerification> VerifyAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (refusal is { } reason)
                return ValueTask.FromException<DataVolumeVerification>(new DataVolumeRefusedException(reason, "refused"));

            var descriptor = new DataVolumeDescriptor(
                1, "install", "volume", providerKind, "none", string.Empty, DataVolumeCiphers.None, instanceClass, "kek", [], [], DateTimeOffset.UnixEpoch);
            return ValueTask.FromResult(new DataVolumeVerification(descriptor, new DataVolumeStatus(true, null, false, false, null), null));
        }

        public void EnsureInsideDataRoot(IEnumerable<string> paths)
        {
        }
    }

    private static SetupPlan Plan(string provider = "dev-plain", string instanceClass = "development") => new()
    {
        Storage = new SetupStorageSection { InstanceClass = instanceClass, Volume = new SetupStorageVolumeSection { Provider = provider } },
    };

    [Fact]
    public void The_step_covers_the_storage_section_and_runs_before_data_steps()
    {
        var step = new StorageVolumeStep([]);

        Assert.Equal("storage.volume", step.Id);
        Assert.Equal(["storage"], step.Covers);
        Assert.True(step.Order < 100);
        Assert.True(SetupPlanSections.IsKnown("storage"));
    }

    [Fact]
    public async Task A_plan_without_a_provider_passes_without_asking_the_guard()
    {
        var guard = new FakeGuard();

        var result = await new StorageVolumeStep([guard]).ApplyAsync(Plan(provider: ""), _context, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(0, guard.Calls);
    }

    [Fact]
    public async Task A_verified_volume_that_matches_the_plan_passes_and_running_again_is_the_same()
    {
        var step = new StorageVolumeStep([new FakeGuard()]);

        var first = await step.ApplyAsync(Plan(), _context, Ct);
        var second = await step.ApplyAsync(Plan(), _context, Ct);

        Assert.True(first.Succeeded);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Without_a_registered_guard_the_step_fails()
    {
        var result = await new StorageVolumeStep([]).ApplyAsync(Plan(), _context, Ct);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task A_refused_volume_fails_the_step_and_names_the_reason_code()
    {
        var step = new StorageVolumeStep([new FakeGuard(refusal: DataVolumeRefusal.NotEncrypted)]);

        var result = await step.ApplyAsync(Plan(), _context, Ct);

        Assert.False(result.Succeeded);
        Assert.Contains("data_volume_not_encrypted", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("luks2", "development")]
    [InlineData("dev-plain", "production")]
    public async Task A_mounted_volume_that_differs_from_the_plan_fails_the_step(string provider, string instanceClass)
    {
        var step = new StorageVolumeStep([new FakeGuard()]);

        var result = await step.ApplyAsync(Plan(provider, instanceClass), _context, Ct);

        Assert.False(result.Succeeded);
    }
}
