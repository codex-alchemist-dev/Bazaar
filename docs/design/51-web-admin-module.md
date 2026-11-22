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
| `GET /admin/api/gateway/catalogue`, `POST /admin/api/gateway/catalogue/refresh` | session + `admin.modules` | PROPOSED. Last gateway model-catalogue refresh, and the manual "check for newer catalogue" (doc 05). Port `IAdminCatalogueRefresh`; the write is audited as `admin.web.gateway.catalogue.refresh` and refused if the audit entry cannot be appended. Per-provider outcome, counts and unclassified ids only. |

**Session.** `/signin` accepts `audience=admin`. On success it issues the handoff code and sends the browser to `/admin/session/redeem` instead of `bazaar://signin`. This is the single exception to doc 32 section 4's "no session cookie beyond the ceremony", and doc 32 gets a note. The cookie is `__Host-bazaar-admin` (`HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/admin`; on loopback HTTP the same cookie without the `__Host-` prefix and without `Secure`). It holds an opaque 256-bit id, and the server keeps only its hash. Lifetime is 15 minutes sliding and 8 hours absolute, as settings `webadmin.session.*`; settings may tighten and never loosen past those floors. A revoked device or account ends its panel sessions at once, and each request re-checks revocation.

**Shared handoff store (PROPOSED, 2026-11-22).** Doc 58 section 5 has the resident TUI redeem admin codes from a separate process, so the code store cannot stay in the server's memory. The `signin` SQL namespace (module `Bazaar.Server.SignIn.Postgres`, migration `0001_handoff_code.sql`) holds one table, `handoff_code`: the SHA-256 hash of the code (primary key), principal id, principal kind, device id, audience (`native` or `admin`), created and expiry times. Rules are doc 32 section 4's, unchanged: single-use, 60 s from issue, bound to an audience, only the hash stored. Redeem is a single `DELETE ... RETURNING` (atomic across processes), run before the audience and expiry checks so a wrong-audience or late attempt burns the code; expiry is judged against the caller's clock reading, not the database's. Issue removes expired rows and trims to 4096 rows (oldest first). The server registers `PostgresHandoffCodeStore` as `IHandoffCodeStore` ahead of the sign-in module's in-memory default (which stays the fallback); the panel's `/admin/session/redeem` and the TUI both redeem through it. It adds no setting, no new audience and no change to the panel cookie.

**Write protection.** Every write needs the header `X-Bazaar-Admin: 1`, a JSON body, and an `Origin` that equals the panel origin. A request without all three is 403. This stacks on `SameSite=Strict`.

**Gate.** Authorization is the Roles-based `AdminGate` (`IPermissionChecker` at instance scope, doc 48 section 4), not a panel-local table. Areas map to capabilities: Overview (status, approvals) any `admin.*` or `kodo.view.status`; Kodo `kodo.view.status`; Audit `kodo.view.audit`; Members `admin.members`; Devices `admin.devices`; Modules `admin.modules`. No new capability or role name is introduced. Only human principals pass, so an agent session never opens the panel. No session is 401 with no body. A session without the capability is 403 with no body, audited once as `admin.web.denied` by the gate.

**Ports (Abstractions).** `IAdminGate`, `IAdminSessionStore`, `IAdminAudit`, and one read or write port per section: `IAdminStatusSource`, `IAdminKodoSource`, `IAdminUserDirectory`, `IAdminDeviceDirectory`, `IAdminAuditReader`, `IAdminApprovalSource` and `IAdminModuleSelection` (catalogue, selection, preview, commit, start rebuild, build history). The owning modules implement them: Kodo, the module manager, identity, the audit log, approvals and the company build module. The panel adds no data of its own beyond its sessions.

**Rebuild.** (Storage of the selection and the rebuild queue, PROPOSED 2026-11-22: doc 37 section 4a, "Module selection and the rebuild queue". The selection is the settings key `modules.selection` with a revision; the revision is the rebuild idempotency key.) A selection commit and a rebuild are two admin actions. The commit records the new selection and its revision. The rebuild starts the company build of doc 37 section 4a, which publishes the update to clients (doc 37 in-place updates). The panel only starts it and shows its state; the build module owns the work. A failed build leaves the previous selection live and says so in the report, as doc 37 requires.

**Audit.** Entries use these kinds: `admin.web.signin`, `admin.web.signout`, `admin.web.denied`, `admin.web.read` (once per section load, with the section name), `admin.web.selection.commit`, `admin.web.rebuild.start`. The actor is the account id. Payloads carry ids and names and never a secret, a token or a cookie value. A write whose audit entry cannot be appended is refused (fail closed).

**Browser hardening.** Responses carry `Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'`, plus `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer` and `Cache-Control: no-store` on every `/admin/api` response. The assets use no inline script or style, so the policy needs no `unsafe-inline`. The panel has its own rate-limit bucket `admin` (doc 32 section 7).

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
