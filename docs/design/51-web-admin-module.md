# Web admin panel (admin-only module)

Status: DECIDED for the rule and scope (owner, 2026-11-16: "Allow admin-only web panel"). PLANNED for mechanics. Module names (module sides rule, owner 2026-11-17): `Bazaar.Server.WebAdmin` and `Bazaar.Server.WebAdmin.Abstractions`, component id `web-admin` [PROPOSED; the earlier provisional `Bazaar.WebAdmin` is dropped]. Section 5 holds the mechanics, all PROPOSED.
Depends on: 02 (HTTP surface), 03 (listeners, bind floor), 15 (approvals, audit), 32 (roles, passkey sign-in page), 37 (company build), 41 (module selection and rebuild), 46 (Kodo health) · Used by: CLAUDE.md, 45 (setup wizard hand-off)

## Purpose
An admin-only web panel the server hosts for administration and health: users, Kodo health, module selection and rebuild, and similar admin views. It sits beside the in-client admin views, which admin accounts see inside the apps. It is a web page for admins, served by the server. Clients stay native, as already built (CLAUDE.md, doc 27). The owner removed the earlier "no web client" rule on 2026-11-17, because it was an agent's paraphrase (DECISIONS-LOG round 26), so this panel needs no exception.

## Decided inputs
- **Panel form (owner, 2026-11-16).** An admin-only web panel, written as plain HTML, CSS and JavaScript. The read-only status page (doc 02, doc 23) and the narrow sign-in page (doc 32 section 4) keep their existing scope.
- **Same permissions as the apps.** An admin sees and does what the same account can do in a client. Every action goes through the same authorization checks and the same audit log (doc 15). The panel adds no privilege.
- **Sign-in.** Admins sign in through the existing passkey page (doc 32 section 4, route `/signin`). That page is the only sign-in path for the panel. There are no passwords and no separate panel login.
- **Zero external installs.** The server serves the panel. Its assets are embedded in the server assembly. The panel loads no CDN script, font or framework, and needs no build step, package install or extra download on the admin's machine.
- **Served by the server.** Like all Bazaar components, the panel is a module of the company's server. It is not a Conduit module, and it is not a client build (doc 37).

## Design

### 1. Scope
| Area | What the panel shows and does | Source |
|---|---|---|
| Admin | Server status, configured settings, pending approvals, audit log view | doc 03, doc 15 |
| Kodo health | Health score, checks, incidents, runbook links (read-only) | doc 46 |
| Module selection and rebuild | Select the company's modules, start a rebuild, see build reports and retries | doc 41 section 10.2, doc 37 section 4a |
| Users | Members, roles, devices, revocation, DM access requests | doc 32 section 6, doc 01 |

Kodo's own rule holds: Kodo adds no interactive browser surface of its own (doc 46 section 15.5). The panel reads Kodo through its API. Any write from the panel is an admin action that goes through the normal approval flow where doc 15 requires one.

### 2. Auth
- Only accounts that hold the admin permission may open the panel. The permission belongs to the roles model (doc 48 section 4, custom roles; doc 32 section 6 lists the built-in role templates). Coworkers get a 403, not a login page.
- The session is the passkey sign-in of doc 32 section 4. It is short-lived, and it ends when the owner revokes the device or the account.
- No content is shown before sign-in. The panel's entry page shows only the server display name and the sign-in control, the same rule as the sign-in page.

### 3. Network
- Listener and bind: doc 03 floors apply. The panel is on loopback by default. A wider bind needs owner approval through the settings registry (doc 46 section 15.5).
- The panel uses the same TLS settings as the server (doc 32 section 2). Plain HTTP is allowed only on loopback.

### 4. Build and delivery
- Assets live in `wwwroot/` inside the module and are embedded in the server assembly. The server serves them with a Content-Security-Policy that allows only same-origin scripts and styles.
- No third-party script, style or font is loaded. A test scans the served assets for external URLs.

### 5. Mechanics [PROPOSED]
The owner decided the scope above. These mechanics fill the gaps the decisions left, chosen as the most robust option. The owner can change any of them.

**Projects.** `Bazaar.Server.WebAdmin.Abstractions` holds the ports and the records the panel exchanges (no implementation). `Bazaar.Server.WebAdmin` holds the endpoints, the gate, the session store, the audit recorder and the embedded `wwwroot/`. Neither references `Bazaar.Server`. It reaches other modules only through their abstractions. A missing port is a soft edge: the matching section shows "unavailable" and nothing else degrades.

