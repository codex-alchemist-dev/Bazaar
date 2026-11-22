using System.Security.Cryptography;
using System.Text;
using Bazaar.Approvals;
using Bazaar.Client.Approvals;

namespace Bazaar.Client.TalentPool;

/// <summary>The card the owner sees: one template version and the exact tools it asks for.</summary>
/// <param name="TemplateId">Template id.</param>
/// <param name="Version">Template version.</param>
/// <param name="Name">Display name.</param>
/// <param name="Tools">The exact tool allowlist the server holds for this version.</param>
public sealed record ToolGrantCard(string TemplateId, int Version, string Name, IReadOnlyList<string> Tools)
{
    /// <summary>The key a hire vote and the grant use: <c>templateId@version</c>.</summary>
    public string HireKey => $"{TemplateId}@{Version}";
}

/// <summary>Sends the signed grant to the server, which verifies it again (<c>TalentToolGrantApprover.ApproveAsync</c>).</summary>
public interface IToolGrantSender
{
    /// <summary>Sends the owner-signed payload.</summary>
    ValueTask SendAsync(string templateId, int version, OwnerPayload payload, byte[] signature, CancellationToken cancellationToken);
}

/// <summary>Lists the template versions whose tool grants wait for this owner's approval.</summary>
public interface IPendingToolGrantSource
{
    /// <summary>The cards to show. Each carries the exact tool list the server holds for that version.</summary>
    ValueTask<IReadOnlyList<ToolGrantCard>> ListPendingAsync(CancellationToken cancellationToken);
}

/// <summary>Why the gate refused. Nothing is sent for any refusal except <see cref="SendFailed"/>.</summary>
public enum ToolGrantRefusal
{
    /// <summary>The request did not come from the card's own button.</summary>
    NotFromCard,

    /// <summary>The card has no tools or a malformed id.</summary>
    InvalidCard,

    /// <summary>The person cancelled the biometric prompt.</summary>
    BiometricCancelled,

    /// <summary>The biometric failed or is unavailable.</summary>
    BiometricFailed,

    /// <summary>The device key could not sign.</summary>
    SigningFailed,

    /// <summary>A grant for this template version was already signed on this device. A signed grant is never replayed.</summary>
    AlreadyDecided,

    /// <summary>The signed grant could not be sent. It is not retried.</summary>
    SendFailed,
}

/// <summary>Outcome of one approval attempt.</summary>
public sealed record ToolGrantResult(ToolGrantRefusal? Refusal)
{
    /// <summary>True when the signed grant was sent.</summary>
    public bool IsSent => Refusal is null;
}

/// <summary>
/// The owner's device side of a Talent tool grant (docs/design/25, docs/design/32 section 5a, docs/design/29 Approvals). Approving
/// asks for a fresh biometric, signs an <see cref="OwnerPayload"/> for action <c>talent.tool_grant</c> whose args hash covers the
/// template version and its exact tool list, and sends it. Only a tap on the card counts; there is no signed "deny", so declining
/// means doing nothing. A signed grant is final on this device.
/// </summary>
public sealed class ToolGrantGate(
    IBiometricPrompt biometric,
    IDeviceApprovalSigner signer,
    IToolGrantSender sender,
    Guid humanId,
    TimeProvider? clock = null)
{
    /// <summary>The owner-signed action name. Equals the server's <c>TalentToolGrantApprover.Action</c>.</summary>
    public const string Action = "talent.tool_grant";

    /// <summary>How long a signed grant stays valid. At most the server's default ceiling of five minutes.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _signed = new(StringComparer.Ordinal);

    /// <summary>The args hash the server expects: SHA-256 of <c>hire_key</c>, a newline and the tool-list hash. Same as <c>TalentToolGrantApprover.ArgsHashFor</c>.</summary>
    public static byte[] ArgsHashFor(string hireKey, IReadOnlyList<string> tools)
    {
        var toolsSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', tools.Order(StringComparer.Ordinal)))));
        return SHA256.HashData(Encoding.UTF8.GetBytes(hireKey + "\n" + toolsSha));
    }

    /// <summary>Runs one approval: validate, biometric, sign, send.</summary>
    public async ValueTask<ToolGrantResult> ApproveAsync(ToolGrantCard card, ApprovalInputSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (source != ApprovalInputSource.CardButton)
        {
            return new ToolGrantResult(ToolGrantRefusal.NotFromCard);
        }

        if (card.Tools.Count == 0 || string.IsNullOrWhiteSpace(card.TemplateId) || card.Version < 1)
        {
            return new ToolGrantResult(ToolGrantRefusal.InvalidCard);
        }

        lock (_gate)
        {
            if (!_signed.Add(card.HireKey))
            {
                return new ToolGrantResult(ToolGrantRefusal.AlreadyDecided);
            }
        }

        var approvalId = Guid.NewGuid();
        var hash = ArgsHashFor(card.HireKey, card.Tools);
        BiometricOutcome outcome;
        try
        {
            outcome = await biometric.AuthenticateAsync(
                new BiometricRequest(approvalId, Convert.ToHexStringLower(hash), "Confirm tool grant", $"{card.Name}: {string.Join(", ", card.Tools)}"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            outcome = BiometricOutcome.Cancelled;
        }
#pragma warning disable CA1031 // Any prompt failure means no biometric success.
        catch (Exception)
#pragma warning restore CA1031
        {
            outcome = BiometricOutcome.Failed;
        }

        if (outcome != BiometricOutcome.Succeeded)
        {
            Forget(card);
            return new ToolGrantResult(outcome == BiometricOutcome.Cancelled ? ToolGrantRefusal.BiometricCancelled : ToolGrantRefusal.BiometricFailed);
        }

        var now = _clock.GetUtcNow();
        var payload = new OwnerPayload(Action, approvalId, hash, null, humanId, signer.DeviceId, now, now + Lifetime, RandomNumberGenerator.GetBytes(OwnerSignedPayload.NonceLength));
        byte[] signature;
        try
        {
            signature = await signer.SignAsync(OwnerSignedPayload.Build(payload), cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A signing failure sends nothing; the reason is not trusted detail.
        catch (Exception)
#pragma warning restore CA1031
        {
            Forget(card);
            return new ToolGrantResult(ToolGrantRefusal.SigningFailed);
        }

        try
        {
            await sender.SendAsync(card.TemplateId, card.Version, payload, signature, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Not retried: a retry would replay a signed grant.
        catch (Exception)
#pragma warning restore CA1031
        {
            return new ToolGrantResult(ToolGrantRefusal.SendFailed);
        }

        return new ToolGrantResult(null);
    }

    /// <summary>Lets the owner try again after a refusal that signed nothing.</summary>
    private void Forget(ToolGrantCard card)
    {
        lock (_gate)
        {
            _signed.Remove(card.HireKey);
        }
    }
}
