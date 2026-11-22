# Spike S-BIO: Biometric assertion format

Branch: `claude/biometric-module` (the session's branch; the plan row is in `docs/design/35-spikes-plan.md`). Design source: `docs/design/48-gap-decisions-security-identity.md` section 8.1 (PROPOSED).

## Question
Can a server verify, from a WebAuthn assertion, that a fresh biometric check on the enrolled owner device covers one exact high-risk action, and what can it not know?

## Pass criteria (fixed before the first run)
Copied from doc 48 section 8.1, committed in `3ab2b1be` before any run.

- **B1 (software, CI).** A software ES256 authenticator produces assertions in the 8.1 format. The verifier accepts a correct one and refuses each of: wrong version, wrong challenge (another action), wrong origin, wrong rpIdHash, UP or UV clear, backup flags set, bad signature, replayed or lower sign count, stale `bazaar.at`, unattested or disallowed factor, liveness not passed on a high-risk action, unknown credential, and oversized or truncated input. Hostile input never throws.
- **B2 (hardware).** An Android Keystore key that requires BIOMETRIC_STRONG on every use signs the challenge, the key attestation chain verifies, and the attested properties show per-use biometric. A Windows Hello credential signs and the platform reports whether the factor was biometric or PIN.
- **B3 (hardware).** The owner's LiDAR device and the multi-lens fallback report the factor and the liveness result in a form the client can place in `bazaar.factor` and `bazaar.liveness`.

Fail consequence: a platform that cannot tell biometric from PIN stays read-only for high-risk actions; a factor with no liveness report is dropped from the allowed list.

## Time-box
B1: a few hours (done). B2 and B3: one session on each of the owner's devices.

## Method
B1 only. `tests/Bazaar.Server.Biometric.Tests` holds `SoftwareAuthenticator`, which builds assertions exactly as 8.1 describes (authenticator data, client data with the `bazaar` members, DER ES256 signature over authenticator data and the client-data hash) and `BiometricAssertionVerifierTests`, which runs them through `BiometricAssertionVerifier` on an in-memory credential store. Environment: cloud container, Linux 6.18, .NET 11 SDK.

## Runs
| Run | Measure | Value |
|---|---|---|
| B1 | tests passed / failed (23 cases: 1 accept, 13 named refusals, replay and revocation, counter race, no-counter authenticator, unknown credential, wrong human and device, wrong key, 11 hostile-input shapes, bad stored key) | 23 / 0 |
| B2 | not run: needs the owner's Android and Windows devices | n/a |
| B3 | not run: needs the owner's LiDAR device | n/a |

## Result
B1: pass. B2 and B3: not run, so the spike is **inconclusive** and the format stays PROPOSED. A software authenticator proves the verifier implements the format and refuses the hostile cases. It cannot show that a real platform reports biometric versus PIN, per-use Keystore authentication, or liveness, which is what 8.1 is unsure about.

## Consequence
The verifier and its credential-store port are built and can be used against fakes. Before any high-risk action relies on it in production: run B2 and B3 on the owner's devices, write the enrolment path (doc 32 question 4) that verifies attestation and fills the credential store, and decide the Windows PIN-versus-biometric question named in 8.1. DECISIONS-LOG entry to write after B2 and B3: status of 8.1 and the Windows decision.