**Routes (all under `/admin`; every other method is 405).**
| Route | Auth | Purpose |
|---|---|---|
| `GET /admin` and `GET /admin/assets/*` | none | The shell and its embedded assets. The shell shows only the server display name and the sign-in control. |
| `GET /admin/session/redeem?code=` | handoff code | Redeems the 60 s single-use handoff code of doc 32 section 4 issued with `audience=admin`, sets the panel cookie, redirects to `/admin`. |
| `POST /admin/api/signout` | session | Ends the session. |
| `GET /admin/api/session` | session | Account, held capabilities and the sections the account may open. |
| `GET /admin/api/status`, `/kodo`, `/users`, `/devices`, `/audit`, `/approvals` | session + section capability | Read views. |
| `GET /admin/api/modules`, `/modules/builds` | session + `admin.modules` | Catalogue, current selection with its revision, build history and reports. |
| `POST /admin/api/modules/preview` | session + `admin.modules` | Dry-run of a selection: dependency problems and the change list. Writes nothing. |
| `PUT /admin/api/modules/selection` | session + `admin.modules` | Commits a selection against an expected revision (409 on a stale revision). |
| `POST /admin/api/modules/rebuild` | session + `admin.modules` | Starts a rebuild. One at a time (409 while one runs). An `Idempotency-Key` header makes a retry safe. |

**Session.** `/signin` accepts `audience=admin`. On success it issues the handoff code and sends the browser to `/admin/session/redeem` instead of `bazaar://signin`. This is the single exception to doc 32 section 4's "no session cookie beyond the ceremony", and doc 32 gets a note. The cookie is `__Host-bazaar-admin` (`HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/admin`; on loopback HTTP the same cookie without the `__Host-` prefix and without `Secure`). It holds an opaque 256-bit id, and the server keeps only its hash. Lifetime is 15 minutes sliding and 8 hours absolute, as settings `webadmin.session.*`; settings may tighten and never loosen past those floors. A revoked device or account ends its panel sessions at once, and each request re-checks revocation.

**Write protection.** Every write needs the header `X-Bazaar-Admin: 1`, a JSON body, and an `Origin` that equals the panel origin. A request without all three is 403. This stacks on `SameSite=Strict`.

**Gate.** Authorization is the Roles-based `AdminGate` (`IPermissionChecker` at instance scope, doc 48 section 4), not a panel-local table. Areas map to capabilities: Overview (status, approvals) any `admin.*` or `kodo.view.status`; Kodo `kodo.view.status`; Audit `kodo.view.audit`; Members `admin.members`; Devices `admin.devices`; Modules `admin.modules`. No new capability or role name is introduced. Only human principals pass, so an agent session never opens the panel. No session is 401 with no body. A session without the capability is 403 with no body, audited once as `admin.web.denied` by the gate.

**Ports (Abstractions).** `IAdminGate`, `IAdminSessionStore`, `IAdminAudit`, and one read or write port per section: `IAdminStatusSource`, `IAdminKodoSource`, `IAdminUserDirectory`, `IAdminDeviceDirectory`, `IAdminAuditReader`, `IAdminApprovalSource` and `IAdminModuleSelection` (catalogue, selection, preview, commit, start rebuild, build history). The owning modules implement them: Kodo, the module manager, identity, the audit log, approvals and the company build module. The panel adds no data of its own beyond its sessions.

**Rebuild.** A selection commit and a rebuild are two admin actions. The commit records the new selection and its revision. The rebuild starts the company build of doc 37 section 4a, which publishes the update to clients (doc 37 in-place updates). The panel only starts it and shows its state; the build module owns the work. A failed build leaves the previous selection live and says so in the report, as doc 37 requires.

**Audit.** Entries use these kinds: `admin.web.signin`, `admin.web.signout`, `admin.web.denied`, `admin.web.read` (once per section load, with the section name), `admin.web.selection.commit`, `admin.web.rebuild.start`. The actor is the account id. Payloads carry ids and names and never a secret, a token or a cookie value. A write whose audit entry cannot be appended is refused (fail closed).

