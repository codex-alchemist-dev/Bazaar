# 85. Resibo receptivity: what the shared CDAX libraries and Bazaar's modules must stay open to

Status: PROPOSED. Design intent only; nothing here is built. Owner correction (2026-11-19): Bazaar is not designing Resibo or its Retail wing. This document lists only the contracts and extension points that the shared CDAX libraries and Bazaar's own modules must keep open, so a future Resibo Retail wing can plug in later without reworking them. Every row is Bazaar's own PROPOSED design.
Depends on: BAZAAR-COMPONENTS.md (module model, section 4.2 `Principal`), 40-core-feature-modules-audit.md, 42-deps-as-modules.md, 02-wire-protocol-and-sync.md, 90-wire-transports-and-codecs.md, 49-gap-decisions-operations.md (offline merge rules), 48-gap-decisions-security-identity.md (audit chain, KEK, amounts), 14-identity-service-and-passport.md, 46-kodo-health-module.md, 74 (hardware probe), 76 (export framework), 81-program-logs-audit-vocabulary-and-retention.md, 83 (client patterns), DECISIONS-LOG.md (rounds 13, 25, 26, 28), `/mnt/project-files/convergence/CONVERGENCE.md` (sections 4 and 4a).
Used by: the extraction of Aludel, Kodo, Ouroboros, Tabula, Annales, Rubedo, the settings registry and the testing kit; doc 84 (the Consumer-side counterpart); any later doc that touches a contract named below.

## 1. Purpose and scope

Resibo is `Resibo.Core` plus two wings built as modules on it. A future Retail wing is a modular point-of-sale system: payment providers, card terminals, registers, offline sync, multi-branch, running on a home server or hosted. Bazaar does not design it. Bazaar only keeps its shared libraries and extractable modules from assuming a company of agents, a single database, a single tenant, or a screen with a human in front of it.

The test for every row is the same: the change is a field, an optional contract or a setting in a Bazaar-owned place, it is useful to Bazaar on its own, and it contains no Retail code or Retail vocabulary beyond the example in the "why" column.

Out of scope, and not decided here: how Resibo-Consumer works (doc 84); any Retail module, screen, schema, rule, pricing or work package; which vendors Retail would support; local tax or fiscal law; owner questions about Retail products. Retail's own design will decide those in its own repo.

## 2. Decided inputs

