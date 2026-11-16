# Web admin panel (admin-only module)

Status: DECIDED for the rule and scope (owner, 2026-11-16: "Allow admin-only web panel"). PLANNED for mechanics. The name `Bazaar.WebAdmin` is provisional under the naming rule in DECISIONS-LOG.
Depends on: 02 (HTTP surface), 03 (listeners, bind floor), 15 (approvals, audit), 32 (roles, passkey sign-in page), 37 (company build), 41 (module selection and rebuild), 46 (Kodo health) · Used by: CLAUDE.md, 45 (setup wizard hand-off)

## Purpose
An admin-only web panel the server hosts for administration and health: users, Kodo health, module selection and rebuild, and similar admin views. It sits beside the in-client admin views, which admin accounts see inside the apps. It is a web page for admins, not a web client for people. Regular users still get no web client. The rule that says so is CLAUDE.md's "No web client", which is narrowed on 2026-11-16 to "no web client for regular users".

## Decided inputs
- **Rule change (owner, 2026-11-16).** "No web client, ever" is narrowed to "no web client for regular users". An admin-only web panel, written as plain HTML, CSS and JavaScript, is allowed. The read-only status page (doc 02, doc 23) and the narrow sign-in page (doc 32 section 4) keep their existing scope.
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
- Only accounts with the admin role may open the panel. Coworkers get a 403, not a login page.
- The session is the passkey sign-in of doc 32 section 4. It is short-lived, and it ends when the owner revokes the device or the account.
- No content is shown before sign-in. The panel's entry page shows only the server display name and the sign-in control, the same rule as the sign-in page.

### 3. Network
- Listener and bind: doc 03 floors apply. The panel is on loopback by default. A wider bind needs owner approval through the settings registry (doc 46 section 15.5).
- The panel uses the same TLS settings as the server (doc 32 section 2). Plain HTTP is allowed only on loopback.

### 4. Build and delivery
- Assets live in `wwwroot/` inside the module and are embedded in the server assembly. The server serves them with a Content-Security-Policy that allows only same-origin scripts and styles.
- No third-party script, style or font is loaded. A test scans the served assets for external URLs.

## Open questions for the owner
1. Should non-owner admin accounts exist, or is the panel for the owner only in v1? Default: owner only, because doc 32 section 6 has one owner per instance in v1.
2. Should the panel be able to start a module-selection rebuild, or is it read-only and rebuilds run from the client? Default: it can start a rebuild, and each rebuild is audited.
3. Should the panel be on by default for a company server, or off until enabled? Default: off until enabled, as with the sign-in page (doc 32 settings).

## Testing
- Authorization: a coworker and a regular user get 403 on every panel route. An admin gets the same results as the apps for the same action.
- Sign-in: the panel refuses access without a passkey session. A revoked device loses access at once.
- Assets: a scan of the served assets finds no external URL, and the CSP header is present.
- Audit: each admin write is recorded in the audit log.

## Work packages
1. Module skeleton and embedded assets (needs 03, 02).
2. Admin gate over the existing authorization (needs 32 section 6, 15).
3. Kodo health view (needs 46).
4. Module selection and rebuild view (needs 37 section 4a, 41 section 10.2).
5. Users and devices view (needs 32).
