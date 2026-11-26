using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Council.Data;
using Bazaar.Server.Council.Service;

namespace Bazaar.Server.Council.Tests.Service;

public class CouncilSealedReasonTests
{
    private static readonly string[] _threeVoters = ["cal", "dot", "eve"];

    private static async Task<(CouncilHarness H, Guid Id, ICouncilService Service)> RevealedAsync()
    {
        var h = new CouncilHarness { Options = new CouncilOptions { ReasonOrder = ReasonOrder.Fixed } };
        h.AddThreeVoters();
        var service = h.CreateService();
        var id = await service.OpenAsync("ada", h.Motion(), CancellationToken.None);
        foreach (var voter in _threeVoters)
        {
            await service.SubmitBallotAsync(id, voter, Ballots.Independent(voter, "yes"), CancellationToken.None);
        }

        return (h, id, service);
    }

    [Fact]
    public async Task Reveal_appends_one_sealed_reason_per_voter_in_reveal_order_with_no_plaintext()
    {
        var (h, id, _) = await RevealedAsync();

        var reasons = h.Events.OfKind("council.reason").ToList();
        Assert.Equal(_threeVoters.Select(v => $"reason:{id:D}:{v}:independent:reveal").ToArray(), reasons.Select(e => e.IdempotencyKey).ToArray());
        Assert.All(reasons, reason =>
        {
            Assert.NotNull(reason.SealedBlob);
            Assert.NotEqual(Guid.Empty, reason.SealedKeyId);
            Assert.DoesNotContain(Ballots.Reasoning, reason.PayloadJson, StringComparison.Ordinal);
            Assert.DoesNotContain(Ballots.Reasoning, Encoding.UTF8.GetString(reason.SealedBlob!), StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(reason.PayloadJson);
            Assert.Equal("reveal", doc.RootElement.GetProperty("context").GetString());
            Assert.Equal("independent", doc.RootElement.GetProperty("phase").GetString());
            Assert.False(doc.RootElement.GetProperty("erased_at_write").GetBoolean());
        });
        Assert.True(h.Events.Events.FindIndex(e => e.Kind == "council.reveal") < h.Events.Events.FindIndex(e => e.Kind == "council.reason"));
    }

    [Fact]
    public async Task Sealed_blob_is_the_stored_seal_repacked_and_opens_with_the_cipher()
    {
        var (h, id, _) = await RevealedAsync();
        var stored = (await h.Store.ListSealedReasonsAsync(id, CancellationToken.None)).Single(r => r.VoterId == "cal");
        var evt = h.Events.OfKind("council.reason").Single(e => e.IdempotencyKey.Contains(":cal:", StringComparison.Ordinal));

        Assert.Equal(stored.Reasoning.KeyId, evt.SealedKeyId);
        Assert.Equal(CouncilSealedBlob.Pack(stored.Reasoning), evt.SealedBlob);
        var unpacked = CouncilSealedBlob.Unpack(evt.SealedBlob!, evt.SealedKeyId!.Value);
        Assert.Equal(stored.Reasoning.Ciphertext, unpacked.Ciphertext);
        Assert.Equal(stored.Reasoning.Nonce, unpacked.Nonce);
    }

    [Fact]
    public async Task Decision_payload_carries_no_reasoning_and_follow_up_reasons_are_sealed_rows()
    {
        var h = new CouncilHarness();
        h.AddThreeVoters();
        var service = h.CreateService();
        var id = await service.OpenAsync("ada", h.Motion(highImpact: true), CancellationToken.None);
        await service.SubmitBallotAsync(id, "cal", Ballots.Independent("cal", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "dot", Ballots.Independent("dot", "yes"), CancellationToken.None);
        await service.SubmitBallotAsync(id, "eve", Ballots.Independent("eve", "no"), CancellationToken.None);
        foreach (var voter in _threeVoters)
        {
            await service.SubmitBallotAsync(id, voter, Ballots.FollowUp(voter, "yes"), CancellationToken.None);
        }

        var decision = h.Events.OfKind("council.decision").Single();
        Assert.DoesNotContain(Ballots.Reasoning, decision.PayloadJson, StringComparison.Ordinal);
        var followUps = h.Events.OfKind("council.reason").Where(e => e.IdempotencyKey.EndsWith(":follow_up:decision", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, followUps.Count);
        Assert.Equal(3, h.Events.OfKind("council.reason").Count(e => e.IdempotencyKey.EndsWith(":independent:reveal", StringComparison.Ordinal)));
    }

    [Fact]
    public void Blob_round_trips_and_rejects_a_bad_format_byte_or_short_input()
    {
        var keyId = Guid.CreateVersion7();
        var source = new ReasoningCiphertext(keyId, RandomNumberGenerator.GetBytes(40), RandomNumberGenerator.GetBytes(12));

        var blob = CouncilSealedBlob.Pack(source);
        var back = CouncilSealedBlob.Unpack(blob, keyId);
        Assert.Equal(source.Ciphertext, back.Ciphertext);
        Assert.Equal(source.Nonce, back.Nonce);

        blob[0] = 9;
        Assert.Throws<CryptographicException>(() => CouncilSealedBlob.Unpack(blob, keyId));
        Assert.Throws<CryptographicException>(() => CouncilSealedBlob.Unpack(new byte[5], keyId));
        Assert.Throws<CryptographicException>(() => CouncilSealedBlob.Pack(new ReasoningCiphertext(keyId, [1], new byte[11])));
    }
}