- Owner, 2026-11-19: Resibo is `Resibo.Core` plus two wings that are modules on it. Bazaar is made receptive; Resibo is not built now. Shared repos hold contracts and pure logic only, with no third-party dependencies (convergence section 4a). Providers stay in Bazaar as plain modules; there is no selector and no role setting (owner, PR #919).
- Owner cards, 2026-11-19: generic `Principal` (agent, user, service, system) and `Scope` (install, tenant, user) in `Cdax.Core`; Aludel has one `IKekProvider` contract and is multi-database; Tabula's contract carries retention window, operation vocabulary and a reason-tagged access-grant event type; the settings registry is in wave 1; Identity stays a Bazaar module for now.
- Owner, 2026-11-19: "modules are a thing"; an installer can use any payload, "just have the modules".
- Owner, 2026-11-19 (DECIDED; PR #872, doc 48 section 11, DECISIONS-LOG round 28): amounts are exact and of arbitrary precision, "make the disk the limit": `numeric` when the value fits, chunked rows in the same Postgres beyond that, no field encryption, a length guard at intake.
- Owner, 2026-11-19 (DECIDED; PR #927, doc 90): JSON over WebSocket stays; every other transport and codec is an optional module.
- Owner rule, 2026-11-19 (DECIDED direction): every Resibo export will eventually be in a BIR-ready format (BIR is the Philippine tax authority). The format details are OPEN until the specifications are in hand; section 9 provides only the slot.
- Owner rule, 2026-11-19 (DECIDED direction): everything is stored in a proper database, never in Excel workbooks. Spreadsheets are export outputs only. This is a floor, not a setting (section 10).
- DECISIONS-LOG: Resource Economy (money budgets) is REJECTED. That concerns agent budgets only and says nothing about money arithmetic in a future commerce module.
- CLAUDE.md: .NET 11; JSON via `IWireCodec`; AGPL root with a module exception; configurability rule (round 13); server does all compute with listed client exceptions.

## 3. Conventions

- `Cdax.*` is a shared library (contracts and pure logic, no third-party dependency). `Bazaar.<X>` is a Bazaar module. A provider is its own plain module registered through the module manifest; an operator enables the ones they want, and when several are enabled a feature asks each (the multi-active provider set, convergence A1).
- Column "Owner" is the doc or module that owns the contract. Column "Change now" is the smallest change to make while it is cheap; "none" means the contract already fits.
- Retail names appear only as a pattern, for example "a physical register is a service principal". They are examples, never designs.

## 4. Cdax.Core: Principal, Scope, manifest, clock

| Ref | Extension point or contract | Owner | Change now |
|---|---|---|---|
| R-01 | A device is a `Principal` of kind service, never a user | `Cdax.Core`, BAZAAR-COMPONENTS 4.2 | Document the canonical text form `service:<device-id>`. No new kind. |
| R-02 | A record may be performed by one principal on behalf of another (a device acting for a signed-in person) | `Cdax.Core`; Tabula contract | Allow an optional `onBehalfOf` `Principal` beside `Principal`. Audit entries carry both. Useful to Bazaar for agents acting for a user. |
| R-03 | Scope has three kinds (install, tenant, user); a tenant may contain sub-units | `Cdax.Core` (owner card A2) | Document a sub-path inside a tenant scope (`path="branch/<id>"`) instead of adding a fourth kind. The three decided kinds stay. |
| R-04 | Module families can be grouped and kept independent | BAZAAR-COMPONENTS sections 2 to 4; doc 40 | Add a free-text manifest field `family` (grouping only, no behaviour). Add an architecture rule that a module never names another family's ids in `dependsOn`; the rule lives in the testing kit. |
| R-05 | Which copy of a data class is authoritative (install, tenant, sub-unit) | manifest, `Cdax.Core` | Add an optional manifest list `dataAuthority` that the sync engine and installer can read. Declarative only. |
| R-06 | Several provider modules of one kind enabled at once | `Cdax.Core` (convergence A1) | Make the multi-active provider set a first-class kind so a feature can ask every enabled provider what it supports. |
| R-07 | Device clocks may be wrong; records need ordering that survives it | `Cdax.Core` clock contract; doc 49 (HLC); testing-kit `FakeClock` | Records carry device time, hybrid logical clock and server arrival time. Drift beyond a limit raises a Kodo attention (doc 81). |

## 5. Settings registry, Aludel, Tabula, Annales

| Ref | Extension point or contract | Owner | Change now |
|---|---|---|---|
| R-08 | Settings resolved by scope with floors that no narrower scope may loosen | `Cdax.Settings` (convergence section 4; doc 03) | Resolve most-specific-wins across install, tenant, tenant sub-path, user, with the floor rule enforced in the resolver. Emit reload events. |
| R-09 | Tenant isolation without callers passing a tenant id | `Cdax.Aludel` contract; `Bazaar.Data` provider modules | Define `ITenantStore.Open(Scope)` returning a store bound to one tenant; no API accepts a tenant id on a query. Provider decides schema-per-tenant or database-per-tenant (operator setting). |
| R-10 | Several databases and engines behind one contract | `Cdax.Aludel` (convergence 4a; doc 04) | Keep the multi-database contract free of Postgres-only assumptions so a second engine can follow. |
| R-11 | Key purposes and per-tenant keys | `Cdax.Aludel`, `ISecretStore`, `IKekProvider` | Add a documented purpose enum (`TenantData`, `DeviceCredential`, `Signing`, `ChannelComms`); per-tenant data keys wrapped by the KEK, so deleting a tenant destroys its key (crypto-erase). |
| R-12 | Signing without exposing the key | `Cdax.Aludel`; doc 48 section 5 | Add an `ISigner` operation that signs inside Aludel and never returns key material. |
| R-13 | Hardware-backed keys on devices (TPM, secure enclave, Android keystore) | `Bazaar.Aludel.<Provider>` modules | None to the contract; each is a provider module. Android keystore ties to doc 29. |
| R-14 | More than one tamper-evident chain, each independently verifiable | `Cdax.Tabula`; doc 48 sections 5.2 to 5.4 | Allow named chains (`ChainId`) instead of one global chain. |
| R-15 | Retention window per class and tenant, with a floor | `Cdax.Tabula` (owner card) | Retention is a per-tenant, per-class setting with a floor. No default is hard-coded in the shared library. |
| R-16 | Checkpoint anchoring outside the machine; verifier usable with no system access | `Cdax.Tabula` sinks; doc 48 section 5.4 | Anchor sinks are provider modules (`Bazaar.Tabula.Anchor.<Sink>`). Ship the verifier as a standalone executable with a documented export file format. |
| R-17 | Fiscal-style facts are kept while personal fields are erased | doc 79 Account Guardian; doc 81 | Write the rule once: retained audit facts stay, personal fields are crypto-erased (R-11). |
| R-18 | Redaction of sensitive number patterns in logs | `Cdax.Annales`; doc 43 redactor | Add a mandatory, non-disableable pattern class in the rule set, and a per-tenant log scope. |

## 6. Rubedo, Kodo, Ouroboros

| Ref | Extension point or contract | Owner | Change now |
|---|---|---|---|
| R-19 | Declarative views that target more than a screen | `Cdax.Rubedo` (doc 41c) | Add a `surface` field on a view (`screen`, `print`, `customer-display`). Pure data, no new dependency. |
| R-20 | Device and degraded states in health | `Cdax.Kodo`; doc 46, doc 75 | Allow a probe to report degraded (for example a resource running low) distinct from failed. Add an aggregate score per scope sub-path for status pages (doc 77). |
| R-21 | Staged, health-gated updates that avoid busy periods | `Cdax.Ouroboros` (rings, release dirs); doc 46, doc 49 section 3 | Take an `IMaintenanceWindow` contract that any module may answer; default always open. |
| R-22 | A client that stays offline for days then meets a new release | `Cdax.Ouroboros`; doc 49 | State a version-skew policy: the server accepts a client at most N releases behind and says which contract versions it still speaks. |
| R-23 | Updates leave a record | doc 81; Tabula | Every update writes an audit entry with old and new version. |

## 7. Testing kit

| Ref | Extension point or contract | Owner | Change now |
|---|---|---|---|
| R-24 | Cross-family independence rule (R-04) | `Cdax.Testing`; doc 78 | Add the architecture rule. |
| R-25 | Forbidden-field scan over contracts and migrations | `Cdax.Testing` | Add a scan that fails on a configurable list of forbidden column and field names (used for the redaction floor in R-18). |
| R-26 | No spreadsheet as a store | `Cdax.Testing` | Add the architecture rule in section 10. |
| R-27 | Hardware-free testing of any provider contract | `Cdax.Testing`; doc 78 failure injection | Every provider contract that touches a device ships a deterministic fake with a failure-injection mode. The kit gets a generic fake-provider harness; specific device fakes are written when a contract exists. |
| R-28 | Export golden-file harness | `Cdax.Testing`; doc 76 | Provide a harness that runs a format module twice on the same input and compares bytes. Samples come only from operators, never from the public repo. |

## 8. Bazaar modules

| Ref | Extension point or contract | Owner | Change now |
|---|---|---|---|
| R-29 | Export formats are provider modules, one per format | doc 76 `Bazaar.Server.ExportFramework`; `Bazaar.Server.Export.<Format>` | Add `IReportFormat<TReport>` and a manifest data field `specReference` (document name, version, date obtained). Detail in section 9. |
| R-30 | Wire transports and codecs are optional modules | doc 90 (PR #927) | None. A future wing and its vendors are ordinary wire clients; they use doc 90's published schema. Nothing is designed here. |
| R-31 | Offline queue and merge rules not tied to GCs | doc 49 section 4; doc 83; doc 02 replay | Extract `IOfflineQueue`, `IMergeRule<T>`, a device sequence and the hybrid logical clock into `Bazaar.Client.OfflineSync` (+ `.Abstractions`). Preserve doc 49's rules: append-only facts de-duplicate by client id; server-owned data is not offline-writable; field-level merge elsewhere. |
| R-32 | Replay-safe inbound callbacks | doc 82 idempotency; doc 15 taint | A generic `Bazaar.Server.Webhooks` module: signature verified before parse, event id stored for replay rejection, bodies treated as untrusted. Bazaar needs it for GitHub callbacks anyway. |
| R-33 | Audit with several actors and chains | doc 81 vocabulary; doc 48 | Vocabulary carries `onBehalfOf` (R-02) and `ChainId` (R-14). |
| R-34 | Money type | doc 48 section 11; PR #872 | See section 11. |
| R-35 | Auth methods beyond passkeys, as modules | `Bazaar.Server.Identity`; doc 14, doc 32 | Make an `AuthMethod` contract so a local short credential (a short local credential) can be a module that unlocks an already-authenticated session on one device only. It never grants server sign-in; the authentication floor is unchanged. |
| R-36 | Roles as data with permission lists, scoped by `Scope` | `Bazaar.Server.Identity`; doc 72 Groups | Role assignments carry a `Scope` (R-03): the same person may hold a role in one sub-unit and not another. |
| R-37 | Second-person approval recorded in the entry | doc 15 approval tiers | Allow the approval card to record the approver's `Principal` in the same audit entry. |
| R-38 | Reason-tagged operator access to tenant data | doc 72 access grants; Tabula event type | None; reuse. Hosted operators read through grants and the tenant owner is notified. |
| R-39 | Devices as a source in the hardware probe, with leases | doc 74 (`DeviceKey`, resource ledger) | Reserve an external-device probe source and reuse the ledger's TTL-and-heartbeat lease for shared devices. No device or relay is designed here. |
| R-40 | LAN-only operation with the internet down | doc 03; doc 39 (Conduit) | Keep Conduit optional and add a `Bazaar.Conduit.Lan` module for LAN discovery. Useful to any home-server install. |
| R-41 | Installer takes any payload | doc 37; doc 42; doc 45 | `InstallPayload` is a data file (module ids, versions, settings), no code. `Bazaar.Installer` reads it, checks signatures, stages modules, runs the wizard; output formats are modules (`Bazaar.Installer.<Format>`). |
| R-42 | Notifications as a channel registry | doc 22; convergence section 4 (candidate library) | Keep `Bazaar.Notifications` a channel-registry candidate. A link to a blob is a signed, expiring URL. |
| R-43 | Per-tenant lifecycle hooks (create, suspend, export, delete) | doc 03, doc 32, doc 49 | Reserve lifecycle events in the module contract so modules can react. Delete follows crypto-erase (R-11). |
| R-44 | Onboarding step that shows what a chosen module set does and does not cover | doc 45; DECISIONS-LOG round 13 | Allow a wizard step to read a module's `coverage` and `notCovered` metadata and record an audited acknowledgment. |
| R-45 | One-way import from legacy files | doc 76 reimport; doc 44 | None; importer modules (`Bazaar.Server.Import.<Format>`) already fit. A spreadsheet is imported into the database, never written back. |
| R-46 | Content from outside is untrusted | doc 15 taint; doc 73 screening | None; webhook bodies and scanned or imported data enter as untrusted. |

## 9. BIR-ready exports: the slot only

Direction is DECIDED, content is OPEN. No BIR specification is in hand, and this document invents no field list, layout, file name or submission rule.

- Doc 76's framework gets one provider module per format and a rule that a format module declares a `specReference` in its manifest data. A format module without one is marked `draft` and cannot be enabled where an operator's configuration requires a BIR output.
- `IReportFormat<TReport>` takes sealed or journalled data in and produces a file out. A format module is a pure transformation: it reads and never writes, and a re-run on the same input is byte-identical (testable with R-28).
- Spreadsheet formats (xlsx, csv) carry `kind: review-copy` in their manifests. Where a BIR format exists, a spreadsheet is not the BIR-ready output.
- Every format module has a golden-file test set. Samples come only from the operator or accountant, never from the public repo (CLAUDE.md: no private material in git).
- Placeholder names such as `Export.Bir.<Report>` may be reserved by whoever writes them; no content is written from guesses.

## 10. Database-only storage (floor)

No module persists state in an Excel workbook, CSV or other flat spreadsheet file. This is a floor, not a setting.

- The database is whatever Aludel's multi-database contract provides; all access goes through that contract, so the rule holds for every engine.
- A spreadsheet may be imported one way into the database (docs 44 and 76) and exported as a review copy. An exported copy of sealed data carries a hash of its source.
- The testing kit enforces it (R-26): no spreadsheet library is referenced from a storage path, and no migration or repository writes a spreadsheet. Spreadsheet-writing code lives only in `Export.<Format>` and importer modules.
- The rule is recorded in the Aludel contract documentation.

## 11. Money

The arbitrary-precision decision exists (PR #872, doc 48 section 11, DECISIONS-LOG round 28). This document adds nothing to it and states only what must stay open:

- Amounts are exact; floating point is never used for money or quantity, and there is no implicit conversion to `double` or `decimal`. Storage is `numeric` when the value fits and chunked rows in the same Postgres beyond that; no second store, no clamping, no field encryption; every entry point (wire, HTTP, import, webhook, configuration) refuses text longer than the intake guard before scanning it.
- The numeric core must be reusable by a commerce module without referencing agent-budget vocabulary. A `Currency` (ISO 4217 code and minor-unit exponent), rounding mode, allocation and exchange-rate value would be added when a commerce module is designed; Bazaar adds none now. Whether the shared core lives in `Cdax.Money` or is copied from `Bazaar.Contracts/Numerics` is a module-boundary matter for doc 40; the rules hold either way.
- The REJECTED Resource Economy concerns agent budgets only; it does not forbid a money type in commerce modules.

## 12. Resolved points

| Ref | Point | Resolution |
|---|---|---|
| C-01 | "Do not add Protobuf tooling" versus gRPC modules | Resolved in doc 90: lifted for the gRPC and Protobuf codec modules only. Nothing further here. |
| C-02 | "Server does all compute" versus local hardware attached to a client | Not decided here. Any future local-device access is a client-side exception that needs its own owner decision at the time a device contract is designed; this document only reserves the probe source (R-39). |
| C-03 | Passwordless authentication versus a short local credential | A local credential only unlocks a session already authenticated by a passkey or device credential (R-35). Authentication floor unchanged. |

## 13. Work packages

None is built now. Each is its own PR with its test and states `Docs section this implements: 85, <section>`.

| WP | Content | Rows | Depends on |
|---|---|---|---|
| WP1 | This docs PR and the README index row | all | none |
| WP2 | `Principal.onBehalfOf` and `Scope` sub-path contracts, serialisation tests | R-02, R-03 | none |
| WP3 | Manifest fields `family`, `dataAuthority`, `specReference`; multi-active provider set; architecture rule | R-04 to R-06, R-24 | WP2 |
| WP4 | Settings resolver with floors | R-08 | WP2 |
| WP5 | Tabula named chains, retention per class, anchor sinks, standalone verifier | R-14 to R-16 | WP2 |
| WP6 | Aludel purposes, per-tenant keys, `ISigner`, `ITenantStore` | R-09, R-11, R-12 | WP2 |
| WP7 | Rubedo `surface`; Kodo degraded state; Ouroboros window and skew | R-19 to R-22 | none |
| WP8 | Annales mandatory pattern; testing-kit rules and harnesses | R-18, R-24 to R-28 | none |
| WP9 | `Bazaar.Client.OfflineSync` extraction | R-31 | WP2 |
| WP10 | `Bazaar.Server.Webhooks` | R-32 | none |
| WP11 | `AuthMethod` contract | R-35 | none |
| WP12 | `Bazaar.Installer` and `InstallPayload` | R-41 | WP3 |
| WP13 | `Bazaar.Conduit.Lan` | R-40 | none |
| WP14 | `IReportFormat<TReport>` in doc 76's framework | R-29 | WP3 |

Wire transports and codecs belong to doc 90's work packages; no package here.

## 14. Overlaps with existing docs

- Doc 84 routes the Consumer wing's features; no row is repeated here.
- Doc 49 section 4: R-31 extracts its queue and merge rules into a module; the rules are not reopened.
- Docs 72, 73, 74, 75, 76, 78, 79, 81: reused or extended as the rows state.
- Doc 90: R-30 points to it and adds no design.