**Browser hardening.** Responses carry `Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'`, plus `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer` and `Cache-Control: no-store` on every `/admin/api` response. The assets use no inline script or style, so the policy needs no `unsafe-inline`. The panel has its own rate-limit bucket `admin` (doc 32 section 7).

### 5a. Where the selection and build state live [PROPOSED]
No module stored a company's module selection, its build history or a running-build marker (the module manager keeps update decisions, line moves and permission approvals; the company build module composes and runs but is stateless). This subsection fills that gap as the most robust option. The owner can change it.

**Module.** `Bazaar.Server.CompanyBuild.State` (component id `company-build-state`, depends on `company-build` and `audit`, soft edge to `approval-queue`) implements `IAdminModuleSelection`. It does not reference `Bazaar.Server` and does not reimplement the composer or the runner: validation and the plan come from `CompanyBuildComposer`, and the build runs through the `ICompanyBuildExecutor` seam, which the host binds to `CompanyBuildPipeline` (targets, identity and output folder are host settings).

**Tables** (migration namespace `company_build_state`, raw Npgsql, SQL-first, no foreign keys):
- `company_module_selection`: exactly one row. Integer `revision` (0 means nothing committed), `selected_module_ids`, the pending approvals (module id and approval-queue item id, in two parallel arrays), `updated_by`, `updated_at`. A commit is one `UPDATE ... WHERE revision = expected RETURNING`, so a stale revision updates nothing and the port answers 409 with the current revision.
- `company_build`: id, `selection_revision`, `status` (`queued`, `running`, `succeeded`, `failed`; `queued` is reserved), `started_by`, `started_at`, `finished_at`, `idempotency_key` (unique, at most 128 characters), `attempts`, `result_summary` (at most 500 characters, never a path or exception text). A partial unique index on `status = 'running'` lets at most one build run, so two concurrent rebuilds cannot both start; the second gets 409. A repeated `Idempotency-Key` returns the first build and starts nothing.

**Mechanics.**
- The catalogue is the component manifests of the host (`IModuleCatalogue`). A selection is valid when every id is known and the composer accepts it (hard dependencies are added and shown in the preview).
- A build still marked `running` when a server process first uses the port is marked `failed` ("abandoned"), since no build can survive a restart.
- A failed or incomplete build leaves the committed selection as it is and says "the previous build stays live" in its summary (doc 37).
- Audit through `IAuditLog`: `module.selection.committed` and `module.rebuild.started` (ids and names only). The rebuild's audit append failing marks the build failed and refuses the start (fail closed). The panel's own `admin.web.*` entries stay as section 5 says.
- Permission: the router's gate for the Modules area (`admin.modules`) runs before any call; the port takes the actor id for the audit trail.
- **Modules that need the owner.** `IModuleApprovalRequirement` (backed by the Bazaar.Install gates of doc 48 section 1: signature and owner-signed permission approval) says whether a module in the plan still needs the owner. A commit that selects one files a tier 2 `module.install` request in the approval queue (payload reference `module:<id>`, 7 days) and stores its id with the selection. A rebuild is refused while that request is pending, denied or expired, and spends an approved one exactly once when it starts. Nothing is installed or approved here, and no signature or approval rule is bypassed. Without the queue, such a module cannot be selected.

**Host bindings (PROPOSED).** Both seams are bound in `Bazaar.Server.Host/Composition`.
- `ICompanyBuildExecutor` is `CompanyBuildExecutor` over `CompanyBuildPipeline` with `DotnetPublishBuildStep`. It is registered only when `Bazaar:CompanyBuild:ServerAddress`, `ServerKeyPin` and at least one `Targets:<id>` project path are set; otherwise a rebuild is refused with a reason, as before. `OutputRoot` and `IdentitySourceDirectory` default under `<Bazaar:Host:DataRoot>/company-build/`; a relative path or a drive or filesystem root is refused. The returned report carries counts and flags only (no paths, no failure text; the operator reads `BUILD-REPORT.txt` in the output folder), sets are reported unsigned (signing stays with the company signing flow), and cancellation propagates.
- `IModuleApprovalRequirement` is `InstallGateApprovalRequirement`. The gate had no per-module read, so `Bazaar.Install.Abstractions` gains two narrow read seams: `IModulePermissionApprovalReader` (the read side of the approvals the module manager already stores) and `IModuleDeclarationSource` (the permissions a module declares in its signed manifest). The rule itself is `PermissionApproval.Evaluate` from `Bazaar.Install`. A module that declares nothing, or is unknown to the source, needs nothing; declared permissions with no readable approvals fail closed. The host's default source declares nothing (built-in modules ship in the signed host build), so no module needs approval until the module manager registers a source for installed packages.

