# Email Subsystem

Status: DRAFT for owner review (2026-10-25) · Owner questions: 16
Depends on: 01-gc-data-model.md (inbox GC, entry kinds) · 03-server-host-and-providers.md (OS providers, inbound reachability) · 04-postgres-schema-and-migrations.md · 05-model-gateway.md (reader model) · 14-identity-service-and-passport.md (mailbox credentials, verification codes) · 15-security-taint-and-approvals.md (taint, tiers, audit) · 34-testing-strategy.md
Used by: 27-client-architecture.md, 28-windows-client.md, 29-android-client.md (quarantine and approval screens) · 33-backup-dr-and-export.md · 36-community-and-external-platforms.md (sign-up mail)

## Purpose
Gives each agent an external mailbox for what must leave the company: service sign-ups, requests to the owner, and outside contacts. Every inbound message is untrusted input, so mail is screened and quarantined before any agent sees it, and nothing leaves the company without the tier gate. Internal coordination stays in GCs.

## Decided inputs
- Email is external only. Internal agent-to-agent coordination stays in GCs (Bazaar.md section 2; CLAUDE.md).
- Domain-agnostic; the operator configures the domain, never hardcoded (Bazaar.md section 2, owner 2026-10-24).
- Four v1 back ends: built-in Bazaar mail client, generic IMAP/SMTP, Cloudflare Email Routing with an HTTPS sender, built-in SMTP/IMAP server (Bazaar.md section 2; Bazaar.Server.md item 9).
- One address per agent (Bazaar.md section 2).
- Scam mail is quarantined; scam-baiting is completely optional behind a setup wizard (Bazaar.md sections 2 and 4, owner 2026-10-24).
- Agents sign up only where terms allow; CAPTCHA, phone and ID stay human (Bazaar.md section 2, owner).
- Inbound content is untrusted; taint plus owner approval gate high-impact actions (Bazaar.Server.md item 13, owner 2026-10-24).
- One OS-agnostic .NET server; OS-specific parts sit behind providers (CLAUDE.md).

## Design

### Components [PROPOSED]
A protocol-free mail core (addresses, store, screening, quarantine, outbound queue) in Bazaar.Server; back ends behind `IMailBackend`, one per domain; an inbound and an outbound pipeline; the setup wizard and DNS checker; the optional scam-baiting protocol.

### Back ends (v1)

| Back end | Transport | Host needs | Trade-off |
|---|---|---|---|
| Built-in Bazaar mail client | IMAP/SMTP via MailKit to Bazaar-managed mailboxes | Any mailbox host | Richest client view; "built-in" needs a definition (Q1) |
| Generic IMAP/SMTP | MailKit to an operator-named provider | Provider account | Simplest; provider sender rules apply |
| Cloudflare Email Routing + HTTPS sender | Routing rules forward to a Worker; Worker hands mail to the server; outbound via an HTTPS API (Resend Free: 3,000/month, 100/day) | Cloudflare DNS, sender account | No port 25; low maintenance; catch-all and Routing pricing unverified |
| Built-in SMTP/IMAP server | Bazaar accepts SMTP and serves IMAP | Public address, port 25, PTR control (VPS) | Full control; port 25, PTR and reputation fail on residential links (Conflicts 1) |

### Account model
- `mail_domain`: one operator domain, its back end, its DNS provider reference.
- `mail_mailbox`: one address per agent, `<local-part>@<domain>`, bound to the Soul. Owner addresses are separate.
- Sender credentials are references into 14. Agent VMs never hold them.
- An agent may send only from its own mailbox. The From header is set by the server.

