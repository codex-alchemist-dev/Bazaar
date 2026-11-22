# 77. Status incidents and support (public status page publishing, ticketing)

Status: PROPOSED. Design intent only; nothing here is built. The owner's direction of 2026-11-19 to adopt Resibo's status page and support ticketing features into Bazaar, as separate modules where they conflict with Bazaar's design, is DECIDED (owner, 2026-11-19, relayed by the coordinating session). The module names, contracts, tables, settings and capability names below are PROPOSED. Resibo material is design input, not code to import. Owner questions: section 14.
Depends on: 02-wire-protocol-and-sync.md (HTTP surface, `/ws` stream registry, `/status` format), 03-server-host-and-providers.md (status listener, bind floor, settings registry), 04-postgres-schema-and-migrations.md, 14-identity-service-and-passport.md (`ISecretStore`, signing key custody), 15-security-taint-and-approvals.md (approval tiers, taint, `audit_log`), 22-email-subsystem.md (external-only email), 23-observatory-and-usage.md (status page contents, telemetry allow-list), 32-remote-access-auth-and-privacy.md (roles, passkey sign-in), 39-conduit-remote-connectivity.md (scopes, offers), 40-core-feature-modules-audit.md (module layout), 43-telemetry-and-crash-reporting.md (crash reports), 45-onboarding.md (Michi tours), 46-kodo-health-module.md (incident mode, health contract, section 12.4 public page), 48-gap-decisions-security-identity.md (custom roles, audit, erasure), 51-web-admin-module.md (admin panel ports), `BAZAAR-COMPONENTS.md` (sections 4.2 Principal, 5 migrations, 6 boundaries).
Used by: 46 (Kodo public page, section 12.4, superseded in part, see section 12), 72 (access-grant reasons, sibling doc in the same change set; its side is OPEN here), 51 (optional admin sections), 45 (two new tours), 28, 29, 30 (client panes).

## 1. Purpose

Two small features from Resibo, each as its own module pair, plus their client panes.

1. **Status incidents** (`Bazaar.Server.StatusIncidents`). An optional, public, unauthenticated status page that stays reachable when the company server is down. It shows coarse component states, a rolling uptime figure and incidents written by people. It is published as a static snapshot to one or more sinks (GitHub Pages, any static host), so the page does not depend on the server, its database or its network path. It never shows content or names (section 5). It is the incident-publishing layer. Kodo detects and journals; this module publishes, after a human or a configured policy approves (section 6).
2. **Support** (`Bazaar.Server.Support`). A simple ticket system: open, in progress, resolved, closed. A human user who hits a problem opens a ticket; staff answer it. A ticket links to the thing it is about (a PR, a GC, a crash report, a Kodo incident). A ticket id can be cited as the reason of an access grant (doc 72).

Both are optional modules on Bazaar.Core. Neither adds code to `Bazaar.Server` or `Bazaar.Client.Core`.

## 2. Decided inputs

