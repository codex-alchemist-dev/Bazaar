# 83. Client patterns from Resibo

Status: PROPOSED (2026-11-19). The owner's direction of 2026-11-19 to adopt the Resibo features that benefit Bazaar, to keep conflicting ones as separate modules, and to account for everything, is DECIDED (as a direction: "adopt this Resibo client feature as module X" is DECIDED for each module named here). Every detail below that direction is PROPOSED or OPEN. Nothing here is built.
Depends on: 02-wire-protocol-and-sync.md (`WireClient` behaviour, `resync`, `lagged`, `ui.changed`, `prefs.set`), 03-server-host-and-providers.md (settings registry, configurability rule), 15-security-taint-and-approvals.md (approvals, audit), 27-client-architecture.md (theme file, offline cache, i18n), 28, 29 (heads), 41-client-gui-customization-and-dsl.md (module contract, mnemonics, section 4.12 navigation grammar), 41a, 41b, 41c (Rubedo and view declarations), 45-onboarding.md (client tour), 46-kodo-health-module.md (module health contract), 51-web-admin-module.md (admin panel, ports, gate), BAZAAR-COMPONENTS.md (sections 2.1 module sides and 4.2 `Principal`), 40-core-feature-modules-audit.md (module layout).
Used by: 27, 41, 45, 51 (once the main session adopts rules W1 to W6 in section 7), 34 (test strategy), 37 (company build supplies the help corpus and credits).

## 1. Purpose

Resibo is a sister program whose user-facing half is a Textual admin console (TUI), a React web app that is mostly scaffold, and a written design system. Its code for those parts is thin (`webapp/src` is 24 files and 147 lines; two TUI custom screens are unbuilt), but its design documents carry patterns Bazaar can use: an admin console driven by data instead of screen classes, a client data layer with a defined reconnect experience, a "route guard is UX only" rule, token-based theming, an onboarding tour that respects reduced motion and mute, a timeline display for change history, and a help assistant that cannot reach user data.

This document accounts for every one of them. Where a Resibo pattern fits a Bazaar decision, it is mapped onto the existing module. Where it conflicts with a Bazaar decision, it becomes its own optional module rather than a choice between the two (owner direction, 2026-11-19). Nothing is copied as code: Resibo is Python, TypeScript and Textual, and Bazaar is .NET and Avalonia.

## 2. Decided inputs

- Adopt the Resibo features that benefit Bazaar, make conflicting ones separate modules, and account for everything (owner, 2026-11-19, DECIDED).
- Every feature is its own module: `Bazaar.Server.<Name>` plus `.Abstractions`, and `Bazaar.Client.<Name>` where there is a client side. Never code in `Bazaar.Server` or `Bazaar.Client.Core` (CLAUDE.md, BAZAAR-COMPONENTS.md section 2.1, doc 41b).
- Providers are plain modules; shared libraries and abstractions hold contracts and logic only, with no third-party dependency. A UI library, an audio library or a model client is a provider module (owner, 2026-11-19).
- Actor attribution is `Principal` (`kind:id`, BAZAAR-COMPONENTS.md section 4.2, owner 2026-11-19).
- The "no web client" rule is removed (owner, 2026-11-17, DECISIONS-LOG round 26). The admin web panel exists (doc 51, DECIDED for scope: plain HTML, CSS and JavaScript, embedded, passkey sign-in).
- Views are Rubedo view documents with a closed set of 13 widgets and four actions; views ship from modules and company builds only, with no runtime import (doc 41c, owner 2026-11-15).
- One theme file drives Avalonia and Monaco (doc 27, `ThemeTokens` in `src/Bazaar.Client.Core/Theme/`).
- Mnemonic navigation, no Ctrl chords for app commands; AI matching in the command bar suggests only and is a separate key (doc 41 sections 4.11 and 4.12, owner 2026-11-12).
- No offline write queue in v1 (doc 41 section 4.13, owner 2026-11-12).
- First-run tour: skippable, re-runnable, progress saved through `prefs.set`, no sample-data company (doc 45 sections 2.3 and 2.4).
- Configurability rule (DECISIONS-LOG round 13): open values become operator settings with safe defaults. Floors and security invariants are not settings.

## 3. What this document does not decide

- The terminal UI library. The choice is a provider module (section 4.2) and is OPEN.
- The audio library, the sound pack and its licence.
- Whether the owner wants a full web client. The rule that blocked it is gone, but no decision replaces it (section 15, Q1).
- Whether Rubedo grows `table`, `diff` and toggle widgets (section 4.3, Q2). Adding a widget is a version change in doc 41c.
- The content of any tour step, help article or credits text.
- The in-client admin views' wire contract, if one already exists elsewhere. This document proposes one (section 4.2) and marks the overlap.
- Anything about Resibo's domain screens (OCR diff, vendor editor, receipt detail). Only the generic display pattern of doc 48 is taken (section 9).

## 4. Module (a): the terminal admin console

### 4.1 Overlap and decision

| Existing | What it is | Relation |
|---|---|---|
| `Bazaar.Client.Terminal` (`Program.cs`, `MessageFormatter`, `StatusTransport`, 419 lines) | A plain-line GC chat head. It is a head (an executable), not a feature. | Stays as it is. The admin console is not added to it, because a head holds no features (doc 41b). The Terminal head may reference the new module and list it. |
| Calcinato (doc 41 section 4.10) | The owner's terminal emulator window inside the Windows client. Not a TUI. | Unrelated. No overlap. |
| `bazaar-admin` CLI (doc 03 open question 2: a small non-TUI admin CLI) | Command-line admin. | Shares the pure menu core below, so `bazaar-admin set <path> <value>` and `bazaar-admin find <words>` use the same data and the same search. |
| "A TUI status client is OPEN" (CLAUDE.md) | An open owner item. | This module is a PROPOSED answer. It does not settle the item. |
| `Bazaar.Client.Kodo` | The Kodo Monitor pane (Avalonia). | The TUI Monitor screen reads the same `KodoStatusFeed`; it does not re-implement Kodo. |

Decision (PROPOSED): a new module, `Bazaar.Client.TerminalAdmin`, not an extension of `Bazaar.Client.Terminal`. Reason: the Terminal head is a head, and doc 41b forbids features in heads.

### 4.2 Module set

| Module | Kind | Holds |
|---|---|---|
| `Bazaar.Client.TerminalAdmin.Abstractions` | client abstractions, pure | `MenuItemSpec`, `MenuTree`, `IMenuCatalog`, `ISettingFinder`, `ITerminalScreen`, `ScreenId`, `ITerminalSurface` (the draw and input seam). No dependency. |
| `Bazaar.Client.TerminalAdmin` | client module, pure | Menu walker, `find_setting` search, closed screen registry, screen state machines, boot and wizard flows, i18n use. Draws only through `ITerminalSurface`. References `Bazaar.Client.Core` contracts, `Bazaar.Rubedo`, `Bazaar.Localisation`, `Bazaar.Ex`, `Bazaar.Client.DataLayer.Abstractions`. |
| `Bazaar.Client.TerminalAdmin.Render.<Lib>` | provider module | One implementation of `ITerminalSurface` over a terminal library. Which library is OPEN (Q3). A second provider with no dependency (plain ANSI) is the fallback and the test double. |
| `Bazaar.Server.AdminWire.Abstractions`, `Bazaar.Server.AdminWire` | server module, id `admin-wire` | Wire handlers `admin.menu.get`, `admin.find`, `admin.<section>.get` that expose the doc 51 ports to device-credential clients. See below. |