### Inbound flow
1. Back end delivers the raw message, or the Worker stores it for pickup.
2. Parse MIME with limits on size, depth and part count. Drop auto-submitted mail to prevent loops.
3. Record SPF, DKIM and DMARC results. A pass proves the sending domain, not intent.
4. Deterministic screens: auth failure, lookalike sender domain, executable or macro attachment, link text that differs from its target, oversize.
5. Reader model [PROPOSED]: a session with no tools returns a schema-valid label and reason codes. It can escalate to quarantine but can never release.
6. Clean mail: post an `external-mail` entry to the agent's inbox GC with `trust=untrusted-external`. The body is a quoted data block. Any session that reads it is tainted (15).
7. Quarantined mail: stored outside all agent context. The owner sees a summary in the client; agents see a count only.

### Outbound flow
1. The agent drafts through a mail tool. The server runs a tier check (15).
2. Tier 1: recipient on the approved list and body built from a template; sent with an after-the-fact notice. Tier 2: any new recipient, any text derived from inbound mail, any attachment, any sign-up reply. The owner approves each message; approvals never carry over. A tainted session always needs Tier 2.
3. The server appends the AI-agent footer and operator contact; the model cannot remove it [PROPOSED; counsel].
4. Send with an idempotency key; retry with backoff; bounces are ingested as untrusted input.
5. Mail between two Bazaar addresses is refused. Internal traffic stays in GCs.

### Quarantine and reporting
- States: `held`, `released` (owner only), `purged` (after retention), `reported`.
- Release keeps the taint. A released message is still untrusted.
- Reporting to a provider's abuse channel needs per-report owner approval.
- Daily owner digest: counts and top sender domains. No message bodies.

### Attachments and links
- No agent opens an attachment in v1. Executables, scripts, macro-enabled Office files and nested archives are held; other files go to the blob store for owner viewing [PROPOSED; Q12].
- HTML renders as text, remote images are stripped, and links show their real host and are never fetched from mail content.
- Verification codes are read only through the Identity Service at Tier 2 (14).

