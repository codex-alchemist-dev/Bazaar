using System.Security.Cryptography;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Approvals;
using Bazaar.Client.Mobile;
using Bazaar.Contracts.GitHub;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.PullRequests.Tests;

/// <summary>A biometric prompt whose outcome the test sets. Counts prompts; a fresh prompt happens on every call.</summary>
internal sealed class FakeBiometric : IBiometricPrompt
{
    public BiometricOutcome Outcome { get; set; } = BiometricOutcome.Succeeded;

    /// <summary>Runs before the outcome is returned, so a test can move the clock during the prompt.</summary>
    public Action? OnPrompt { get; set; }

    public int Prompts { get; private set; }

    public ValueTask<BiometricOutcome> AuthenticateAsync(BiometricRequest request, CancellationToken cancellationToken)
    {
        Prompts++;
        OnPrompt?.Invoke();
        return ValueTask.FromResult(Outcome);
    }
}

/// <summary>An ECDSA P-256 device key that signs with the same IEEE P1363 layout the server verifies.</summary>
internal sealed class FakeSigner : IDeviceApprovalSigner, IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string DeviceId => "phone-1";

    public int Signs { get; private set; }

    public ECDsa PublicKey => _key;

    public ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken)
    {
        Signs++;
        return ValueTask.FromResult(_key.SignData(canonicalPayload.Span, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>A PR client that records each call and answers from queued replies.</summary>
internal sealed class FakePullRequestClient : IPullRequestClient
{
    public List<PrListReply> Lists { get; } = [];

    public Queue<PrSummaryReply> Summaries { get; } = new();

    public Queue<PrHunksReply> Hunks { get; } = new();

    public Queue<PrReviewReply> Reviews { get; } = new();

    public Queue<PrMergeReply> Merges { get; } = new();

    public List<PrMergeRequest> MergeRequests { get; } = [];

    public List<PrReviewRequest> ReviewRequests { get; } = [];

    public List<PrHunksRequest> HunksRequests { get; } = [];

    public Task<PrListReply> ListAsync(PrListRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Lists.Count > 0 ? Lists[0] : new PrListReply([], null));

    public Task<PrSummaryReply> SummaryAsync(string prId, CancellationToken cancellationToken) =>
        Task.FromResult(Summaries.Dequeue());

    public Task<PrHunksReply> HunksAsync(PrHunksRequest request, CancellationToken cancellationToken)
    {
        HunksRequests.Add(request);
        return Task.FromResult(Hunks.Dequeue());
    }

    public Task<PrReviewReply> ReviewAsync(PrReviewRequest request, CancellationToken cancellationToken)
    {
        ReviewRequests.Add(request);
        return Task.FromResult(Reviews.Dequeue());
    }

    public Task<PrMergeReply> MergeAsync(PrMergeRequest request, CancellationToken cancellationToken)
    {
        MergeRequests.Add(request);
        return Task.FromResult(Merges.Dequeue());
    }
}

/// <summary>Stands in for the socket's request path. It records the message type and answers with a queued object of the asked type.</summary>
internal sealed class FakeWireRequester : IWireRequester
{
    public List<string> Types { get; } = [];

    public object? Reply { get; set; }

    public Task<TResponse> RequestAsync<TRequest, TResponse>(string type, TRequest payload, CancellationToken cancellationToken = default)
    {
        Types.Add(type);
        return Task.FromResult((TResponse)Reply!);
    }
}

internal static class Rig
{
    public const string HeadA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string HeadB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public static readonly Guid Human = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public static PrListRow Row(string head = HeadA, bool withIdentity = true, PrChecksRollup checks = PrChecksRollup.Passing, bool draft = false) =>
        new(
            "PR_1", "acme/widgets", 42, "Add widget", "bot", PrAuthorKind.Agent, draft, head, checks, 7,
            withIdentity ? 9001 : null, withIdentity ? "acme" : null, withIdentity ? "widgets" : null);

    public static PrSummaryReply Summary(string head = HeadA, PrChecksRollup checks = PrChecksRollup.Passing, bool identity = true) =>
        new(
            "PR_1", head, "Adds the widget.", [new PrChangedFile("src/widget.cs", "modified", 3, 1)], checks, true, null,
            identity ? 9001 : null, identity ? "acme" : null, identity ? "widgets" : null, identity ? 42 : null);

    public static AndroidSettings Settings(bool mergeFromFolded = true, bool foldedFullDiff = false) =>
        new() { MergeFromFolded = mergeFromFolded, FoldedFullDiff = foldedFullDiff };
}
