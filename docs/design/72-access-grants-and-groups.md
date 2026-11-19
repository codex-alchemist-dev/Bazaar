# 72. Access grants, groups and sign-in policy

Status: PLANNED. Design intent only; no code, migration or pane exists yet. The owner's direction of 2026-11-19 to adopt Resibo's break-glass, Groups and auth rules as Bazaar modules is DECIDED (as "adopt this Resibo feature as module X", with conflicts split into separate modules). Every detail below it is PROPOSED unless it restates a DECIDED item from another doc. Owner questions: section 17.
Depends on: 01-gc-data-model.md (`gc_access_request`, DM rules, Thinking GCs), 02-wire-protocol-and-sync.md (per-stream access check, `stream.revoked`), 03-server-host-and-providers.md (settings registry, provider seam), 04-postgres-schema-and-migrations.md (conventions, per-person keys), 14-identity-service-and-passport.md (keys, leases), 15-security-taint-and-approvals.md (taint, `IAuditLog`, notice channels), 18-vault.md (areas and the Vault access layer), 32-remote-access-auth-and-privacy.md (sign-in, tokens, roles table, DM access request), 39-conduit-remote-connectivity.md (exposure signal), 40-core-feature-modules-audit.md and BAZAAR-COMPONENTS.md (manifests, `Principal`, migration namespaces), 46-kodo-health-module.md (health contract), 48-gap-decisions-security-identity.md (custom roles, audit, biometrics).
Used by: 01 (GC read access), 18 (Vault access layer), 32 (sign-in and roles), 46 (Kodo checks), 51 (web admin sign-in section), 41 and 27 (client pane registry).

## 1. Purpose

Bazaar has two ways to decide who may read something today: a role (doc 48 section 4, capabilities over scopes) and a fixed server rule (the owner reads all). Three real needs fall between them, and Resibo (a sister program) solved all three:

1. **A person other than the owner needs to read something private, once, for a stated reason.** Doc 01 and doc 32 say a DM's other members "may request access" and leave the flow OPEN. Doc 18 has the same hole for another agent's private memory and for Thinking GCs ("other members by access request"). This is Resibo's break-glass grant: time-boxed, reason-tagged, owner-decided, dual-notified, audited.
2. **A team structurally sees its own resources.** A group manager should see the group's resources without a grant, the way a team lead sees the team's work. This is Resibo's Groups, a third access shape that is deliberately not "a grant that never expires".
3. **Sign-in must have floors.** Resibo's passwordless-only rule and its two-factor floor on a publicly exposed multi-user install are stated as policy that settings may tighten and never loosen. Bazaar needs the same floors for remote access.

This doc designs three modules for those needs (two named by the owner, one added to keep sign-in policy out of the others) and one client pane. It reuses the audit log, the Approvals signature check, the settings registry, the GC `system` event path and Kodo's health contract. It builds none of them again.

## 2. Decided inputs

- Owner, 2026-11-19: incorporate everything from Resibo that benefits Bazaar; where something conflicts with Bazaar's design, make it a separate module instead of choosing; account for everything.
- Owner, 2026-10-24 (DECIDED, `Bazaar.md` section 2): DMs are two-member GCs; the owner reads all; other members may request to read a DM. The request flow was left OPEN. This doc proposes the flow.
- Doc 48 section 4 (DECIDED): roles are custom, a grant is (capability, scope), role changes are audited, the owner role keeps floors, role changes that widen a grant on a human need the owner's signed approval and a biometric check. Agents never hold `admin.*`.
- Doc 48 section 5 (DECIDED): the audit log is a hash chain recording admin and security events and every data read and write by humans and agents.
- Doc 48 section 8 (DECIDED): biometric step-up is required for sign-in on a new device and for high-risk actions, including role change and grant widening.
- Doc 15 (C18): approvals gate outward high-impact actions and merges only. This doc does not touch that. Access grants are inward data access.
- `Principal` (BAZAAR-COMPONENTS section 4.2, owner 2026-11-19): every record of who acted carries `kind:id`.
- Configurability rule (DECISIONS-LOG round 13): open values are operator settings with safe defaults; DECIDED items and security invariants are not settings; settings may tighten, never loosen.
- Doc 32 section 6: an agent's request or approval is never honoured as human consent.
- Module rule (CLAUDE.md, BAZAAR-COMPONENTS section 2.1): each feature is its own `Bazaar.Server.<Name>` plus `.Abstractions`, with a `Bazaar.Client.<Name>` module for its client half. Nothing lands in `Bazaar.Server` or `Bazaar.Client.Core`.

## 3. What this doc does not decide

- Approvals (doc 15, `Bazaar.Approvals`). They gate outward actions. A grant is never an approval and the two share no table. Section 12 states the one primitive they share.
- The role table and role evaluation (doc 48 section 4, PLANNED). Groups supplies a membership fact that a role scope may name; it adds no capability.
- The audit log's storage, chain and anchoring (doc 48 section 5). This doc only defines events and calls `IAuditLog`.
- How the GC module and the Vault service call the grant check. Section 9 names the seam each needs; the change inside each module is a work package of that module, not decided here.
- The decryption path for DM bodies encrypted under per-person keys (doc 04 Question 2, doc 14). A grant lets the server serve the log; how the key is used is that doc's decision. See section 17.
- Erasure, retention of grant rows (doc 48 section 9). Grant rows are audit-class records; retention follows the audit category until doc 48 section 9 says otherwise.
- Resibo's Persistence, Search/Query and Export Framework consumers of groups. Bazaar has no receipts; the equivalents are named in section 9.

## 4. Design overview

### 4.1 Three access shapes, kept apart

| Shape | Source | Lifetime | Who decides | Audit and notice | Failure mode if conflated |
|---|---|---|---|---|---|
| Role (doc 48) | Capability over a scope | Until changed | Owner, signed | Role change event | n/a |
| Group (this doc) | Membership in a group a resource belongs to | No timer; re-checked on every call | Owner or delegate adds members and managers | Membership events, no per-read notice | A "forever grant" hides standing access from the subject's notice and from the review queue |
| Grant (this doc) | One requester, one target, one reason | Always bounded by a time box | Owner or delegate (standard) or self-activated (emergency) | Notice to subjects at activation, review for emergency | A "role with an end date" skips the request, reason and subject notice |

Resibo's own text (`docs/apis/v3-deepdive-41-groups.md` section 2) names the conflation as a real mistake, with different audit expectations, UX and failure modes. This doc keeps the shapes in separate modules for that reason.

### 4.2 Modules