`Bazaar.Server.AdminWire` exists because the TUI, the Avalonia admin views and the web panel must show the same data from the same checks. Doc 51 defines the ports (`IAdminGate`, `IAdminStatusSource`, `IAdminKodoSource`, `IAdminUserDirectory`, `IAdminAuditReader`, `IAdminApprovalSource`, `IAdminModuleSelection`) and an HTTP surface for the browser. AdminWire adds the same ports on the wire for native clients and adds no port of its own except the menu catalogue. It reuses `IAdminGate` and the capability names of doc 51 section 5 (`admin.*`, `kodo.view.*`) and invents none. Open overlap: if the in-client admin views already have a wire contract (doc 27 mentions in-client admin views), AdminWire is that contract and this document must be reconciled with it (Q4).

### 4.3 Menu data is a Rubedo view declaration

Resibo's rule (deepdive-14 section 3, `docs/PRINCIPLES.md` 1.4): any screen that is "a list of labelled actions" is declarative menu data rendered by one generic screen, and a closed list of custom screens covers everything stateful. Bazaar already has the mechanism for declarative screens: Rubedo view documents (doc 41c). So the menu is a view declaration, not a second schema.

The record, as sketched in C# (data, no code, one codec):

```csharp
namespace Bazaar.Client.TerminalAdmin;

public enum MenuKind { Action, Bool, Number, Text, Choice, Submenu }

/// <summary>One row of the admin menu. Keys, not prose: text is resolved by Bazaar.Localisation.</summary>
public sealed record MenuItemSpec(
    string Path,                       // dotted, current location, e.g. "gateway.free_lane.preset"
    IReadOnlyList<string> FormerPaths, // earlier locations; kept so old references still resolve
    string LabelKey,
    string TooltipKey,
    string? DocsRef,                   // doc path and section, e.g. "05b#guided-setup"
    MenuKind Kind,
    string Target,                     // setting key, or a command id from a module manifest
    string? CapabilityHint);           // UX hint only; the server decides (section 7, W1)

public interface IMenuCatalog
{
    ValueTask<MenuTree> GetAsync(CancellationToken ct);   // admin.menu.get
}
```

How it becomes a view, precisely:

