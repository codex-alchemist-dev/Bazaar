using Bazaar.Install.Abstractions;
using Bazaar.Server.Host.Composition;

namespace Bazaar.Server.Host.Tests.CompanyBuild;

public sealed class InstallGateApprovalRequirementTests
{
    private sealed class Declarations(params ModuleDeclaration[] items) : IModuleDeclarationSource
    {
        public Task<ModuleDeclaration?> FindAsync(string moduleId, CancellationToken ct = default) =>
            Task.FromResult(items.FirstOrDefault(i => i.ModuleId == moduleId));
    }

    private sealed class Approvals(params string[][] approvedSets) : IModulePermissionApprovalReader
    {
        public Task<IReadOnlyList<ModulePermissionApprovalRecord>> ListPermissionApprovalsAsync(string moduleId, string version, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ModulePermissionApprovalRecord>>([.. approvedSets.Select(p =>
                new ModulePermissionApprovalRecord(moduleId, version, p, "d", "k", [1], "owner", DateTimeOffset.UnixEpoch))]);
    }

    private static ModuleDeclaration Decl(string id, params string[] perms) => new(id, "1.0.0", perms.ToHashSet());

    [Fact]
    public async Task Module_with_unapproved_permissions_needs_the_owner()
    {
        var sut = new InstallGateApprovalRequirement(new Declarations(Decl("m", "net:host", "fs:path")), new Approvals(["net:host"]));
        Assert.True(await sut.NeedsOwnerApprovalAsync("m", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Module_with_all_permissions_approved_does_not_need_the_owner()
    {
        var sut = new InstallGateApprovalRequirement(new Declarations(Decl("m", "net:host", "fs:path")), new Approvals(["net:host"], ["fs:path"]));
        Assert.False(await sut.NeedsOwnerApprovalAsync("m", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unknown_module_and_module_declaring_nothing_need_nothing()
    {
        var sut = new InstallGateApprovalRequirement(new Declarations(Decl("quiet")), new Approvals());
        Assert.False(await sut.NeedsOwnerApprovalAsync("unknown", TestContext.Current.CancellationToken));
        Assert.False(await sut.NeedsOwnerApprovalAsync("quiet", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Declared_permissions_with_no_readable_approvals_fail_closed()
    {
        var sut = new InstallGateApprovalRequirement(new Declarations(Decl("m", "net:host")), null);
        Assert.True(await sut.NeedsOwnerApprovalAsync("m", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Host_default_source_declares_nothing()
    {
        var sut = new InstallGateApprovalRequirement(new HostModuleDeclarationSource(), null);
        Assert.False(await sut.NeedsOwnerApprovalAsync("any", TestContext.Current.CancellationToken));
    }
}