| Module | Projects | Component id | Migration namespace | Kind |
|---|---|---|---|---|
| Access grants | `Bazaar.Server.AccessGrants`, `Bazaar.Server.AccessGrants.Abstractions` | `access-grants` | `access_grants` | feature |
| Groups | `Bazaar.Server.Groups`, `Bazaar.Server.Groups.Abstractions` | `groups` | `groups` | feature |
| Sign-in policy | `Bazaar.Server.SignInPolicy`, `Bazaar.Server.SignInPolicy.Abstractions` | `signin-policy` | `signin_policy` | feature |
| Client pane | `Bazaar.Client.AccessGrants` | `client-access-grants` | none | client-pane |

Migration files sit at `src/<Project>/Data/Sql/<namespace>/NNNN_name.sql` (BAZAAR-COMPONENTS section 5). Hard dependencies: all three server modules `DependsOn` the audit seam (`audit`, `Bazaar.Audit.Abstractions`). `SoftDependsOn` for `access-grants`: `gc`, `vault`, `groups`, `approvals` (signature check), `kodo`. `groups` soft-depends on `access-grants` and `kodo`. `signin-policy` soft-depends on `conduit` (exposure) and `remote`. Soft edges follow BAZAAR-COMPONENTS section 4.1: a missing target degrades only the feature that names it, and may form cycles.

The name "Groups" collides with doc 48's scope kind "an agent group". They are related, not equal. The `groups` module is the registry that such a scope kind can name (`group:<id>`); whether the existing "agent group" scope becomes a group of kind `agents` is OPEN (section 17).

### 4.3 What a grant covers

A grant names an `AccessTarget`: a kind and an id, with an optional start point.