1. One view document `bazaar.admin.menu` (dslVersion 1) is shipped by `Bazaar.Client.TerminalAdmin`. Its single data source `items` is the capability `wire:admin.menu.get` (doc 41c section 4: a view reads only sources its manifest names). The current submenu `path` is the view's one parameter.
2. Root is a `stack`: a `text` heading from `LabelKey`, then a `list` over `data.items` keyed by `Path`, with one item template. The item template is a `row` of a `text` (label), a `badge` whose `value` is an expression over the current setting value, and one control.
3. Controls map onto the 13 widgets and 4 actions of doc 41c section 6: `Action` is a `button` with `invoke`; `Submenu` is a `button` with `navigate` to the same document and the child path; `Bool`, `Number`, `Text` and `Choice` are an `input` whose write goes through `setSetting`; the tooltip is a `text` bound to the focused row and shown on the status line; `DocsRef` is a `navigate` to the help pane.
4. The generic screen is not a class per menu. It is the one terminal adapter of doc 41c ("terminal on Calcinato" there) that maps the 13 widgets to `ITerminalSurface` calls. That adapter is a separate small module, `Bazaar.Client.Views.Terminal`, parallel to the existing Avalonia adapter `Bazaar.Client.Views` (`ViewMapper`). Doc 41c already promises a terminal adapter; this document only adds the menu as its first consumer.
5. Adding a menu item is an edit to server-side data (the owning module's setting or command metadata), never new screen code. A new module's settings appear in the menu with no client release.

Gaps in the closed widget set, stated plainly rather than patched (Q2): there is no toggle widget (a `Bool` is an `input`), no table widget, and no diff widget. The first two cases are written as `input` and keyed `list` today. A real table and a diff are needed by the approvals queue and the diff review screen (section 4.4); until the owner extends the set, those two screens are in the custom list. Whether a view node accepts a per-node visibility expression is not stated in doc 41c (its `visibleWhen` is for layout nodes). If it does not, the item template uses one `list` per `MenuKind` fed by a filtered source. Also unclear: doc 41c `setSetting` names "a setting key the manifest declares", and a menu spans every module's settings. Proposed: the manifest declares a wildcard capability `setting:*`, and the server checks the real role and approval tier per key (Q5).

### 4.4 Closed list of custom screens

Resibo's list is closed by design, and adding to it needs written justification. Bazaar keeps the rule and the form. Each row says why it cannot be menu data.

| Screen id | Bazaar content | Why it is not menu data | Resibo source |
|---|---|---|---|
| `boot` | Start-up and update-restart screen: connect, Kodo readiness, "Updating to v{target}" with a secondary "skip animation" control. | Time-based state machine. | deepdive-14 3.2, 3.3 |
| `run-monitor` | Streaming view of agent turns, company builds and module rebuilds (status and progress). | Live stream with its own state. | run_monitor |
| `fleet-updates` | Module versions, channel and ring, health, pin and update; client update. Reads `Bazaar.Server.Modules` and `Bazaar.Updates` through the doc 51 ports. | Two controls of different shapes; confirm texts name exactly what restarts. | deepdive-14 3.3 |
| `approvals-queue` | Pending approvals (doc 15) with the shared diff review. | Table plus per-row decision flow. | staff_audit_queue |
| `diff-review` | One shared screen showing `{field, old, new}` rows with accept, reject and edit callbacks; used by the approvals queue, settings imports and module update review. | The shared-component rule: one diff UI, not two that drift (deepdive-45 section 4). | DiffViewer |
| `members-roles` | Members, then their roles and devices (doc 32 section 6, doc 48 section 4). Role changes are privileged and audited, so the screen asks for confirmation. | Two-level relational structure; the same justification as Resibo's Groups screen (deepdive-14 3.2.1). | Groups screen |
| `wizard` | The doc 45 setup wizard in the console ("console prompts for the first install"), driven by the same setup plan. | Stepwise state with validation and resume. | none (Bazaar-only; Resibo's Setup is a separate service) |
| `monitor` | Kodo health, as the Avalonia pane shows it. | Reads `KodoStatusFeed`. | health screens |
| `credits` | Third-party notices, source offer, version history and codename (module `Bazaar.Client.Credits`, section 8.4). | Static pages with search. | credits screen |
| `find` | `find_setting` results (section 4.6). | Search input with result list. | find_setting |

Persistent header: product name, version `0.MM.PP+commit` and the major's codename (doc 37, `docs/VERSIONING.md`), server name, connection state. This is a header strip, not a screen. Resibo's OCR diff, vendor and branch editor, and receipt screens are domain-specific and not adopted. Adding a screen id needs a doc change and a justification line, enforced by a test that compares the registry to the table in this document.

### 4.5 i18n and accessibility

- All text is `Bazaar.Localisation` keys resolved by `LocalisedText.Resolve`. A missing key renders as `<key>`, which already makes a gap visible. A hard-coded string in a screen fails an analyser test, the TUI equivalent of Resibo's `t("...")` discipline (deepdive-14 section 6.1). Adding a language is a catalogue file, never a code change.
- Resibo's Tagalog note (some business terms stay untranslated) is adopted as a rule: each locale may carry a short list of terms the translator keeps in the source language (for example product names and setting paths). The list is data in the catalogue pack, not code.
- Every control has a text label, so a screen reader reading the terminal buffer gets the same information. Colour is never the only carrier of state. Reading order is child order (doc 41c section 7). `Bazaar.Accessibility` (`AccessibleName`, `SpokenMnemonic`) supplies the spoken form of mnemonics.
- No Ctrl chords. Navigation uses the `Bazaar.Ex` mnemonic grammar (doc 41 section 4.12). Terminal-specific limits (a terminal cannot tell some key combinations apart) are handled by the provider module, and any key that cannot be represented gets an alternative.

### 4.6 find_setting

`ISettingFinder` flattens the `MenuTree` into rows and scores a typed phrase against label, path and tooltip text. Resibo uses rapidfuzz `partial_ratio` with a substring fallback (deepdive-14 section 3.1). Bazaar adds no library: it reuses the existing `FuzzyScorer` and `CommandMatcher` in `src/Bazaar.Client.Core/Commands/` (also used by the palette), reached through the command abstraction. Added behaviours from Resibo:

- **Former paths.** When a direct or fuzzy match finds nothing, the finder checks `FormerPaths`, so a saved deep link, a stale doc reference or muscle memory still lands on the setting's new home. Entries are never pruned automatically.
- **Hit counting.** A redirect hit is counted server-side (table `setting_redirect`, section 6) so that a deliberate later edit can remove an entry that nothing uses.
- **Search never changes anything.** It returns a location and the client navigates to it.

## 5. Module (b): client data layer

### 5.1 What already exists

`Bazaar.Client.Core/Wire/WireClient.cs` already implements, per doc 02:

| Resibo data-layer behaviour (deepdive-46) | Already in `WireClient` | Gap |
|---|---|---|
| Reconnect with exponential backoff | Yes. `BackoffDelay`: doubling, jitter, from 1 s up to `wire.reconnect_backoff_max_s` (default 60 s; Resibo caps at 30 s). | None. Do not duplicate. The operator can tighten the cap to 30. |
| Resubscribe after reconnect | Yes, from per-stream cursors; `lagged` resubscribes; unanswered requests resent with the same id (idempotent). | None. |
| Full resync after a gap | Partly. A gap over `wire.replay_cutoff_entries` yields `resync` (tail plus unread counts), surfaced on `Resyncs`. For GC streams the cursor protocol replaces "full state". | Snapshot streams (`board:<id>`, presence, Kodo feed) need "full state on reconnect"; no consumer-side helper exists. |
| Visible "Reconnecting" state | No. Only `ConnectionCount` and `Stopped`. | Needs an observable connection state. |
| Query cache with invalidation by event | No. | New. |
| One global handler for expired sessions | Partly. `Stopped` carries `AuthFailed` and `Revoked`; each head handles it. | One shared reaction. |
| Streaming query helper | No. | New. |
| Offline mutation queue | No. | Conflicts with the owner's v1 decision. Separate module (section 5.4). |

### 5.2 Modules

| Module | Holds |
|---|---|
| `Bazaar.Client.DataLayer.Abstractions` | `ConnectionState`, `IConnectionStateSource`, `QueryKey`, `IQueryCache`, `InvalidationRule`, `ISnapshotStream`. Pure, no dependency. |
| `Bazaar.Client.DataLayer` | Implementation: state machine, cache with `TimeProvider`, invalidation by event, snapshot-stream helper, global error policy. References `Bazaar.Client.Core` contracts only (`IWireRequester`). |
| `Bazaar.Client.DataLayer.Avalonia` | The small status-bar indicator and its automation name. |
| `Bazaar.Client.DataLayer.OfflineQueue` | Optional, off. See section 5.4. |

One edit lands in the adapter, and it is plumbing, not a feature: `WireClient` exposes `State` (`Connecting`, `Connected`, `Reconnecting`, `Stopped(reason)`) and a `StateChanged` event, set from the existing loop (before the backoff delay, `Reconnecting`; after `welcome`, `Connected`). Its tests extend the existing client tests. This needs a docs-first change to doc 02 section 8, listed in the work packages.

```csharp
namespace Bazaar.Client.DataLayer;

public enum ConnectionPhase { Connecting, Connected, Reconnecting, Stopped }

public sealed record ConnectionState(
    ConnectionPhase Phase,
    int Attempt,                 // 0 when connected
    TimeSpan? NextTryIn,
    WireClientStopReason? StopReason,
    DateTimeOffset Since);

public interface IConnectionStateSource
{
    ConnectionState Current { get; }
    event Action<ConnectionState> Changed;
}

/// <summary>Query key in the form ["gc", gcId] or ["board", boardId]. Equality by segments.</summary>
public readonly record struct QueryKey(params string[] Segments);

public interface IQueryCache
{
    ValueTask<T> GetAsync<T>(QueryKey key, Func<CancellationToken, ValueTask<T>> fetch, CancellationToken ct);
    void Set<T>(QueryKey key, T value);                // a stream is the source of truth once connected
    void Invalidate(QueryKey prefix);                  // marks stale; next read refetches
    IDisposable Watch(QueryKey key, Action changed);
}

/// <summary>Declared in a client module manifest: this wire event invalidates these keys.</summary>
public sealed record InvalidationRule(string EventType, string StreamPattern, Func<WireEvent, QueryKey?> KeyFor);
```

### 5.3 Behaviour

- **Visible state.** Whenever the phase is `Reconnecting`, a small persistent indicator reads "Reconnecting" (localised) and goes away on the next received frame. It is never silent and never a full-screen error (deepdive-46 section 3.1). Reads show cached data, marked stale. To avoid a flicker on an instant reconnect, the indicator waits `client.datalayer.reconnect_indicator_delay_ms` (default 1500 ms; the setting may lower it, and a floor of 5 s is the longest it may wait).
- **Full state after a gap, where state is a snapshot.** A stream declared snapshot (`board:<id>`, Kodo feed, presence) refetches its full state on every reconnect, so a gap self-heals with no gap-fill code. GC streams keep the cursor protocol of doc 02. Both rules sit in the stream's registration, not in the screen.
- **Invalidation by wire event.** A mutation invalidates only its own keys (a group change invalidates `["gc", id]` only). `ui.changed { revision }` invalidates `["ui", document]`. A module declares its `InvalidationRule`s in its manifest, and the data layer applies them with no polling.
- **Optimistic updates** only where rollback is simple and the benefit is real (a read marker, a presence toggle, a preference that touches no access control). Never for approvals, access, roles, security settings or anything that spends money. The exclusion list is a floor, not a setting.
- **Global session handling.** `Stopped(AuthFailed)` or `Stopped(Revoked)` is handled in one place, which asks the head to show its sign-in surface and remembers where the person was. Individual hooks carry no auth code. An `UpgradeRequired` stop shows the update prompt, never a reconnect loop.
- **Immutable reads.** Values returned from the cache are records, so a component cannot edit the cache by accident (the frontend immutability pledge of deepdive-44, as a C# `record` rule).

### 5.4 Offline queue (conflict, separate module)

Resibo resolved that offline mutations queue locally with a visible "pending sync" state (deepdive-46). The owner's decision for Bazaar is the opposite for v1: when the server is down, clients show cached state, and no offline write queue is built (doc 41 section 4.13, 2026-11-12). Both are kept: `Bazaar.Client.DataLayer.OfflineQueue` is a separate optional module that is not composed into any head and has no setting that turns it on in v1. It carries the design only (an ordered queue of idempotent requests with stable ids, which `WireClient` already supports, a pending-sync indicator, per-type allow-list, conflict surface). It cannot be enabled until the owner lifts the v1 rule (Q6). Cost to adopt later is a work package, not a rewrite.

## 6. Data tables and settings

### 6.1 Tables

Only two modules store anything server-side. Tour progress, tour mute and similar are person preferences saved with `prefs.set`, so they need no table (doc 45 section 2.3).

`admin-wire` (migration namespace `src/Bazaar.Server.AdminWire/Data/Sql/admin-wire/`):

```sql
-- 0001_setting_redirect.sql
CREATE TABLE admin_wire_setting_redirect (
    old_path     text        PRIMARY KEY,           -- a former menu path
    new_path     text        NOT NULL,
    since_version text       NOT NULL,              -- SemVer of the release that moved it
    hits         bigint      NOT NULL DEFAULT 0,
    last_hit_at  timestamptz,
    created_at   timestamptz NOT NULL DEFAULT now()
);
-- Rows are written from the shipped `former_paths` metadata at start; only `hits` and `last_hit_at` change at runtime.
```

`help-assistant` (namespace `src/Bazaar.Server.HelpAssistant/Data/Sql/help-assistant/`). No query text is stored:

```sql
-- 0001_help_request.sql
CREATE TABLE help_request (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    ts           timestamptz NOT NULL DEFAULT now(),
    principal    text        NOT NULL,              -- canonical Principal text, e.g. 'user:dom'
    provider     text        NOT NULL,              -- 'offline' | 'npu' | 'gateway' | provider id
    query_sha256 bytea       NOT NULL,              -- never the query text
    outcome      text        NOT NULL CHECK (outcome IN ('answered','no_match','refused','rate_limited','error')),
    tokens_in    integer,
    tokens_out   integer,
    latency_ms   integer
);
CREATE INDEX help_request_principal_ts ON help_request (principal, ts DESC);
```

### 6.2 Settings registry rows

All are new rows. "Who" follows the registry roles (Operator, Owner). A setting may tighten and never loosen past its floor.

| Key | Type | Default | Scope | Who | Notes |
|---|---|---|---|---|---|
| `client.terminal_admin.enabled` | bool | true | install | Operator | Off hides the console; the web panel and clients are unaffected. |
| `client.terminal_admin.boot_animation` | enum `off`/`short`/`full` | `short` | install | Operator | The "skip" control is always present (a floor). `reducedMotion` forces `off`. |
| `client.terminal_admin.find.min_score` | int 0 to 100 | 70 | install | Operator | Score under which the finder offers "ask for help". 70 is Resibo's value (deepdive-54 section 3); tune against the golden queries. |
| `client.terminal.color_depth` | enum `auto`/`truecolor`/`256`/`16`/`none` | `auto` | install | Operator | `auto` honours `NO_COLOR`. |
| `client.datalayer.reconnect_indicator_delay_ms` | int | 1500 | install | Operator | Floor: the indicator shows by 5000 ms. |
| `client.datalayer.query_stale_s` | int | 30 | install | Operator | Default staleness for cache reads that no event covers. |
| `client.datalayer.optimistic.enabled` | bool | true | install | Operator | The exclusion list is not a setting. |
| `client.tour.animation` | enum `auto`/`reduced`/`full` | `auto` | install, person may tighten | Operator | `auto` follows the theme's `reducedMotion` token and the OS flag. |
| `client.tour.sound` | bool | false | install, person may override | Operator | Off by default for accessibility and shared spaces (section 8.1). |
| `client.tour.auto_start` | bool | true | install | Operator | First authenticated load only, per doc 45. Never a reminder banner (floor). |
| `help.assistant.enabled` | bool | false | install | Operator | Whole module off until chosen. |
| `help.assistant.provider` | enum `offline`/`npu`/`gateway` | `offline` | install | Operator | Others are separate provider modules. |
| `help.assistant.rate_per_hour` | int | 30 | install | Operator | Per principal. |
| `help.assistant.gateway.model` | model whitelist ref | none | install | Operator | Empty means the gateway provider is unavailable. Uses the per-agent whitelist mechanism (doc 05) for `service:help-assistant`. |

## 7. Module (c): "the route guard is UX only; the server decides", applied to the web admin panel

Resibo's rule (deepdive-47 and `webapp/CLAUDE.md`): a frontend guard only avoids showing the wrong screen. The security boundary is the server, which refuses the underlying call regardless. Doc 51 already implements the boundary: `AdminGate` per request, `Roles`-based capabilities, 401 with no body when there is no session, 403 with no body and one `admin.web.denied` audit when a capability is missing, revocation re-checked on each request, and a section that shows "unavailable" when its port is missing. What this document adds are the browser-side rules, which doc 51 does not yet state. They are numbered so the main session can fold them into doc 51 section 5:

- **W1. Guards are UX only.** `GET /admin/api/session` returns held capabilities and openable sections. The shell uses them to hide navigation entries. It never uses them to withhold data, and no write is skipped in the browser. A test removes the guard code and shows the server still answers 401 or 403 for every route.
- **W2. One place handles an expired session.** A single `fetch` wrapper (`wwwroot/js/api.js`) turns any 401 into a redirect to `/signin?audience=admin&returnTo=<path>`. No section script handles auth on its own. The panel never counts the session down or refreshes it on a timer (the 15 minute sliding window belongs to the server).
- **W3. Return to where I was.** `returnTo` is accepted only when it is a same-origin path under `/admin`. Anything else is dropped to `/admin`. This closes the open-redirect hole that Resibo's deep-link rule would open if copied literally.
- **W4. Multi-tab.** Sign-out or revocation in one tab notifies the others through `BroadcastChannel` (with a `storage` event fallback), carrying no secret and no user data.
- **W5. Bootstrap by one "who am I" call.** The shell makes exactly one call (`GET /admin/api/session`), as in Resibo's step 6. It makes no other call before it succeeds.
- **W6. No single-user bypass.** Resibo's single-tenant implicit-owner path is not adopted. Bazaar's panel always requires the passkey session of doc 32 section 4.

Where it differs from Resibo: the cookie stays `SameSite=Strict` and `__Host-` prefixed (Resibo uses Lax for an OIDC redirect; Bazaar's sign-in is a passkey ceremony and does not need Lax). Auth is passkey, not OIDC. The panel stays plain JavaScript: Resibo's React, Zustand and TanStack Query are not adopted, because doc 51 decided plain HTML, CSS and JavaScript with zero external installs. Only the patterns are taken. No new module is created for this: the rules are implemented in `Bazaar.Server.WebAdmin` assets, with tests in its test project, once doc 51 adopts them.

## 8. Modules (d), (e), (f), and the supporting client modules

### 8.1 Design-system tokens (d)

Resibo's design system (deepdive-45) has `theme/tokens.ts` (colour, spacing, type scale), a `ThemeProvider` reading a per-deployment override before first paint, dark mode as another token set, shadcn primitives, a generic `DataTable<T>` and `DiffViewer`, and a stated accessibility requirement. Bazaar already has the equivalent rule: one validated theme file (`ThemeModel`, `ThemeValidator`, `ThemeTokens.Required`, schemaVersion 1) drives Avalonia (`AvaloniaThemeGenerator`) and Monaco (`MonacoThemeGenerator`), is revisioned per scope (install, company, person, device), rejects URLs, scripts and remote fonts, and enforces 4.5:1 contrast pairs. The rule is: map, do not fork.

| Resibo | Bazaar mapping |
|---|---|
| `tokens.ts` colour, spacing, type scale | The existing required tokens (`color.*`, `space.*`, `font.*`, `radius.*`, `reducedMotion`). No second token list. |
| Per-deployment override before first paint | Server-revisioned theme scopes (doc 41 section 4.4). Test: first paint uses the cached profile, no flash of the default (doc 41 section 4.7 target, to be measured). |
| Dark mode as a token set | Another theme document in the same schema. |
| Accessibility requirement | Contrast pairs already enforced; the keyboard and name rules of doc 41c section 7. |
| Primitives (Button, Dialog, Toast) | The 13 Rubedo widgets. A dialog and a toast are not in the set (Q2). |
| `DataTable<T>`, `DiffViewer` | Not widgets today. Section 4.3 and Q2. |

Two more generators consume the same file, so the rule "one theme file" extends to every surface Bazaar now has:

1. A CSS generator for the web admin panel (`:root { --color-text-primary: ... }` custom properties), served from the embedded assets. It must emit only the validated token values, never user text.
2. A terminal palette generator for the TUI: tokens to 24-bit colour, with 256 and 16 colour fallbacks and a no-colour mode, checked against the same contrast pairs after quantisation.

Prerequisite, an overlap with doc 41b: the theme model, validator and generators sit in `Bazaar.Client.Core/Theme/`, which the server cannot reference, and the server serves the panel. The pure parts (`ThemeModel`, `ThemeTokens`, `ThemeValidator`, the CSS and terminal generators) belong in a shared module, `Bazaar.Theme` (pure, no dependency), with the Avalonia and Monaco generators staying client-side. Doc 41b notes the Client.Core theme code "needs its own map" and none exists, so this move is PROPOSED here (work package P1). Candidate token additions are OPEN and need a schema version bump, since unknown tokens are rejected: status colours (`color.status.ok`, `.warn`, `.error`, `.info`, which Kodo and the TUI both need), a focus ring colour, and a motion duration. Nothing is added by this document.

### 8.2 Guided tour (e)

Existing: `Bazaar.Michi` has `TourDefinition` and `TourStep` (title and body keys, optional anchor), `TourSession` (next, back, skip, restart, resume), `TourProgress(TourId, StepId, Status)` and `ITourStateStore`, a deliberately synchronous per-device store whose failure never blocks a tour. Doc 45 section 2.3 says progress is saved with `prefs.set` so it follows the account, and a local cache is a convenience.

What Resibo adds (deepdive-53): a closed animation set (`fade-in`, `slide-in`, `pulse-highlight`), optional per-step sound, a visible mute control that persists across steps, `useReducedMotion()` as a hard requirement with the full content reachable, server-side progress for cross-device resume, `skippedAt` and `completed` kept as history so replay does not erase them, auto-trigger on first authenticated load only, and never a reminder banner.

Mapping, with no change to Michi's existing records (additive, in a new module):

| Module | Holds |
|---|---|
| `Bazaar.Michi.Presentation` (pure) | `StepPresentation(StepId, TourAnimation Animation, string? SfxId)` kept apart from `TourStep`; `MotionPolicy` (resolves `full` or `reduced` from the theme `reducedMotion` token, the OS flag and `client.tour.animation`); `TourAudioPrefs(Muted)`; `TourHistory(CompletedAt, SkippedAt)`. |
| `Bazaar.Client.Michi.Sync` | An `ITourStateStore` over `prefs.set` and `ui.get`: loads into a local cache, writes behind, and tolerates failure as the interface requires. The saved JSON is `{stepId, status, completedAt?, skippedAt?, muted}` under key `tour.<tourId>`. Replay (`tour` palette command, doc 45) calls `Restart()` and keeps `completedAt`. |
| `Bazaar.Client.Michi.Avalonia` | Overlay with highlight and the animations. Also the Terminal head's text overlay (a highlighted anchor, no animation). |
| `Bazaar.Client.Michi.Audio.<Platform>` | Provider modules, since audio playback needs a platform library. Not shipped in v1. |

Rules adopted: reduced motion gives instant transitions, same content, same order (test: the step list is identical under both policies); the mute control is visible on every step and persists; skip and complete retire the tour for the account, which resumes on any device; no persistent reminder, ever (a regression test asserts no widget is created after skip or completion); auto-start only on the first authenticated load. Where Bazaar differs: sound is off by default (Resibo plays sound by default), because doc 45 section 2.2 forbids content that appears only through an effect, and a licensed SFX pack must be redistributable under AGPL. Whether a sound pack ships at all, and under what licence, is OPEN (Q7). Doc 45's rule "no timers, no auto-advance" is kept; Resibo's animation never advances a step.

### 8.3 Help assistant (f), optional, PROPOSED

Resibo's assistant (deepdive-54) answers natural-language queries from static bundled context only: the menu data that `find_setting` indexes plus an authored troubleshooting KB. It has zero access to user data, no persistent chat surface, no tool-calling loop, one request and one response, and it falls back to the normal search when it cannot resolve a query. It runs on a Cloudflare Worker with Workers AI.

Bazaar fit: the command bar already has an "AI matching suggests only, never runs" key (Shift+Enter, doc 41 sections 4.11 and 4.12). The assistant is the backend for the help and settings part of that key, not a new surface. Resibo's automatic offer at a score under 70 becomes a hint line ("press Shift+Enter to ask"), because Bazaar's AI key is explicit.

Modules:

| Module | Holds |
|---|---|
| `Bazaar.Client.HelpAssistant.Abstractions` | `IHelpCorpus`, `HelpQuery`, `HelpSuggestion(Target, Why)`, `IHelpAnswerProvider`. Pure. |
| `Bazaar.Client.HelpAssistant` | Corpus loader, offline retrieval (lexical scoring over the corpus, using the same scorer as find), result validation. Works with no model. |
| `Bazaar.Client.HelpAssistant.Npu` | Provider over the local inference module (`Bazaar.Client.LocalInference`, doc 31). It uses the existing client compute exception for NPU inference. |
| `Bazaar.Server.HelpAssistant.Abstractions`, `Bazaar.Server.HelpAssistant` | Component id `help-assistant`. A provider that sends one request through the model gateway as `service:help-assistant` under a model whitelist. |
| `Bazaar.Server.HelpAssistant.Cloudflare` | Optional provider with Resibo's exact shape (a Worker with Workers AI). Off, not needed by anything, listed so that the Resibo option is accounted for. It requires the operator's own Cloudflare account, and it needs a privacy note, since the typed query leaves the install. Not proposed for any default (Q8). |

```csharp
namespace Bazaar.Client.HelpAssistant;

/// <summary>The only input a provider ever sees: the typed text and static corpus snippets.</summary>
public sealed record HelpQuery(string Text, string Locale, IReadOnlyList<CorpusSnippet> Snippets);

/// <summary>An answer can only point at an id in the closed catalogue. Free text is a rationale, never a command.</summary>
public sealed record HelpSuggestion(string TargetId, TargetKind Kind, string Why);

public interface IHelpAnswerProvider
{
    string Id { get; }
    ValueTask<IReadOnlyList<HelpSuggestion>> AskAsync(HelpQuery query, CancellationToken ct);
}
```

Hard boundaries, each with a test (taken from deepdive-54 section 8, made stronger):

1. **No data access.** The client module and the server module reference no data module. An architecture test fails the build if either project references anything other than `Bazaar.Contracts`, its own abstractions and the settings and command catalogue abstractions. The corpus is a file produced at build time (the company build, doc 37), never a live query.
2. **No tools, one round.** The provider interface has no tool parameter and no loop. The gateway call has an empty tool list, and a test asserts it.
3. **Output is validated against the catalogue.** A suggestion naming an id that is not in the corpus is dropped. A hallucinated setting cannot be navigated to. A suggestion is only a navigation or a doc link, and the user's Enter performs it (suggest only, never run).
4. **The query is untrusted input.** It never enters an agent session and never reaches any other module. A model answer is untrusted text (taint rules of doc 15 apply: the output is shown, not acted on).
5. **Never a dead end.** Any failure falls back to the offline result list.
6. **Privacy.** The typed query can contain anything a person types. With `provider = gateway`, the query leaves the device for the model route; setup warns that some free tiers train on prompts (CLAUDE.md, free-tier data rule). The audit row holds a hash of the query, never the text.

The support-ticket integration of Resibo's assistant (suggestions before filing) has no Bazaar counterpart yet, since no support-ticket module exists. The nearest are the crash-report and feedback flows (doc 43, `Bazaar.Diagnostics`). Offering corpus suggestions before a report is filed is OPEN (Q9) and not designed here.

### 8.4 Credits and version history

Resibo's credits screen satisfies third-party notice duties and doubles as the codename and version-history screen (deepdive-14 section 5). Bazaar has the same duty: AGPL-3.0 core, other licences in dependencies, and a notice file in the build (doc 47, doc 37). Module `Bazaar.Client.Credits` (+ `.Abstractions`) reads a notice list produced by the company build (`ICreditsSource`: component, version, SPDX id, notice text, source link) and the version history (`0.MM.PP+commit`, codename per major, kept indefinitely because LTSC channels can run an old major). It is shown by the TUI `credits` screen and by an Avalonia About pane. Whether the screen must carry an AGPL network source offer, and its exact wording, is a counsel question (doc 47) and is not stated here.

## 9. Module: history timeline (the Historian display pattern only)

Resibo's receipt screen (deepdive-48) interleaves data-change entries with narrative entries in one timeline. The generic pattern: render the server's order verbatim; never merge or re-sort on the client; show each revision as a distinct block, never merged; load more by paging, not by virtualisation; subscribe to live updates through the same mechanism as the run monitor; and let a person click a field to see why it changed.

Bazaar has several places that want exactly this: GC message versions ("every version is kept with an edited marker"), Kanban card history, Vault page history, and audit browsing. Module: `Bazaar.Client.Timeline` (+ `.Abstractions`): a view declaration `bazaar.timeline.history` over a keyed `list`, so it needs no new widget, plus the contract below. It adds no server table. Each owning module (GC, Kanban, Vault, audit) implements `IHistorySource` over its own data, and the actor of every entry is shown as a `Principal` (`user:dom`, `agent:charles`).

```csharp
public sealed record HistoryEntry(
    string Id, DateTimeOffset At, Principal Actor, HistoryKind Kind,   // Change | Narrative | Revision
    string? FieldPath, string TextKey, IReadOnlyDictionary<string,string>? Args);

public interface IHistorySource
{
    IAsyncEnumerable<HistoryEntry> PageAsync(string subjectId, string? before, int limit, CancellationToken ct);
    IAsyncEnumerable<HistoryEntry> TailAsync(string subjectId, CancellationToken ct);  // live, via the data layer
}
```

The narrative track stays server-made: the client shows localised text and never composes a sentence from fields. Resibo's `LEVEL_STYLE_HINT` idea (severity styling comes from a hint the server sends, no colour map in the client) is kept as a rule: the entry carries a severity token and the client maps it through theme tokens only.

## 10. Audit events, Kodo health, and interaction

### 10.1 Audit events

All use `AuditEntry(Ts, Actor, Kind, Payload)` with `Actor` the canonical `Principal` text. Payloads carry ids and names, never a secret, a token, a cookie or query text. A write whose audit row cannot be appended is refused (fail closed), as in doc 51.

| Kind | Written by | Principal | Payload |
|---|---|---|---|
| `admin.wire.read` | `admin-wire`, once per section load | `user:<id>` | section, surface (`tui`, `avalonia`) |
| `admin.wire.denied` | `admin-wire` gate | `user:<id>` | route or section, missing capability |
| `admin.wire.write` | `admin-wire`, per privileged action | `user:<id>` | target, outcome; the owning module still writes its own row (settings, roles, modules) |
| `admin.menu.redirect` | `admin-wire`, first hit of each former path per day | `user:<id>` | old path, new path |
| `help.request` | `help-assistant` | `user:<id>` asking; the model call is attributed to `service:help-assistant` | provider, outcome, token counts, query hash |
| `help.provider.refused` | `help-assistant` | `service:help-assistant` | reason (rate limit, unavailable, validation dropped all) |

Tour progress, mute and skip are preferences, not audit events.

### 10.2 Kodo health contract

Each server module implements `IModuleHealthContract` (`LivenessAsync`, `ReadinessAsync`, `SelfTestAsync(scope)`, doc 42 and `Bazaar.Server.Modules.Abstractions`), with Kodo ids `<module>.<check>`:

| Module | Liveness | Readiness | Self-test |
|---|---|---|---|
| `admin-wire` | Handlers registered | Gate and at least the settings catalogue port resolve; a missing section port degrades that section only (soft edge, amber) | Round trip `admin.menu.get` against a fake catalogue; every menu target resolves to a registered setting or command (the integrity check of section 13) |
| `help-assistant` | Loop running | Corpus file present and parsed; for `gateway`, the model route is reachable. Disabled means "not applicable", not red. | Query a known phrase and assert a catalogue id comes back |

Client modules have no Kodo contract of their own. The data layer's `ConnectionState` is a client health signal. It is shown by the Kodo Monitor pane (`Bazaar.Client.Kodo`) and may be sent as telemetry only under the consent rules of doc 43 (Q10).

### 10.3 What is reused, exactly

| Reused | From | For |
|---|---|---|
| View documents, closed widgets and actions, validator, golden corpus | `Bazaar.Rubedo`, `Bazaar.Client.Views` (doc 41c) | The menu and the timeline |
| `FuzzyScorer`, `CommandMatcher`, `CommandRegistry` | `Bazaar.Client.Core/Commands` | `find_setting`, help retrieval |
| Mnemonic grammar and session | `Bazaar.Ex` | Terminal navigation, no Ctrl chords |
| `LocalisedText`, `MessageCatalog` | `Bazaar.Localisation` | All text |
| `AccessibleName`, `SpokenMnemonic` | `Bazaar.Accessibility` | Names for controls and mnemonics |
| `WireClient`, backoff, cursors, `resync`, `lagged`, idempotent resend | `Bazaar.Client.Core/Wire` | Data layer (not duplicated) |
| `TourDefinition`, `TourSession`, `ITourStateStore` | `Bazaar.Michi` | Tour (extended, not replaced) |
| Admin ports, `IAdminGate`, capability names | doc 51 (`Bazaar.Server.WebAdmin.Abstractions`) | AdminWire |
| `KodoStatusFeed` | `Bazaar.Client.Kodo` | TUI Monitor |
| `IAuditLog`, `AuditEntry` | `Bazaar.Audit.Abstractions` | Section 10.1 |
| Theme file and validator | `Bazaar.Client.Core/Theme` | Moved to `Bazaar.Theme` (P1), not forked |
| `IModuleHealthContract` | `Bazaar.Server.Modules.Abstractions` | Section 10.2 |
| Local inference module | `Bazaar.Client.LocalInference` (doc 31) | The NPU help provider |

## 11. What is taken from Resibo, and what is changed

| Resibo source | Taken | Changed for Bazaar, and why |
|---|---|---|
| `docs/apis/v3-deepdive-14-interface-api.md` sections 3 to 3.3, 5, 6; `services/interface/CLAUDE.md` | Menu-data rule, `MenuItemSpec` with `former_paths`, `find_setting`, the closed custom-screen list with written justification, Fleet and Updates wording, boot screen with a skip control, credits, key-based i18n | Menu data is a Rubedo view, not a Python dataclass tree. Search reuses `FuzzyScorer`, not rapidfuzz. Screens are Bazaar's (approvals, members, wizard), not OCR and vendors. |
| `v3-deepdive-44-webapp.md` | Thin client, no business logic, immutability, route groups as UX only | No React, no Zustand, no TanStack. Doc 51 stays plain JavaScript. A full web client is not proposed (Q1). |
| `v3-deepdive-45-design-system.md` | Tokens not literals, per-deployment override, dark as a token set, accessibility, one table and one diff component | One theme file, extended to CSS and terminal outputs. Table and diff blocked on the widget set (Q2). |
| `v3-deepdive-46-client-data-layer.md` | Visible "Reconnecting", full state on reconnect for snapshot streams, event invalidation, optimistic update exclusions, one global auth handler | Backoff, resubscribe and replay already exist and are not rebuilt. Offline queue becomes a separate off module. Cap stays the operator setting (60 s default). |
| `v3-deepdive-47-frontend-auth-session.md` | Guard is UX only, one global expiry handler, multi-tab notice, return-to-page | Passkey, not OIDC; `SameSite=Strict`; `returnTo` restricted to `/admin`; no single-tenant bypass. |
| `v3-deepdive-48-receipt-detail-screen.md` | Server order verbatim, distinct revision blocks, paging, live tail, field drill-down | Domain screen not adopted; only the pattern, as `Bazaar.Client.Timeline`. |
| `v3-deepdive-53-guided-onboarding-tour.md` | Animation set, mute, reduced motion as a hard rule, server-side progress, no nag, replay keeps history | Built on Michi, not beside it. Sound off by default. Auto-start follows doc 45. |
| `v3-deepdive-54-webapp-assistant.md` | Static-corpus-only, zero data access, no tools, single round, fallback, tests | Backend is the client NPU, the model gateway, or an optional Cloudflare provider; suggest only; catalogue-validated output. |
| Not taken | OCR diff viewer, vendor and branch editor, receipt screens, billing and account screens, Gateway-side build-and-serve of the webapp, shadcn | Domain-specific, or replaced by an existing Bazaar decision. |

Verification label: deepdives 14, 45 and 54 and `webapp/CLAUDE.md` were read directly. Deepdives 44, 46, 47, 48 and 53 were read through the convergence inventory (`/mnt/project-files/convergence/inventory/resibo-40-49.md`, `resibo-50-56.md`), which quotes their decisions and paths. Treat details drawn from those five as unverified against the originals until a worker opens them.

## 12. Security and privacy

- **The server decides.** Every AdminWire request goes through `IAdminGate` with the caller's `Principal`. Only human principals pass; an agent session gets nothing. A hidden menu entry is not protection. A test calls every `admin.*` request with no capability and expects `denied`.
- **Menu data carries no values.** `admin.menu.get` returns structure and the current value for a row only when the caller may read that key. Secrets are never shown: a setting that holds a secret reference shows the reference id only (doc 14).
- **Writes use the existing paths.** A setting change goes through the settings store and its approval tier; a role change through the roles model. The console adds no privilege. Privileged actions ask for confirmation in the console, and that confirmation is UX only.
- **Terminal safety.** All text from the server (names, tooltips, narrative lines, help answers) is stripped of control and escape sequences before it reaches the terminal, since an agent-authored or model-authored string can carry ANSI codes. This is an injection defence, tested with escape-laden fixtures.
- **Web panel.** Rules W1 to W6 in section 7. The embedded assets load no external URL, which doc 51's asset scan already tests.
- **Help assistant.** Section 8.3. The query hash, never the text, is logged. The module cannot reach user data by construction.
- **Theme output.** The CSS and terminal generators emit only validated tokens. A test feeds a theme with a hostile value and confirms the validator rejects it before generation.
- **Credits.** The notice list is generated at build time, never fetched at runtime.

## 13. Testing

xUnit v3 everywhere. UI changes get `Avalonia.Headless` tests. The TUI has no display, so a fake `ITerminalSurface` records draw calls and key input, which makes every screen testable without a terminal.

- **Menu integrity.** Every `MenuItemSpec.Target` resolves to a registered setting or a manifest command; every `LabelKey` and `TooltipKey` exists in the default catalogue; no two items share a path; every former path points at an existing path. This is the TUI form of Resibo's CI menu-data check.
- **Menu view.** The `bazaar.admin.menu` document validates against the Rubedo view validator and renders to the same drawn output under the terminal adapter and (headless) the Avalonia adapter.
- **find_setting regression.** A golden file of phrases and expected paths; a moved setting resolves through its former path; the redirect counter increments once.
- **Closed screen list.** The screen registry equals the table in section 4.4. A new id fails the test until the doc changes.
- **Data layer.** With a fake transport and `FakeTimeProvider`: the state goes `Connected`, `Reconnecting`, `Connected`; the indicator shows after the delay and clears on the next frame; a snapshot stream refetches in full after reconnect; a GC stream does not refetch and keeps its cursor; an event invalidates only its declared keys; an `AuthFailed` stop is handled once, in one place; the optimistic exclusion list refuses an access-control change.
- **Offline queue module.** It is not referenced by any head (architecture test).
- **Web panel.** Guard removed, server still refuses; every 401 funnels through the one wrapper; `returnTo` outside `/admin` is dropped; sign-out in one tab reaches another (a script test with a stub `BroadcastChannel`).
- **Tour.** Reduced motion and full motion give the same step list; mute persists across steps; skip then restart keeps the history; no widget appears after skip or complete; progress resumes across two fake devices; an unavailable store never blocks the tour (existing contract).
- **Help assistant.** The static reference test (section 8.3, item 1); an empty tool list; a hallucinated id is dropped; each failure falls back to offline results; the audit row has a hash and no text.
- **Timeline.** Server order is rendered verbatim; revisions stay distinct; paging asks for the next page; live tail appends; severity maps through tokens only.
- **Theme.** CSS and terminal palettes derived from one fixture theme pass the same contrast pairs; no remote URL in any output.
- **Terminal safety.** Escape-laden strings are rendered inert.
- **Accessibility.** A keyboard-only walk of every screen with the fake surface; a screen-reader pass in the owner's environment before release (headless tests cannot cover it, doc 17).
- **Kodo.** `Bazaar.Kodo.Testing` base class run over `admin-wire` and `help-assistant`.

## 14. Work packages

Each package is small, owns one project with its tests, and is its own PR. Docs first: the doc changes (D1 to D3) land before code. "Needs" lists dependencies; unlisted ones are independent.

| ID | Package | Needs |
|---|---|---|
| D1 | Doc 02: `WireClient.State` and `StateChanged`; snapshot-stream rule. Doc 51 section 5: rules W1 to W6. Doc 41c: terminal adapter, widget questions. | owner Q2, Q5 |
| D2 | Doc 41b and 40: add `Bazaar.Theme`, `Bazaar.Client.TerminalAdmin`, `.DataLayer`, `.Timeline`, `.Credits`, `.HelpAssistant`, `Bazaar.Michi.Presentation` to the module tables. | none |
| D3 | Doc 03/CLAUDE.md open item "TUI status client": point to this module as the PROPOSED answer. | owner Q3 |
| P1 | `Bazaar.Theme` (pure): move model, tokens and validator; add CSS and terminal palette generators. Client.Core keeps only the Avalonia and Monaco generators. | D2 |
| P2 | `Bazaar.Client.DataLayer.Abstractions` (types, no logic). | none |
| P3 | `WireClient` state and event in `Bazaar.Client.Core` (plumbing only). | D1 |
| P4 | `Bazaar.Client.DataLayer`: state machine, cache, invalidation, snapshot streams, global error policy. | P2, P3 |
| P5 | `Bazaar.Client.DataLayer.Avalonia`: indicator and automation name. | P4 |
| P6 | `Bazaar.Client.DataLayer.OfflineQueue`: contracts and queue only; not composed anywhere. | P4, owner Q6 |
| P7 | `Bazaar.Server.AdminWire.Abstractions` and `Bazaar.Server.AdminWire`, with the `admin-wire/0001` migration, menu catalogue port and gate use. | doc 51 ports |
| P8 | `Bazaar.Client.TerminalAdmin.Abstractions`: `MenuItemSpec`, `ITerminalSurface`, screen ids. | none |
| P9 | `Bazaar.Client.Views.Terminal`: text adapter for the 13 widgets over `ITerminalSurface`, with the plain ANSI surface as fallback and test double. | P8, D1 |
| P10 | `Bazaar.Client.TerminalAdmin`: menu walker, `find_setting`, former-path redirect, menu view document, screens `find`, `monitor`, `credits`. | P7, P8, P9 |
| P11 | TerminalAdmin screens `boot`, `run-monitor`, `fleet-updates`. | P10, P4 |
| P12 | TerminalAdmin screens `approvals-queue`, `diff-review`, `members-roles`, `wizard`. | P10, doc 45 setup plan |
| P13 | `Bazaar.Client.TerminalAdmin.Render.<Lib>` provider. | P8, owner Q3 |
| P14 | `Bazaar.Michi.Presentation` (pure). | none |
| P15 | `Bazaar.Client.Michi.Sync` (`prefs.set` store) and tests. | P14 |
| P16 | `Bazaar.Client.Michi.Avalonia` overlay with headless tests; Terminal overlay. | P14 |
| P17 | `Bazaar.Client.Credits` and its build-time notice list contract. | doc 37 build hook |
| P18 | `Bazaar.Client.Timeline` and one pilot `IHistorySource` (GC message versions). | P4 |
| P19 | `Bazaar.Client.HelpAssistant.Abstractions` and `Bazaar.Client.HelpAssistant` with offline retrieval and the architecture test. | P10 |
| P20 | `Bazaar.Server.HelpAssistant.Abstractions`, `Bazaar.Server.HelpAssistant` (gateway provider), `help-assistant/0001` migration, Kodo contract. | P19, gateway port |
| P21 | `Bazaar.Client.HelpAssistant.Npu`. | P19, doc 31 backend |
| P22 | Web panel rules W1 to W6 in `Bazaar.Server.WebAdmin` assets with tests. | D1, doc 51 skeleton |
| P23 | `Bazaar.Server.HelpAssistant.Cloudflare` (optional, off). | P20, owner Q8 |
| P24 | `Bazaar.Client.Michi.Audio.<Platform>` providers. | P16, owner Q7 |

Order of the first wave: D1, D2, P2, P8, P14 (all independent), then P1, P3, P4, P7, P9, P15, then the screens and remaining modules.

## 15. Open questions for the owner

Each has a default, the one this document uses.

1. **Full web client.** The 2026-11-17 removal allows one, and Resibo has a React design. Default: no. Doc 51's panel stays plain JavaScript, a `Bazaar.Client.Web` is not designed, and only patterns are taken. If wanted, it would be its own module and must reuse the data-layer contracts as generated JSON shapes, not hand-copied.
2. **Rubedo widget set.** Add `table`, `diff`, a toggle and a dialog or toast, as a doc 41c version change, or keep the 13 and use custom screens? Default: keep the 13; table and diff stay in the custom screen list.
3. **Terminal UI library and the TUI status client.** Which library (a provider module), and is this the answer to the CLAUDE.md OPEN "TUI status client"? Default: plain ANSI provider first; library choice after a spike; this module is a PROPOSAL only.
4. **In-client admin wire contract.** Does one already exist or is planned elsewhere? Default: `Bazaar.Server.AdminWire` is that contract.
5. **Wildcard setting capability.** May the TUI and clients declare `setting:*`, with the server checking each key? Default: yes, server decides.
6. **Offline write queue.** Keep the v1 rule (default) or lift it for selected request types? The module exists but stays unreferenced until you say so.
7. **Tour sound.** Ship a sound pack at all, and under which licence? Default: none shipped; setting off; audio providers deferred.
8. **Cloudflare assistant provider.** Wanted as an optional provider? Default: designed as a stub only, no default, not needed.
9. **Help before reports.** Offer corpus suggestions before a crash or feedback report is filed (doc 43)? Default: not designed.
10. **Client connection telemetry.** May `ConnectionState` summaries be sent under the doc 43 consent rules? Default: no, local only.
11. **Theme extraction.** Confirm moving the pure theme code into `Bazaar.Theme`, and approve candidate status, focus and motion tokens as a schema version bump. Default: extraction yes; tokens not added until approved.
