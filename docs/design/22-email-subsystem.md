# Email Subsystem

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 14
Depends on: 01-gc-data-model.md (inbox GC, `gc_message` kinds, type registry) · 02-wire-protocol-and-sync.md (authenticated HTTP surface for blobs) · 03-server-host-and-providers.md (OS providers, no-inbound default) · 04-postgres-schema-and-migrations.md (conventions, migrations) · 05-model-gateway.md (reader-session model calls) · 14-identity-service-and-passport.md (`ISecretStore`, mailbox credentials, verification codes) · 15-security-taint-and-approvals.md (taint, tiers, reader sessions, audit) · 34-testing-strategy.md
Used by: 27-client-architecture.md, 28-windows-client.md, 29-android-client.md (quarantine and approval screens) · 33-backup-dr-and-export.md · 36-community-and-external-platforms.md (sign-up mail)

## Purpose
Gives each agent an external mailbox for what must leave the company: service sign-ups, requests to the owner, and outside contacts. Every inbound message is untrusted input, so mail is screened and quarantined before any agent sees it, and only reader sessions touch raw content. Nothing leaves the company without the tier gate. Internal coordination stays in GCs.

## Decided inputs
- Email is external only. Internal agent-to-agent coordination stays in GCs (Bazaar.md section 2; CLAUDE.md).
- Domain-agnostic; the operator configures the domain, never hardcoded (Bazaar.md section 2, owner 2026-10-24).
- Four v1 back ends: built-in Bazaar mail client, generic IMAP/SMTP, Cloudflare Email Routing with an HTTPS sender, built-in SMTP/IMAP server (Bazaar.md section 2; Bazaar.Server.md item 9).
- One address per agent (Bazaar.md section 2).
- Scam mail is quarantined; scam-baiting is optional behind a setup wizard (Bazaar.md sections 2 and 4, owner 2026-10-24).
- Agents sign up only where terms allow; CAPTCHA, phone and ID stay human (Bazaar.md section 2; 15 Tier 3).
- Ingress: the home host accepts no inbound connections by default; mail is pulled from the provider or Worker queue over outbound HTTPS (DECISIONS-LOG round 8, C30).
- Taint: external content taints hard; a session that reads raw external mail is hard-tainted; Tier 2 is gated on hard taint (DECISIONS-LOG round 2, C06).
- Reader and actor split is a v1 primitive for external input; actors never read external content directly (DECISIONS-LOG round 6, C18, owner statement; Claude's reading to confirm).
- Tier names and approval ladder come from 15, with 36's stricter defaults (DECISIONS-LOG round 7, C21).
- Secrets: provider and sender keys live in the Identity Service behind `ISecretStore` (14); agent VMs never hold them (DECISIONS-LOG round 2, C05).
- Erasure: personal fields are per-person encrypted payloads from day 1 (DECISIONS-LOG round 2, C01).
- Schema: each area's design doc owns its tables; the `mail_*` tables are defined here (DECISIONS-LOG round 6, C16).
- Shared rate buckets live in 15's quota table; this doc owns only mail-specific limits (DECISIONS-LOG round 10, C36).
- One OS-agnostic .NET server; OS-specific parts sit behind providers (CLAUDE.md).

## Design

### Components [PROPOSED]
A protocol-free mail core (addresses, store, screening, quarantine, outbound queue) in Bazaar.Server; back ends behind `IMailBackend`, one per domain; an inbound and an outbound pipeline; the setup wizard and DNS checker; the optional scam-baiting protocol.

### Back ends (v1)

| Back end | Transport | Host needs | Trade-off |
|---|---|---|---|
| Built-in Bazaar mail client | IMAP/SMTP via MailKit to Bazaar-managed mailboxes | Any mailbox host | Richest client view; "built-in" needs a definition (Q1) |
| Generic IMAP/SMTP | MailKit to an operator-named provider | Provider account | Simplest; provider sender rules apply |
| Cloudflare Email Routing + HTTPS sender | Routing rules forward to a Worker, which queues mail; the host pulls over outbound HTTPS; outbound via an HTTPS API (Resend Free: 3,000/month, 100/day) | Cloudflare DNS, sender account | No port 25; no inbound ports; catch-all and Routing pricing unverified |
| Built-in SMTP/IMAP server | Bazaar accepts SMTP and serves IMAP | Public address, port 25, PTR control (VPS) | Full control; needs inbound, which the no-inbound default (C30) excludes on a home link (Conflicts 1) |

### Account model
- `mail_domain`: one operator domain, its back end, its DNS provider reference.
- `mail_mailbox`: one address per agent, `<local-part>@<domain>`, bound to the Soul. Owner addresses are separate.
- Sender credentials are references into 14, resolved by the server's `ISecretStore`. Agent VMs and clients never hold them.
- An agent may send only from its own mailbox. The From header is set by the server.

### Inbound flow
1. The back end delivers the raw message, or the host pulls it from the Worker queue (pull is the default).
2. Parse MIME with limits on size, depth and part count. Drop auto-submitted mail to prevent loops.
3. Record SPF, DKIM and DMARC results. A pass proves the sending domain, not intent.
4. Deterministic screens: auth failure, lookalike sender domain, executable or macro attachment, link text that differs from its target, oversize.
5. Reader session (15): tool-less, always hard-tainted, returns a registered `mail_triage` hand-over with reason codes [PROPOSED]. It can escalate to quarantine but can never release.
6. Clean mail: the pipeline posts a system `gc_message` of kind `external-mail` with `source_trust=external` to the agent's inbox GC. It carries sender domain, time, screen codes and a `mail_message` reference, never the body. The accepted hand-over follows as a separate message. Actor sessions read only the approved hand-over in a fresh session, so the mail's raw content never reaches them (15 handover rule).
7. Quarantined mail: stored outside all agent context. The owner sees a summary in the client; agents see a count only.

### Outbound flow
1. The agent drafts through a mail tool. The server runs a tier check (15).
2. Tier 1 (15): recipient on the approved list and body built from a template; sent with an after-the-fact notice [PROPOSED mail mapping]. Tier 2: any new recipient, any text derived from inbound mail, any attachment, any sign-up reply. The owner approves each message; approvals never carry over. A session with hard taint always needs Tier 2 (C06).
3. The server appends the AI-agent footer and operator contact; the model cannot remove it [PROPOSED; counsel].
4. Send with an idempotency key; retry with backoff; bounces are ingested as untrusted input through the inbound pipeline.
5. Mail between two Bazaar addresses is refused. Internal traffic stays in GCs.

### Quarantine and reporting
- States: `held`, `released` (owner only), `purged` (after retention), `reported`.
- Release keeps the taint. A released message is still untrusted.
- Reporting to a provider's abuse channel needs per-report owner approval.
- Daily owner digest: counts and top sender domains. No message bodies.
- Raw quarantined content is personal data: encrypted per person (C01), purged after retention; the metadata entry stays in the GC.

### Attachments and links
- No agent opens an attachment in v1. Executables, scripts, macro-enabled Office files and nested archives are held; other files go to the blob store for owner viewing [PROPOSED; Q11].
- HTML renders as text, remote images are stripped, and links show their real host and are never fetched from mail content.
- Verification codes are read only through the Identity Service at Tier 2 (14, 15).

### Scam-baiting protocol (optional)
- Off by default. The wizard states the risks in plain words: harm to real victims, harassment complaints, provoking criminals, malware, domain suspension, and legal exposure that varies by jurisdiction (needs counsel).
- Enabling records a dated acknowledgement in the audit log (`audit_log`, 15).
- The bait identity is a fictional persona with its own mailbox. It never uses an agent's real address, a real person's name, Passport access, real payment data or real personal data.
- Every reply is owner-approved with a written reason.
- Automatic stop and disable on: any sign of a real third party, any threat, any request for credentials or money.

### DNS and deliverability guidance
The wizard checks the operator's domain (example.com as placeholder):
- SPF: one `v=spf1` record that includes the sender only, ending `-all`.
- DKIM: the selector TXT that the sender provides.
- DMARC: start `v=DMARC1; p=none; rua=mailto:<operator-report-address>`. Move to `quarantine`, then `reject`, once reports are clean.
- PTR (self-hosted only): forward-confirmed reverse DNS, set by the host provider.
- Warn when a domain approaches Gmail bulk-sender thresholds (over 5,000 messages a day) [RESEARCHED].
- Free subdomain registries are not recommended. The research found pricing and reputation unverified.

## Data and interfaces

```csharp
public interface IMailBackend
{
    Task<IReadOnlyList<RawMail>> ReceiveAsync(MailboxRef box, CancellationToken ct); // pull
    Task<SendReceipt> SendAsync(OutboundMail mail, CancellationToken ct); // idempotent on mail.Key
    Task<BackendHealth> ProbeAsync(CancellationToken ct);
}
```

Tables owned by this doc (C16); migrations run through 04. Personal fields (`body`, raw blobs, addresses) use the per-person encrypted payload pattern from 04 (C01).
- `mail_domain(id, domain, backend_kind, config_ref, state)`
- `mail_mailbox(id, soul_id NULL for owner, domain_id, local_part, state)`
- `mail_message(id, mailbox_id, direction, rfc_message_id, from_addr, auth_json, screen_codes, state, gc_message_id, blob_ref, received_at, retention_until)`
- `mail_outbound(idempotency_key, message_id, approval_id, status, attempts)`
- `mail_blob(id, sha256, size_bytes, scan_state)`; served over the authenticated HTTP surface in 02 (C15).

Inbound GC message: `gc_message` kind `external-mail`, `source_trust=external` (15's provenance enum, C20), `mailbox_id`, `mail_message` reference. The `gc_event`/`gc_message`/`gc_message_version` model in 01 and 04 applies (C09). The kind is registered in 01's GC type registry.

## Behaviour and edge cases
- Limits [PROPOSED]: 10 MiB per message, 5 attachments. Cloudflare's 25 MiB inbound cap is the hard ceiling (verified).
- Duplicate delivery is normal; dedupe on `rfc_message_id` plus backend id. Order is per mailbox by receive time only.
- Outage: inbound waits at the Worker or back end; outbound queues with backoff. No silent drop.
- Quarantined and held content never enters a prompt, memory or the Vault. Hand-overs are the only path from a reader to actors.
- Logs redact addresses and bodies; credentials never appear in logs.
- Rate caps [PROPOSED]: 20 new-recipient messages a day per agent; a breach pauses the mailbox and notifies the owner. The shared buckets sit in 15's quota table.
- Cloudflare allows 200 routing rules per domain (verified), so one domain holds about 200 mailboxes. Resend Free caps the whole operator at 100 messages a day; Pro is $20 a month (pricing dated 2026-10-24, secondary).

## Testing
- **Fake mail servers:** in-process SMTP and IMAP listeners on loopback; MailKit runs against them unchanged. A stub `HttpMessageHandler` stands in for the HTTPS sender. A signed Worker-style payload fixture covers bad-signature, replay and valid cases.
- **Corpus:** synthetic mail only (auth failure, lookalike domain, zip bomb, nested multipart, remote image, injected instructions, malformed MIME, oversize, bounce and auto-responder loops).
- **Key cases:** quarantined mail never reaches agent context; an actor session cannot read a raw `mail_message` body; the reader session cannot release; a clean message sets hard taint on its reader session; a hard-tainted send needs Tier 2 approval; a repeated idempotency key sends once; the bait protocol needs acknowledgement and cannot use an agent address; the DNS checker runs against a stub resolver.
- **Real Postgres** (34); **Avalonia.Headless** for quarantine and approval screens.
- **Real deliverability** is not tested in CI. A manual checklist runs on a test domain against an owner-controlled seed inbox.

## Work packages
1. Schema and contracts (`mail_*` tables, `IMailBackend`). Needs 04.
2. MIME parser with limits, plus the synthetic corpus.
3. Deterministic screens and the reader-session stub (fake model via 05). Needs 15's reader primitive.
4. Inbound pipeline to GC message with taint. Needs 01 and 15.
5. Quarantine store, release and digest. Needs 2 and 3.
6. Outbound pipeline: tiers, footer, idempotency. Needs 15.
7. MailKit back ends for generic IMAP/SMTP and the built-in client. Needs 6.
8. Cloudflare Worker, pull receiver and HTTPS sender. Needs 6.
9. Built-in SMTP/IMAP server. Needs Q2 and 7.
10. DNS checker and setup wizard. Needs 1.
11. Scam-baiting protocol, last and off by default. Needs 6 and 4.
12. Client screens (27 to 29). Needs 4 to 6.

Order: 1; then 2, 3 and 10 in parallel; then 4; then 5 and 6; then 7 to 9; then 11.

## Conflicts
1. **Built-in SMTP/IMAP server vs the no-inbound default.** It is a DECIDED v1 back end, but it must accept inbound on port 25, and port 25, PTR and reputation rule out a home connection. C30 sets no inbound by default. Workable only on a VPS, which needs an explicit owner decision (Q2).
2. **Scam-baiting.** The owner decided it is optional. Research (email note KQ6; threat model section 5) recommends dropping autonomous baiting entirely. This design follows the owner: optional, owner-approved per reply. Counsel and research disagree.
3. **Quarantine vs erasure.** Raw quarantined mail holds stranger personal data. C01 gives per-person encryption and key destruction, but legal sufficiency is unknown (Server.md Q17, needs counsel). Counsel decides whether quarantine is exempt. Default: metadata entry in the GC; raw content purged after retention.
4. **Reader/actor split for mail.** C18 is an owner statement with Claude's reading attached ("reader sessions for all external input in v1"). This design follows it; the reading still needs the owner's confirmation (15 Q1).
5. **"Built-in Bazaar mail client"** is undefined in the sources and may overlap generic IMAP/SMTP (Q1).
6. **EU AI Act Article 50 timing** (2 August 2026, or a possible December 2026 grace period) governs the footer. Needs counsel.
7. **Cloudflare Routing pricing and catch-all** are unverified; the "free" claim is secondary.
8. **For other docs:** 04 must drop any `mail_*` outline and use this doc's tables (C16). 02 must define the blob HTTP surface that `mail_blob` uses (C15). 15 must register the `mail_triage` hand-over schema.

## Questions for the owner
1. What is "built-in Bazaar mail client" vs generic IMAP/SMTP? Default: same MailKit wire code; built-in means Bazaar-managed mailboxes with a client view.
2. Keep the built-in SMTP/IMAP server in v1, VPS-only? Default: yes, with no home-host support and an explicit exception to the no-inbound default.
3. Are quarantined messages visible to agents? Default: no; agents see a count.
4. Who releases quarantine? Default: owner only; release keeps the taint.
5. Can the reader model release mail? Default: no; it can only add quarantine.
6. Keep scam-baiting in v1, off by default? Default: yes, owner-approved per reply. Research advises dropping it.
7. Rename "Nigerian Prince Protocol"? Default: a neutral name such as "Bait protocol"; the current name names a nationality.
8. Bait identity: fictional persona or owner's name? Default: fictional persona with its own mailbox.
9. Footer wording and placement? Default: states an AI agent sent it, with operator contact. Counsel reviews.
10. Raw quarantine retention? Default: 90 days, then purge; metadata stays in the GC. Backup retention must match (33, C01).
11. Attachments: owner viewing only, or PDF and images to agents? Default: owner viewing only.
12. Per-agent caps? Default: 20 new recipients a day; 100 messages a day in total.
13. Sign-up verification codes to the agent? Default: yes, via the Identity Service at Tier 2.
14. Owner's address forwards into the owner's inbox GC? Default: yes, owner-only.

ANSWERED: Cloudflare inbound is pull; the Worker queues and the host polls over outbound HTTPS, Worker POST optional later (DECISIONS-LOG round 8, C30).
ANSWERED: Sender API keys sit in the Identity Service behind `ISecretStore`, never in agent VMs (DECISIONS-LOG round 2, C05).