| Target kind | Meaning | Enforced by | Note |
|---|---|---|---|
| `gc` | Read a GC the requester is not a member of (DMs, Thinking GCs, any GC under a privacy rule) | GC read access (doc 01) and the wire subscribe check (doc 02) | Read-forward from `from_seq`, as doc 32 section 6 says |
| `vault_area` | Read a Vault area or one document (another agent's private memory, a draft, a review note) | Vault access layer (doc 18 Access) | Peer read default stays none (`vault.memory_peer_read`); a grant is the exception path |
| `audit_view` | See a slice of the audit log hidden from the requester's role | Audit viewer (doc 48 section 5.4) | Never grants what the owner may not see (doc 48 section 4.1 floor) |
| `custom:<kind>` | Registered by another module through the target registry | That module | Extension point; registry rows, not code |

Modes are `read` only in v1. A write grant is not designed (OPEN, section 17).

## 5. The access grants module

### 5.1 Lifecycle

```
request ──> pending ──decide(owner/delegate)──> active ──time passes──> expired
              │                                   │
              ├─deny──> denied                    ├─revoke──> revoked
              └─withdraw / lapse──> withdrawn     └─(emergency only) review ──> reviewed
emergency: activate ──> active(emergency) ──> expired|revoked ──> review pending ──> reviewed
```

1. **Request.** A principal files a request: target, reason tag, free-text reason (non-empty, enforced by the service, not by the caller), requested minutes, optional reference (a Kanban card or PR id as an `entry_ref`-style pointer, never required). A request on a GC also writes the `gc_access_request` row and a `system` event in that GC's log, exactly as doc 01 and doc 32 section 6 already specify; this module owns the decision record and the grant, doc 01 keeps `gc_access_request` as the GC-side reference. Agents may file a request; an agent's request needs a human decision and is never counted as human consent.
2. **Decision.** The owner is always an approver. Delegates come from the setting `access_grants.approvers` (mirrors `gc.dm_access_approvers`, doc 01). A requester never decides their own request. Approving or denying records a `Principal` and a decision note. Whether the other DM member may object is doc 32 Question 2; this module supports an `objection` row that blocks activation for `objection_window_minutes` (default 0, so no change from doc 32's current default until the owner answers).
3. **Activation.** Activation writes `starts_at`, `expires_at`, and the notices (5.4) in one transaction. Expiry is `min(requested, max_minutes)`. For a standard grant `max_minutes` defaults to 480 (Resibo's `max_duration_minutes`); the default duration is 60.
4. **Check.** `IAccessGrantChecker.CheckAsync` is a single indexed query whose predicate includes `expires_at > now()` and `revoked_at is null`. Expiry is enforced at check time, never by a sweep (Resibo `core/auth/break_glass/grant.py`). A sweep job exists only to mark rows `expired` for listings and to publish `stream.revoked` (section 9); a late or dead sweep cannot extend access by a second.
5. **Revocation.** The owner, a delegate, the requester (self-release) or the subject (if setting `access_grants.subject_may_revoke` is true; default false until the owner answers section 17 Q3) may revoke. Revocation takes effect on the next check and closes live streams.

### 5.2 Reason tags

Reason tags are a registry (rows, extensible, like GC kinds in doc 01), not an enum in code. Shipped tags: `support`, `incident`, `security-investigation`, `legal-hold`, `moderation`, `owner-delegate-review`, `onboarding`, `audit`, `other`. `other` requires at least a sentence of free text (setting `access_grants.other_min_chars`, default 20). Every request needs a tag and free text. Tags drive review routing and the pane's filters, and are written to the audit payload. A tag is a category, never a justification by itself.

### 5.3 Emergency (break-glass) mode

This is the Resibo break-glass shape, tightened for Bazaar's agent-heavy setting.

- **Who.** Only a human principal holding the capability `access.emergency` (a doc 48 capability; no built-in template holds it except `owner`; the owner may delegate it to, for example, a security reviewer role). Agents never hold it. This is a floor, not a setting.
- **How.** The holder activates without a prior decision. A step-up check (doc 48 section 8, biometric) is required and recorded. The reason tag must be in the emergency subset (`incident`, `security-investigation`, `legal-hold`, `other`).
- **Time box.** Default 30 minutes, ceiling 120 (`access_grants.emergency.max_minutes`). No renewal; a second activation is a new, separately reviewed emergency.
- **Rate.** Default 2 activations per principal per day (`access_grants.emergency.per_principal_per_day`).
- **Notice.** Subjects, the owner and every delegate are notified at activation, not after. If the notice sink fails, an emergency still activates (an incident must not be blocked by a broken channel), the grant is flagged `notice_failed`, and the failure raises a Kodo red state and a retry (setting `access_grants.emergency.on_notice_failure`: `proceed-flagged` default, `block` stricter).
- **After-the-fact review.** Every emergency creates a review item due in `access_grants.emergency.review_hours` (default 24). The owner or a delegate who is not the activator marks it `justified` or `unjustified` with a note. An overdue review is a Kodo amber state, then an owner alert at 2x. An `unjustified` review suspends the activator's `access.emergency` capability (setting `access_grants.emergency.suspend_on_unjustified`, default true), is audited, and is offered to the Psych Ward feed as a trust event [OPEN: whether it feeds Reputation, doc 11; section 17].
- **Reads are recorded in full.** Every read under an emergency grant writes an audit data event with the grant id (doc 48 section 5.1 already records reads; this adds `grant_id` to the payload).

### 5.4 Notification to the subjects

A "subject" is anyone whose data the grant exposes: both members of a DM, the owner of a Vault area (the agent for an agent's private memory), the members of a Thinking GC's parent working relationship. Rules:

- Subjects are notified when a request is filed, when it is decided, when it activates, and when it expires or is revoked. The owner is always notified of activation and expiry even when the owner is not a subject.
- The notice is a port, `IGrantNoticeSink`, with these adapters: a GC `system` event in the target GC (DM members see it, as doc 32 section 6 requires: "the grant is visible to both members"); an agent inbox row (doc 06 inbox, through the Agents abstractions) for an agent subject; and the content-free push provider (CLAUDE.md, Android push) for the owner's devices. No notice carries the content of what is read.
- A standard grant does not activate unless at least one subject notice has been written (`access_grants.notice_required`, a floor for standard grants; not a setting). A subject cannot be silently skipped.
- When the subject is the requester's only audience (the owner reading an agent's own private memory), the owner-read rule already applies (doc 18) and no grant is needed; the grants module is never consulted for the owner.

### 5.5 Contracts (sketch)

```csharp
namespace Bazaar.Server.AccessGrants;   // Bazaar.Server.AccessGrants.Abstractions

public enum GrantClass { Standard, Emergency }
public enum GrantState { Pending, Denied, Withdrawn, Active, Expired, Revoked }
public enum AccessMode { Read }

public readonly record struct AccessTarget(string Kind, string Id, long? FromSeq = null);

public sealed record AccessGrantRequest(
    AccessTarget Target, string ReasonTag, string Reason, int RequestedMinutes,
    string? Reference = null);

public sealed record AccessGrant(
    Guid GrantId, GrantClass Class, GrantState State, AccessTarget Target,
    Principal Requester, Principal? DecidedBy, string ReasonTag, string Reason,
    DateTimeOffset RequestedAt, DateTimeOffset? StartsAt, DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt, Principal? RevokedBy, bool NoticeFailed);

public interface IAccessGrantService
{
    Task<AccessGrant> RequestAsync(Principal requester, AccessGrantRequest request, CancellationToken ct);
    Task<AccessGrant> DecideAsync(Principal decider, Guid grantId, bool approve, string note,
                                   int? approvedMinutes, CancellationToken ct);
    Task<AccessGrant> ActivateEmergencyAsync(Principal holder, AccessGrantRequest request,
                                              StepUpProof proof, CancellationToken ct);
    Task<bool> RevokeAsync(Principal by, Guid grantId, string note, CancellationToken ct);
    Task<IReadOnlyList<AccessGrant>> ListAsync(GrantQuery query, CancellationToken ct);
    Task<IReadOnlyList<AccessGrant>> ListForSubjectAsync(Principal subject, CancellationToken ct); // all history, not only live
}

/// <summary>The hot path other modules call. Fails closed: any error is Denied.</summary>
public interface IAccessGrantChecker
{
    ValueTask<GrantVerdict> CheckAsync(Principal reader, AccessTarget target, AccessMode mode,
                                       SessionTaint taint, CancellationToken ct);
}
public sealed record GrantVerdict(bool Allowed, Guid? GrantId, long? FromSeq, DateTimeOffset? ExpiresAt);

public interface IGrantReviewService
{
    Task<IReadOnlyList<GrantReview>> PendingAsync(Principal reviewer, CancellationToken ct);
    Task ReviewAsync(Principal reviewer, Guid grantId, ReviewOutcome outcome, string note, CancellationToken ct);
}

public interface IGrantNoticeSink { Task NotifyAsync(GrantNotice notice, CancellationToken ct); }
public interface IAccessTargetKind { string Kind { get; } Task<IReadOnlyList<Principal>> SubjectsAsync(AccessTarget t, CancellationToken ct); }
```

`SessionTaint` is the doc 15 type; the checker denies when the reading session is hard-tainted (a floor; section 11). `StepUpProof` wraps the doc 48 section 8 device assertion. The subject list comes from the target-kind registry so a module adding a target kind states who must be told.

### 5.6 Data (SQL sketch, namespace `access_grants`)

```sql
-- src/Bazaar.Server.AccessGrants/Data/Sql/access_grants/0001_grants.sql
create table access_grant (
  grant_id      uuid primary key,
  class         text not null check (class in ('standard','emergency')),
  state         text not null check (state in ('pending','denied','withdrawn','active','expired','revoked')),
  target_kind   text not null,
  target_id     text not null,
  from_seq      bigint,
  requester     text not null,              -- Principal canonical text, kind:id
  decided_by    text,
  reason_tag    text not null references access_reason_tag(tag),
  reason        text not null check (length(btrim(reason)) > 0),
  reference     text,
  requested_at  timestamptz not null,
  starts_at     timestamptz,
  expires_at    timestamptz,
  revoked_at    timestamptz,
  revoked_by    text,
  notice_failed boolean not null default false,
  check (state <> 'active' or (starts_at is not null and expires_at is not null))
);
create index access_grant_check on access_grant (requester, target_kind, target_id, expires_at)
  where state = 'active' and revoked_at is null;

create table access_reason_tag (tag text primary key, emergency_ok boolean not null, description text not null);

create table access_grant_subject (grant_id uuid references access_grant, subject text not null,
  notified_at timestamptz, notice_ref text, primary key (grant_id, subject));

create table access_grant_review (grant_id uuid primary key references access_grant,
  due_at timestamptz not null, reviewer text, outcome text check (outcome in ('justified','unjustified')),
  note text, reviewed_at timestamptz);
```

`reason` is free text that may name a person; it is stored in an encrypted personal field under the per-person key rules of doc 04 where it does, per the encrypted-personal-field rule in CLAUDE.md. The grant row is never rewritten after activation except `state`, `revoked_*` and the review row; the database role has no delete.

## 6. The groups module

### 6.1 What it is

A group is a named set of principals that owns a set of resources. A group manager (a per-membership flag, not a system-wide role) sees the group's resources and its member list without a grant. Visibility ends exactly at that group's resources. This restates Resibo `docs/apis/v3-deepdive-41-groups.md` sections 4 and 4.1.

Examples in Bazaar: a "web team" group whose manager reads the team's working GCs and Kanban board; a group of agents working one product; a group of coworkers on a project. Membership is many-to-many, as Resibo section 11 resolves.

### 6.2 What changes from Resibo

- Resibo stamps a `group_id` column on each receipt row at ingestion through one chokepoint (`get_effective_group`). Bazaar modules may not write each other's tables (BAZAAR-COMPONENTS section 5), so the Groups module owns a `group_resource(group_id, resource_kind, resource_id)` table instead. A resource kind registers itself as `groupable` through `IGroupableKind`, and its creating module calls `IEffectiveGroupResolver` once at the creation chokepoint to learn the active group (the user's explicitly set active group, defaulting to the most recently used, as Resibo resolves). The pattern and the "one chokepoint" principle are Resibo's; the storage is Bazaar's.
- Resibo's gate is "owner/staff or that group's manager". Bazaar's gate is the `Principal` plus, optionally, a role scope `group:<id>` (section 6.4). There is no `staff` role in Bazaar; the owner's all-read rule already covers the system-wide case.
- **Floors for groupable kinds.** DMs, Thinking GCs, agent private memory, Passport and credential material, and personal fields are never groupable. They stay behind the owner rule and the grants module. Registering them as groupable is rejected at registration and by an architecture test. This is the privacy floor that keeps Groups from becoming a back door around doc 01 and doc 18.
- **Agents.** An agent may be a member. An agent is never a group manager (a manager reads human members' resources; doc 48 floor "agents never hold `admin.*`" is applied by analogy; PROPOSED).

### 6.3 Contracts (sketch)

```csharp
namespace Bazaar.Server.Groups;   // Bazaar.Server.Groups.Abstractions

public sealed record Group(Guid GroupId, string Name, string Kind, Principal CreatedBy, DateTimeOffset CreatedAt);
public sealed record GroupMembership(Guid GroupId, Principal Member, bool IsManager,
                                     DateTimeOffset JoinedAt, Principal AddedBy);

public interface IGroupAdmin
{   // owner or a delegate holding admin.groups; every call audited
    Task<Group> CreateAsync(Principal by, string name, string kind, CancellationToken ct);
    Task AddMemberAsync(Principal by, Guid groupId, Principal member, CancellationToken ct);
    Task RemoveMemberAsync(Principal by, Guid groupId, Principal member, CancellationToken ct);
    Task SetManagerAsync(Principal by, Guid groupId, Principal member, bool isManager, StepUpProof proof, CancellationToken ct);
}

/// <summary>Live and uncached. Fails closed. Called per read, not per session.</summary>
public interface IGroupVisibility
{
    ValueTask<bool> CanSeeAsync(Principal reader, string resourceKind, string resourceId, CancellationToken ct);
    ValueTask<bool> IsManagerAsync(Principal who, Guid groupId, CancellationToken ct);
    IAsyncEnumerable<Group> GroupsManagedByAsync(Principal who, CancellationToken ct);
}

public interface IGroupMembershipReader   // for role scopes "group:<id>" (doc 48)
{
    ValueTask<bool> IsMemberAsync(Principal p, Guid groupId, CancellationToken ct);
}

public interface IEffectiveGroupResolver
{
    ValueTask<Guid?> GetEffectiveGroupAsync(Principal creator, CancellationToken ct);
    Task SetActiveGroupAsync(Principal who, Guid groupId, CancellationToken ct);
}

public interface IGroupableKind { string Kind { get; } }   // registered by the owning module
public interface IGroupResourceBinder
{
    Task BindAsync(Guid groupId, string resourceKind, string resourceId, CancellationToken ct);
    Task UnbindAsync(Guid groupId, string resourceKind, string resourceId, CancellationToken ct);
}
```

Doc 48 says a role change that widens a grant on a human needs the owner, signed approval and a biometric check. `SetManagerAsync` is an access elevation of the same class, so it requires the owner's step-up proof, and writes its own audit event (Resibo section 11 resolves the same: owner/staff only, audited). Managers can see the member list and the group's resources; they cannot add or remove members (Resibo `permission_gate.py`: visibility, never membership authority).

### 6.4 Roles, scopes and the live re-check

Doc 48 role scopes include "an agent group". The Groups module exposes `IGroupMembershipReader` so a role grant scoped `group:<id>` resolves membership through the Groups contract, not through a copied table. Groups holds no capability and never decides what a member may do; it answers "is this principal in this group" and "does this group own this resource".

"Persistent" means no timer, not no re-check. `CanSeeAsync` reads current rows on every call (no cache anywhere in the module, Resibo `core/groups/permission_gate.py`). On a membership removal or a manager flip, the module publishes `GroupVisibilityChanged`, and the GC module closes affected streams with `stream.revoked` (doc 02). A removed manager is denied on the next call even without the event.

### 6.5 Data (SQL sketch, namespace `groups`)

```sql
-- src/Bazaar.Server.Groups/Data/Sql/groups/0001_groups.sql
create table grp (group_id uuid primary key, name text not null, kind text not null,
  created_by text not null, created_at timestamptz not null, archived_at timestamptz);
create table grp_member (group_id uuid references grp, member text not null,
  is_manager boolean not null default false, joined_at timestamptz not null, added_by text not null,
  primary key (group_id, member));
create table grp_resource (group_id uuid references grp, resource_kind text not null,
  resource_id text not null, bound_by text not null, bound_at timestamptz not null,
  primary key (resource_kind, resource_id, group_id));
create table grp_active (member text primary key, group_id uuid references grp, set_at timestamptz not null);
create index grp_manager_idx on grp_member (member) where is_manager;
```

## 7. The sign-in policy module

Resibo's passwordless-only rule and its 2FA floor are policy, so they sit in a module of their own rather than inside Remote (doc 32) or the Identity Service (doc 14): `Bazaar.Server.SignInPolicy` supplies one `ISignInPolicy` that Remote, the interactive sign-in page and the web admin consult. This keeps both existing modules unchanged and satisfies "provider and policy are separate".

### 7.1 Tenancy mode as `Scope`

Resibo's `tenancy_mode: single|multi` becomes a `Scope`, because Bazaar has an installation, companies inside it, and people:

```csharp
public enum ScopeKind { Install, Tenant, User }
public readonly record struct Scope(ScopeKind Kind, string Id);   // Install: "install"; Tenant: company id; User: Principal id

public sealed record InstallShape(bool MultiHuman, bool PubliclyReachable);   // derived, see below
```

An install with one human (owner) is "single": the multi-user machinery below is structurally skipped, not defaulted to always-owner (Resibo `core/auth/tenancy.py`, section 6.2 of the Auth deep-dive). With a second human it is "multi". The `Tenant` scope is the company; a policy value may be set at any scope and resolves most-specific-first, but a floor is computed at the `Install` scope and no lower scope may go below it. This is the same "may tighten, never loosen" direction the configurability rule requires.

`Scope` here is a policy scope. It is not doc 48's role scope list (instance, company, GC, GC tag, module, repository, agent group); a `Tenant` policy scope equals doc 48's "company" and `Install` equals "instance". Whether one shared `Scope` type should live in `Bazaar.Core` is OPEN (section 17).

### 7.2 The floors

1. **No password credential exists.** Sign-in is a passkey, a device-bound refresh credential (doc 32 section 5), or a biometric device assertion (doc 48 section 8). The module defines a closed set of `SignInFactor` kinds with no password member, and an architecture test fails on a password-hash type, a password field in a sign-in view model, or a "confirm with your password" step-up. Step-up uses the same factors, never a memorised secret. This is Resibo `v3-deepdive-05` section 4 and its "no-password-fallback-anywhere" test, applied to Bazaar. A floor, not a setting. I read doc 32 and doc 48 and found no password flow in them; I did not search every doc, so this is PROPOSED until the owner confirms (section 17).
2. **Second-factor floor when publicly reachable and multi-human.** When `MultiHuman` and `PubliclyReachable`, every principal that holds an elevated capability must sign in with two distinct factor classes. "Elevated" is any principal holding `admin.*`, `access.emergency`, a role with human approval, or any group-manager flag (computed from roles, not a role name). The policy ladder is Resibo's `TwoFactorPolicy`: `optional`, `required_for_elevated`, `required_for_all`. The floor on a public multi-human install is `required_for_elevated`. An explicit setting below the floor is rejected at validation with a message (never silently raised, Resibo `enforce_policy_floor`); a setting above is accepted. The floor lifts only when the install genuinely stops being publicly reachable, not by config alone.
3. **Bazaar's biometric rule still applies on top.** Doc 48 section 8 already requires biometric step-up on a new device and for high-risk actions. Those are not weakened by `optional`. A passkey with user verification plus a device biometric assertion counts as two factor classes (doc 48 section 8 already pairs them), so a conforming owner device satisfies the floor without extra setup.
4. **Public reachability is detected, not just declared.** Resibo reads a `public_facing` flag once from config. Bazaar's exposure is real state: Conduit (doc 39) knows whether a tunnel or public listener is up. `signin.policy.public_facing` is `auto|true|false`; the effective value is `declared OR detected`, so declaring `false` while Conduit reports a public path does not lower the floor. Detection failure resolves to `true` (fail closed).
5. **Session lifetime.** Resibo shortens sessions on public installs (30 to 7 days). Doc 32 section 5 already has refresh credentials (30-day sliding, 90 absolute) as `auth.*` settings. This module adds no new lifetime key; it only supplies a tightening: when the install is publicly reachable, the effective refresh sliding window is `min(configured, signin.policy.public_refresh_days)` (default 7).
6. **Role change revokes sessions.** Resibo requires a role change to call `revoke_all_for_user` because the role is snapshotted at session creation. Bazaar's doc 32 section 5 checks a device revision on connect and refresh, and doc 48 says role changes take effect at the next request. The module therefore only states the invariant as a test: a role change, a manager flip or an emergency-capability change must bump the device revision so the next request re-reads. No new mechanism.

### 7.3 Contracts (sketch)

```csharp
public enum SignInFactor { Passkey, DeviceBiometric, HardwareKey, RefreshCredential /* no password member, by design */ }
public enum SecondFactorPolicy { Optional, RequiredForElevated, RequiredForAll }

public interface ISignInPolicy
{
    SecondFactorPolicy Effective(Scope scope);                  // never below Floor
    SecondFactorPolicy Floor(Scope scope);
    bool SecondFactorRequired(Principal who, ElevationFacts facts, Scope scope);
    PolicyValidation Validate(SignInPolicySettings proposed);   // rejects loosening, returns reasons
}
public interface IInstallShapeSource { ValueTask<InstallShape> GetAsync(CancellationToken ct); }  // Conduit adapter, fail closed
```

### 7.4 Data (SQL sketch, namespace `signin_policy`)

```sql
create table signin_policy_value (scope_kind text, scope_id text, key text, value jsonb not null,
  set_by text not null, set_at timestamptz not null, primary key (scope_kind, scope_id, key));
create table signin_policy_decision (id bigserial primary key, ts timestamptz not null, principal text not null,
  scope_kind text not null, outcome text not null, reason text not null);   -- why a sign-in demanded a second factor or was refused
```

## 8. Settings registry rows

Safe defaults per the configurability rule. "Floor" means a setting may tighten and never loosen below it. Keys are PROPOSED.

| Key | Type | Default | Scope | Who may change | Floor or approval |
|---|---|---|---|---|---|
| `access_grants.approvers` | list of Principal | owner only | install | owner | Owner is always an approver; list may add, never remove the owner |
| `access_grants.standard.default_minutes` | int | 60 | install | operator (owner) | Must be at most `max_minutes` |
| `access_grants.standard.max_minutes` | int | 480 | install | operator (owner) | Upper bound 10080 (7 days); tighten freely |
| `access_grants.dm.standing_allowed` | bool | false | install | operator (owner) | Doc 32 describes a read-forward grant with no stated expiry; true permits a standing DM grant. Open question 1 |
| `access_grants.other_min_chars` | int | 20 | install | operator (owner) | Min 1 |
| `access_grants.objection_window_minutes` | int | 0 | install | operator (owner) | Doc 32 Q2 pending |
| `access_grants.subject_may_revoke` | bool | false | install | operator (owner) | Open question 3 |
| `access_grants.emergency.max_minutes` | int | 30 | install | operator (owner) | Ceiling 120 |
| `access_grants.emergency.per_principal_per_day` | int | 2 | install | operator (owner) | Min 1 |
| `access_grants.emergency.review_hours` | int | 24 | install | operator (owner) | Ceiling 168 |
| `access_grants.emergency.on_notice_failure` | enum `proceed-flagged`, `block` | `proceed-flagged` | install | operator (owner) | |
| `access_grants.emergency.suspend_on_unjustified` | bool | true | install | operator (owner) | |
| `groups.enabled_kinds` | list | `team`, `agents`, `project` | install | operator (owner) | Cannot include a non-groupable kind (floor) |
| `groups.manager_requires_stepup` | bool | true | install | operator (owner) | Floor: true when `MultiHuman` |
| `signin.policy.second_factor` | enum `auto`, `optional`, `required_for_elevated`, `required_for_all` | `auto` | install, tenant | operator (owner) | `auto` resolves to the floor; below the floor is rejected |
| `signin.policy.public_facing` | enum `auto`, `true`, `false` | `auto` | install | operator (owner) | Effective = declared OR detected |
| `signin.policy.public_refresh_days` | int | 7 | install | operator (owner) | Min 1 |

Every change is audited with the `Principal`, the old and new value and the time, and changes to rows marked "floor" need the owner's step-up proof.

## 9. Interaction with existing modules

| Module | How it connects | What it reuses |
|---|---|---|
| `gc` (doc 01, 02) | GC read access and the subscribe check ask `IAccessGrantChecker` when membership and the owner rule deny; `gc_access_request` stays in doc 01 and gets a `grant_id` reference; the grant publishes `stream.revoked` on expiry or revocation | `gc_access_request`, `system` events, `stream.revoked`. This doc adds nothing to the GC log schema. **Seam (GC work package):** the exact hook in the GC read-access service is OPEN (section 17). The repo audit `docs/audit/DRIFT-2026-11-16.md` item 4 shows the read-access code has an owner/DM drift of its own; fix that first |
| `vault` (doc 18) | The Vault access layer asks the checker for `vault_area` targets that its own rules deny; groupable Vault areas use `IGroupVisibility` | The Vault access layer; sparse checkout stays: a grant does not put another agent's private memory into a workspace worktree (grants serve reads through the Vault service only) |
| `approvals` (doc 32 5a, `Bazaar.Approvals.Abstractions`) | Owner decisions and `SetManagerAsync` verify an owner device signature with `IOwnerSignatureVerifier` and `OwnerSignedPayload` (a new `action` value, `access.grant.decide`, `groups.manager.set`) | The signature layout, nonce store and key registry. No approval row, tier or request queue is shared. Grants are not Tier 2 approvals (doc 15 gates outward actions only) |
| `audit` (doc 48 5, `Bazaar.Audit.Abstractions`) | All events in section 10 through `IAuditLog.AppendAsync`; reads under a grant carry `grant_id` | The chain, checkpoints, role-scoped views |
| `secrets` / Identity (doc 14, `Bazaar.Secrets`) | No access: credentials, Passport and key material are never a grant target (floor). `Bazaar.Secrets` is read only for the owner-key registry path through Approvals | Nothing |
| `remote` (doc 32) | Consults `ISignInPolicy` on sign-in and refresh; its tokens, rate limits and device revision are unchanged | Sign-in, tokens, revocation |
| `conduit` (doc 39) | Supplies `IInstallShapeSource` input (a public path is up) | Probe results |
| `kodo` (doc 46) | Each module implements `IModuleHealthContract`; Kodo shows the pane's "pending reviews" as a signal | Module health contract |
| `agents` (doc 06) | Agent subject notices go to the inbox through the Agents abstractions; agents may request, never decide | Inbox |
| `web-admin` (doc 51) | Its Users section can list DM access requests through `IAccessGrantService.ListAsync`; the sign-in section reads `ISignInPolicy` | Port-per-section model |
| Roles (doc 48 4, PLANNED) | Capabilities `access.request`, `access.decide`, `access.emergency`, `access.review`, `admin.groups` are added to the capability list; scope `group:<id>` resolves through `IGroupMembershipReader` | The role table and its audit |

Hard-taint rule (floor): the checker receives the session's taint. A hard-tainted session is denied a grant read, because a manipulated session could otherwise be steered into reading private data. This is the one place the two systems touch, and it uses doc 15's taint state without sharing its tables.

## 10. Audit events (all carry a `Principal`)

Written through `IAuditLog.AppendAsync` with `actor` as the canonical `kind:id` text (BAZAAR-COMPONENTS section 4.2). Payloads carry ids and tags, never content.

| Event kind | Actor | Payload highlights |
|---|---|---|
| `access.request` | requester | grant id, target, reason tag |
| `access.decide` | decider | approve or deny, note id, signed-approval id |
| `access.activate` | system (`system:access-grants`) or decider | starts, expires, subject list |
| `access.notice` | `service:access-grants` | subject, sink, result |
| `access.read` | reader | grant id, object id and type, result count, taint tags |
| `access.expire` | `system:access-grants` | grant id (also emitted when check-time expiry is first noticed) |
| `access.revoke` | revoker | grant id, who, note |
| `access.emergency.activate` | holder | tag, minutes, step-up proof id, notice failure |
| `access.emergency.review` | reviewer | outcome, note id, suspension applied |
| `access.denied` | reader | target, reason (no grant, tainted, expired); sampled when the rate is high |
| `groups.create`, `groups.member.add`, `groups.member.remove` | admin | group, member, added_by |
| `groups.manager.set` | admin | group, member, flag, step-up proof id |
| `groups.resource.bind`, `groups.resource.unbind` | binder | group, kind, id |
| `groups.read` | reader | group id, object id and type (manager reads) |
| `signin.policy.change` | operator | key, old, new, floor result |
| `signin.policy.decision` | `system:signin-policy` | principal, outcome, factor classes seen |

## 11. Security and privacy

- **Fail closed.** The checker and `IGroupVisibility` deny on any exception, unresolved principal, missing dependency or unknown target kind. "Cannot tell" and "not permitted" return the same answer (Resibo `core/groups/permission_gate.py`, `DenyAllSessions`). With the module missing, nothing is granted.
- **Humans only for consent.** Agents file requests but never decide, never hold `access.emergency`, and are never group managers. An agent's request or approval is never human consent (doc 32 section 6). A decision identity comes from the platform-signed device assertion, never from message text.
- **Self-decision is refused.** A requester is never the decider of their own standard request. An owner needs no grant for their own all-read rule; the owner's DM read remains subject to `privacy.owner_dm_read_marker` (doc 32 Q8), unchanged by this doc.
- **Existence is not leaked.** A denied reader gets the same shape of response for "no such target" and "no access", as doc 18 does for unresolved private links.
- **Scope of exposure.** A grant to a GC is read-forward from `from_seq` unless the decider widens it to history; widening is the more protective of the two defaults being narrow, and needs the decider's explicit choice and an audit note.
- **Personal data.** The `reason` text and any member names are encrypted personal fields (doc 04, doc 32 section 9). Erasure destroys the key, never breaks the chain (doc 48 section 5.5). The Constellation or other graph views must apply `IAccessGrantChecker` to derived projections, as doc 19 says for Vault access.
- **Privacy law.** Reading a coworker's DM is the employer-monitoring question doc 32 already flags for counsel. This module makes each such read visible to the subject. It does not decide legality. Group manager visibility over employees' resources has the same counsel dependency.
- **Replay and forgery.** The nonce store and expiry in `OwnerSignedPayload` already stop a replayed decision; nothing new is added.
- **Clock.** Expiry uses the database clock (`now()`), not the app clock, so a skewed server process cannot extend a grant.
- **What the pane cannot do.** The pane only calls server APIs. A client never decides locally and never caches a verdict.

## 12. What is taken from Resibo, and what is changed

| Resibo item (path) | Taken | Changed for Bazaar, and why |
|---|---|---|
| Break-glass grant: time-boxed, reason-tagged, one staff/one client pair (`core/auth/break_glass/grant.py`; `docs/apis/v3-deepdive-05-auth-tenancy-api.md` sections 3, 6.3) | The grant object, required non-empty reason, default 60 and max 480 minutes, expiry at check time not by sweep, early revoke, history visible to the subject, audit sink | Target is a generic `AccessTarget` (GC, Vault area, audit view), not a client folder. Requester and decider are `Principal`s, not `staff_user_id`. A decision step is added (Resibo's Auth is "ledger, not workflow"; Bazaar needs the workflow because the DM request flow is OPEN). A reason tag registry is added. Audit sink injection is replaced by the shared `IAuditLog`. |
| Dual notification (`PRINCIPLES.md` section 4.3, Auth deep-dive section 6.3) | Subject and owner both told; never silent | Notice goes through a port with GC-event, inbox and push adapters. Standard grants refuse to activate with no notice written. |
| Sweep job registered in Background Workers (Auth deep-dive section 6.3) | Sweep exists for tidiness only | Bazaar's job runner (`Bazaar.Server.Jobs`, seen in doc 40) hosts it; it also publishes `stream.revoked`. |
| (none) Emergency mode | Concept of "break-glass" in the name only | Resibo's grant is already the emergency path for staff. Bazaar splits a self-activated emergency class with after-the-fact review, a rate limit and capability suspension, because Bazaar has non-owner humans and agents and needs one path with a prior decision and one without. This is the "separate thing" the owner's direction allows. |
| Groups (`core/groups/*`; `v3-deepdive-41-groups.md`) | Group, membership with `is_manager`, multi-group, active-group selector, manager sees only own group, owner/admin-only manager flips with audit, live re-check with no cache, three-shape separation, tests (isolation by default, manager scope, live re-check) | `group_resource` table replaces the receipt column; non-groupable floor for DMs and private memory; agents are never managers; `is_group_manager` flips need step-up; role scopes consume membership through a reader. |
| Third access shape table (`v3-deepdive-41-groups.md` section 2) | Used verbatim as section 4.1's argument | Rendered as a table with Bazaar's audit and notice rows. |
| Passwordless only; four methods; step-up with the same methods (`v3-deepdive-05` section 4) | The rule and the no-password test | Factor set is Bazaar's (passkey, device biometric, hardware key, refresh credential); Resibo's SSO, email OTP and SMS OTP are not adopted (see below). |
| 2FA policy ladder and floor when `public_facing` (`core/auth/tenancy.py`, `v3-deepdive-05` section 4.6.1) | `optional`, `required_for_elevated`, `required_for_all`; floor on public multi-user installs; explicit-below-floor is an error, not a silent raise | `public_facing` is detected from Conduit, not only declared. "Elevated" is derived from capabilities, not role names. Biometric step-up (doc 48 section 8) already exceeds Resibo's floor on a new device and for high-risk actions. |
| `tenancy_mode` single\|multi short-circuit (`core/auth/tenancy.py`, `v3-deepdive-05` section 6.2) | Multi-user machinery structurally skipped on a one-human install | Expressed as `Scope` (Install, Tenant, User) with most-specific resolution and an Install-scope floor. |
| Session TTL by exposure (`v3-deepdive-05` sections 10, 12) | Shorter lifetime when public | Applied as a `min` over doc 32's existing refresh window; no new session system. |
| Role snapshot needs session revocation on change (`v3-deepdive-05` section 5.3) | The invariant | Expressed as a device-revision test, since Bazaar uses refresh credentials with a revision check (doc 32 section 5). |
| Frontend auth flow, cookies, CSRF, route guards, cross-tab broadcast (`v3-deepdive-47-frontend-auth-session.md`) | Principle: guards are UX, the server is the boundary; "remember where I was" redirect after sign-in; react to expiry globally in one place | **Not adopted as built:** Bazaar has no web client and no cookies (doc 32 section 5, CLAUDE.md), so no cookie session, SameSite or CSRF token applies, except on the narrow sign-in page (doc 32 section 4), which keeps its own rules. The client pane applies the principle (section 13); the BroadcastChannel multi-tab pattern maps to the pane registry's shared server-stream event, not a browser API. |
| OIDC/SSO, email OTP, SMS OTP providers (`v3-deepdive-05` sections 4.2 to 4.4) | Recorded, not designed here | Bazaar sign-in is device-bound (doc 32). As optional auth-method providers they would be separate provider modules on `ISignInPolicy`'s factor set; not v1, and not owner-requested. OPEN question 7. |

## 13. The client pane

`Bazaar.Client.AccessGrants` (id `client-access-grants`, a `client-pane` module) references `Bazaar.Client.Core` and `Bazaar.Contracts` only, through `IPaneModule`/`PaneRegistry` (doc 41, 41b). Wire messages for requests, decisions, reviews and group lists are namespaced `AccessGrants` and `Groups` in `Bazaar.Contracts`. The mnemonic for opening the pane and its command-palette entries are proposals; the mnemonic table is owned by doc 41 and no Ctrl chord is used.

Views:
- **My requests:** file a request on a GC or Vault area (reason tag picker, required text, minutes), see state, withdraw.
- **To decide:** pending requests with reason, requester, target kind, and what the decision will show to the subjects, with a decide action that triggers the step-up prompt. Only visible to approvers.
- **Active grants:** live grants with a countdown, revoke, and a link to the audited reads under that grant.
- **Emergency:** activate (shows rate left and the review deadline), and **Reviews due** with justified/unjustified.
- **About my data:** for any principal, every grant that has touched their data, live or past, and who decided it (Resibo's `list_for_client` principle: the subject sees the whole history, not only what is live).
- **Groups:** a two-level screen (groups, then members and resources) with manager toggles, as Resibo needed its own screen for the two-level structure (`v3-deepdive-41-groups.md` section 11, TUI). Visible by role: managers see their own groups; the owner sees all.
- **Sign-in policy:** read-only effective values and the floor, with a message when a proposed change would loosen.

Accessibility (doc 27, 41): every view is keyboard-complete, labelled for a screen reader through `Bazaar.Accessibility` (`AccessibleName`), and reads countdowns as text. The pane never decides locally: a stale client that shows a grant as live still fails at the server check. Deep-link, "remember where I was" and a shared expiry/revocation event (from the stream) close the pane's active views and return to the previous pane. Android renders the same pane in its phone shell; no feature code lands in `Bazaar.Client.Core`.

## 14. Kodo health contract

Each server module implements `IModuleHealthContract` (`Bazaar.Server.Modules.Abstractions`, doc 46): `LivenessAsync` (no I/O), `ReadinessAsync` and `DependenciesAsync`. Module ids: `access-grants`, `groups`, `signin-policy`.

| Module | Readiness checks | Degraded when | Failing when |
|---|---|---|---|
| `access-grants` | A synthetic expired grant is denied by `CheckAsync` (the check-time expiry canary); the audit seam answers; the notice sink answers | Overdue emergency reviews; `notice_failed` grants present; a soft dependency (`gc`, `vault`) missing | Canary allows an expired grant; the checker throws; audit unavailable (a grant cannot be recorded) |
| `groups` | A just-removed test membership is denied on the next call (live re-check canary); a non-groupable kind is rejected | A groupable kind has unbound resources older than a setting | Live re-check canary passes access it should deny |
| `signin-policy` | `Effective` is never below `Floor` for every scope tested; `IInstallShapeSource` answers | Install shape detection failing (resolves to public, fail closed) | A settings row below the floor is loaded; a password factor kind is registered |

Canaries run against reserved test principals that own no data. Kodo reads the counts (pending decisions, active grants, overdue reviews) as signals, never the contents.

## 15. Testing (xUnit v3; Avalonia.Headless for the pane)

Each test class lives with its module (`tests/Bazaar.Server.AccessGrants.Tests`, `Bazaar.Server.Groups.Tests`, `Bazaar.Server.SignInPolicy.Tests`, `Bazaar.Client.AccessGrants.Tests`). Architecture tests extend `tests/Bazaar.Architecture.Tests`.

- **Grants:** empty or whitespace reason rejected; duration above max rejected; a requester cannot decide their own request; an agent cannot decide and cannot activate emergency; an expired grant is denied at the instant of expiry with the sweep never run; revocation takes effect on the next check; hard-tainted reader denied; emergency rate limit, review due date and capability suspension; standard grant refuses activation without a notice; emergency with notice failure activates and is flagged; subject history includes expired and revoked grants; every read writes `access.read` with `grant_id`; check uses the database clock under a skewed app clock.
- **Groups:** a user in no group sees no change (isolation by default); a manager's visibility stops at their own group; a removal denies the very next call (live re-check); DM, Thinking GC and private-memory kinds cannot be registered groupable; agent cannot be manager; manager flip needs a step-up proof; managers cannot add or remove members.
- **Sign-in policy:** no password member exists in `SignInFactor`; an architecture test fails on a password-hash type or field; `Effective < Floor` rejected; floor applies on public multi-human and not on single; effective public is `declared OR detected`, and detection failure means public; policy at a lower scope cannot go below the Install floor; role change bumps the device revision.
- **Cross-module:** GC read of a DM by a non-member is denied without a grant and allowed under one, and `stream.revoked` fires at expiry; Vault private-area read follows the same pattern; the module graph validates with each module disabled (grants disabled means no grants, not an error).
- **Pane (headless):** the "To decide" view shows only for approvers; countdown text updates; step-up prompt triggers on decide; an expired grant removes its view; screen-reader names exist for every control.
- **Seeded violations:** the architecture rules in BAZAAR-COMPONENTS section 6 each get a seeded violation for these projects, as the existing pattern does.

## 16. Work packages

Small, independent, each its own project or file with tests. Order is by dependency.

| WP | Content | Needs |
|---|---|---|
| 72-1 | `Bazaar.Server.AccessGrants.Abstractions` (section 5.5 types, reason tag registry types) | Principal in `Bazaar.Core` |
| 72-2 | `Bazaar.Server.Groups.Abstractions` (section 6.3 types) | Principal |
| 72-3 | `Bazaar.Server.SignInPolicy.Abstractions` (`Scope`, `ISignInPolicy`, factors) | Principal |
| 72-4 | `access_grants/0001` migration and Postgres repository | 72-1 |
| 72-5 | `IAccessGrantChecker` with check-time expiry and fail-closed tests | 72-4 |
| 72-6 | Request, decide and revoke services, with approver rules and signature verification through Approvals | 72-4, Approvals abstractions |
| 72-7 | Emergency class, rate limit, review queue, suspension | 72-6 |
| 72-8 | Notice sink port and the GC `system` event adapter | 72-6, GC abstractions |
| 72-9 | Agent inbox and push adapters for notices | 72-8 |
| 72-10 | Expiry job: marks rows, publishes revoke events | 72-5 |
| 72-11 | `groups/0001` migration and repository | 72-2 |
| 72-12 | `IGroupVisibility`, groupable kind registry, non-groupable floor and architecture test | 72-11 |
| 72-13 | `IEffectiveGroupResolver`, active-group selector | 72-11 |
| 72-14 | `IGroupMembershipReader` role-scope adapter | 72-11, roles module |
| 72-15 | `ISignInPolicy`, floors, validation, Conduit adapter, no-password architecture test | 72-3 |
| 72-16 | Settings registry rows and audit events for all three modules | 72-4, 72-11, 72-15 |
| 72-17 | Kodo health contracts and canaries | 72-5, 72-12, 72-15 |
| 72-18 | GC integration (DM access through the checker) | 72-5, GC read-access fix |
| 72-19 | Vault integration (area access through the checker; groupable areas) | 72-5, 72-12 |
| 72-20 | `Bazaar.Client.AccessGrants` pane, headless tests | 72-6, 72-12 |
| 72-21 | Web admin sections (ports) | 72-6, doc 51 |

## 17. Open questions

1. **Standing DM grants.** Doc 32 section 6 describes a DM grant read-forward from `seq` with no stated expiry. This doc proposes every grant has a time box and a renewal, with `access_grants.dm.standing_allowed` (default false) to permit a standing grant. Which does the owner want? A standing DM grant is the shape Resibo warns about; the alternative is a group of kind `dm-review`, which is rejected here for DMs.
2. **Other DM member approval and objection** (doc 32 Q2, doc 01 Q4). Default stays: the owner approves; the other member is notified and may object. `objection_window_minutes` is 0 until answered.
3. **May a subject revoke a grant over their own data?** Default no (`subject_may_revoke` false). Owner to decide.
4. **Write grants.** Not designed. Does any case need one, for example an owner delegate fixing a Vault page?
5. **Decryption path.** DM bodies are encrypted under per-person keys (doc 04 Q2). How the server decrypts for a grant holder belongs to docs 04 and 14; this doc assumes a server-side path that audits per read.
6. **Review feeding Reputation or Psych Ward.** Should an `unjustified` emergency review count against reputation (doc 11) or go to the Psych Ward feed? Left OPEN.
7. **Resibo's SSO, email OTP and SMS OTP providers.** Not adopted. If wanted for hosted or multi-human installs, they would be provider modules behind the factor set. Not a v1 item unless the owner asks.
8. **`Scope` type placement and the "agent group" role scope.** Whether one shared `Scope` belongs in `Bazaar.Core`, and whether doc 48's "agent group" scope becomes a `groups` row of kind `agents`.
9. **Owner confirmation that no password path exists.** This doc found none in docs 32 and 48 and makes absence a floor. A bootstrap or recovery flow needing a memorised secret would contradict it; doc 48 section 7 uses hardware keys, not passwords.
10. **Sign-in policy as a third module.** The owner named two modules; the sign-in policy was put in a third so Remote and the Identity Service stay unchanged. Merge it into one of the two only if the owner prefers fewer modules.
11. **Counsel.** The owner reading, and an employer's manager seeing, coworkers' communications. Same counsel list as doc 32 section 9.
12. **Capability names.** `access.request`, `access.decide`, `access.emergency`, `access.review`, `admin.groups` are proposed additions to doc 48's capability list; the owner owns that list.