- Adopt Resibo's status page and support ticketing as Bazaar modules; where they conflict with Bazaar's design, make a separate module instead of choosing (owner, 2026-11-19, DECIDED).
- Bazaar's browser rule: no web client; a read-only server status page and a narrow interactive sign-in page are allowed (CLAUDE.md; doc 02, doc 23, doc 32 section 4). The admin panel of doc 51 is a separate, owner-decided exception. This doc adds no interactive public page.
- Every feature is its own module `Bazaar.Server.<Name>` plus `.Abstractions`, with `Bazaar.Client.<Name>` when it has a client side (doc 40 section 4; owner's module sides rule, 2026-11-17). Providers are plain modules on Core with no selector.
- Shared libraries and modules hold contracts and logic only, with no third-party dependencies. Anything that talks to a third party is a provider module (owner, 2026-11-19).
- Actor attribution is `Principal` (kind `agent`, `user`, `service` or `system`; text form `kind:id`), BAZAAR-COMPONENTS section 4.2 (owner, 2026-11-19).
- Configurability rule (DECISIONS-LOG round 13): open values are operator settings with safe defaults, changes audited. DECIDED items and security invariants are not settings. Legal or privacy exposure settings are operator choices among documented options with the most protective default.
- The `/status` contents belong to doc 23 (C40, DECISIONS-LOG round 12). That endpoint stays as is (section 12).
- Kodo's incident mode is internal: freeze, evidence, guided recovery. Exit is human only, with step-up (doc 46 section 16.3).
- Email is external-only (CLAUDE.md). GCs are the coordination system; events and software reactions serve GCs and never replace them.
- Public posting approval tiers are operator-configured with shipped defaults (CLAUDE.md; doc 15, doc 36).

## 3. What this document does not decide

- Kodo's detection, scoring, journal and incident-mode mechanics (doc 46). Section 6 only names the port this module reads.
- The roles model and capability table (doc 48 section 4, doc 32 section 6). Capability names below are PROPOSED and must be registered there.
- Doc 72's access-grant mechanics. Section 9.4 states what the Support module offers it, and what it needs back.
- The sign-in floors raised by public exposure (section 10). This doc states the Conduit mapping and the contract; the floor values belong to doc 32.
- Sink hosting choices of an operator, DNS, TLS for the sink host, and whether to use a custom domain.
- Legal text on the page (terms, privacy notice). The page carries an operator-supplied footer string. Counsel review is needed before any public launch.
- Erasure of ticket content (doc 48 section 9 mechanics are OPEN).

## 4. Module layout

```
Bazaar.Server.StatusIncidents.Abstractions   contracts: incidents, components, snapshot, sink, linter, ports
Bazaar.Server.StatusIncidents                logic: aggregator, incident service, snapshot renderer, signer seam, linter,
                                             local cache, SQL migrations (namespace status-incidents), no third-party deps
Bazaar.Server.StatusIncidents.KodoBridge     adapter: Kodo state and incidents -> signals and drafts (references Kodo.Abstractions)
Bazaar.Server.StatusIncidents.Sink.FileSystem   provider: writes the snapshot to a directory (any static host mounts it)
Bazaar.Server.StatusIncidents.Sink.GitHubPages  provider: commits to a dedicated status repository as a GitHub App (doc 21)
Bazaar.Server.StatusIncidents.Sink.S3        provider: S3-compatible object store (R2, MinIO, S3)
Bazaar.Server.StatusIncidents.Subscribers    optional: operator-managed email list for incident updates (section 4.3)
Bazaar.Client.StatusIncidents                client pane: draft, preview, publish, maintenance

Bazaar.Server.Support.Abstractions           contracts: tickets, messages, links, resolver and notifier ports
Bazaar.Server.Support                        logic: lifecycle, assignment, links, auto-close, SQL (namespace support), no third-party deps
Bazaar.Server.Support.GcBridge               optional adapter: mirrors ticket events into a chosen GC as `system` events
Bazaar.Server.Support.Sla                    optional, not in first waves: priorities and response targets (section 9.9)
Bazaar.Client.Support                        client pane: report a problem, my tickets, staff queue, thread
```

| Component id | Project | Kind | `DependsOn` | `SoftDependsOn` |
|---|---|---|---|---|
| `status-incidents` | `Bazaar.Server.StatusIncidents` | first-party module | `audit`, `identity` | `kodo`, `web-admin` |
| `status-incidents-kodo-bridge` | `...KodoBridge` | adapter | `status-incidents`, `kodo` | none |
| `status-sink-filesystem`, `status-sink-github-pages`, `status-sink-s3` | `...Sink.*` | provider | `status-incidents` | `github` (Pages sink) |
| `status-incidents-subscribers` | `...Subscribers` | optional | `status-incidents` | `email` |
| `support` | `Bazaar.Server.Support` | first-party module | `audit`, `identity` | `gc`, `blobs`, `kodo`, `crash-reports`, `status-incidents`, `erasure` |
| `support-gc-bridge` | `...GcBridge` | adapter | `support`, `gc` | none |
| `support-sla` | `...Sla` | optional | `support` | none |

Every implementation project references only `Bazaar.Contracts`, `Bazaar.Sdk`, the kernel contract project, its own Abstractions and the Abstractions of modules it depends on (BAZAAR-COMPONENTS section 6, rule 1). None references `Bazaar.Server`. Migrations live in each module's own namespace folder (`Data/Sql/status-incidents/`, `Data/Sql/support/`), not in `common/`.

### 4.1 Why the Kodo adapter is its own project

`status-incidents` must not reference `Bazaar.Kodo`, and `kodo` must not reference this module. The module defines the consumer-side ports (`IPublicStatusSource`, `IIncidentSuggestionSource`) and the bridge implements them from Kodo's abstractions. If the owner never enables Kodo, the module still runs on manual status alone.

### 4.2 Why sinks are provider modules

A sink carries third-party dependencies (GitHub API, S3 client libraries), so by the owner's rule of 2026-11-19 it lives in its own project. Several sinks can run at once; there is no selector. A snapshot goes to every enabled sink, each with its own breaker and freshness (section 7.3).

### 4.3 Why subscribers are separate

Resibo sends email on incident updates. A subscriber list is personal data of people who are not members, and a public sign-up form would be an interactive public browser page, which CLAUDE.md does not allow. So there is no public form. The optional `Subscribers` module holds a list an operator adds to by hand (or by import) in the client pane, with a recorded consent note per address, and sends through doc 22's external mail path. It is off by default. The Atom feed (section 7.2) covers readers who want pushes without any list.

## 5. What may never appear on the public page

The page is public, unauthenticated and, once on a third-party host, outside Bazaar's control. The rules are structural, not best effort.

**Never shown, in any field, snapshot file, feed or sink commit message:**
- Any content: GC messages, Vault text, memory, tickets, mail, prompts, logs, stack traces, crash text.
- Any name: agent names or handles, member and user names, company-internal project, repository, GC or Kanban names, vendor and customer names. The company display name appears only if the operator sets `statusincidents.page.title`; the default is generic.
- Any identifier: agent, GC, device, VM, Kodo probe, module or key ids, internal host names, IP addresses, ports, file paths, email addresses, ticket ids, PR numbers, version strings (unless `statusincidents.page.show_version` is set).
- Any number that identifies scale or spend: agent counts, queue depth, token or cost figures (doc 23's `status.show_spend_and_tokens` stays a doc 23 setting; this module never publishes spend).

**What the page does show:** operator-defined coarse component names and their state; a rolling uptime percentage per component; incident titles, phases and update messages that a person wrote and the linter passed; scheduled maintenance windows; a "generated at" time and a "valid until" time.

Four enforcement layers, each tested:

1. **Closed schema.** The snapshot is built from allow-listed fields only (`PublicSnapshot` in section 7.1). No internal object is ever serialised; the renderer takes the public DTOs only, so a new internal field cannot leak by accident (the same allow-list idea as doc 23's telemetry allow-list).
2. **Linter at authoring and again at publish.** `IPublicTextLinter` rejects text that matches the live deny sets: every agent handle and display name, every member name, GC and project names the server knows, internal module and probe ids, host names and addresses, email addresses, URLs not on the operator allow-list, key-like and token-like strings, and the operator's extra deny list (`statusincidents.lint.extra_denylist`). A blocked publish names the rule, not the secret. The linter fails closed: if it cannot load a deny set, publishing is refused.
3. **Preview and diff.** A publish shows the exact public bytes and the diff from the last snapshot. Nothing goes out unseen.
4. **Sink history warning.** A sink that keeps history (GitHub Pages keeps git history) is flagged `RetainsHistory`. Removing leaked text from the page does not remove it from history, so the linter is the real control. Scrubbing sink history is a history rewrite on a repository outside Bazaar and needs the owner's explicit approval each time (CLAUDE.md rule on shared history).

Text typed by an agent or taken from Kodo or a log is never published without a human reading it (section 6). A Kodo-derived draft never carries Kodo text; it is built from templates (section 6.2).

## 6. Authoring: who, and the relation to Kodo

### 6.1 Principals

| Principal kind | May do | May not |
|---|---|---|
| `user` with `status.incident.publish` | Create, update, resolve, redact and publish incidents and maintenance. Publish still follows `statusincidents.publish.approval` (6.4) | n/a |
| `user` with `status.incident.draft` | Create and edit drafts; cannot publish | Publish |
| `user` with `status.component.manage` | Edit the component list and mapping, set a manual component override | Publish incidents |
| `agent` | Propose a draft through a tool call, if the operator allows it (`statusincidents.agent.draft`, default false). The draft is marked agent-authored and soft-tainted | Publish, edit published text, change components, change sinks. An agent's approval is never accepted as human consent (doc 32) |
| `service` (`service:status-incidents-kodo`) | Create drafts from Kodo suggestions, with template text only | Publish |
| `system` (`system:status-policy`) | Publish a templated notice when the operator has enabled a policy (6.3) | Free text, edits, anything outside the policy |

This follows Resibo's rule that an incident is a deliberate staff action, never an automatic consequence of a health flip (Resibo `docs/apis/v3-deepdive-51-status-page.md` section 4), and extends it for Bazaar's agents and policies. The owner holds all capabilities by default; no other role holds any until the operator grants it (doc 46 section 14.5 pattern).

### 6.2 Kodo detects, this module publishes

Kodo's incident mode (doc 46 section 16.3) is the internal record: severity, trigger, timeline, freeze, evidence. This module's incident is a separate public record with its own phases. The two are linked only by an internal reference (`kodo_incident_ref`) that never leaves the server.

```
Kodo opens an incident (auto or manual)
  -> KodoBridge raises an IncidentSuggestion (severity, affected Kodo categories, a stable fingerprint)
  -> status-incidents maps the categories to public components and builds a TEMPLATE draft
       ("We are investigating a problem affecting {component names}.")
  -> draft appears in the Bazaar.Client.StatusIncidents pane and notifies holders of status.incident.draft
  -> a human edits and publishes, OR the optional policy publishes the template unchanged (6.3)
  -> later updates are human-written
  -> when Kodo's incident exits (human, with step-up, doc 46), the bridge suggests "resolved"; a human or the policy posts it
```

- Draft text is built from templates and the public component names only. No Kodo title, timeline entry, log line or probe id is ever copied, because those are internal and may carry untrusted content (doc 15 taint, doc 46 section 13.5).
- Kodo's post-incident summary (doc 46 section 16.4) can seed a public postmortem draft only as a suggestion the human rewrites; it passes the linter like any text.
- A suggestion is deduplicated by fingerprint: a Kodo incident gets at most one open public draft or incident.
- Kodo's freeze never blocks publishing. Publishing a status notice is not a risky change; it must work during an incident (section 7 explains why it must also work when the server is not).

### 6.3 Policy publish (optional, OFF)

`statusincidents.policy.auto_publish` is off. When the owner turns it on, `system:status-policy` may publish the unchanged template draft when (a) the matching Kodo incident has been open longer than `statusincidents.policy.after_minutes` (default 15), and (b) at least one mapped public component is `outage` or `degraded` for that long. It posts only `investigating` and, after the human exit in Kodo, a templated `resolved`. Every policy publish is audited with the policy version and the rule that matched. Enabling is a Tier 2 action under doc 15 (it changes what the company says in public).

### 6.4 Publish approval

`statusincidents.publish.approval`: `owner` (default; a non-owner publisher's action waits for the owner's approval through doc 15's flow, and the owner's own publish needs none) or `capability` (any holder of `status.incident.publish` publishes directly). Moving from `owner` to `capability` loosens a public-posting control, so it needs owner approval and is audited (CLAUDE.md public-posting tiers).

### 6.5 Editing and correction

Published incident updates are immutable, as published post-incident summaries are in doc 46 section 16.4. A correction is a new update. A `redact` operation replaces a published update's text with "[removed]" in the next snapshot and keeps the original in the database with its hash for audit; it is audited with the actor and reason. Drafts are freely editable, and every edit is a new revision.

## 7. Staying up when the server is down

Resibo's reason for a separate process is that a status page must be reachable when the product is not (`v3-deepdive-51` section 3). Bazaar's answer is a static snapshot pushed to places that do not run Bazaar.

### 7.1 The snapshot

A snapshot is a small set of static files, rendered by the server:

| File | Content |
|---|---|
| `index.html` | The page: component rows, uptime, open incidents, last 14 days of resolved incidents, maintenance. No script needed to read it |
| `status.css` | Styling (the page loads no external font, script or image) |
| `freshness.js` | Optional, same-origin, no network calls: compares the current time with `valid_until` and shows a "this page may be out of date" banner |
| `status.json` | The same data for programs (schema `bazaar.status/1`: `generatedAt`, `validUntil`, `components[]`, `incidents[]`, `maintenance[]`, `uptime`) |
| `status.json.sig` | Detached Ed25519 signature over the canonical JSON (RFC 8785), if signing is on |
| `feed.atom` | Atom feed of incident updates |
| `history/<yyyy-mm>.json` | Resolved incidents by month (older ones are not re-rendered into the main page) |

Every file carries `generatedAt` and `validUntil`. The page states both times in plain text, so the staleness is visible even with scripts off.

### 7.2 Stale means unknown, not green

If the server is down, no new snapshot is published, and the sink keeps the last one. The page must never keep saying "all operational" for an hour-old snapshot. Two defences:

- `validUntil` defaults to four times the publish interval (`statusincidents.snapshot.valid_for_seconds`, default 1200). `freshness.js` shows a banner past it, and the no-script text says the same ("Generated at X. If this time is old, the service may be down or unreachable"). This matches Kodo's rule that a client never shows stale green (doc 46 sections 8.3 and 14.1).
- The server publishes a heartbeat snapshot every interval (default 300 s, minimum 60 s) even when nothing changed. A missing heartbeat is therefore itself the signal.

Resibo's design uses a poller plus its own small cache so the page still rendered "last-known state plus any manual incident". Bazaar gets the same property from the sink holding the last good snapshot, plus a local cache (7.4).

### 7.3 Sinks

```csharp
// Bazaar.Server.StatusIncidents.Abstractions
public sealed record SnapshotFile(string Path, ReadOnlyMemory<byte> Bytes, string ContentType, string Sha256);
public sealed record SnapshotBundle(string BundleId, DateTimeOffset GeneratedAt, DateTimeOffset ValidUntil,
                                    IReadOnlyList<SnapshotFile> Files);

public sealed record SinkCapabilities(bool RetainsHistory, bool SupportsDelete, bool SupportsAtomicSwap,
                                      long MaxFileBytes, int MaxFiles);

public interface IStatusSink
{
    string Id { get; }                       // for example "github-pages"
    SinkCapabilities Capabilities { get; }
    ValueTask<SinkProbeResult> ProbeAsync(CancellationToken ct);                 // no writes
    ValueTask<SinkPublishResult> PublishAsync(SnapshotBundle bundle, CancellationToken ct);  // diff by Sha256, idempotent by BundleId
}
```

- **FileSystem** writes the bundle to a directory with write-to-temp then atomic rename per file, then flips an `index.html` last. An operator points any static host (a mounted share, a synced folder, a reverse proxy) at that directory.
- **GitHubPages** commits to one dedicated status repository as a GitHub App installation scoped to that repository with contents write only (GitHub identity is GitHub Apps, CLAUDE.md; doc 21). The installation token comes from the Identity Service as a short lease; the module never stores it. It does not use a machine account or a personal token.
- **S3** puts objects with `Cache-Control: max-age=60`, then the index last.
- Other hosts (Cloudflare Pages direct upload, SFTP, WebDAV) are OPEN and would be further provider modules.

Each sink runs behind its own breaker (closed, open after 5 consecutive failures, half-open after 60 s, the shape of doc 46 section 7.4) and reports its own last success time. A failing sink never blocks the others, and never blocks incident authoring. Sinks have independent publish credentials in the Identity Service (doc 14), and none is ever shown to an agent.

First enabling of any sink, and any change of its destination, is an owner-approved action (doc 15), because it creates a public endpoint.

### 7.4 Local cache and offline operation

The module keeps `status-cache/` in its own data directory: the last good bundle, the pending drafts and an append-only edit journal (JSON lines, one record per authoring action, with a hash chain like doc 46 section 6.5). If Postgres is unreachable, the module:

- keeps serving the cached bundle on its optional local route (7.5);
- still accepts a staff incident update through the local `bazaar-admin status` command (a work package), which writes to the journal and republishes to every sink from the cache;
- replays the journal into Postgres when it returns, idempotently by record hash.

This is deliberately separate from Kodo's journal spill (doc 46 section 7.5): different records, different owner, and a status edit must be possible when Kodo itself is unhealthy. The shared idea is a hash-chained local file; sharing the code is OPEN (section 14, question 7).

If the whole server is down, no one can publish through Bazaar. The documented fallback is the sink itself: the operator edits `status.json` and `index.html` at the host (for GitHub Pages, a commit). The schema `bazaar.status/1` is published so a hand edit is valid. A hand edit has no signature; the page then shows "unsigned update" when signing is on. This is a stated limit, not a gap to hide.

### 7.5 Optional local route

`statusincidents.listener.local_route.enabled` (default false) serves the same bundle from the status listener at `/public-status/*`. It follows doc 03's bind floor: loopback by default, and a non-loopback bind is refused unless `Bazaar:Host:Status:OwnerApprovedWideBind` is true (doc 46 section 15.5). The route is read-only, GET and HEAD only, with a strict CSP (`default-src 'none'; style-src 'self'; script-src 'self'; img-src 'self'`), no cookies, and its own rate-limit bucket (doc 32 section 7). It adds no sign-in and no interactive element. It is a convenience for an operator who already exposes the status listener; the sinks are the independent path.

### 7.6 Signing

With `statusincidents.sign.enabled` (default true when a sink is enabled), the snapshot is signed with a status key held in the Identity Service, distinct from the release key and from Kodo's journal key (doc 46 section 6.5). The public key is published at `/.well-known/bazaar-status-key.json` in the bundle. A consumer who pinned the key out of band can verify that a copy on a third-party host was produced by this install. Key rotation follows doc 46 section 6.1 (new key under an old-key signature). Whether signing is worth shipping in the first wave is OPEN (question 6).

## 8. Components, status and uptime

### 8.1 Components

Resibo maps many internal services to few public components and shows the worst state of a group (`v3-deepdive-51` section 5, config `component_mapping`). Bazaar keeps the idea and changes the source.

- Public components are operator-defined rows (`status_component`): an id, a display name, a sort order. Shipped defaults are generic (`Core service`, `Messaging`, `Automation`, `Connectivity`); the operator renames or replaces them.
- Each component has one or more **sources** (`status_component_source`): a source kind (`kodo-category`, `manual`) and a reference such as the Kodo category `Messaging`. References are internal and never published. A component's state is the worst of its sources.
- States: `operational`, `degraded`, `outage`, `maintenance`. Resibo has the first three; `maintenance` comes from scheduled windows (8.4).

### 8.2 Mapping Kodo state to a public state

The bridge turns Kodo's per-category health (doc 46 section 8) into signals: `healthy` is `operational`; `degraded` and `recovering` are `degraded`; `failing` is `outage`; `frozen` shows the underlying state (the word "frozen" is internal). `unknown` or `unobserved` is `degraded` with the reason "status unknown", never `operational`.

To keep a transient blip from becoming a public record, a signal must hold for `statusincidents.component.damping_minutes.degraded` (default 5) or `.outage` (default 10) before the public state changes, and the public state returns to `operational` only after `statusincidents.component.recover_minutes` (default 5) of green. Kodo already has hysteresis; this is a second, public-facing one. Component state is automatic once the module is enabled; an incident is not (section 6). A person can pin a component to a state manually (`status_component_override`, with a reason and an expiry, audited).

### 8.3 Uptime

Resibo shows a rolling 90-day percentage per component (`v3-deepdive-51` section 10). Bazaar computes it from the module's own samples (`status_component_sample`, one row per minute bucket rolled up daily), not from Kodo's journal, so it does not depend on Kodo's retention or health. `maintenance` windows are excluded from the denominator and shown separately. The window (`statusincidents.uptime.window_days`, default 90) and retention (default 365 days) are settings. Uptime is a published figure about the company, so it is part of the page content that needs counsel review before launch.

### 8.4 Maintenance

A scheduled window (`status_maintenance`) has a start, an end, affected components and a title. It is authored like an incident (draft, preview, publish), published ahead of time, and moves the component to `maintenance` for its duration. This is an addition beyond Resibo's three states.

## 9. Support module

### 9.1 What is taken and kept

Lifecycle `open`, `in_progress`, `resolved`, `closed`; `resolved` can go back to `in_progress` (a reply of "that did not fix it"); `closed` is terminal; resolved tickets auto-close after 14 days of no activity and only resolved tickets do; unassigned tickets are visible to all staff; any staff member can take an unassigned ticket or one they hold; taking a colleague's ticket is an owner action; ticket ids carry a `TKT-` prefix so they can be found in free text; a denied read is an error, never an empty list. All from `core/support_ticketing/contracts.py`, `lifecycle.py`, `assignment.py` and `v3-deepdive-52-support-ticketing.md` sections 2 to 4 and 9, and the folder's `CLAUDE.md`.

### 9.2 Contracts

```csharp
// Bazaar.Server.Support.Abstractions
public enum TicketStatus { Open, InProgress, Resolved, Closed }
public enum TicketCategory { Problem, Question, AccessRequest, PlatformProblem, Feedback }
public enum MessageVisibility { Public, Internal }          // Internal = staff-only note

public readonly record struct TicketId(string Value);       // "TKT-" + 12 Crockford base32 characters

public sealed record Ticket(
    TicketId Id, Principal CreatedBy, TicketCategory Category, string Subject, TicketStatus Status,
    Principal? AssignedTo, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool AgentOrigin);

public sealed record TicketMessage(TicketId Ticket, long Seq, Principal Author, MessageVisibility Visibility,
                                   string Body, int Revision, DateTimeOffset PostedAt);

public sealed record TicketLink(TicketId Ticket, TicketLinkKind Kind, string Ref, Principal AddedBy, DateTimeOffset AddedAt);
public enum TicketLinkKind { PullRequest, Gc, GcMessage, CrashReport, KodoIncident, KodoAlert, StatusIncident,
                             KanbanCard, VaultNote, AuditEvent, AccessRequest, Module, Blob }

public interface ITicketService
{
    ValueTask<TicketResult> CreateAsync(NewTicket request, Principal actor, CancellationToken ct);
    ValueTask<MessageResult> PostAsync(TicketId id, NewMessage message, Principal actor, CancellationToken ct);
    ValueTask<TicketResult> ChangeStatusAsync(TicketId id, TicketStatus target, Principal actor, CancellationToken ct);
    ValueTask<TicketResult> AssignAsync(TicketId id, Principal? assignee, Principal actor, CancellationToken ct);
    ValueTask<TicketResult> AddLinkAsync(TicketId id, TicketLinkKind kind, string reference, Principal actor, CancellationToken ct);
    ValueTask<TicketPage> ListAsync(TicketQuery query, Principal viewer, CancellationToken ct);        // narrowed to own tickets for non-staff
    ValueTask<IReadOnlyList<TicketMessage>> MessagesAsync(TicketId id, Principal viewer, CancellationToken ct); // throws TicketAccessDenied
}

/// Implemented by the module that owns the linked thing (GC, Kodo, crash reports, GitHub). Soft: a missing resolver shows the raw reference.
public interface ITicketLinkResolver
{
    TicketLinkKind Kind { get; }
    ValueTask<LinkResolution> ResolveAsync(string reference, Principal viewer, CancellationToken ct); // title, deep link, and whether viewer may open it
}

/// For doc 72 and any other free-text reason field.
public interface ISupportReasonLinks
{
    ValueTask<IReadOnlyList<ReasonTicketRef>> ResolveReasonAsync(string freeText, Principal viewer, CancellationToken ct);
    ValueTask<TicketId> CreateFromReasonAsync(NewTicket request, Principal actor, CancellationToken ct);
}

/// Where ticket events go. The client stream, content-free push and the optional GC bridge implement it.
public interface ISupportEventSink { ValueTask OnEventAsync(SupportEvent e, CancellationToken ct); }
```

Errors are data at module boundaries (`TicketResult.Error`), as in Resibo, except a denied conversation read, which throws, for the reason Resibo gives: an empty list for a forbidden thread would be a disclosure that the ticket exists.

### 9.3 Who may file, and what

- **Human users** (Principal kind `user`, any member) open tickets about problems, questions and feedback. This is the case the owner named: a human reports a problem. They see only their own tickets and the public messages on them.
- **Staff** hold `support.staff`. They see every ticket, take unassigned ones, add internal notes (`Internal` visibility, never shown to the requester), link objects and change status. Reassigning a ticket held by someone else needs `support.reassign` (owner by default), as in Resibo.
- **Agents may file tickets, in a narrow lane (PROPOSED, owner question 3).** An agent (Principal kind `agent`) may open a ticket only in the categories `AccessRequest` and `PlatformProblem`, addressed to the human support queue, for things only a person can fix or grant: an access grant (doc 72), a broken tool or VM, a platform bug. Agent-to-agent work does not use tickets; it stays in GCs and Kanban, because GCs are the coordination system and a ticket queue must not become a second one (CLAUDE.md). Limits: `support.agent.max_open` (default 3 per agent), a per-agent cooldown, deduplication by subject fingerprint, and the loop guard of doc 06 applies. An agent sees only its own tickets. An agent's ticket carries `AgentOrigin = true`; its text is soft-tainted and rendered as plain text to staff, never interpreted as an instruction, and an agent's reply or "resolved" claim is never accepted as human consent (doc 32). An agent can withdraw its own ticket (`closed`) but cannot resolve it.
- **Services and system.** `service:kodo` and `service:crash-intake` do not open tickets themselves; a human opens one from an alert or a crash report through the link affordance. `system:support-autoclose` performs the 14-day closure and nothing else.
- **External senders** (a support mailbox that turns mail into tickets) are OPEN. Such text is `external` and taints hard (doc 15, doc 36), so it would need a reader session. Not designed here.

### 9.4 Links and doc 72

Resibo's `related_receipt_id` generalises to many links, because Bazaar's tickets are about different things. A `TicketLink` holds a kind and an opaque reference (for example `PullRequest` with `owner/repo#123`, `Gc` with a GC id, `CrashReport` with a report id from doc 43). Resolution goes through `ITicketLinkResolver` implemented by the owning module, so Support never reads another module's tables. A link opens only if the viewer may read the target: a staff member without access to a DM gets "linked to a private conversation" and a request path (doc 01 access request), not the content. The owner's read-all rule is unchanged.

**Access-grant reasons (doc 72).** Resibo's break-glass `reason` is free text that may name a ticket (`v3-deepdive-05` section 6, `v3-deepdive-52` sections 1 and 9). Bazaar mirrors it: doc 72's grant flow may offer "link or create a ticket" and calls `ISupportReasonLinks`. The link is a resolution, never a requirement; a grant is never refused for lacking a ticket. A `TKT-` id in the reason resolves to a real ticket if the viewer may see it, and to nothing otherwise. Counters `reason_refs_resolved` and `reason_refs_unresolvable` exist because that link would otherwise break silently (Resibo `CLAUDE.md`, folder note). Doc 72 was not in the tree when this doc was written; its calling side is OPEN (question 4).

### 9.5 Conversation and attachments

Ticket messages live in `support_message`, append-only; an edit is a new revision and every revision is kept with an "edited" marker (the GC rule, doc 01). Attachments (screenshots) go through the Blobs module's `IBlobStore` (soft dependency; `support.attachments.enabled` default false until the operator turns it on, because a screenshot may carry personal data). Crash-report attachments are not copied; the link points at the redacted report of doc 43.

### 9.6 Why not just a GC

Bazaar already has GCs, and a ticket could be "a GC with a state". The Support module keeps its own message table for three reasons: ticket visibility is per ticket and per role (internal notes, requester-only), while GC membership and the owner's read-all DM rule have different semantics; a ticket needs a state machine and an assignee; and the requester may not be a member of any shared GC. To honour "events serve GCs", the optional `GcBridge` module mirrors ticket events (opened, assigned, resolved) into a GC the operator chooses as `system` events carrying the ticket id and subject only, never message bodies. Whether a ticket should instead be hosted inside a GC is OPEN (question 2); if the owner prefers that, it is a different module, `Bazaar.Server.Support.GcThreads`, behind the same `ITicketService`, not a rewrite.

### 9.7 Notification

Support does not own delivery (Resibo `v3-deepdive-52` section 1). It raises `SupportEvent` to `ISupportEventSink` implementations: the client `support` stream topic on `/ws` (doc 02), the content-free push wake-up (doc 29; content is fetched over the authenticated connection), and the optional GC bridge. Email to a requester is external-only and off (doc 22).

### 9.8 Auto-close and counters

A sweep (a background job) closes tickets that have been `Resolved` for `support.auto_close_days` (default 14) with no activity, as `system:support-autoclose`. A reply restarts the window. Counters (created, assigned, reassigned, resolved, closed, reopened, auto-closed, invalid transitions, denied by role, denied by ownership, reason refs resolved and unresolvable) go to the doc 23 pipeline with module ids as labels and never ticket or agent ids (doc 46 section 15.4 cardinality rule).

### 9.9 Priority and SLA (separate, optional)

Resibo rejects priority tiers and SLA tracking at its scale (`v3-deepdive-52` sections 4 and 9). Bazaar need not decide that for every install, so priority and response targets are a separate optional module, `Bazaar.Server.Support.Sla` (`support-sla`), off by default and not in the first waves. The core module has no priority field. This keeps the simple default and still lets an operator add the machinery.

### 9.10 Optional assistant

Resibo's webapp assistant suggests troubleshooting steps inside tickets. In Bazaar this is an optional module, off, that asks the company's gateway (doc 05) for suggested next steps on a ticket. It follows doc 46 section 13.5: read-only, no tools, deterministic redaction first, output labelled "unverified suggestion", and the ticket text taints the session. Not scheduled; OPEN (question 8).

## 10. Exposure and sign-in floors (Resibo tunnel exposure, mapped to Conduit)

Resibo's Tunnel Exposure (`v3-deepdive-43`) has a swappable `TunnelProvider` (Cloudflare default, a manual reverse proxy, named ngrok and Tailscale extension points) and a `public_facing` flag that raises the sign-in floor when the instance becomes reachable from the internet. Bazaar already has the first half: `Bazaar.Conduit` with `IConduitProvider`, `ConduitScope`, a registry, Cloudflared, a manual port forward and overlay modules (doc 39). Nothing is duplicated here.

What is taken is the second half, which Bazaar lacks as a stated rule:

- **A derived state, `ExposureLevel`,** computed from the offers Conduit currently publishes. `Public` when any live offer has scope `Internet`, `Relay` or `Onion` (a relay or onion service is reachable by anyone who knows the address). `Private` for `Loopback`, `Lan` and `Overlay`. `PeerToPeer` is OPEN (question 5). It is never stored as a setting; it is derived, so it cannot be forgotten.
- **A rule for doc 32,** not for this module: while `ExposureLevel` is `Public`, the sign-in and step-up floor rises (candidates: step-up for more action classes, shorter sessions, stricter rate buckets, no new-device enrolment over a public path without owner approval). The floor can rise and never fall below the base floor, and Conduit grants no identity (doc 39 section 3, "reachability is not identity"). Floor values are OPEN (question 5).
- **Contract.** Conduit has no Abstractions project yet, so a consumer cannot reference it without breaking BAZAAR-COMPONENTS rule 1. A small `IExposureState` (`ExposureLevel Current`, a change notification) belongs in a Conduit Abstractions project. It is a work package against Conduit, filed here for tracking (WP 12).

How this module relates: the sink-hosted page runs on someone else's host and does not make Bazaar reachable, so it raises no floor. The optional local route (7.5) lives on the status listener and is covered by the bind floor. Tunnel health is already a Kodo probe (`conduit.<provider>.probe`, doc 46 section 9.1), not a new mechanism, as in Resibo's own section 5.

## 11. Client panes

### 11.1 `Bazaar.Client.Support`

A pane module registered through the pane registry (doc 41), referencing `Bazaar.Client.Core` contracts only. Keyboard-first, no Ctrl chords; every state word has a screen reader label; strings are localised keys (Michi's convention).

- **Report a problem** (any member): subject, category, body, optional links (the pane can prefill a link from the current GC, PR or last crash). Before submitting it shows open public incidents from the status module, so a known incident is not reported twice.
- **My tickets:** list, thread, status, a "reopen" reply on a resolved ticket.
- **Staff queue** (needs `support.staff`): a table of tickets filtered by status, unassigned and assignee; take, assign, internal notes, change status, linked-object chips that open the target if the viewer may.
- Proposed mnemonics (subject to the doc 41 registry): `sup` opens the pane, `supn` reports a problem, `supq` the staff queue.
- Android renders the same pane in a compact layout; dictation can fill the body. The terminal head uses the list and thread views.
- Offline: the pane shows the cached list with its age and queues a new ticket locally for send, marked unsent.

### 11.2 `Bazaar.Client.StatusIncidents`

A compact pane for the staff who author: component list and state, open drafts (including Kodo-suggested ones, marked "suggested, template only"), editor with live linter messages, the preview of the exact public page, sink freshness, maintenance, and publish. Mnemonics proposed: `stat`, `statp`. The optional admin-panel section (doc 51) would read through an `IAdminStatusIncidentsSource` port; whether to add it is OPEN (question 9) and is a change to doc 51, not made here.

### 11.3 Tours (Resibo `v3-deepdive-53`, mapped to Michi)

The guided tour mechanism already exists as `Bazaar.Michi` (`TourDefinition`, `TourStep`, `TourSession`, `ITourStateStore`; doc 45). Nothing is rebuilt here. This doc contributes two tour definitions, "Report a problem" (Support) and "Publish a status update" (StatusIncidents), each registered with Michi as data. Resibo's design points are mapped to Michi as requests for the Michi owner, not changes made here: no persistent nagging banner after a skip, with a regression test; progress stored on the server per user so a tour resumes on another device (Michi's store is in-memory today); reduced-motion respected; a visible mute control for any sound cue. See section 13.

## 12. Interaction with existing modules and docs

| Module or doc | Interaction | Reuse or change |
|---|---|---|
| Kodo (46) | Reads state through KodoBridge; Kodo supplies suggestions only | Reuses Kodo's state vocabulary and the health contract. **Overlap with section 12.4 and 19 conflicts 5 and 6:** Kodo proposes an optional public page (components, uptime, incidents, approved summaries). This module is that page, with the publishing path that survives a server outage. Proposed: Kodo 12.4 delegates to this module and keeps only the data port. Needs owner ratification; doc 46 is not edited here |
| Doc 23 `/status` | Unchanged. It is the operational status (agent counts, queue depth), loopback by default | The public snapshot is a different, coarser surface and shares no fields with it |
| Doc 51 web admin | Optional section via a port; the panel reads, authoring stays in client panes | Soft edge only |
| Conduit (39) | `ExposureLevel` derived from offers; Cloudflared and others reused as they are | No Conduit code duplicated; one Abstractions project requested |
| Michi (45) | Tours registered as data | Mechanism reused; Resibo points handed over |
| GC (01) | `GcBridge` mirrors events; links resolve GCs | Reused, never replaced |
| Crash reporting (43) | `CrashReport` links resolve to redacted reports | Reused |
| Audit (15, 48) | All events carry a `Principal` | Reused |
| Identity (14) | Sink credentials and the status signing key | Reused |
| Email (22) | Subscribers module sends external mail | Reused |
| Erasure (48 section 9) | Closed ticket content honours legal hold | Soft edge |
| Blobs | Attachments | Soft edge |

## 13. What is taken from Resibo, and what is changed and why

| Resibo | Source | Bazaar |
|---|---|---|
| Public unauthenticated status page, separate from the product | `v3-deepdive-51` sections 1 and 3, `services/status_page/CLAUDE.md` | Static snapshot to sink providers instead of a separate web app, because Bazaar forbids interactive public pages and a static host is the simplest independent path |
| Staff-authored incidents, never automatic | same, section 4 | Kept. Added: drafts from Kodo, optional policy publish, agent drafts off by default |
| Coarse components, worst-of mapping | same, section 5, `component_mapping` config | Kept; mapping source is Kodo categories, operator-defined components |
| Cache independent of core database | same, section 3 (own SQLite) | Local hash-chained file cache plus the sinks' copy |
| Rolling 90-day uptime | same, section 10 | Kept; computed from own samples |
| Email subscribers | same, section 10 | Separate optional module, operator-managed list, no public form (browser rule and personal data) |
| RSS as lower priority | same, section 10 | Atom feed in the snapshot from the start (no personal data) |
| Hosted-only (self-hosted installs skip it) | same, section 1 | Optional on every install, off by default |
| Ticket lifecycle, assignment, auto-close, `TKT-` ids, break-glass resolution | `core/support_ticketing/CLAUDE.md`, `v3-deepdive-52` | Kept as above, with the `Principal` type instead of `user_id` strings |
| `related_receipt_id` | `v3-deepdive-52` | Generalised to typed links |
| No priority or SLA | `v3-deepdive-52` section 4 | Separate optional module `support-sla` |
| Staff and user author as `"user"` or `"staff:<id>"` | `v3-deepdive-52` | `Principal` plus visibility |
| Notifications via inbox | `v3-deepdive-09` | Event sink port; stream, push, optional GC mirror |
| Tunnel provider registry, `public_facing` floor | `v3-deepdive-43` | Conduit is the registry; derived `ExposureLevel`; floors in doc 32 |
| Guided tour mechanism, no-nag, reduced motion, mute, server-side progress | `v3-deepdive-53` | Michi; points handed to Michi (11.3) |
| Webapp assistant for tickets | `v3-deepdive-54` | Optional, off, labelled unverified |

Resibo's gRPC, FastAPI, SQLite and React pieces are not carried over (different stack). Only Resibo's docs are a source here.

## 14. Security and privacy

- **Public surface is write-only from the server.** Nothing public accepts input. The module opens no listener unless the optional local route is on.
- **Fail closed.** Linter, signer or approval unavailable means no publish. A missing sink only reduces reach.
- **Taint.** Kodo text, logs, agent text and ticket text are never published or acted on directly. Agent tickets are soft-tainted and rendered as plain text. Ticket bodies are escaped on display (the Kodo log rule).
- **Secrets.** Sink credentials and the signing key stay in the Identity Service and are leased per publish; they never reach agents, logs or config.
- **Personal data.** Ticket bodies and subscriber addresses are personal data. They use the per-person encrypted personal-field rule (doc 01 decisions) where the field names a person. The snapshot has none. Retention (`support.retention_days`, default 0 meaning keep, matching Bazaar's append-only stance) and erasure are OPEN and need counsel (question 10).
- **Audit.** Entries carry the `Principal` text form as `actor`. Status kinds: `status.component.set`, `status.component.override`, `status.incident.draft`, `status.incident.suggested`, `status.incident.publish`, `status.incident.update`, `status.incident.redact`, `status.maintenance.publish`, `status.sink.enable`, `status.sink.publish`, `status.sink.failed`, `status.lint.blocked`, `status.policy.publish`. Support kinds: `support.ticket.create`, `.message`, `.status`, `.assign`, `.reassign`, `.link`, `.autoclose`, `.read.denied`, `.agent.file`, `.reason.resolve`. Payloads hold ids, kinds and hashes, never bodies or secrets; a write whose audit append fails is refused.
- **Rate limits.** Per-principal limits on tickets and messages (`support.user.max_open`, message size and rate), per-agent quotas, and the `status` and `support` buckets of doc 32 section 7.
- **Publishing is outward and high-impact.** It follows doc 15 tiers; an agent can never publish.

## 15. Settings (proposed registry rows)

Registered in doc 03's registry; changes audited; "approval" means owner approval through doc 15. Settings may tighten, never loosen below the floors in section 5.

| Key | Type | Default | Approval |
|---|---|---|---|
| `statusincidents.enabled` | bool | false | yes, when enabled |
| `statusincidents.page.title` | string | `Service status` | no |
| `statusincidents.page.footer` | string | unset | no (counsel note) |
| `statusincidents.page.show_version` | bool | false | yes |
| `statusincidents.components` | list | four generic rows | no |
| `statusincidents.component.auto` | bool | true | no |
| `statusincidents.component.damping_minutes.degraded` / `.outage` | int | 5 / 10 | no |
| `statusincidents.component.recover_minutes` | int | 5 | no |
| `statusincidents.snapshot.interval_seconds` | int | 300 (min 60) | no |
| `statusincidents.snapshot.valid_for_seconds` | int | 1200 | no |
| `statusincidents.sign.enabled` | bool | true | no |
| `statusincidents.free_text` | enum `lint`, `templated_only` | `lint` | yes, if loosened |
| `statusincidents.lint.extra_denylist` | list | empty | no |
| `statusincidents.publish.approval` | enum `owner`, `capability` | `owner` | yes, to `capability` |
| `statusincidents.kodo.suggest` | bool | true | no |
| `statusincidents.agent.draft` | bool | false | yes, when enabled |
| `statusincidents.policy.auto_publish` | bool | false | yes, Tier 2 |
| `statusincidents.policy.after_minutes` | int | 15 | no |
| `statusincidents.uptime.window_days` / `.retention_days` | int | 90 / 365 | no |
| `statusincidents.listener.local_route.enabled` | bool | false | yes |
| `statusincidents.sink.<id>.enabled` | bool | false | yes, when enabled or changed |
| `support.auto_close_days` | int | 14 (1 to 90) | no |
| `support.user.max_open` | int | 20 | no |
| `support.agent.file.enabled` | bool | true | no |
| `support.agent.max_open` | int | 3 | no |
| `support.agent.categories` | list | `AccessRequest`, `PlatformProblem` | yes, to widen |
| `support.reassign.requires_owner` | bool | true | yes, to disable |
| `support.attachments.enabled` | bool | false | yes |
| `support.message.max_chars` | int | 8000 | no |
| `support.retention_days` | int | 0 (keep) | counsel |
| `support.gc_bridge.gc_id` | id | unset | no |

Capabilities, PROPOSED for registration in the custom-roles table (doc 48 section 4): `status.view`, `status.incident.draft`, `status.incident.publish`, `status.component.manage`, `status.sink.manage`, `status.settings`, `support.file`, `support.staff`, `support.reassign`, `support.view.all`, `support.settings`. The owner holds all by default; `support.file` is held by every human member by default.

## 16. Data (SQL sketches)

Namespace `status-incidents`, file `Data/Sql/status-incidents/0001_status.sql`.

```sql
create table status_component(
  id text primary key, display_name text not null, sort_order int not null, enabled bool not null default true);
create table status_component_source(
  component_id text references status_component, source_kind text not null,   -- kodo-category | manual
  source_ref text not null, primary key(component_id, source_kind, source_ref));
create table status_component_override(
  id bigserial primary key, component_id text references status_component, state text not null,
  reason text not null, actor text not null, expires_at timestamptz not null, created_at timestamptz not null);
create table status_component_sample(
  component_id text, bucket timestamptz, state text not null, primary key(component_id, bucket));
create table status_incident(
  id uuid primary key, title text not null, phase text not null, impact text not null,
  state text not null,                      -- draft | published | resolved | retracted
  origin text not null,                     -- user | agent | kodo-suggestion | policy
  kodo_incident_ref text null,              -- internal only, never rendered
  created_by text not null, created_at timestamptz not null, published_at timestamptz null, resolved_at timestamptz null);
create table status_incident_component(incident_id uuid references status_incident, component_id text references status_component,
  primary key(incident_id, component_id));
create table status_incident_update(
  incident_id uuid references status_incident, seq int, revision int, phase text not null,
  message text not null, state text not null,            -- draft | published | redacted
  author text not null, created_at timestamptz not null, content_hash bytea not null,
  primary key(incident_id, seq, revision));
create table status_maintenance(
  id uuid primary key, title text not null, starts_at timestamptz not null, ends_at timestamptz not null,
  state text not null, created_by text not null);
create table status_snapshot(
  bundle_id uuid primary key, generated_at timestamptz not null, valid_until timestamptz not null,
  content_hash bytea not null, signature bytea null);
create table status_sink_state(
  sink_id text primary key, last_bundle uuid null, last_success_at timestamptz null, consecutive_failures int not null default 0, last_error_code text null);
```

Namespace `support`, file `Data/Sql/support/0001_support.sql`.

```sql
create table support_ticket(
  id text primary key,                        -- TKT-XXXXXXXXXXXX
  created_by text not null,                   -- Principal text form
  category text not null, subject text not null, status text not null,
  assigned_to text null, agent_origin bool not null default false,
  created_at timestamptz not null, updated_at timestamptz not null, resolved_at timestamptz null);
create table support_message(
  ticket_id text references support_ticket, seq bigint, revision int, author text not null,
  visibility text not null, body text not null, posted_at timestamptz not null,
  primary key(ticket_id, seq, revision));
create table support_link(
  ticket_id text references support_ticket, kind text not null, ref text not null,
  added_by text not null, added_at timestamptz not null, primary key(ticket_id, kind, ref));
create table support_event(
  seq bigserial primary key, ticket_id text, kind text not null, actor text not null, at timestamptz not null);
```

Status values are text with check constraints written in the migration. `support_message` is append-only; no update or delete statement is issued by the module.

## 17. Kodo health contract

Both modules implement `IModuleHealth` (doc 46 section 10) and ship `ModuleHealthContractTests` subclasses.

| Module | Liveness | Readiness | Hard deps | Probes (catalogue additions, PROPOSED) | Self-test (`Quick`, `Full`) |
|---|---|---|---|---|---|
| `status-incidents` | no I/O | database reachable or cache present | `audit`, `identity` | `statusincidents.snapshot.age` (non-critical), `statusincidents.sink.<id>.fresh` (age of last successful publish; non-critical, critical if it is the only sink and the module is enabled), `statusincidents.cache.writable`, `statusincidents.signer` | Quick: render a fixture snapshot and verify the schema. Full: run the linter against planted internal names and require a block; publish to a scratch FileSystem sink and verify the hash; no residue |
| `support` | no I/O | database reachable | `audit`, `identity` | `support.sweep.age` (last auto-close sweep), `support.queue.oldest_unassigned_minutes` (non-critical) | Quick: lifecycle table check. Full: sandbox transaction creates a ticket, walks all legal transitions and checks illegal ones are refused, rolled back |

A sink that fails does not fail the module's liveness; the readiness stays `Pass` with a `Warn` fact so a dead third-party host cannot take the module down. Probe additions are a catalogue change, so they ship in the Kodo signed runtime slot, not as settings (doc 46 section 9.1).

## 18. Testing

xUnit v3 throughout; `Avalonia.Headless` for the panes; each behaviour change ships with a test.

- **Independence under outage.** With Postgres and Kodo fakes failing, the module still renders the cached bundle and publishes an update from the local cache to a FileSystem sink.
- **Stale is not green.** A bundle past `validUntil` renders the banner text; a missing heartbeat is visible in the file.
- **No-leak suite.** A planted corpus of agent names, member names, GC and project names, probe and module ids, hosts, IPs, emails, URLs and key-like strings fails the linter at authoring and at publish; the snapshot built from a database full of such values contains none of them (static scan of every file in the bundle); the schema check proves only allow-listed fields exist; a snapshot built from a Kodo suggestion with hostile title text contains the template only.
- **Authoring rules.** Agent, `service` and `system` principals cannot publish outside the policy; policy publish needs the setting and emits only the template; an agent approval does not satisfy `owner` approval; redact keeps the original in the database.
- **Aggregation.** Damping and recovery, worst-of mapping, manual override expiry, unknown maps to degraded, maintenance excluded from uptime.
- **Sinks.** Idempotent publish by hash; breaker opens and recovers; one failing sink does not block another; the GitHub sink uses a scoped lease and never persists a token (credential-handling test); `RetainsHistory` shown in preview.
- **Signing.** A tampered file fails verification; rotation works under an old-key signature.
- **Support lifecycle.** Legal and illegal transitions, `resolved` back to `in_progress`, `closed` terminal; auto-close only from `resolved` and reset by a reply; self-assign allowed, taking a colleague's ticket needs `support.reassign`; denied thread read throws; non-staff listing is narrowed; internal notes never reach the requester.
- **Agents.** The two allowed categories only, quota and cooldown, dedupe, no access to others' tickets, cannot resolve, soft taint flag set, text rendered as plain.
- **Links and reasons.** A resolver per kind; a link is not opened without read access; `TKT-` extraction from prose; a reason naming nothing resolves to nothing and refuses nothing; the resolved and unresolvable counters move.
- **Architecture.** No implementation project references `Bazaar.Server` or another module's implementation; migrations are in their own namespace; no third-party package in the two core modules; registration tests resolve services in a test host.
- **Panes.** Headless tests for keyboard reach, screen reader labels, report-a-problem flow, staff queue, linter messages in the editor, unsent state offline.
- **Kodo contract.** Both modules inherit `ModuleHealthContractTests`.

## 19. Work packages

Each is one project or file group with tests, its own branch `agent/<wp-id>`, merged in dependency order. Packages marked (other module) are filed here for tracking and executed in that module's own change.

| WP | Content | Needs |
|---|---|---|
| 1 | `StatusIncidents.Abstractions`: records, enums, `IStatusSink`, `IPublicTextLinter`, ports | none |
| 2 | `Support.Abstractions`: records, `ITicketService`, resolver and reason ports | none |
| 3 | Support lifecycle and assignment (pure logic and tests) | 2 |
| 4 | Support persistence, SQL `support/0001`, service, auto-close sweep, audit | 3 |
| 5 | Support link resolvers: GC, crash report, PR, Kodo (each its own small file in the owner module's adapter) | 2 |
| 6 | StatusIncidents aggregator, damping, uptime, SQL `status-incidents/0001` | 1 |
| 7 | Public text linter and deny sets with the no-leak corpus | 1 |
| 8 | Snapshot renderer (HTML, CSS, JSON, Atom, `freshness.js`, embedded assets), schema, signer seam | 1, 7 |
| 9 | Incident service: drafts, publish, redact, maintenance, approval flow | 6, 7, 8 |
| 10 | Local cache and `bazaar-admin status` offline command | 9 |
| 11 | Sink providers, one project each: FileSystem, then GitHubPages, then S3 | 8 |
| 12 | `KodoBridge`; Conduit `IExposureState` contract (other module); Michi no-nag, server-side progress and mute points (other module) | 9; Kodo Abstractions |
| 13 | `Client.Support` pane and tour | 4 |
| 14 | `Client.StatusIncidents` pane and tour | 9 |
| 15 | Optional modules: `Support.GcBridge`, `StatusIncidents.Subscribers`, `Support.Sla` | 4, 9 |
| 16 | Kodo health contract tests, probes and self-tests for both modules | 4, 9 |

## 20. Open questions

1. **Kodo 12.4 and status page ownership.** Proposed: this module is the public page; Kodo keeps the data port and drops its own page rendering. Ratify or reject (affects doc 46 section 19 conflicts 5 and 6).
2. **Ticket thread in support tables or in a GC.** Proposed: support tables plus an optional GC mirror (9.6). Owner to confirm, or choose the `GcThreads` alternative.
3. **May agents file tickets.** Proposed: yes, in two categories, human queue only, quota 3, default on. Alternative: off by default.
4. **Doc 72 interface.** `ISupportReasonLinks` is a proposal; doc 72 was not readable when this doc was written. Confirm the calling side and who creates a ticket from a grant request.
5. **Exposure floors.** Which scopes count as public (`PeerToPeer`?), and which floors rise (step-up classes, session length, rate buckets, new-device enrolment). Belongs to doc 32 and Conduit owners.
6. **Snapshot signing.** Ship in the first wave or later; whether the value justifies the key custody.
7. **Shared code with Kodo.** The breaker and the hash-chained local file are Kodo-internal today. Extract to a shared contracts-and-logic library, or accept a small duplicate here.
8. **Ticket assistant** (9.10): wanted at all, and in which wave.
9. **Admin-panel section.** Add a status and support section to doc 51 through ports, or keep both in client panes only.
10. **Retention and erasure of tickets**, and whether subscribers or any outside-requester data is ever held. Needs counsel.
11. **First-wave sinks.** FileSystem, GitHub Pages and S3 are proposed; the owner may name the host the owner's install will use first.
12. **Public component names and the default uptime figure** for the owner's own instance, including whether the owner's page is public at all.
13. **External email to tickets** (a support mailbox): wanted, and via a reader session.
