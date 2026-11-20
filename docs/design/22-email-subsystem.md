# Email Subsystem

Status: RECONCILED with DECISIONS-LOG (2026-10-25), cross-doc pass done · Owner questions: 8
Depends on: 01-gc-data-model.md (inbox GC, `gc_message` kinds, type registry) · 02-wire-protocol-and-sync.md (authenticated HTTP surface for blobs) · 03-server-host-and-providers.md (OS providers, no-inbound default, settings registry) · 04-postgres-schema-and-migrations.md (conventions, migrations) · 05-model-gateway.md (reader-session model calls) · 14-identity-service-and-passport.md (`ISecretStore`, mailbox credentials, verification codes) · 15-security-taint-and-approvals.md (taint, tiers, reader sessions, hand-overs, audit, quota table) · 34-testing-strategy.md
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
- Configurability: plain values here are settings with shipped defaults (DECISIONS-LOG round 13).
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
2. Parse MIME with limits on size, depth and part count (`mail.max_message_bytes`, `mail.max_attachments`). Drop auto-submitted mail to prevent loops.
3. Record SPF, DKIM and DMARC results. A pass proves the sending domain, not intent.
4. Deterministic screens: auth failure, lookalike sender domain, executable or macro attachment, link text that differs from its target, oversize.
5. Reader session (15's reader primitive): tool-less, always hard-tainted, returns a `mail_triage` hand-over (schema registered in 15) with reason codes [PROPOSED]. It can escalate to quarantine but can never release.
6. Clean mail: the pipeline posts a system `gc_message` of kind `external_mail` with `source_trust = external` to the agent's inbox GC. It carries sender domain, time, screen codes and a `mail_message` reference, never the body. The accepted hand-over follows as a separate message. Actor sessions read only the approved hand-over in a fresh session, so the mail's raw content never reaches them (15 handover rule).
7. Quarantined mail: stored outside all agent context. The owner sees a summary in the client; agents see a count only.

### Outbound flow
1. The agent drafts through a mail tool. The server runs a tier check (15).
2. Tier 1 (15): recipient on the approved list and body built from a template; sent with an after-the-fact notice [PROPOSED mail mapping]. Tier 2: any new recipient, any text derived from inbound mail, any attachment, any sign-up reply. The owner approves each message; approvals never carry over. A session with hard taint always needs Tier 2 (C06).
3. The server appends the AI-agent footer (wording in `mail.footer.wording`) and operator contact. The footer cannot be disabled and the model cannot remove it [PROPOSED; counsel].
4. Send with an idempotency key; retry with backoff; bounces are ingested as untrusted input through the inbound pipeline.
5. Mail between two Bazaar addresses is refused. Internal traffic stays in GCs.

### Quarantine and reporting
- States: `held`, `released` (owner only), `purged` (after retention), `reported`.
- Release keeps the taint. A released message is still untrusted.
- Reporting to a provider's abuse channel needs per-report owner approval.
- Daily owner digest: counts and top sender domains. No message bodies.
- Raw quarantined content is personal data: encrypted per person (C01), purged after `mail.quarantine.raw_retention_days`; the metadata entry stays in the GC. Backup retention must match (33).

### Attachments and links
- No agent opens an attachment in v1. Executables, scripts, macro-enabled Office files and nested archives are held; other files go to the blob store for owner viewing [PROPOSED].
- Agent reading of other files is governed by `mail.attachments.agent_readable` (default off). Any agent read goes through a reader session, so hard taint holds [PROPOSED].
- HTML renders as text, remote images are stripped, and links show their real host and are never fetched from mail content.
- Verification codes are read only through the Identity Service at Tier 2 (14, 15).

### Scam-baiting protocol (optional)
- Off by default (`security.scam_mail.protocol_enabled`, false; owned by 15). The wizard states the risks in plain words: harm to real victims, harassment complaints, provoking criminals, malware, domain suspension, and legal exposure that varies by jurisdiction (needs counsel).
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

### DNS checker module (WP10) [PROPOSED, 2026-11-18]
Own module pair: `Bazaar.Server.MailDns` and `Bazaar.Server.MailDns.Abstractions` (namespace `Bazaar.Server.MailDns`). It references `Bazaar.Server.Mail.Abstractions` and `Bazaar.Server.Setup.Abstractions`, never `Bazaar.Server.csproj`.
- `IDnsResolver` (in the abstractions): `TxtAsync(name)`, `PtrAsync(ip)`, `AddressesAsync(name)`, `MxAsync(name)`. A resolver failure is a result (`Timeout`, `ServFail`, `NxDomain`), never an exception the checker lets escape. The shipped resolver sits behind this interface, so tests run on a stub resolver and CI needs no network.
- `IMailDnsChecker.CheckAsync(MailDnsRequest, ct)` returns a `MailDnsReport`: one `DnsCheckResult` per check (`Spf`, `Dkim`, `Dmarc`, `Ptr`, `BulkSender`), each `Pass`, `Warn`, `Fail` or `NotApplicable`, with a plain-words `Detail` and a `Fix` hint. The request carries the domain, the back end kind, the DKIM selector (optional), the sender's SPF include (optional), the DMARC report address (optional), whether the host is self-hosted, and the expected daily volume (optional). Nothing in the module names a domain, a provider or a selector (domain-agnostic rule).
- Rules (from the section above): SPF is exactly one `v=spf1` record, includes the sender only, and ends `-all` (`~all` is `Warn`, `+all` and `?all` are `Fail`, two records are `Fail`, more than 10 DNS lookups is `Fail`). DKIM is a TXT at `<selector>._domainkey.<domain>` with `v=DKIM1` and a non-empty `p=`; an empty `p=` (revoked key) is `Fail`; no selector given is `NotApplicable`. DMARC is a TXT at `_dmarc.<domain>` with `v=DMARC1`; `p=none` with a `rua=mailto:` is `Pass` for a new domain, `p=none` without `rua` is `Warn`, no record is `Fail`, and `quarantine` or `reject` is `Pass`. PTR applies only when self-hosted: forward-confirmed reverse DNS (the PTR name resolves back to the sending IP), else `Fail`; hosted back ends are `NotApplicable`. `BulkSender` is `Warn` when the expected daily volume is over 5,000 [RESEARCHED].
- The report is advice, not a gate: DNS may not have propagated, so a `Fail` never blocks setup. The setup step records the report in the audit row (counts only) and tells the operator what to fix. Nothing the checker reads is trusted as an instruction (DNS text is untrusted input; values are parsed, never shown as markup).
- Setup step: `MailDnsSetupStep` (an `ISetupStep`, doc 45 step 11) does nothing when `email.enabled` is false. When true it runs the checker for the plan's mail domain and reports the result as a message. Plan fields added to `email` (all optional, doc 45): `domain`, `backendKind` (`generic`, `cloudflare`, `builtin`), `dkimSelector`, `dmarcReportAddress`, `selfHosted`. An unknown field is still rejected by the loader.

### Client mail screens (WP12) [PROPOSED, 2026-11-18]
Own module pair: `Bazaar.Client.Mail` and `Bazaar.Client.Mail.Abstractions`. Panes follow the existing pane contract (doc 27): the server derives a JSON model, the view model only formats it, and a missing required field falls back to the generic view. Pane kinds are added to `PaneKinds` in `Bazaar.Contracts`: `bazaar.mail.quarantine`, `bazaar.mail.mailbox`, `bazaar.mail.approval`, `bazaar.mail.dns`.
- Quarantine: held messages as metadata only (reason codes, sender domain, auth result, received time, state `held`, `released`, `purged`, `reported`) and the daily digest counts. No message body ever appears; raw content is not in the model.
- Mailbox: the Bazaar-managed mailbox view (Q1 default): messages as plain text. HTML never renders, remote images are never loaded, and every link shows its real host and is never fetched or opened from the pane.
- Approval: the card for a hard-tainted outbound send (Tier 2): the server-set From, the recipients, the subject, the text body, the footer preview and the reason the send needs approval.
- DNS: the `MailDnsReport` as rows with a status chip and the fix hint.
- The panes are display-only. Release, report and send approvals go through the approval cards and the owner signature path of `Bazaar.Approvals` (docs 15 and 48); a pane never holds a release action. Owner-only release and the reader-cannot-release rule are enforced on the server and are not client settings.
- Server data feeds (WP4 to WP6) are not built yet. The pane models are the contract; the screens are tested with fixture models and `Avalonia.Headless`.

Built details (PROPOSED, from the first implementation, 2026-11-18):
- DNS: `MailDnsRequest` also carries an optional `SendingIp`; when absent the PTR check uses the addresses of the domain's MX hosts (up to 4), else the domain's own address, and warns if none is found. A `Timeout` or `ServFail` is `Warn` ("could not be checked"); `NxDomain` on a required record is `Fail`. SPF with no `all` term is `Fail`; the walk stops after 10 lookups, 10 levels and 16 TXT fetches; an include loop is `Fail`. DMARC: several records is `Fail`; `p=none` whose `rua` differs from the configured report address is `Warn`. Answers are capped at 32 records and 4096 characters per TXT, and DNS text shown in a message is reduced to letters, digits and `.-_:`. The shipped resolver uses the `DnsClient` package. `MailDnsSetupStep` has order 11 and puts counts and fixes in the step message only (the apply writes the audit row). PROPOSED: `SetupStepResult` gains an optional `AuditCounts` map of name to integer (counts only, never text); the applier writes it into the step's audit row, and the audit payload guard still rejects anything else.
- Panes: model keys are `digest {held, released, purged, reported, topSenderDomains[{domain,count}]}` and `items[{senderDomain, reasonCodes, authResult, receivedAt, state}]` (quarantine); `messages[{from, subject, receivedAt, text, links[url]}]` (mailbox); `from, recipients[], subject, body, footerPreview, reason` (approval); `domain?, rows[{kind, status, detail, fix?}]` (DNS). Enum values travel as names (`held`; `Spf`, `Pass`), so the server serializer must use string enums. A link shows its host and the full URL as plain text. PROPOSED: the approval card carries an `approvalId` that links it to the `Bazaar.Approvals` record, and the quarantine digest carries a `date` (ISO date, the day it covers). Heads compose `MailPanes.Modules` into their pane registry.

### Deterministic screens: lookalike sender and archive bombs (WP3) [PROPOSED, 2026-11-19]
Two more pure screens in `InboundQuarantineRules`, appended to `QuarantineReason` (`LookalikeSenderDomain`, `NestedArchive`) so existing values and sort order stay as they are. No network, no built-in domains or brands.
- Lookalike sender (`LookalikeDomainDetector`): the From domain is held when it is not a protected domain (nor a host under one) and either its confusable skeleton equals a protected domain's skeleton or it is one edit away (insertion, deletion, substitution, adjacent swap). The skeleton decodes punycode (IDN), applies Unicode compatibility folding, maps Cyrillic and Greek look-alikes and the digits 0 and 1 to ASCII, and folds `rn` to `m` and `vv` to `w`. The edit rule applies only when the protected name's main label has at least 4 characters. Protected set: the operator's own mail domains plus a caller-supplied list, given as `InboundQuarantineOptions.ProtectedDomains` (PROPOSED setting `mail.protected_domains`, default empty, which turns the screen off; the operator's own domains are added by the host from the account configuration). The address parser accepts ASCII domains only, so a Unicode From domain reaches the screen in its punycode form.
- Archives (`ZipArchiveInspector`, `ArchiveFacts` on `AttachmentInfo`): the inspector reads only the ZIP central directory, never decompresses and never reads more entries than the limit. An attachment archive is held (`NestedArchive`) when an entry name has an archive extension (nested archive), when the directory is malformed, truncated, ZIP64 or declares more entries than the limit, or when the declared sizes pass the limits. Nested archives stored under a plain name are not detected by this screen; other formats (7z, rar, tar) are not inspected and rely on the attachment-extension policy.
- PROPOSED settings, held once in `MailLimits` (`InboundQuarantineOptions.Limits` references it; no limit default is repeated): `mail.max_archive_entries` (default 100), `mail.max_archive_uncompressed_bytes` (default 100 MiB), `mail.max_archive_ratio` (declared uncompressed to compressed, default 100). Operators may tighten them, not raise them above the shipped floor of safety.
- Wiring (PROPOSED, 2026-11-19): `MailScreeningService` takes the `ParsedMail`, the message size, `MailAuthResults` and an `AttachmentBytesReader` (raw bytes by attachment index), runs `ZipArchiveInspector` over every attachment whose bytes are a ZIP file (whatever its name; never decompressing; attachments above `mail.max_message_bytes` are skipped and push the message size figure up so the oversize screen holds it), and returns the `QuarantineDecision`. The protected set comes from the port `IProtectedMailDomains` (operator domains plus `MailScreeningOptions.ExtraProtectedDomains`, setting `mail.protected_domains`); the Postgres reader reads every `mail_domain.domain` row whatever its state, and an in-memory fake exists for tests. `MailComponent` registers it through `AddBazaarMailScreening` (every registration is `TryAdd`, so a host can substitute). The display-name screen needs the From header with its display name: `ParsedMail.FromDisplayName` (PROPOSED, 2026-11-19, additive; `MailTextCleaner.CleanDisplayName` strips control and format characters including bidirectional overrides, collapses whitespace and caps the name at 200 characters) carries it, and `MailScreeningService` composes a quoted `"Name" <address>` From value so `DisplayNameMismatch` fires when the name shows another address.

