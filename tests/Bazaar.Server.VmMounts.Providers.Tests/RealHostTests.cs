using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Providers.Smb;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.VmMounts.Providers.Tests;

/// <summary>
/// Real-host checks of the SMB scripts: they need an elevated Windows host with SMB signing required. Skipped everywhere else and
/// by default; the NUC run sets <c>BAZAAR_HYPERV_TESTS=1</c> and selects <c>Category=Hyperv</c>. Until that run, the live behaviour of the
/// scripts (cmdlet parameter names, the ACL read-back, <c>Close-SmbOpenFile</c>) is UNVERIFIED.
/// </summary>
[Trait("Category", "Hyperv")]
public sealed class RealHostTests
{
    public static bool Enabled => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("BAZAAR_HYPERV_TESTS") == "1";

    [Fact(SkipUnless = nameof(Enabled), SkipType = typeof(RealHostTests), Skip = "Needs an elevated Windows host; set BAZAAR_HYPERV_TESTS=1.")]
    public async Task A_workspace_share_is_created_with_exactly_one_entry_and_removed_again()
    {
        using var env = new MountEnv();
        var account = $@"{Environment.MachineName}\{Environment.UserName}";
        using var provider = new SmbMountProvider(
            env.Options, new ProcessHostCommandRunner(), new FakeIdentity { Account = account }, new FakeEndpoint(), NullLogger<SmbMountProvider>.Instance);
        var request = env.Request(MountKind.Workspace, MountMode.Rw);

        await provider.AttachAsync(request, TestContext.Current.CancellationToken);
        var listed = await provider.ListPrefixedSharesAsync(TestContext.Current.CancellationToken);
        Assert.Contains(SmbShareNames.For(request.AgentId, request.Kind, request.MountId), listed);

        await provider.DetachAsync(request, TestContext.Current.CancellationToken);
        await provider.DetachAsync(request, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(
            SmbShareNames.For(request.AgentId, request.Kind, request.MountId),
            await provider.ListPrefixedSharesAsync(TestContext.Current.CancellationToken));
    }
}