### 6. Approvals queue [PROPOSED]
One shared queue serves the panel's approvals list (`IAdminApprovalSource`) and the mail thread's approval gateway (doc 22 tiers). Projects: `Bazaar.Server.ApprovalQueue.Abstractions` (`IApprovalQueue`, `NewApproval`, `ApprovalItem`, `ApprovalStatus`) and `Bazaar.Server.ApprovalQueue` (Postgres store, service, component id `approval-queue`, depends on `roles` and `audit`). Neither references `Bazaar.Server`. It does not replace the owner-signature contract of `Bazaar.Approvals.Abstractions` (doc 32 section 5a); a request that needs the owner's signature keeps using that, and this queue holds routine tier 1 and tier 2 approvals checked by Roles permissions.

**Status machine.** `Pending` becomes `Approved` or `Denied` once, by a decision. `Approved` becomes `Consumed` once, by a consumer. `Expired` is never stored: a `Pending` item past `ExpiresAt` reads as `Expired`, cannot be decided and is not listed. `Denied`, `Expired` and `Consumed` are final.

**Rules.**
- The ttl is clamped to 1 minute at least and 30 days at most (constants for now; a setting contribution can replace them).
- A request needs a kind, a subject and tier 1 or 2. It stores a `PayloadRef` (a pointer), never the contents.
- A decision needs `Pending`, an approver who is not the requester, and the approval capability (`approve`, doc 48 section 4) at instance scope through `IPermissionChecker`. Agents never hold it. A tier 2 item additionally needs a human approver. The approver's kind comes from their role assignment.
- Consuming is one guarded `UPDATE ... WHERE status = 'approved' RETURNING`, so it returns true exactly once per approval.
- Audit: `approval.requested`, `approval.decided` and `approval.consumed`, with the item id, kind, tier and outcome only (no subject, reference or reason).
- Storage: table `approval_queue_item` in the `approval_queue` migration namespace, raw Npgsql, SQL-first.

## Open questions for the owner
1. **Who may use the panel. DECIDED (owner, 2026-11-16): any account with admin rights.** Any admin account can sign in to the panel, with the same permissions as the client admin views. Doc 32 section 6 has one owner per instance and no admin permission yet. The admin permission is named in the roles model of doc 48 section 4 (custom roles win over the fixed table in doc 32 section 6), and doc 32 section 6 must list it as a built-in role template. No new role name is introduced here.
2. **Can the panel change the module selection and start a rebuild. DECIDED (owner, 2026-11-16): yes, and every rebuild is recorded in the audit log.** Admins can change the module selection and start rebuilds from the web panel, with the same permission check as the client (section 2) and an audit entry for each rebuild (doc 15).
3. **Is the panel on by default. DECIDED (owner, 2026-11-16): on for every server.** The panel is on wherever the server is reachable. Admin-only passkey sign-in (doc 32 section 4) still applies, so nobody gets in without it.

## Testing
- Authorization: a coworker and a regular user get 403 on every panel route. An admin gets the same results as the apps for the same action.
- Sign-in: the panel refuses access without a passkey session. A revoked device loses access at once.
- Assets: a scan of the served assets finds no external URL, and the CSP header is present.
- Audit: each admin write is recorded in the audit log, and a write is refused when its audit entry cannot be appended.
- Session and CSRF: a write without the header, the JSON body or the matching `Origin` is 403; an expired, revoked or absolute-lifetime-exceeded session is 401; the cookie flags are as section 5 says.
- Module selection: a stale revision is 409, a second rebuild while one runs is 409, a repeated `Idempotency-Key` returns the first result.
- Soft edges: with a port missing, its section reports unavailable and the other sections still work.
- Architecture: neither project references `Bazaar.Server`; `Bazaar.Server.WebAdmin` is listed in doc 40.

## Work packages
1. Module skeleton and embedded assets (needs 03, 02).
2. Admin gate over the existing authorization (needs 32 section 6, 15).
3. Kodo health view (needs 46).
4. Module selection and rebuild view (needs 37 section 4a, 41 section 10.2).
5. Users and devices view (needs 32).