### Quarantine store slice (WP5) [PROPOSED, 2026-11-18]
Not a new module: new files in `Bazaar.Server.Mail` (`Quarantine/`, namespace `Bazaar.Server.Mail.Quarantine`) and `Bazaar.Server.Mail.Abstractions` (`Quarantine/`), beside the inbound rules. `Bazaar.Server.Mail` gains a reference to `Bazaar.Erasure.Abstractions` for `ILegalHoldReader`.
- State machine (`QuarantineStateMachine`, pure): `held` to `released` (owner only), `held` to `reported` (owner plus a per-report `Bazaar.Approvals` approval id), `held` to `purged` (system retention job only, after the due time, never under legal hold). Everything else is illegal: `released`, `purged` and `reported` are terminal. A reader session can only add (admit) and is refused release and report; an agent session can do nothing; the system pipeline may admit. An owner actor without a human id is not an owner. Release keeps the taint (`QuarantineItem.Tainted` is always true). Signature and nonce checks stay in `Bazaar.Approvals`; this slice only requires the approval id.
- Port `IMailQuarantineStore` (admit, get, list, transition, count held) with `InMemoryMailQuarantineStore`. Items carry the sender domain only, never the full address or any body.
- Retention (`QuarantineRetention`, pure): due at `RetentionUntil` if the store set one, else received time plus `mail.quarantine.raw_retention_days` (default 90, needs counsel); the boundary is inclusive; days below 1 are rejected. Held items of a person under legal hold are never due (`ILegalHoldReader`, scope `person` only, as in doc 48 section 9). The job mechanism is not defined here: the retention job (on `Bazaar.Server.Jobs.Abstractions` once it lands) calls `DueForPurge`, then transitions each item as `System`.
- Digest (`QuarantineDigestBuilder`): `QuarantineDigest {date, held, released, purged, reported, topSenderDomains[{domain,count}]}`, matching the pane model keys; top domains rank held items by count then name. Agents get `QuarantineCountView {held}` and nothing else.
- Postgres store (PROPOSED, 2026-11-18): the doc gaps are closed by `mail/0022_mail_quarantine.sql`: additive nullable `mail_message` columns `person_id`, `sender_domain` (lower-case, no `@`, checked), `released_by`, `released_at`, `report_approval_id` and `purged_at`, plus a partial index for the owner's list; 0021 is unchanged and existing rows stay valid (a row is a quarantine item only once admitted, that is, `person_id` is set and the state is held, released, purged or reported). `PostgresMailQuarantineStore` (raw Npgsql, in `Bazaar.Server.Mail/Quarantine/`) applies `QuarantineStateMachine` under a row lock. It never selects `body_*`, `from_addr` or `blob_ref`. Admit updates the row the inbound pipeline already stored (state `received`); an unknown id is `NotFound`, and the sender is cut to its domain. Release records `released_by` and `released_at`, report records the approval id, and purge sets `purged_at` and clears the encrypted body columns (the blob behind `blob_ref` is the retention job's to delete). `GetDigestAsync` builds the digest from two aggregate queries. Host wiring (registering the store with the host's `NpgsqlDataSource`) is not done yet. Open: whether `released` or `reported` items also purge after retention (raw content stays personal data); whether an owner may purge early.

### Outbound policy module (WP6 slice) [PROPOSED, 2026-11-18]
Own module pair: `Bazaar.Server.MailOutbound` and `Bazaar.Server.MailOutbound.Abstractions` (namespace `Bazaar.Server.MailOutbound`). It decides; it never sends, queues or builds MIME (`OutboundMessageBuilder` in `Bazaar.Server.MailBackends` stays the only MIME builder). It references `Bazaar.Server.Mail.Abstractions` (`MailboxRef`) and `Bazaar.RateLimits.Abstractions`, never `Bazaar.Server.csproj`. It adds no gate and no counter: counters and quotas are the shared `IRateLimiter` (doc 15, C36), and an approval is the caller's `Bazaar.Approvals` request raised on `NeedsOwnerApproval`.
- `IOutboundMailPolicy.EvaluateAsync(OutboundRequest, ct)` returns an `OutboundDecision`: `Send` (Tier 1, GC notice after), `NeedsOwnerApproval` (Tier 2), `Refuse` or `Pause`, each with a reason code, the derived idempotency key and the effective footer. `Pause` carries the limiter's `RetryAfter`; the caller pauses the mailbox and notifies the owner.
- Rules, in order: no recipient or a malformed address is refused; a message whose every recipient is a Bazaar-managed address is refused (`IBazaarAddressDirectory`); a message with both Bazaar and external recipients is not refused and needs the owner (`MixedBazaarRecipients`, coordinator decision 2026-11-18); more attachments than `mail.max_attachments`, or more bytes than `mail.max_message_bytes` capped by the back end hard cap, is refused; the daily total is the shared `MailSentPerDay` policy at install scope (one bucket for the install, coordinator decision 2026-11-18) with the limit from `security.quota.mail_sent_per_day`, plus a per-agent sub-limit (`mail_sent_per_agent_day`, PROPOSED setting `mail.sent_per_agent_day`, default 20), both enforced with two limiter calls, the agent bucket first so a denial leaks no slot in the shared bucket (an install-total denial can cost that agent one slot); the new-recipient cap is a policy named `mail_new_recipients_per_agent_day` with the limit from `mail.new_recipients_per_agent_day`, one slot per new recipient; a denial pauses. Tier 2 when the session is hard-tainted (always, first), recipients mix Bazaar and external addresses, a recipient is not on the approved list (`IApprovedRecipients`), text derives from inbound mail, there is an attachment, the message is a sign-up reply, or the body is not from a template. Otherwise Tier 1.
- Floors: the settings port is read only. A blank `mail.footer.wording` falls back to the shipped footer, never to none. PROPOSED setting `mail.footer.operator_contact` (blank by default): when set it adds one `Operator contact:` line after the footer; it never replaces the AI-agent statement and cannot disable the footer. Hard taint is not a setting. A negative limit means the shipped default; 0 is the tightest value and pauses. Settings can tighten and never loosen below these floors; raising the new-recipient default needs owner approval where the setting is written, not here.
- Idempotency: `OutboundIdempotency.Derive` is a SHA-256 over agent, mailbox, sorted de-duplicated lower-case recipients, subject, body and a caller nonce, length-prefixed. The same inputs give the same key; `mail_outbound` (via the outbound pipeline) makes a repeated key send once, also after a restart.
- Ports defined here: `IBazaarAddressDirectory`, `IApprovedRecipients`, `IOutboundMailSettings`. `MailTaint` mirrors Clean, Soft and Hard until `Bazaar.Server.Security.Abstractions` (PR 743) lands, then it is replaced by `SessionTaint`. `AddBazaarMailOutbound` registers the policy; the host supplies the ports and the limiter.
- Open (PROPOSED): (1) `IRateLimiter.TryAcquire` consumes a slot on evaluation, so the caller must evaluate once per send and not re-evaluate after the owner approves. (2) The kill switch, token budgets and `ApprovalRequest` live in PR 743 and are not in this base; wiring `Pause` to the kill switch and the approval request to the Approvals store is the caller's job. (3) Rate-limit setting keys (`mail.new_recipients_per_agent_day`, `mail.sent_per_agent_day`) are not yet registered (same TODO as the shared rows). (4) Bazaar recipients of a mixed message do not count as new recipients.

### Outbound pipeline (WP6) [PROPOSED, 2026-11-19]
Same module pair as the policy (`Bazaar.Server.MailOutbound` and `.Abstractions`); no new module. It is the caller of `IOutboundMailPolicy` and adds no approval, rate limit, kill switch or audit mechanism: each is a port onto the existing module. Overlap check done against doc 15 (approvals, audit, kill switch), `Bazaar.RateLimits`, `Bazaar.Approvals`, `Bazaar.Server.AgentSafety` and PR 743 (`Security.Abstractions`): none of them holds mail sending, so nothing is duplicated.
- `IOutboundMailPipeline`: `SendAsync(OutboundRequest)`, `ResumeAsync(key)`, `ResumeDueAsync(max)` and `RecordBounceAsync(key)` (the hook the inbound pipeline calls when it ingests a bounce; ingestion itself stays in the inbound pipeline).
- Flow: derive the key; a key already stored is advanced and never re-evaluated (evaluation consumes a quota slot, and one message gets one approval). Otherwise evaluate the policy once. `Refuse` returns the reason and stores nothing. `Pause` returns `RetryAfter` and stores nothing. `NeedsOwnerApproval` stores the send as `awaiting_approval`, raises one approval through `IMailApprovalGateway` (an adapter onto the Bazaar.Approvals service, host wired), and a resume sends on Approved, ends as `rejected` on Denied and `expired` on Expired; an approval is bound to its message's key and is consumed once, so it never carries to a second message. `Send` stores the row and delivers.
- Storage order: one transaction inserts the `mail_message` row (state `sending`, body encrypted through `IMailBodyProtector`) and the `mail_outbound` row (`ON CONFLICT DO NOTHING`, so two racing callers create one). Every later change is a compare-and-set on status and attempts, so two resumers cannot both claim a send. A crash after the insert leaves a `sending` row that a resume delivers once. Delivery is at least once toward the back end and exactly once delivered, because the back ends already send an Idempotency-Key header or a deterministic Message-ID; a crash after the back end accepted a message and before the receipt resends under the same key after the stale window.
- Retries: exponential backoff from `mail.outbound.retry_base_seconds` (default 30), doubled per failed attempt, capped by `mail.outbound.retry_max_seconds` (default 3600), at most `mail.outbound.max_attempts` attempts (default 5, kept between 1 and 10); then `failed` and the stored text is cleared. A missing back end counts as a failed attempt. A `sending` row is treated as interrupted only after a stale window (default 5 minutes), so an in-flight send is not raced. These three setting keys are PROPOSED and not yet registered.
- Footer: the policy's resolved footer is stored with the send and passed on `OutboundMail.Footer`; the back end appends it through `OutboundMessageBuilder` in place of its own default. A blank footer falls back to the back end default, never to none. The model supplies neither.
- Audit (`IAuditLog`, actor `mail.outbound`, kinds `mail.outbound.<state>`: refused, paused, queued_for_approval, approval_requested, approved, sending, sent, retry_scheduled, failed, rejected, expired, bounced): payload is agent id, key, message id, approval id, reason code, attempt and recipient and attachment counts. No address, subject or body.
- Schema: `mail/0023_mail_outbound_pipeline.sql`, additive. `mail_message.state` gains `sending`; `mail_outbound.status` gains `awaiting_approval`, `rejected` and `expired`; `mail_outbound` gains nullable `tier`, `reason` and `footer` (the policy's answer, no personal data); partial index for the resumer. The recipients, subject and text are one JSON envelope encrypted into the message row's `body_*` columns, never in clear; a send that ends `failed`, `rejected` or `expired` clears them, and `sent` rows keep them. `PostgresOutboundMailStore` is raw Npgsql; `InMemoryOutboundMailStore` is the fake.
- Not built, open (PROPOSED): (1) the host adapters for `IMailApprovalGateway` (onto `ApprovalService`, which moves into `Security.Abstractions` with PR 743), `IMailBackendResolver` and `IMailBodyProtector` (the per-person key, doc 04) and their registration; (2) a hosted timer that calls `ResumeDueAsync`; (3) wiring the kill switch: an engaged kill switch should stop `SendAsync` and resumes, which needs the PR 743 `IKillSwitch` port; (4) the Tier 1 after-the-fact GC notice and the mailbox pause on `Pause` are the caller's (the mail tool's) job; (5) the approval gateway needs an expiry that matches `security.approval.expiry_hours`, and a rejected approval is not retried by the pipeline (the agent must send a new message with a new nonce).
### Inbound pipeline module (WP4 slice) [PROPOSED, 2026-11-19]
Own module pair: `Bazaar.Server.MailInbound` and `Bazaar.Server.MailInbound.Abstractions` (namespace `Bazaar.Server.MailInbound`). It wires inbound flow steps 1 to 7 from parts that already exist: `IMailBackend.ReceiveAsync`, `MimeMailParser`, `MailScreeningService` and `IMailQuarantineStore`. It adds no GC, taint, reader or approval mechanism; it reaches them through thin ports (see Ports) until PR 743 (Security.Abstractions) and a GC writer contract in an abstractions project land.
- Two stages per mailbox per pass (`IInboundMailPipeline.ProcessMailboxAsync`). Stage A stores every pulled message: parse with limits (a limit break or unparseable MIME is held with the code `Limit_<name>` or `MalformedMessage`; a missing From is held as `MissingSender`), screen, seal the raw message under the person's key, insert a `mail_message` row in state `received`, and admit a held message to quarantine as `System` (then post the count notice). Stage B finishes stored clean messages oldest first, at most `mail.inbound.max_batch` per pass (PROPOSED, default 50): announce, reader triage, then deliver or escalate. Storing is never bounded because IMAP marks mail seen when it is pulled and `IMailBackend` has no acknowledge call; the stored row, not a redelivery, is what makes a crashed or deferred message come back.
- Dedupe: `(mailbox, rfc_message_id)` or `(mailbox, backend_id)`, the first enforced by the 0021 unique index (insert is `ON CONFLICT DO NOTHING`, then a re-read, so two workers cannot store one message twice). A duplicate adds nothing and is counted. Order: oldest receive time first, then storing order. Every step is idempotent (GC posts are keyed on the mail message id), so a crash between steps resumes cleanly. The first stage-B failure ends the mailbox's pass so a later message never overtakes an earlier one.
- Clean message: a system `external_mail` entry with `source_trust = external` (`ExternalMailEntry`: inbox GC, mailbox, mail message reference, sender domain, time, screen codes; the type has no field for subject, display name, address or body). Then a reader session is started, hard taint is set through the taint port before the reader sees anything, and the reader returns a `mail_triage` hand-over (`MailTriageHandover`: verdict `Routine`, `NeedsAttention` or `Escalate`, plus codes from the closed set `MailTriageReasonCodes`, at most 5, no free text). An invalid hand-over (unknown code, repeat, wrong message id) fails closed into quarantine. `Escalate` admits the message as `QuarantineActor.Reader`; the verdict set has no release, and the store refuses reader release anyway. The accepted hand-over is posted as its own message, then the row moves to `delivered`.
- Held message: `AdmitAsync` as `System` (screen) or `Reader` (escalation). The only GC post is `QuarantineNotice`: mail reference, closed reason codes and the held count. It names no sender, because a sender domain is attacker-controlled text. A reader escalation comes after the clean announcement, so that one GC entry (metadata only) precedes its notice.
- Storage: `IMailMessageStore`. `PostgresMailMessageStore` (raw Npgsql, on the existing 0021 and 0022 columns, no new migration) writes `sender_domain`, `auth_json.summary`, `screen_codes` and the sealed body (`body_key_id`, `body_ct`, `body_nonce`); it never writes `from_addr`. Only `ReadSealedBodyAsync` and the pending list select body columns, both encrypted. `MailBodyAccessPolicy`: a reader or the owner may read a body; an agent or the system gets `Denied`. `IMailBodySealer` (Seal, Open) is the host's key service port. `InMemoryMailMessageStore` is the fake.
- Failures are results: a back end outage, a directory outage and a step failure come back as `InboundFailure(code, ref)` with an exception type name and a hash, never an exception message, address or body. `IInboundMailEvents` receives only ids and codes. `IInboundMailWorker.RunPassAsync` loops the mailboxes; a scheduler calls it, and no background service is registered here. `AddBazaarMailInbound` is `TryAdd` throughout.
- Ports defined here (replace when the owners land): `IInboxGcPoster` (for the GC writer, doc 01), `IMailReaderSessions`, `IReaderSessionTaint`, `IMailTriageReader` (for doc 15 and the gateway, doc 05), `IMailBodySealer`, `IInboundMailboxDirectory`.
- Not built: link extraction for the link-text screen (the screen exists; the pipeline passes no links), the `mail_triage` schema registration in doc 15, host wiring, attachment storage in `mail_blob`.
- Open (PROPOSED): (1) add an acknowledge call to `IMailBackend` so a back end marks mail seen only after the row is stored; today a failure inside stage A (for example the database down mid-pass) can lose the pulled messages not yet stored. (2) A sender who reuses a Message-ID makes a later message a duplicate and it is not processed (counted, never silent); a content hash column would tell a redelivery from a collision. (3) A reader escalation leaves the `external_mail` entry in the GC, with the notice after it.
### Mail runtime (host wiring) [PROPOSED, 2026-11-20]
Module pair `Bazaar.Server.MailRuntime` and `Bazaar.Server.MailRuntime.Abstractions`: the host-side wiring for the inbound (WP4) and outbound (WP6) pipelines. It adds adapters and two schedulers and no gate, counter, key service, GC writer or approval mechanism. `AddBazaarMailRuntime` also calls `AddBazaarMailInbound` and `AddBazaarMailOutbound`; every registration is `TryAdd`, so a host registers its own port first and the default steps aside.
- Real adapters: `IInboxGcPoster` (`InboxGcPoster`, over `IMailGcAppender`, a one-method port with the `Bazaar.GC.Abstractions` types that the host forwards to `IGcLogWriter.AppendAsync`; the writer interface itself lives in `Bazaar.Server`). `external_mail` is written by the mail bridge (`external_bridge`, fixed author id), `source_trust = external`, `TaintedAtWrite = true`, payload of ids, sender domain, time and screen codes. The quarantine notice is a `system` event and the triage hand-over a `handover` event with `schema = mail_triage`, because `gc_event_kind` has no `mail_quarantine_notice` row and this change adds no migration. Client message ids derive from the mail message id, so a repeat returns the first event. An invalid hand-over is refused. `IBazaarAddressDirectory` (`PostgresBazaarAddressDirectory`, open mailboxes of non-retired domains, case-insensitive). `IMailBackendResolver` (`MailBackendCatalog`): one back end per active `mail_domain` row, built from a JSON configuration read from the secret store under `config_ref` (`imap_smtp`: hosts, ports, security; `cloudflare`: an https `apiBase`); `bazaar_client` and `builtin_server` have no back end here. `Resolve` is synchronous, so it reads a snapshot that the outbound loop refreshes before each pass; a bad domain is skipped and reported by error kind only. Logins come from `SecretStoreMailCredentialSource` (secret `mail.login.<local>.<domain>`, installation scope, host caller `mail-runtime`, read at call time, never kept). `IOutboundMailSettings` defaults to the shipped values (`DefaultOutboundMailSettings`); a host with a live settings service registers its own. `IOutboundMailStore` is the Postgres store over `IMailBodyProtector`.
- Schedulers: `InboundMailHostedService` calls `IInboundMailWorker.RunPassAsync` and `OutboundMailResumeHostedService` calls `IOutboundMailPipeline.ResumeDueAsync`, both `BackgroundService`. Settings (PROPOSED, not yet registered): `mail.runtime.enabled` (default true), `mail.runtime.inbound_interval_seconds` (default 60, floor 15), `mail.runtime.outbound_interval_seconds` (default 30, floor 10), `mail.runtime.outbound_resume_batch` (default 50, kept between 1 and 500); both intervals are capped at 3600. Settings tighten and never go under a floor. A failed pass is logged by error kind and the loop continues; only host shutdown ends it. Logs carry counts and error kinds, never an address, subject or body. The options are read every iteration, so a change applies without a restart.
- Fail-closed defaults (marked `IFailClosedMailPort`), because the backing module does not exist: `IMailApprovalGateway` (request throws, status stays Pending, consume is false), `IMailBodyProtector` and `IMailBodySealer` (throw: no clear text is stored), `IMailReaderSessions`, `IReaderSessionTaint`, `IMailTriageReader` (throw: no mail text reaches a model), `IInboundMailboxDirectory` (empty), `IApprovedRecipients` (nobody approved, so every send is Tier 2). A loop whose required ports are still fail-closed does nothing and says so once in the log, because a pull marks mail seen at the provider and would lose it with no safe place to store it.
- Open (PROPOSED): (1) `IMailApprovalGateway` needs an adapter over `ApprovalService`, which lives in `Bazaar.Server` (it needs a `DispatchContext` and a session), so no module can wrap it until approvals move to an abstractions project; the doc 40 claim that `Bazaar.Approvals.Abstractions` backs it does not hold, that project holds only the owner-signature contract. (2) Sealed storage for `IMailBodyProtector` and `IMailBodySealer` (doc 04 per-person keys), reader sessions, the reader model call and the taint port need their modules. (3) `IInboundMailboxDirectory` needs a source that maps a mailbox to its person and inbox GC; `mail_mailbox` has only `soul_id`. (4) `IApprovedRecipients` needs the owner's approved-recipient list (no table or abstraction exists). (5) Register the kinds `mail_quarantine_notice` and `mail_triage` in `gc_event_kind` (a GC-owned migration) so the notice and hand-over can leave `system` and `handover`. (6) The runtime settings keys and the `mail.*` outbound keys need registering with the settings service. (7) The kill switch should stop both loops (needs the PR 743 `IKillSwitch` port). (8) Host composition line: `services.AddSingleton<IMailGcAppender>(sp => new DelegateMailGcAppender((r, ct) => sp.GetRequiredService<IGcLogWriter>().AppendAsync(r, ct))); services.AddBazaarMailRuntime();` after `AddBazaarMailScreening`, the quarantine store, `ISecretStore`, `IRateLimiter`, `IAuditLog` and the `NpgsqlDataSource` are registered.


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

Inbound GC message: `gc_message` kind `external_mail` (registered in 01's GC type registry), `source_trust = external` (15's provenance enum, C20), `mailbox_id`, `mail_message` reference. The `gc_event`/`gc_message`/`gc_message_version` model in 01 and 04 applies (C09).

### Durable outbound store (option (a) built, PROPOSED, 2026-11-19)
`SendLedger` (MailBackends) stays the in-memory fast path inside a back end. The durable copy is `mail_outbound`, whose `message_id` is NOT NULL with a foreign key to `mail_message`. Option (a) is built: `OutboundMail` gains optional `MessageId` and `Footer` (additive; the existing constructor calls compile), and the send path inserts the `mail_message` row in state `sending` before the SMTP or HTTPS call, then records the receipt on `mail_outbound`. Option (b), a nullable `message_id`, loses the link to the message and was rejected. Details are in "Outbound pipeline (WP6)" below.

### Settings
Registry rows (03 owns the registry; 27 and 32 surface them in the setup wizard and advanced settings). Changes are audited (15 `audit_log`).

| Key | Type | Default | Scope | Who may change | Change needs approval? |
|---|---|---|---|---|---|
| `mail.max_message_bytes` | int | 10485760 (10 MiB) | install | operator | No; cannot exceed the back end's hard cap (25 MiB on Cloudflare, verified) |
| `mail.max_attachments` | int | 5 | install | operator | No |
| `mail.new_recipients_per_agent_day` | int | 20 | agent | operator | Raising it needs owner approval [PROPOSED]; a breach pauses the mailbox |
| `mail.quarantine.raw_retention_days` | int | 90 | install | operator | Needs counsel; backup retention must match (33, C01) |
| `mail.footer.wording` | string | states an AI agent sent the mail, with operator contact | install | operator | Needs counsel; the footer itself cannot be disabled |
| `mail.owner_forward_to_inbox` | bool | true | install | owner | No |
| `mail.attachments.agent_readable` | bool | false | agent | operator | Yes [PROPOSED]; any agent read goes through a reader session (hard taint) |
| `security.scam_mail.protocol_enabled` | bool | false | install | per 15 | Owned by 15 (referenced here, not redefined); needs counsel |
| `security.quota.mail_sent_per_day` | int | 20 | install | per 15 | Owned by 15 (referenced here, not redefined); see Conflicts 9 |

## Behaviour and edge cases
- Limits: `mail.max_message_bytes` and `mail.max_attachments` [PROPOSED]. Cloudflare's 25 MiB inbound cap is the hard ceiling (verified).
- Duplicate delivery is normal; dedupe on `rfc_message_id` plus backend id. Order is per mailbox by receive time only.
- Outage: inbound waits at the Worker or back end; outbound queues with backoff. No silent drop.
- Quarantined and held content never enters a prompt, memory or the Vault. Hand-overs are the only path from a reader to actors.
- Logs redact addresses and bodies; credentials never appear in logs.
- Rate caps: the per-agent new-recipient limit is `mail.new_recipients_per_agent_day`; the daily total sent is 15's `security.quota.mail_sent_per_day`. A breach pauses the mailbox and notifies the owner. The shared buckets sit in 15's quota table.
- Cloudflare allows 200 routing rules per domain (verified), so one domain holds about 200 mailboxes. Resend Free caps the whole operator at 100 messages a day; Pro is $20 a month (pricing dated 2026-10-24, secondary). Provider caps bound any setting.

## Testing
- **Fake mail servers:** in-process SMTP and IMAP listeners on loopback; MailKit runs against them unchanged. A stub `HttpMessageHandler` stands in for the HTTPS sender. A signed Worker-style payload fixture covers bad-signature, replay and valid cases.
- **Corpus:** synthetic mail only (auth failure, lookalike domain, zip bomb, nested multipart, remote image, injected instructions, malformed MIME, oversize, bounce and auto-responder loops).
- **Key cases:** quarantined mail never reaches agent context; an actor session cannot read a raw `mail_message` body; the reader session cannot release; a clean message sets hard taint on its reader session; a hard-tainted send needs Tier 2 approval; a repeated idempotency key sends once; the bait protocol needs acknowledgement and cannot use an agent address; the DNS checker runs against a stub resolver; a settings value cannot loosen a floor (footer, hard taint, reader-only release).
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
10. DNS checker and setup wizard. Needs 1. Module `Bazaar.Server.MailDns` (PROPOSED above).
11. Scam-baiting protocol, last and off by default. Needs 6 and 4.
12. Client screens (27 to 29). Needs 4 to 6. Module `Bazaar.Client.Mail` (PROPOSED above; fixture models until 4 to 6 exist).

Order: 1; then 2, 3 and 10 in parallel; then 4; then 5 and 6; then 7 to 9; then 11.

## Conflicts
1. **Built-in SMTP/IMAP server vs the no-inbound default.** It is a DECIDED v1 back end, but it must accept inbound on port 25, and port 25, PTR and reputation rule out a home connection. C30 sets no inbound by default. Workable only on a VPS, which needs an explicit owner decision (Q2).
2. **Scam-baiting.** The owner decided it is optional. Research (email note KQ6; threat model section 5) recommends dropping autonomous baiting entirely. This design follows the owner: optional, owner-approved per reply. Counsel and research disagree.
3. **Quarantine vs erasure.** Raw quarantined mail holds stranger personal data. C01 gives per-person encryption and key destruction, but legal sufficiency is unknown (Server.md Q17, needs counsel). Counsel decides whether quarantine is exempt. Default: metadata entry in the GC; raw content purged after `mail.quarantine.raw_retention_days`.
4. **Reader/actor split for mail.** C18 is an owner statement with Claude's reading attached ("reader sessions for all external input in v1"). This design follows it; the reading still needs the owner's confirmation (15 Q1).
5. **"Built-in Bazaar mail client"** is undefined in the sources and may overlap generic IMAP/SMTP (Q1).
6. **EU AI Act Article 50 timing** (2 August 2026, or a possible December 2026 grace period) governs the footer. Needs counsel.
7. **Cloudflare Routing pricing and catch-all** are unverified; the "free" claim is secondary.
8. **For other docs:** 04 must drop any `mail_*` outline and use this doc's tables (C16). 02 must define the blob HTTP surface that `mail_blob` uses (C15). 15 must register the `mail_triage` hand-over schema.
9. **Daily send total.** The earlier draft proposed 100 messages a day in total. The shared key `security.quota.mail_sent_per_day` (15, shipped default 20) now governs the daily total, so this doc no longer sets its own total. The two figures disagree; 15 holds the value, and the owner should confirm it there. Provider caps (Resend Free, 100 a day) still bound any setting.

## Questions for the owner
1. What is "built-in Bazaar mail client" vs generic IMAP/SMTP? Default: same MailKit wire code; built-in means Bazaar-managed mailboxes with a client view.
2. Keep the built-in SMTP/IMAP server in v1, VPS-only? Default: yes, with no home-host support and an explicit exception to the no-inbound default. (Changes the C30 ingress default, so it stays a question.)
3. Are quarantined messages visible to agents? Default: no; agents see a count. (Loosening it would breach the quarantine invariant.)
4. Who releases quarantine? Default: owner only; release keeps the taint.
5. Can the reader model release mail? Default: no; it can only add quarantine.
6. Rename "Nigerian Prince Protocol"? Closed (owner, 2026-11-16): keep the name "Nigerian Prince Protocol". The proposal to rename it is dropped.
7. Bait identity: fictional persona or owner's name? Default: fictional persona with its own mailbox. (Using the owner's name would break this doc's rule against real names.)
8. Sign-up verification codes to the agent? Default: yes, via the Identity Service at Tier 2. (Touches secret handling.)

MOVED TO SETTINGS: mail.max_message_bytes, mail.max_attachments, mail.new_recipients_per_agent_day, mail.quarantine.raw_retention_days, mail.footer.wording, mail.owner_forward_to_inbox, mail.attachments.agent_readable, security.scam_mail.protocol_enabled, security.quota.mail_sent_per_day (DECISIONS-LOG round 13)

ANSWERED: Cloudflare inbound is pull; the Worker queues and the host polls over outbound HTTPS, Worker POST optional later (DECISIONS-LOG round 8, C30).
ANSWERED: Sender API keys sit in the Identity Service behind `ISecretStore`, never in agent VMs (DECISIONS-LOG round 2, C05).
