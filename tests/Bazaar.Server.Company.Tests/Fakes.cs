using Bazaar.Server.Security.Audit;
using Bazaar.Server.Setup;

namespace Bazaar.Server.Company.Tests;

internal sealed class MemoryAudit : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];

    public bool Fail { get; set; }

    public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            throw new InvalidOperationException("audit down");
        }

        Entries.Add(entry);
        return Task.CompletedTask;
    }
}

internal sealed class MemoryProfileStore : ICompanyProfileStore
{
    public CompanyProfileRequest? Held { get; set; }

    public Task<CompanyProfileResult> EnsureAsync(CompanyProfileRequest request, CancellationToken cancellationToken)
    {
        if (Held is null)
        {
            Held = request;
            return Task.FromResult(CompanyProfileResult.WasCreated);
        }

        return Task.FromResult(Held == request ? CompanyProfileResult.AlreadyPresent : CompanyProfileResult.Refused("conflict"));
    }

    public Task RemoveAsync(string slug, CancellationToken cancellationToken)
    {
        if (Held?.Slug == slug)
        {
            Held = null;
        }

        return Task.CompletedTask;
    }
}

internal sealed class MemoryOwnerStore : IOwnerStore
{
    public string? Name { get; set; }

    public OwnerRequest? Choices { get; set; }

    public Task<OwnerResult> EnsureAsync(OwnerRequest request, CancellationToken cancellationToken)
    {
        if (Name is not null && Name != request.DisplayName)
        {
            return Task.FromResult(OwnerResult.Refused("another owner"));
        }

        var ownerId = Name is null ? Guid.NewGuid() : (Guid?)null;
        Name = request.DisplayName;
        var choicesCreated = Choices is null;
        Choices ??= request;
        return Task.FromResult(new OwnerResult(new OwnerOutcome(ownerId, choicesCreated), null));
    }

    public Task RollbackAsync(OwnerOutcome outcome, CancellationToken cancellationToken)
    {
        if (outcome.OwnerCreatedId is not null)
        {
            Name = null;
        }

        if (outcome.ChoicesCreated)
        {
            Choices = null;
        }

        return Task.CompletedTask;
    }
}

internal static class Plans
{
    public const string Operator = "operator-test";

    public static SetupPlan Plan(string ownerName = "Secret Owner Name", string? bundleRef = null, bool printed = false) => new()
    {
        Install = new SetupInstallSection { CompanyName = "Example Studio", CompanySlug = "example-studio", Locale = "en", Timezone = "Europe/London" },
        Owner = new SetupOwnerSection
        {
            DisplayName = ownerName,
            RequireSecondFactor = true,
            Recovery = new SetupRecoverySection { BundleRef = bundleRef, PrintedCodes = printed },
        },
    };

    public static SetupApplyContext Context(string hash = "hash-1") =>
        new(hash, SetupEntryPath.Cli, Operator, null, null, SetupStateSnapshot.Empty, []);
}
