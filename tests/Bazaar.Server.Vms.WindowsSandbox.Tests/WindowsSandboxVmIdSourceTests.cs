namespace Bazaar.Server.Vms.WindowsSandbox.Tests;

public sealed class WindowsSandboxVmIdSourceTests
{
    // Captured on the owner's PC (S-WSB step 9).
    private const string _stopped =
        "6c3aeb4a-e1ed-4b44-a173-5b05ff72b3b2\n    VM,                       \tSavedAsTemplate, 6C3AEB4A-E1ED-4B44-A173-5B05FF72B3B2, CmService\n\n" +
        "D695B51A-CC14-4632-8EFE-537FD1FA4785\n    VM,                       \tRunning, D695B51A-CC14-4632-8EFE-537FD1FA4785, WSL\n";

    private const string _running =
        "6c3aeb4a-e1ed-4b44-a173-5b05ff72b3b2\r\n    VM,                       \tSavedAsTemplate, 6C3AEB4A-E1ED-4B44-A173-5B05FF72B3B2, CmService\r\n\r\n" +
        "add62adb-34a9-425d-befe-54bedad0ee78\r\n    VM,                       \tRunning, ADD62ADB-34A9-425D-BEFE-54BEDAD0EE78, WindowsSandbox\r\n\r\n" +
        "D695B51A-CC14-4632-8EFE-537FD1FA4785\r\n    VM,                       \tRunning, D695B51A-CC14-4632-8EFE-537FD1FA4785, WSL\r\n";

    [Fact]
    public void The_running_sandbox_is_the_entry_owned_by_WindowsSandbox()
    {
        Assert.Equal(new Guid("add62adb-34a9-425d-befe-54bedad0ee78"), HcsDiagVmIdSource.Parse(_running));
    }

    [Fact]
    public void No_running_sandbox_fails_closed()
    {
        Assert.Throws<WindowsSandboxVmIdException>(() => HcsDiagVmIdSource.Parse(_stopped));
        Assert.Throws<WindowsSandboxVmIdException>(() => HcsDiagVmIdSource.Parse(string.Empty));
    }

    [Fact]
    public void Two_sandboxes_are_refused_not_guessed()
    {
        var two = _running + "11111111-1111-1111-1111-111111111111\n    VM,\tRunning, 11111111-1111-1111-1111-111111111111, WindowsSandbox\n";

        Assert.Throws<WindowsSandboxVmIdException>(() => HcsDiagVmIdSource.Parse(two));
    }

    [Fact]
    public void A_stopped_sandbox_entry_is_not_counted()
    {
        var saved = "22222222-2222-2222-2222-222222222222\n    VM,\tSavedAsTemplate, 22222222-2222-2222-2222-222222222222, WindowsSandbox\n";

        Assert.Throws<WindowsSandboxVmIdException>(() => HcsDiagVmIdSource.Parse(saved));
    }

    [Fact]
    public async Task The_source_reads_through_the_runner()
    {
        var source = new HcsDiagVmIdSource(new FixedRunner(_running));

        Assert.Equal(new Guid("add62adb-34a9-425d-befe-54bedad0ee78"), await source.FindRunningAsync(TestContext.Current.CancellationToken));
    }

    private sealed class FixedRunner(string output) : IHcsDiagRunner
    {
        public Task<string> ListAsync(CancellationToken ct) => Task.FromResult(output);
    }
}