### Scam-baiting protocol (optional)
- Off by default. The wizard states the risks in plain words: harm to real victims, harassment complaints, provoking criminals, malware, domain suspension, and legal exposure that varies by jurisdiction (needs counsel).
- Enabling records a dated acknowledgement in the audit log (15).
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
    Task<IReadOnlyList<RawMail>> ReceiveAsync(MailboxRef box, CancellationToken ct);
    Task<SendReceipt> SendAsync(OutboundMail mail, CancellationToken ct); // idempotent on mail.Key
    Task<BackendHealth> ProbeAsync(CancellationToken ct);
}
```

Tables (outline only; migrations in 04):
- `mail_domain(id, domain, backend_kind, config_ref, state)`
- `mail_mailbox(id, soul_id NULL for owner, domain_id, local_part, state)`
- `mail_message(id, mailbox_id, direction, rfc_message_id, from_addr, auth_json, screen_codes, state, gc_entry_id, blob_ref, received_at, retention_until)`
- `mail_outbound(idempotency_key, message_id, approval_id, status, attempts)`
- `mail_blob(id, sha256, size_bytes, scan_state)`

Inbound GC entry: `kind=external-mail`, `trust=untrusted-external`, `source=email`, `mailbox_id`, `screen_codes`.

## Behaviour and edge cases
- Limits [PROPOSED]: 10 MiB per message, 5 attachments. Cloudflare's 25 MiB inbound cap is the hard ceiling (verified).
- Duplicate delivery is normal; dedupe on `rfc_message_id` plus backend id. Order is per mailbox by receive time only.
- Outage: inbound waits at the Worker or back end; outbound queues with backoff. No silent drop.
- Quarantined and held content never enters a prompt, memory or the Vault.
- Logs redact addresses and bodies; credentials never appear in logs.
- Rate caps per agent [PROPOSED]: 20 new-recipient messages a day; a breach pauses the mailbox and notifies the owner.
- Cloudflare allows 200 routing rules per domain (verified), so one domain holds about 200 mailboxes. Resend Free caps the whole operator at 100 messages a day; Pro is $20 a month (pricing dated 2026-10-24).

## Testing
- **Fake mail servers:** in-process SMTP and IMAP listeners on loopback; MailKit runs against them unchanged. A stub `HttpMessageHandler` stands in for the HTTPS sender. A signed Worker-style payload fixture covers bad-signature, replay and valid cases.
- **Corpus:** synthetic mail only (auth failure, lookalike domain, zip bomb, nested multipart, remote image, injected instructions, malformed MIME, oversize, bounce and auto-responder loops).
- **Key cases:** quarantined mail never reaches agent context; the reader model cannot release; a clean message sets taint; a tainted send needs approval; a repeated idempotency key sends once; bait protocol needs acknowledgement and cannot use an agent address; DNS checker against a stub resolver.
- **Real Postgres** (34); **Avalonia.Headless** for quarantine and approval screens.
- **Real deliverability** is not tested in CI. A manual checklist runs on a test domain against an owner-controlled seed inbox.

## Work packages
1. Schema and contracts (`mail_*` tables, `IMailBackend`). Needs 04.
2. MIME parser with limits, plus the synthetic corpus.
3. Deterministic screens and a fake reader model.
4. Inbound pipeline to GC entry with taint. Needs 01 and 15.
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
1. **Built-in SMTP/IMAP server vs research.** It is a DECIDED v1 back end, but port 25, PTR and reputation rule out a home connection. Workable only on a VPS. The remaining-gaps report already asks the owner to decide this.
2. **Inbound delivery to a home host.** The Cloudflare path has the Worker POST to the host. Bazaar.md says the home host never accepts inbound. The fix is host-initiated pull or an outbound tunnel. Check against 32.
3. **Scam-baiting.** The owner decided it is optional. Research (email note KQ6; threat model section 5) recommends dropping autonomous baiting entirely. This design follows the owner: optional, owner-approved per reply. Counsel and research disagree.
4. **Reader and actor split.** The threat model makes it a P0 requirement before any agent reads email. Bazaar.Server.md item 13 leaves it OPEN. This design requires it for mail readers.
5. **Quarantine vs append-only.** GCs are never purged, but raw quarantined mail holds stranger personal data that may need purging. Counsel decides whether quarantine is exempt. Default: a metadata entry in the GC; raw content purged after retention.
6. **"Built-in Bazaar mail client"** is undefined in the sources and may overlap generic IMAP/SMTP (Q1).
7. **EU AI Act Article 50 timing** (2 August 2026, or a possible December 2026 grace period) governs the footer. Needs counsel.
8. **Cloudflare Routing pricing and catch-all** are unverified; the "free" claim is secondary.

## Questions for the owner
1. What is "built-in Bazaar mail client" vs generic IMAP/SMTP? Default: same MailKit wire code; built-in means Bazaar-managed mailboxes with a client view.
2. Keep the built-in SMTP/IMAP server in v1, VPS-only? Default: yes, with no home-host support.
3. Cloudflare inbound: pull or tunnel? Default: pull; the Worker queues mail and the host polls over outbound HTTPS.
4. Are quarantined messages visible to agents? Default: no; agents see a count.
5. Who releases quarantine? Default: owner only; release keeps the taint.
6. Can the reader model release mail? Default: no; it can only add quarantine.
7. Keep scam-baiting in v1, off by default? Default: yes, owner-approved per reply. Research advises dropping it.
8. Rename "Nigerian Prince Protocol"? Default: a neutral name such as "Bait protocol"; the current name names a nationality.
9. Bait identity: fictional persona or owner's name? Default: fictional persona with its own mailbox.
10. Footer wording and placement? Default: states an AI agent sent it, with operator contact. Counsel reviews.
11. Raw quarantine retention? Default: 90 days, then purge; metadata stays in the GC.
12. Attachments: owner viewing only, or PDF and images to agents? Default: owner viewing only.
13. Per-agent caps? Default: 20 new recipients a day; 100 messages a day in total.
14. Sender API keys? Default: operator secret store (14), never agent VMs.
15. Sign-up verification codes to the agent? Default: yes, via the Identity Service at Tier 2.
16. Owner's address forwards into the owner's inbox GC? Default: yes, owner-only.
