# 85. Resibo-Retail wing: what it needs from the shared CDAX libraries and from Bazaar's modules

Status: PROPOSED. Design intent only; no code, migration or module in this document exists yet. The owner information of 2026-11-19 (Resibo is `Resibo.Core` plus two very large wings built as modules on it, one of them Resibo-Retail, a modular point-of-sale system) is DECIDED as direction. Every need, module name, contract and rule below is Bazaar's own PROPOSED design unless a row cites a Resibo document. Resibo-Retail has no Resibo design docs in the inventories, so nothing here describes Retail behaviour as fact. Owner questions: 5 (section 21.3).
Depends on: BAZAAR-COMPONENTS.md (module model, section 4.2 `Principal`), 40-core-feature-modules-audit.md (module table), 42-deps-as-modules.md, 02-wire-protocol-and-sync.md (wire, `IWireCodec`), 90-wire-transports-and-codecs.md (transport and codec modules; PR #927), 49-gap-decisions-operations.md (offline merge rules, signing), 48-gap-decisions-security-identity.md (audit chain, KEK), 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md, 46-kodo-health-module.md, 39-conduit-remote-connectivity.md, 81-program-logs-audit-vocabulary-and-retention.md, 84-resibo-exchange-ledger.md, DECISIONS-LOG.md (rounds 13, 25, 26), `/mnt/project-files/convergence/CONVERGENCE.md` (sections 4, 4a, 7 and the decisions log).
Used by: 84 (it routes Resibo-Consumer features; this doc is its Retail counterpart), the future extraction of Aludel, Kodo, Ouroboros, Tabula, Annales, Rubedo, the settings registry, the testing kit and Constellation.Renderer, and any later doc that adds a Retail-facing contract.

## 1. Purpose

Resibo is not being built now. Bazaar is being made receptive: the shared CDAX libraries and Bazaar's extractable modules should not assume a company of agents, a single database, a single tenant, or a screen with a human in front of it. This document lists what Resibo-Retail will need from them, so each library's contract can carry the small change that makes it fit, today, while it is cheap.

Resibo-Retail, in the owner's words, is a POS system that runs on any device, with the server and database on a home server or rented or hosted, very modular, supporting receipts, Xola and other providers, card terminals, physical registers and more. This document takes "and more" seriously and over-engineers on purpose (owner standing rule, doc 84 section 1.1): every area below gets an abstraction plus one module per option.

For each need the document gives: a reference (R-001 and so on, so other docs can cite it), the need, what already covers it, what must change to be receptive (a concrete contract or setting), and a module name. Section 19 reconciles the rows into Covered, Covered with change and Gap.

## 2. Decided inputs

- Owner, 2026-11-19: Resibo is `Resibo.Core` plus two wings that are modules on it. Resibo-Consumer is today's Receipt-System-V3; only its docs count as a source. Resibo-Retail is the POS wing described above.
- Owner, 2026-11-19: Bazaar is made receptive to Resibo; Resibo is not built now. Shared repos hold contracts and pure logic only, with no third-party dependencies (convergence section 4a). Providers stay in Bazaar and are plain modules; there is no selector and no role setting (owner, PR #919).
- Owner, 2026-11-19 (cards): generic `Principal` (agent, user, service, system) and `Scope` (install, tenant, user) in `Cdax.Core`; Aludel has one `IKekProvider` contract and is multi-database; Tabula's contract carries retention window, operation vocabulary and a reason-tagged access-grant event type; the settings registry is in wave 1; Identity stays a Bazaar module for now.
- Owner, 2026-11-19 (timeline): "modules are a thing", keep the current, add HTTP/3 over QUIC, add gRPC, add everything; an installer ("Bazaar installer") can use any payload, "just have the modules".
- CLAUDE.md: .NET 11 everywhere; JSON via `IWireCodec`, "do not add Protobuf tooling" (conflict C-01, section 21.1; narrowed by doc 90); AGPL root with a module exception; configurability rule (DECISIONS-LOG round 13); server does all compute, with narrow listed client exceptions.
- DECISIONS-LOG: Resource Economy (money budgets) is REJECTED. This concerns agent budgets only; it says nothing about money arithmetic in a commerce module (row R-049 states that explicitly).
- Owner, 2026-11-19 (DECIDED; PR #872, `docs/design/48-gap-decisions-security-identity.md` section 11 and DECISIONS-LOG round 28; gateway counters in `docs/design/05-model-gateway.md`): amounts are exact and of arbitrary precision, "make the disk the limit". `numeric` when the value fits, chunked rows in the same Postgres beyond that, no field encryption, and a length guard at intake against denial of service. Section 11 applies it to commerce money.
- Owner, 2026-11-19 (DECIDED; PR #927, `docs/design/90-wire-transports-and-codecs.md`): JSON over WebSocket stays; every other transport (HTTP/3 over QUIC, WebTransport, gRPC and gRPC-Web) and codec (MemoryPack, CBOR, MessagePack, Protobuf) is an optional module. Section 12 reuses those modules and decides nothing about the wire.
- Owner, 2026-11-19 (DECIDED direction): every Resibo export will eventually be in a BIR-compliant, BIR-ready format (BIR is the Philippine tax authority), not generic sheets. The format details are OPEN until the actual BIR specifications are in hand (section 17.3).
- Owner, 2026-11-19 (DECIDED direction): unlike Resibo V2, everything is stored in a proper database, never in Excel workbooks. Spreadsheet files are export outputs only, never a store (section 17.4).
- Doc 49 section 4 (DECIDED): offline editing with merge, with a rule per data class. Doc 48 section 5: audit hash chain, signed checkpoints, anchoring.

Money decision note: the arbitrary-precision decision exists. It is the owner's amounts decision of 2026-11-19 in PR #872 (section 2, last decided inputs). Section 11 rebases `Cdax.Money` on it and states only what Retail adds.

## 3. What this document does not decide

- Anything about how Resibo-Consumer works. That is doc 84.
- Retail's schema, screens, branding, pricing or licence. Retail's own repo will decide them. Bazaar only needs its shared contracts to stand up under them.
- Which payment processors or card-terminal vendors Retail will ship. Rows name the provider modules as a pattern (`.<Vendor>` suffix); the list of vendors is Retail's.
- Xola's API. Nothing in the inventories describes it. It appears only as one case of a booking or ticketing provider (R-014).
- Local tax law. Section 17.2 makes it an operator choice with documented options; the tax rules themselves are provider modules written later.

## 4. Conventions in this document

- `Cdax.*` is a shared library (contracts and pure logic, no third-party dependency). `Bazaar.<X>` is a Bazaar module. `Resibo.Retail.<X>` is a Retail-side module name, always a working name.
- Every provider is its own plain module: `<Feature>.<Vendor>` registered through the module manifest. There is no `ProviderSelector` and no `provider = x` setting. An operator enables the modules they want; when several are enabled at once the feature asks each (the multi-active provider set of convergence item A1).
- "Receptive change" means the smallest contract, field or setting that lets Retail plug in. It never means Retail code in Bazaar.
- Statuses of rows: Covered (existing doc covers it with no change), Covered with change (an existing doc or library needs the stated change), Gap (nothing covers it; a new module is proposed).
- Source tags: **[R]** a Resibo doc (cited), **[B]** a Bazaar doc (cited), **[P]** Bazaar's own PROPOSED design, with no source behind it.

## 5. The shape of the wings

Resibo.Core is the module kernel (it is `Cdax.Core`, doc 40). Resibo-Consumer and Resibo-Retail are two large module families on it. They share the libraries and may share a database server, but neither depends on the other. Where both need something (identity, audit, tenancy) it lives in a shared library or in Core, never in one wing.

| Ref | Need | Covered by | Receptive change | Module name | Status |
|---|---|---|---|---|---|
| R-001 | Two wings as module families on one kernel, independently installable | BAZAAR-COMPONENTS sections 2 to 4 (manifest, hard and soft edges, `kind`); doc 40 table | Add a manifest field `family` (free text, for grouping in the module manager and in installer payloads). No behaviour. | `Cdax.Core` (field only) | Covered with change |
| R-002 | A wing may be absent entirely; Retail must run with no Consumer module present | Hard and soft edge rule, BAZAAR-COMPONENTS 4.1 | Architecture rule: no module of one family names the other family's ids in `dependsOn`. The cross-family test lives in the testing kit. | `Cdax.Testing` rule | Covered with change |
| R-003 | Installer payload is "anything": an install is a set of modules | Owner timeline note 2026-11-19; doc 37 (distribution), doc 42 (deps as modules) | `InstallPayload` is a data file listing module ids, versions and settings, with no code in it. The installer is one module (R-082) and may emit several output formats. | `Bazaar.Installer` (section 16) | Gap |

R-003 is a design statement, not a feature: a payload has no meaning to the installer beyond a list of modules and their settings, so a Retail payload, a Consumer payload and a Bazaar payload differ only in content.

## 6. Actor and scope: the type everything hangs on

Retail's actors are cashiers, managers, owners, devices, integrations and scheduled jobs. Its scopes are the install (one deployment), the tenant (a business) and the user (a person). Branches are inside a tenant.

| Ref | Need | Covered by | Receptive change | Module name | Status |
|---|---|---|---|---|---|
| R-004 | Record who did a sale, void or refund, whether human, device or service | `Principal` (BAZAAR-COMPONENTS 4.2) | None for kinds. Add a canonical device form: a physical register or terminal is `service:<device-id>`, never `user:`; the cashier is carried separately (R-005). | `Cdax.Core` | Covered |
| R-005 | One transaction needs two actors: the terminal that rang it and the cashier logged in | `Principal` is a single value per record | Allow records to carry a `Principal` plus an optional `onBehalfOf` `Principal`. Audit entries carry both; the text form is `service:till-3` with `onBehalfOf=user:ana`. | `Cdax.Core`, Tabula contract | Covered with change |
| R-006 | Scope: install, tenant, user | Owner card 2026-11-19 (A2) | Add `branch` as a documented sub-scope of tenant, not a fourth kind: `Scope(kind=tenant, id, path="branch/<id>")`. Keeps the three owner-decided kinds. | `Cdax.Core` | Covered with change |
| R-007 | Settings values by scope with floors | Settings registry, convergence section 4 and doc 03 | Registry must resolve most-specific-wins across install, tenant, branch path, user, with a floor that no narrower scope can loosen (doc 03 rule) | `Cdax.Settings` | Covered with change |

## 7. Payments, terminals and external providers

Rule that governs this schedule (also section 17.1): Retail never stores primary account numbers, track data, card verification values or PINs. A card terminal does the card work and returns a result and a token.

| Ref | Need | Covered by | Receptive change | Module name | Status |
|---|---|---|---|---|---|
| R-008 | Payment provider abstraction: charge, capture, void, refund, status, webhook verification | Resibo Billing `PaymentProvider` (create_charge, verify_webhook, check_status; PayMongo and Xendit) [R], resibo-30-39 inventory. That is SaaS billing, not point of sale. | Do not reuse Billing's shape as-is. Define a Retail payment contract with `Authorize`, `Capture`, `Void`, `Refund`, `Status` and a webhook verifier. Money in the contract uses `Cdax.Money` (R-043). | `Resibo.Retail.Payments.Abstractions`; vendors `Resibo.Retail.Payments.<Vendor>` | Gap |
| R-009 | Several payment modules enabled at once (cash, card, e-wallet, bank transfer, voucher) | Provider registry pattern; convergence A1 multi-active provider set | Core needs the "provider set" kind (A1) so the tender screen can ask all enabled payment modules what they accept | `Cdax.Core` | Covered with change |
| R-010 | Card terminal abstraction: start a sale on the terminal, receive result and token, cancel, reverse, settle batch | None | Contract `ICardTerminal` with `StartSale(amount, reference)`, `Cancel`, `Reverse`, `CloseBatch`, and an event stream of terminal states. It returns result, masked last four, scheme and a processor token only. | `Resibo.Retail.Terminals.Abstractions`; `Resibo.Retail.Terminals.<Vendor>` | Gap |
| R-011 | Terminal link types: network (TCP or TLS), USB or serial, Bluetooth, vendor cloud | Doc 20 engine adapter protocol is the nearest pattern (adapters over a protocol) | Each link type is its own transport module under the terminal contract, so a terminal vendor module does not hard-code its link | `Resibo.Retail.Terminals.Link.<Kind>` | Gap |
| R-012 | Payment webhooks must be verified, replay-protected and idempotent | Doc 82 job classes and idempotency; doc 15 taint (external content taints) | A webhook receiver module that treats every inbound body as untrusted, verifies the signature before parse, and stores the provider event id for replay rejection | `Bazaar.Server.Webhooks` (generic; Retail and Billing both use it) | Gap |
| R-013 | Refund through the original tender, or to store credit, or in cash | None | Refund contract carries `OriginalPaymentRef` and `RefundMethod`; provider modules declare which methods they support | `Resibo.Retail.Payments.Abstractions` | Gap |
| R-014 | Booking and ticketing providers (Xola and others): import bookings, mark them paid or checked in, push sales | None in Bazaar. Resibo docs say nothing of Xola. | Provider contract `IBookingSource` with pull and push, built on the generic webhook and job modules. Shape is Bazaar's own guess and must be confirmed when Retail is designed. | `Resibo.Retail.Bookings.Abstractions`; `Resibo.Retail.Bookings.Xola` and others | Gap |
| R-015 | Outbound integrations are plain modules; no selector | Owner, PR #919; doc 42 | Nothing. This row restates the rule so reviewers can cite it. | all provider modules | Covered |
| R-016 | Receipt delivery channels: print, email, SMS, messaging app, QR link | Resibo Notifications `OutboundChannel` registry (email, SMS with Semaphore, PhilSMS, Twilio) [R]; Bazaar email subsystem, doc 22 | Notifications lib stays a candidate (convergence section 4). Retail needs it content-addressed: a receipt link is a signed, expiring URL to a blob (R-043). | `Bazaar.Notifications` (candidate) plus Retail delivery modules | Covered with change |

## 8. Physical registers and devices

A register is a set of devices around a till: cash drawer, receipt printer, barcode scanner, scale, customer display, label printer, and a terminal. They are local to the till, which means a client-side exception to "the server does all compute" (CLAUDE.md lists the current ones; row R-017 asks for a new one).

| Ref | Need | Covered by | Receptive change | Module name | Status |
|---|---|---|---|---|---|
| R-017 | Devices attached to the till must be driven from the till | CLAUDE.md client-side exceptions (NPU, terminal, Studio bridge relay, branch sync) | Add one named exception to the list: "point-of-sale peripherals, driven by a device-side relay". Owner decision (Q1). The relay is a small headless process, the same pattern as the Studio bridge relay (doc 20). | `Resibo.Retail.DeviceRelay` | Gap |
| R-018 | One device contract family: `IDevice` (id, kind, state, capabilities) and per-kind contracts | Doc 74 `DeviceKey` naming is the nearest device identity | Reuse doc 74's `DeviceKey` shape for device ids so the hardware probe and device relay agree. | `Resibo.Retail.Devices.Abstractions` | Covered with change |
| R-019 | Receipt printer: ESC/POS-style text and graphics, cut, status, paper-out | None | Contract `IReceiptPrinter` taking a neutral `PrintDocument` (lines, styles, image, barcode, QR, cut). Dialects are modules, not branches in one class. | `Resibo.Retail.Printers.Abstractions`; `.EscPos`, `.Star`, `.SystemSpooler`, `.Pdf` | Gap |
| R-020 | Receipt layout is data, not code | Rubedo view declarations (doc 41c) | Rubedo's declarative views must be able to target a print surface as well as a screen: a "surface" field on the view with `screen`, `print`, `customer-display`. Pure data, so no third-party dependency. | `Cdax.Rubedo` | Covered with change |
| R-021 | Cash drawer open, close, status; open-on-sale and no-sale reasons | None | Contract `ICashDrawer`; the open command is routed through the printer on kick-port drawers or directly on others. Every no-sale open writes an audit entry with a reason. | `Resibo.Retail.Drawers.Abstractions`; vendor modules | Gap |
| R-022 | Barcode scanner: keyboard-wedge, HID, serial, camera | None | Contract `IScanner` emitting `ScanEvent(symbology, data)`. Camera scanning uses the platform camera, which is a per-head module. | `Resibo.Retail.Scanners.Abstractions`; `.Wedge`, `.Hid`, `.Camera` | Gap |
| R-023 | Scale: stable weight, tare, price-by-weight, legal-for-trade flag | None | Contract `IScale` returning `WeightReading(value, unit, stable, tare)` with `Cdax.Money`-style exact decimal (R-043). Legal-for-trade is a flag a provider must declare; the sale refuses an unstable or non-certified reading where an operator setting requires it. | `Resibo.Retail.Scales.Abstractions`; vendor modules | Gap |
| R-024 | Customer-facing display and label printer | None | Same pattern as R-019, lower priority | `Resibo.Retail.Displays`, `Resibo.Retail.Labels` | Gap |
| R-025 | Device health and discovery | Kodo health contract (doc 46), watchdog (doc 75), hardware probe (doc 74) | Device modules register Kodo probes (liveness: paper-out is a degraded state, not a failure). The hardware probe gets a `device-relay` source for peripherals. | `Bazaar.Server.HardwareProbe` (source module) | Covered with change |
| R-026 | Devices may be shared between tills (one printer, two tills) | None | Leases: a till takes a lease on a device with a TTL kept alive by heartbeat, the same shape as doc 74's resource ledger reservation | `Bazaar.Server.ResourceLedger` (reuse) | Covered with change |
| R-027 | Run with a device missing (printer down) without losing the sale | Doc 49 offline editing | The sale completes; print jobs are queued with the sale and drained when the device returns. A queued job is an operation, with an audit entry when it finally prints. | `Resibo.Retail.PrintQueue` | Gap |

## 9. Offline POS, sync and conflicts

A till must keep selling when the network or the server is down. Doc 49 section 4 already decides offline editing with a merge rule per data class; Retail adds data classes whose merge rules differ from any Bazaar class because money is involved.

| Ref | Need | Covered by | Receptive change | Module name | Status |
|---|---|---|---|---|---|
| R-028 | Offline queue on the device with durable local storage | Doc 49 section 4 (device queue, "as of" cache, banner); doc 02 replay | Extract the queue and merge contract into a module that is not tied to GC: `IOfflineQueue`, `IMergeRule<T>`, a device sequence and a hybrid logical clock (doc 49 already uses HLC for preferences). | `Bazaar.Client.OfflineSync` (and `.Abstractions`) | Covered with change |
| R-029 | Sales are append-only facts, so there is no conflict on a sale | Doc 49 item 1 (GC log: ordered append, client ids de-duplicate replays) | A sale, void, refund and payment are each an immutable record with a client-generated id. Server rule: append in arrival order, drop replays by id. Corrections are new records that reference the old (R-063 to R-065). | `Resibo.Retail.Sales` | Covered with change |
| R-030 | Document numbers (invoice, receipt) must be gapless and unique even offline | None | Each till is issued a block of numbers (a lease) by the server while online; it uses only its block offline, and a block never overlaps another. When a till runs out offline, a policy decides: stop selling, or continue with a provisional per-till prefix that is reconciled later. A void of an unused number is a recorded event, never a silent skip. Operator setting, default stop. | `Resibo.Retail.Numbering` | Gap |
| R-031 | Merge rules for non-append data: price list, product catalogue, stock count, customer, staff, settings | Doc 49 items 3, 5, 8 (field-level merge with base versions; last-writer-wins only for cosmetic keys; settings not offline-writable) | Catalogue and price changes are server-owned: not offline-writable (like settings). Stock is a ledger of movements, merged as an ordered append, and the balance is derived, never merged (R-066). Customer notes use field-level merge with a conflict card. | `Bazaar.Client.OfflineSync` merge rule set | Covered with change |
| R-032 | The tills disagree about price or stock when they reconnect | None | A reconnecting till uploads sales with the price-list version it used. The server accepts them at that price, and flags a sale whose version is older than the operator's limit for review. The default is accept and flag, never reprice a closed sale. | `Resibo.Retail.Sales` | Gap |
| R-033 | Offline authorization of a card payment | None | A terminal that can approve offline does so under its own rules. Retail records the offline-approved flag and limit. Bazaar stores no card data (section 17.1). Whether to accept offline card approvals at all is an operator setting, default off. | `Resibo.Retail.Terminals.Abstractions` | Gap |
| R-034 | Clock trust: a till's clock may be wrong | HLC in doc 49; FakeClock in testing kit | Records carry the device time, the HLC and, on arrival, the server time. Reports use server arrival time for ordering and device time for display. A till whose clock drifts beyond a limit raises a Kodo attention (doc 81 `Attention`). | `Cdax.Core` clock contract | Covered with change |
| R-035 | Sync transport options: WebSocket JSON today, HTTP/3 over QUIC, gRPC | Doc 02 (WebSocket plus `IWireCodec`); doc 39 (p2p over QUIC carrying the same pinned wire) | Transports and codecs are the optional modules of doc 90 section 2 (`Bazaar.Server.Transport.*` and `Bazaar.Client.Transport.*`, `Bazaar.Wire.Codec.*`). Retail adds none. See section 12. | doc 90 modules | Gap |

## 10. Tenancy, branches and deployment

| Ref | Need | Covered by | Receptive change | Module name | Status |
|---|---|---|---|---|---|
| R-036 | Multi-tenant: many businesses on one install, hard isolation | Resibo `tenancy_mode` and "structural isolation over permission checks" (per-user folder isolation) [R], convergence row 11; Bazaar is one company per install (convergence A4) | Tenancy is a `Scope` (R-006). The data layer needs a contract: `ITenantStore.Open(Scope)` returns a store bound to one tenant, and no API accepts a tenant id on a query. Postgres provider: a schema or database per tenant, operator setting; the choice does not change callers. | `Cdax.Aludel` contract; `Bazaar.Data.Tenancy` provider modules | Covered with change |
| R-037 | Branches inside a tenant with their own tills, stock, hours, taxes | None | `Scope` path (R-006). A branch is a data class with settings overrides (R-007). | `Resibo.Retail.Branches` | Gap |
| R-038 | Central versus branch-local data: which copy is authoritative | Doc 49 (server-held values) | Per data class, a declared authority: catalogue and prices at tenant, stock movements at branch, sales at till then branch, staff at tenant. Declared in the module manifest `settings` or a `dataAuthority` list, so the sync engine and installer can read it. | manifest field (`Cdax.Core`) | Covered with change |
| R-039 | Cross-tenant operator view (hosted provider seeing counts, health, not data) | Doc 72 access grants and break-glass | Operator support reads go through reason-tagged access grants (doc 72, Tabula event type), tenant owner notified. Resibo Auth break-glass is the model [R] (resibo-10-19 inventory: time-boxed, reason-tagged, dual notification). | `Bazaar.Server.AccessGrants` (reuse) | Covered |
| R-040 | Home-server deployment: one machine, residential network, intermittent internet | Doc 03 (headless host), doc 39 (Conduit: cloudflared, p2p), doc 33 (backup) | Retail needs the LAN to work with the internet down: tills discover the server by LAN name or mDNS-style discovery module, and the Conduit is optional. | `Bazaar.Conduit.Lan` | Gap |
| R-041 | Rented or hosted deployment: many tenants, operator-run | Doc 03, doc 32, doc 49 | Same binaries; settings differ. Hosted adds per-tenant quotas, per-tenant backup and a tenant lifecycle (create, suspend, export, delete). Delete follows crypto-erase (per-tenant keys, R-052). | `Resibo.Retail.TenantLifecycle` | Gap |
| R-042 | Tills on any device: Windows, Android, Linux, iOS or browser | Doc 27, 28, 29, 30; CLAUDE.md: Avalonia, no web client "ever" except status and narrow sign-in pages | Retail "any device" collides with "no web client" and has no iOS head (C-02, Q2). Avalonia covers Windows, Android, Linux; macOS and iOS are Avalonia targets that Bazaar has not listed. | `Resibo.Retail.Client.<Head>` | Gap |

## 11. Money and tax arithmetic

Retail is an exact-arithmetic domain. Floating point is never used for money, quantity or tax. This section is the contract that other rows refer to as `Cdax.Money`. Its precision and storage rules are not new: they are the owner's amounts decision (PR #872, doc 48 section 11, DECISIONS-LOG round 28), applied to commerce money. Retail adds currency, rounding, allocation and conversion on top.

| Ref | Need | Covered by | Receptive change | Module name | Status |
|---|---|---|---|---|---|
| R-043 | Arbitrary-precision money amount, stored exactly | DECIDED (owner, 2026-11-19; PR #872, doc 48 section 11; DECISIONS-LOG round 28): one exact amount type with no maximum of its own (`TokenAmount` in `Bazaar.Contracts` for agent budgets), a separate bounds policy (`TokenAmountBounds`), amounts as JSON strings and never through `double`, `numeric` storage when the value fits (131,072 digits before the point, 16,383 after), chunked rows in the same Postgres beyond that (`token_amount_large` and `token_amount_large_chunk`, base-100 packing, SHA-256 integrity, streamed arithmetic), no field encryption, intake length guard `agent.budget.amount_intake_max_digits` | `Cdax.Money` holds `Amount` (exact integer with a decimal scale, same representation rules as `TokenAmount`), `Currency` (ISO 4217 code with a registry of minor-unit exponents) and `Money = Amount + Currency`. No implicit conversion to `double` or `decimal`. Storage follows the two tiers: inline `numeric` when it fits, chunked rows in the same Postgres when it does not, never a second store, never clamped or replaced by null. Amounts are not personal data and are not field-encrypted (doc 48 section 5.5); they sit behind role grants and the operator's disk encryption. Every entry point (wire, HTTP, import, provider webhook, configuration) refuses text longer than a per-tenant intake guard before scanning it, with the same audited operator setting shape as the budget guard. Retail sets its own guard default per money field class; line prices and tenders are small in practice, so the shipped default is the inline tier. The code of the chunk tier is in PR #872 (`Bazaar.Contracts/Numerics`); whether `Cdax.Money` shares that numeric core or copies its rules is a module-boundary matter for doc 40, and the rules above hold either way | `Cdax.Money` | Covered with change |
| R-044 | Rounding is explicit, named and recorded | Resibo Reconciliation `vat_math` check: 12 percent, tolerance 0.02 [R] (Consumer-side, for checking a printed receipt) | Rounding modes are an enum (`HalfUp`, `HalfEven`, `Down`, `Up`, `Cash` for coin-less currencies). A calculation returns the result and the rounding events, and rounding is applied at stated points only (line, subtotal or total, set by the tax profile), so two engines give identical totals. | `Cdax.Money` | Gap |
| R-045 | Allocation without losing a cent: split a total across lines, tenders or people | None | `Allocate(Money, ratios)` returns parts that sum exactly to the input; the remainder rule is a parameter, default first-part. | `Cdax.Money` | Gap |
| R-046 | Currency conversion with recorded rate | None | `ExchangeRate` is a value with source, time and precision. Conversion is a function of an explicit rate, never of "now". Rate feeds are provider modules. | `Cdax.Money`; `Resibo.Retail.Rates.<Source>` | Gap |
| R-047 | Tax calculation: inclusive and exclusive, multiple taxes per line, exempt, zero-rated, compound | Consumer checks VAT after the fact [R]; nothing computes it forward | Contract `ITaxEngine.Calculate(lines, profile)` returning tax lines per rate and per jurisdiction, with the rounding events (R-044). Tax rules are data in a versioned, dated `TaxProfile`, so a rate change is a new profile effective from a date, never an edit. Jurisdiction rule sets are provider modules. | `Resibo.Retail.Tax.Abstractions`; `.Tax.<Jurisdiction>` | Gap |
| R-048 | Prices, discounts, promotions and loyalty | None | Pricing is an ordered pipeline of modules (each returns adjustments with a reason). The pipeline order is a documented setting. | `Resibo.Retail.Pricing` | Gap |
| R-049 | The REJECTED Resource Economy must not be read as banning money types | DECISIONS-LOG row on agent budgets: "No money caps. Cost is shown for display only." | State in the Retail docs that money handling in commerce modules is unrelated to agent budgets. This row is the citation. | doc note only | Covered |

## 12. Wire transports and codecs as modules

Owner direction: keep the current, add HTTP/3 over QUIC, add gRPC, add everything. This is now designed in **doc 90** (PR #927, DECIDED owner input 2026-11-19). Retail does not design any of it again; it reuses doc 90's modules and adds only the Retail needs below.

- Doc 90 section 2 catalogue, reused as is: `Bazaar.Wire.Abstractions`, `Bazaar.Wire.Generators`, `Bazaar.Wire.Conformance`; transports `Bazaar.Server.Transport.WebSocket` and `Bazaar.Client.Transport.WebSocket` (the current one, the floor with JSON), `Bazaar.Server.Transport.Http3`, `Bazaar.Server.Transport.QuicNative` and `Bazaar.Client.Transport.QuicNative`, `Bazaar.Server.Transport.WebTransport`, `Bazaar.Server.Transport.Grpc` and `Bazaar.Client.Transport.Grpc` (with gRPC-Web), `Bazaar.Client.Transport.AndroidHttp3`; codecs `Bazaar.Wire.Codec.MemoryPack`, `.Cbor`, `.MessagePack`, `.Protobuf` (JSON stays in `Bazaar.Contracts` as `JsonWireCodec`); discovery `Bazaar.Server.Wire.Discovery`.
- Negotiation, limits, schema publication, settings (`wire.*`) and the S-WTX benchmark are doc 90 sections 4 to 10. Authentication, pinning and replay (docs 02, 32, 39) apply identically to every transport, as doc 90 states.
- What Retail needs from it (not a wire design): a till, a device relay and a third-party or device vendor are ordinary wire clients. A vendor that speaks gRPC or needs a foreign-language codec uses doc 90's published schema (`wire.proto`, JSON Schema, CDDL, golden vectors, section 5). Retail tills on poor networks use the same transport ladder and negative cache as any client (doc 90 section 4.1). Offline queues replay through the existing idempotent request layer, which every transport keeps (doc 90, decided inputs).
- Conflict C-01 is narrowed by doc 90 (Protobuf tooling lifted for the gRPC and Protobuf codec modules only); see section 21.1.

| Ref | Need | Covered by | Receptive change | Module name | Status |
|---|---|---|---|---|---|
| R-050 | Transport as module | Doc 90 section 2 (transport modules; `ws` moved out of Server.Wire and Client.Core), doc 02, doc 39 | None for Retail | `Bazaar.Server.Transport.WebSocket`, `Bazaar.Client.Transport.WebSocket` | Covered |
| R-051 | HTTP/3 over QUIC | Doc 90 sections 2 and 9 (`Http3` listener feature, `QuicNative`, `AndroidHttp3`); doc 39 (pinning) | None for Retail | `Bazaar.Server.Transport.Http3`, `Bazaar.Server.Transport.QuicNative`, `Bazaar.Client.Transport.QuicNative`, `Bazaar.Client.Transport.AndroidHttp3` | Covered |
| R-052 | gRPC and Protobuf for vendors | Doc 90 section 2 (`Grpc`, `Codec.Protobuf`) and its lifted Protobuf rule; published schema in section 5 | None for Retail beyond using the published schema | `Bazaar.Server.Transport.Grpc`, `Bazaar.Client.Transport.Grpc`, `Bazaar.Wire.Codec.Protobuf` | Covered |
| Ref | Need | Covered by | Receptive change | Module name | Status |
|---|---|---|---|---|---|
| R-053 | Tamper-evident fiscal journal: hash chain, signed checkpoints, offline verifier | Doc 48 section 5.2 to 5.4 (chain, checkpoints, anchoring, verifier); doc 81 | Retail needs several chains, one per till and one per branch, each independently verifiable. Tabula's contract must allow named chains (`ChainId`), not one global chain. | `Cdax.Tabula` | Covered with change |
| R-054 | Journal entries are the only way to change fiscal state | Doc 49 item 7 (audit never client-ordered); doc 82 | Fiscal tables are written only through a journal service in one transaction with the journal entry (audit-in-same-transaction is already Bazaar's rule, convergence row 6) | `Resibo.Retail.FiscalJournal` | Covered with change |
| R-055 | Retention window by law, per tenant | Tabula optional extras (owner card): retention window; Resibo Audit default 3650 days for BIR, fixed or indefinite [R] (resibo-10-19 inventory) | Retention is a per-tenant, per-class setting with a floor. The 3650-day figure is a Resibo Consumer default tied to its domain; Retail takes no default from it. | `Cdax.Tabula` setting | Covered with change |
| R-056 | End of day (Z) close: lock the day, produce totals, never alter | None | A close is a journal entry that seals a business day for a till: cumulative totals, counters (voids, refunds, no-sales), tender totals, tax totals, opening and closing numbers. The seal is a checkpoint (R-053). No sale may be appended to a sealed day; a late sale lands on the next day with a flag. | `Resibo.Retail.DayClose` | Gap |
| R-057 | X report (interim, non-closing) and Z report (closing) | None | Report templates are Rubedo print views (R-020) generated from journal data only, so a re-print is byte-identical | `Resibo.Retail.Reports.Abstractions`; `.XReport`, `.ZReport` | Gap |
| R-058 | Cash count and variance at close | None | Cash-up record: counted by denomination, expected from the journal, variance, signed by a `Principal` with the manager role (R-070) | `Resibo.Retail.CashUp` | Gap |
| R-059 | Branch and tenant roll-up reports | Doc 23 (Observatory) is the nearest reporting module | Roll-ups read sealed days only | `Resibo.Retail.Reporting` | Gap |
| R-060 | Export to accounting and to government formats | Doc 76 export framework (`Bazaar.Server.ExportFramework`, `.Export.<Format>`); Resibo export formats (Excel, SLSP, QuickBooks, Xero) [R] | Retail export formats are `Export.<Format>` modules: same framework, new formats. Reimport with three-way diff (doc 76) covers corrections coming back from an accountant. BIR-ready targets are in section 17.3 (R-091 to R-094). | `Bazaar.Server.Export.<Format>` | Covered |
| R-061 | Anchoring the journal outside the machine | Doc 48 section 5.4 (anchors, replica) | A home server is easy to alter: a recommended anchor module sends checkpoints to a second place (another device, object storage, a mail copy). Anchor sinks are providers. | `Cdax.Tabula` sinks; `Bazaar.Tabula.Anchor.<Sink>` | Covered with change |
| R-062 | Verifier usable by an auditor with no access to the system | Doc 48 verifier | Ship the verifier as a standalone executable and a documented file format for exported journals | `Bazaar.Tabula.Verifier` | Covered |

## 14. Receipts, refunds and inventory

| Ref | Need | Covered by | Receptive change | Module name | Status |
|---|---|---|---|---|---|
| R-063 | The sale document: lines, tenders, taxes, numbers, reprints marked as copies | None | `SaleDocument` is an immutable value rendered by R-020 print views; a reprint stamps "copy" and writes an audit entry | `Resibo.Retail.Receipts` | Gap |
| R-064 | Refund and return: partial, by line, with reason, with approval above a limit | Doc 15 approvals (taint-linked); doc 72 reason-tagged grants | A refund is a new document that references the original (R-029). Above an operator limit it needs a second `Principal` (manager) in the same entry. | `Resibo.Retail.Refunds` | Gap |
| R-065 | Void before payment versus refund after payment | None | Two different journal entry kinds with different permissions; a void after payment is never allowed (use refund) | `Resibo.Retail.Sales` | Gap |
| R-066 | Inventory: items, variants, units of measure, serials, lots, expiry | None | Stock is a movement ledger (R-031). Balance is derived. Variants and units use `Cdax.Money` quantity types for exactness. | `Resibo.Retail.Inventory` | Gap |
| R-067 | Stock counts, transfers between branches, purchase receiving | None | Each is a document with its own lifecycle; transfers are two movements linked by one id; counts post adjustments, never overwrite | `Resibo.Retail.Inventory.Counts`, `.Transfers`, `.Receiving` | Gap |
| R-068 | Importing supplier invoices and receipts into inventory | Resibo-Consumer receipt processing (OCR, matching) [R] is exactly this ingestion | This is the natural bridge between wings: Consumer produces structured receipts, Retail purchasing may consume them. Only through a contract, never by wing-to-wing reference (R-002). | `Cdax.Retail.Bridge` (contract only) | Gap |
| R-069 | Search for products and customers | Doc 03 search provider (Postgres full-text, `pg_search`) | A search provider is already a role; SQLite per user variants fall under Aludel's multi-database contract | `Cdax.Aludel` search | Covered |

## 15. Staff, roles and shifts

| Ref | Need | Covered by | Receptive change | Module name | Status |
|---|---|---|---|---|---|
| R-070 | Roles: owner, manager, cashier, stock clerk, auditor, support | Resibo Auth roles owner, staff, client [R]; doc 48 permission classes; doc 72 Groups | Roles are data with permission lists, scoped by `Scope` (R-006): a cashier at branch A is not one at branch B | `Bazaar.Server.Identity` roles; `Bazaar.Server.Groups` | Covered with change |
| R-071 | Fast sign-in at the till: PIN, card, fingerprint, then full sign-in for sensitive acts | Resibo Auth is passwordless only, with passkeys, OTP, SSO [R]; Bazaar passkey panel (doc 32) | A PIN is a local, short credential bound to a device and a person, valid only on that device, with lockout. It never grants access outside the till. It is an additional `AuthMethod` module, so the passwordless rule of Auth stays. Conflict C-03. | `Bazaar.Identity.Method.Pin`, `.Badge`, `.Biometric` | Gap |
| R-072 | Shifts: clock in and out, breaks, assigned till, cash accountability per shift | None | A shift binds a person, a till and a drawer float; cash variance (R-058) is charged to the shift. | `Resibo.Retail.Shifts` | Gap |
| R-073 | Manager override for restricted acts (discount, void, refund over limit, price change) | Doc 15 approval tiers | A second-person approval with the approver's `Principal` in the entry, using the existing approval card shape; offline, a manager PIN on the same device is accepted and flagged for review | `Bazaar.Server.Approvals` use | Covered with change |
| R-074 | Staff schedule and time reports | RyuseiFuda / company calendar (doc 24) are agent-oriented | Not a Bazaar concern; Retail module | `Resibo.Retail.Staffing` | Gap |
| R-075 | Account guardian: data subject requests, consent | Doc 79 Account Guardian; Resibo Account Guardian (DSR export, delete with grace) [R] | Customer and staff personal data covered by doc 79 mechanics, with retention exceptions for fiscal records (R-055) written as a rule: fiscal facts are kept, personal fields are crypto-erased | `Bazaar.Server.AccountGuardian` | Covered with change |

## 16. Sealed keys, field updates and the installer

| Ref | Need | Covered by | Receptive change | Module name | Status |
|---|---|---|---|---|---|
| R-076 | Keys for tenant data, device credentials, signing keys for the journal, terminal keys | Aludel: `ISecretStore`, one `IKekProvider`, sealed-storage format (convergence section 4a) | Add key purposes as a documented enum: `TenantData`, `DeviceCredential`, `JournalSigning`, `TerminalComms`. Per-tenant data keys wrapped by the KEK, so deleting a tenant destroys its key (crypto-erase). | `Cdax.Aludel` | Covered with change |
| R-077 | Hardware-backed keys on a till (TPM, secure enclave, Android keystore) | Aludel KEK providers (DPAPI, TPM, key file) stay in Bazaar | Each is a provider module. Add `Aludel.Android.Keystore` and a platform-key module per head. | `Bazaar.Aludel.<Provider>` | Covered with change |
| R-078 | Journal signing key must not be exportable; signing is done by Aludel, never by the module | Doc 48 section 5 | `ISigner` operation in Aludel contract that signs without exposing the key | `Cdax.Aludel` | Covered with change |
| R-079 | Updates in the field: staged, health-gated, rollback, per ring | Ouroboros (channels, release dirs, rings); Kodo slot model (doc 46, doc 49 section 3) | Retail adds a quiet-hours rule: no update during open trading hours unless the operator forces it. Ouroboros takes an `IMaintenanceWindow` contract answered by a Retail module. | `Cdax.Ouroboros` contract; `Resibo.Retail.TradingHours` | Covered with change |
| R-080 | A till that is offline for days when an update arrives | Doc 49 | Version skew policy: the server accepts a till at most N releases behind and says which contract versions it still speaks | `Cdax.Ouroboros` | Covered with change |
| R-081 | Fiscal-relevant updates need a record | Doc 81 vocabulary; Tabula | Every update is a journal entry with the old and new version, and the tax profile version in force | `Resibo.Retail.FiscalJournal` | Covered with change |
| R-082 | Installer with any payload | Owner timeline note; doc 37 | `Bazaar.Installer`: reads an `InstallPayload` (module list and settings), checks signatures, stages modules, and runs the setup wizard (doc 45). Output formats are modules: MSI, MSIX, AppImage, APK, a plain folder, a container. | `Bazaar.Installer`, `Bazaar.Installer.<Format>` | Gap |
| R-083 | Health of a deployment as a whole: server, database, tills, devices | Kodo (doc 46), status incidents (doc 77) | Aggregate Kodo score per branch; the status page lists tills by branch | `Bazaar.Server.StatusIncidents` | Covered with change |

## 17. PCI scope, security and local-law exposure

### 17.1 PCI boundary (PROPOSED invariants)

These are Bazaar's own proposed invariants, in the style of doc 15; they are floors, not settings (configurability rule).

1. No component in a Retail install stores, logs or transmits a primary account number, magnetic stripe data, card verification value or PIN. A card terminal handles them and returns a token and masked data.
2. Card data never enters a log. The redaction rule set (doc 43; convergence A6) gets a mandatory card-number pattern that operators cannot disable.
3. The terminal contract (R-010) has no field that can carry card data. A test inspects the contract for such field names.
4. Manual card entry (keying a number into a till) is a separate module and not shipped enabled; if an operator builds it, it is the operator's compliance question and out of Bazaar's scope.
5. Terminal-to-till traffic uses authenticated, encrypted links where the terminal supports it. Keys come from Aludel (R-076).
6. Receipts print masked data supplied by the terminal only.

| Ref | Need | Covered by | Receptive change | Module name | Status |
|---|---|---|---|---|---|
| R-084 | Enforce "never store card data" | Doc 43 redactor; doc 15 | Mandatory redaction rule plus a data-shape architecture test in the testing kit that scans contracts and migrations for forbidden column names | `Cdax.Testing` rule; `Cdax.Telemetry` rule set | Covered with change |
| R-085 | Reduce PCI scope of the server | None | Document the scope boundary: tills and terminals are in a segment; the server sees tokens only | doc note, section 17.1 | Covered |
| R-086 | Untrusted input: webhook bodies, imported booking data, scanned codes | Doc 15 taint; doc 73 content screening | Scanner and webhook data is untrusted input by default | `Bazaar.Server.ContentScreening` use | Covered |

### 17.2 Local-law exposure as operator choice

Tax, fiscal-device, receipt-format and retention rules differ by country, and in some places by city. Per the configurability rule, Bazaar takes no position: it ships a documented set of options with the most protective defaults and records the choice.

| Ref | Need | Covered by | Receptive change | Module name | Status |
|---|---|---|---|---|---|
| R-087 | Jurisdiction packs: tax profile, receipt format, numbering rules, retention floor, required reports | DECISIONS-LOG round 13 (operator choice) | A pack is a module containing data and rules (R-047, R-030, R-055, R-057). One tenant may enable several (branches in different places). | `Resibo.Retail.Jurisdiction.<Code>` | Gap |
| R-088 | Certified or registered fiscal devices where a country demands them | None | A `IFiscalDevice` contract, optional, with a vendor module per device. When enabled, a sale is not complete until the fiscal device returns its record. | `Resibo.Retail.FiscalDevices.Abstractions`; `.<Vendor>` | Gap |
| R-089 | Operator warnings: the setup wizard lists which duties the chosen pack does and does not cover | Doc 45 onboarding | Wizard step reading pack metadata `coverage` and `notCovered`; the operator acknowledges, audited | `Bazaar.Server.Onboarding` step | Covered with change |
| R-090 | Privacy law for customers and staff | Doc 79, doc 32 | As R-075 | `Bazaar.Server.AccountGuardian` | Covered with change |

### 17.3 BIR-ready export formats (DECIDED direction, details OPEN)

Owner, 2026-11-19: every Resibo export will eventually be in a BIR-compliant, BIR-ready format (BIR is the Philippine tax authority), not generic sheets. The direction is DECIDED. The formats themselves are OPEN: no BIR specification is in hand, and this document does not invent field lists, layouts, file names or submission rules. The Resibo docs record only that the SLSP byte-level DAT specification is unknown (resibo-50-56 inventory [R]); nothing more is assumed here.

The design provides the slot now, not the content:

- Doc 76's `ExportFramework` gets one provider module per format, `Bazaar.Server.Export.<Format>`, and a rule that a format module declares a **specification reference** (document name, version, date obtained) in its manifest data. A format module with no reference is marked `draft` and cannot be enabled for a tenant whose jurisdiction pack requires BIR output.
- Retail outputs that need a format slot: Z-reports and X-reports (R-057), books and journals, receipts and invoices (R-063), and summary lists. Each has a contract (`IReportFormat<TReport>`), so one sealed day (R-056) can render as a printed report, a spreadsheet review copy, or a BIR-ready file, depending on which format modules are enabled.
- A format module is a pure transformation of sealed journal data. It reads and never writes, and a re-run on the same sealed input is byte-identical (testable in the testing kit).
- Every format module has a golden-file test set. Samples come only from the operator or the accountant, never from the public repo (CLAUDE.md: no private material in git).
- Spreadsheet formats (xlsx, csv) remain as review copies. They are not the BIR-ready output where a BIR format exists, and never a store (section 17.4).

| Ref | Need | Covered by | Receptive change | Module name | Status |
|---|---|---|---|---|---|
| R-091 | Per-format provider slot for every export and report | Doc 76 `ExportFramework`; Resibo export framework provider registry [R] | Add `IReportFormat<TReport>` and the manifest `specReference` data field | `Bazaar.Server.ExportFramework`; `Bazaar.Server.Export.<Format>` | Covered with change |
| R-092 | BIR-ready formats as the target for Retail Z-reports, books and receipts | None (specifications not in hand) | Placeholder format modules named `Export.Bir.<Report>`. Contents are OPEN until the BIR specifications are obtained. No implementation from guesses. | `Resibo.Retail.Export.Bir.<Report>` (names only) | Gap |
| R-093 | Resibo-Consumer exports (SLSP and others) also move to BIR-ready formats | Resibo `slsp_summary`: thresholds are config, `.dat` is BIR's electronic format, byte spec unknown [R] | Same slot. Consumer content is doc 84's concern; this row records that the slot is shared. | `Resibo.Consumer.Export.Bir.<Report>` | Gap |
| R-094 | Know which format a tenant must use, and fail loudly if it is missing | Jurisdiction packs (R-087) | The pack lists required report formats; the setup wizard and Kodo flag a missing or `draft` format module before the first close | `Resibo.Retail.Jurisdiction.<Code>`; `Bazaar.Server.Onboarding` step | Gap |

### 17.4 Storage: a proper database, never spreadsheets (DECIDED direction)

Owner, 2026-11-19: unlike Resibo V2, all Resibo data lives in a proper database. Spreadsheet workbooks are export outputs only, never a store.

- This is a floor, not a setting. No module may persist state in an Excel workbook, CSV or other flat spreadsheet file. A spreadsheet may be imported one way, into the database (docs 44 and 76).
- The database is whatever Aludel's multi-database contract provides (Postgres first, SQLite per tenant possible; Q5). All access goes through that contract, so the rule holds for either engine.
- Sealed fiscal days (R-056) and the journal (R-053) are database rows plus the chain, not files. A spreadsheet of a sealed day is a rendering of it and carries a hash of its sealed source so it can be checked.
- The testing kit gets an architecture rule: no spreadsheet library is referenced from a storage path, and no migration or repository writes a spreadsheet. Spreadsheet-writing code lives only in `Export.<Format>` modules and importer modules.

| Ref | Need | Covered by | Receptive change | Module name | Status |
|---|---|---|---|---|---|
| R-095 | Database-only storage rule | Aludel multi-database contract (convergence section 4a); doc 04 | Record the rule in the Aludel contract documentation and enforce it by test | `Cdax.Aludel`; `Cdax.Testing` rule | Covered with change |
| R-096 | Spreadsheets are outputs only | Doc 76 export; doc 44 portability | Spreadsheet export formats carry `kind: review-copy` in their manifests; R-095 forbids them as a store | `Bazaar.Server.Export.Xlsx`, `.Csv` | Covered |
| R-097 | Migrating legacy workbook data (Resibo V2 style) into the database | Doc 76 reimport framework; doc 44 | One-way importer modules with three-way diff and an import report; the workbook is never written back | `Bazaar.Server.Import.<Format>` | Covered with change |

## 18. Shared-library receptivity summary

What each CDAX library must change, gathered from the rows above. Each is a contract or setting, not an implementation.

| Library | Changes | Rows |
|---|---|---|
| `Cdax.Core` | `Principal.onBehalfOf`; `Scope` sub-path; manifest fields `family` and `dataAuthority`; multi-active provider set; clock contract | R-001, R-005, R-006, R-009, R-034, R-038 |
| `Cdax.Money` (new, contracts and pure logic, BCL only) | Money, currency, rounding, allocation, exchange rate | R-043 to R-046 |
| `Cdax.Aludel` | Key purposes; non-exporting `ISigner`; per-tenant key; tenant store contract; database-only storage rule | R-036, R-076 to R-078, R-095 |
| `Cdax.Tabula` | Named chains; retention per class; anchoring sinks; standalone verifier format | R-053, R-055, R-061, R-062 |
| `Cdax.Kodo` | Degraded states for devices; aggregate score per branch | R-025, R-083 |
| `Cdax.Ouroboros` | Maintenance-window contract; version skew policy | R-079, R-080 |
| `Cdax.Annales` | Mandatory card-pattern redaction; per-tenant log scope | R-084 |
| `Cdax.Rubedo` | View `surface` (screen, print, customer-display) | R-020, R-057 |
| `Cdax.Settings` | Scope resolution with floors; reload events | R-007 |
| `Cdax.Testing` | Cross-family rule; forbidden-field scan; no-spreadsheet-store rule; export golden-file harness; store-agnostic fixture; device fakes (printer, drawer, terminal, scanner, scale) | R-002, R-084, R-095 |
| `Constellation.Renderer` | None found. Retail may reuse it for a branch-and-device map, optional. | none |

Device fakes in the testing kit matter most: every device contract ships with a deterministic fake and a failure-injection mode (doc 78), so a till can be tested with no hardware.

## 19. Reconciliation: Retail need against coverage

Counts are of rows R-001 to R-097 (97 rows). Each row's status in sections 5 to 17 is authoritative; this table is built from them.

| Coverage | Count | Rows |
|---|---|---|
| Covered | 13 | R-004, R-015, R-039, R-049, R-050, R-051, R-052, R-060, R-062, R-069, R-085, R-086, R-096 |
| Covered with change | 38 | R-001, R-002, R-005, R-006, R-007, R-009, R-016, R-018, R-020, R-025, R-026, R-028, R-029, R-031, R-034, R-036, R-038, R-043, R-053, R-054, R-055, R-061, R-070, R-073, R-075, R-076, R-077, R-078, R-079, R-080, R-081, R-083, R-084, R-089, R-090, R-091, R-095, R-097 |
| Gap | 46 | R-003, R-008, R-010, R-011, R-012, R-013, R-014, R-017, R-019, R-021, R-022, R-023, R-024, R-027, R-030, R-032, R-033, R-035, R-037, R-040, R-041, R-042, R-044, R-045, R-046, R-047, R-048, R-056, R-057, R-058, R-059, R-063, R-064, R-065, R-066, R-067, R-068, R-071, R-072, R-074, R-082, R-087, R-088, R-092, R-093, R-094 |

Reading the reconciliation:

- **BIR-ready output and database-only storage (owner, 2026-11-19) add a format slot and a floor rule, not new foundations.** The BIR format content stays OPEN until the specifications are obtained.
- **Most of the foundation is covered with a small change.** Principal, Scope, audit chain, KEK, settings, update model and offline merge already exist or are designed; the changes are fields and optional contracts.
- **Gaps cluster in three places:** devices and terminals (section 8), Retail domain modules (sales, numbering, inventory, shifts), and wire transports (section 12). None of them touch Bazaar's feature code.
- **No row asks Bazaar.Server or Bazaar.Client.Core to gain feature code.** Where a Bazaar module is proposed (`Bazaar.Server.Webhooks`, `Bazaar.Client.OfflineSync`, `Bazaar.Installer`, `Bazaar.Conduit.Lan`) it is its own module, and each is useful to Bazaar itself.

## 20. Modules proposed in this document

### 20.1 Bazaar-side modules (benefit Bazaar and Retail)

| Module | Rows | Why Bazaar benefits |
|---|---|---|
| Doc 90 transport and codec modules (not new here) | R-035, R-050 to R-052 | Transport choice for clients on bad networks; QUIC suits mobile |
| `Bazaar.Server.Webhooks` | R-012 | GitHub and Stripe-like callbacks need the same replay-safe receiver |
| `Bazaar.Client.OfflineSync` | R-028, R-031 | Already needed by doc 49; extracting it makes it reusable |
| `Bazaar.Conduit.Lan` | R-040 | A Conduit for LAN-only installs |
| `Bazaar.Installer`, `Bazaar.Installer.<Format>` | R-003, R-082 | Doc 37 packaging and doc 45 onboarding |
| `Bazaar.Identity.Method.Pin`, `.Badge`, `.Biometric` | R-071 | Quick local unlock on Android and desktop |
| `Bazaar.Tabula.Anchor.<Sink>`, `Bazaar.Tabula.Verifier` | R-061, R-062 | Doc 48 anchors |
| `Bazaar.Aludel.<Provider>` (Android keystore, platform keys) | R-077 | Doc 29 Android |
| `Bazaar.Notifications` (candidate) | R-016 | Doc 22 and doc 77 |
| `Bazaar.Server.HardwareProbe` device source | R-025 | Doc 74 |

### 20.2 Retail-side modules (names only)

`Resibo.Retail.Payments.*`, `.Terminals.*`, `.Bookings.*`, `.Devices.Abstractions`, `.DeviceRelay`, `.Printers.*`, `.Drawers.*`, `.Scanners.*`, `.Scales.*`, `.Displays`, `.Labels`, `.PrintQueue`, `.Sales`, `.Numbering`, `.Branches`, `.TenantLifecycle`, `.Tax.*`, `.Pricing`, `.Rates.*`, `.FiscalJournal`, `.DayClose`, `.Reports.*`, `.CashUp`, `.Reporting`, `.Receipts`, `.Refunds`, `.Inventory.*`, `.Shifts`, `.Staffing`, `.TradingHours`, `.Jurisdiction.*`, `.FiscalDevices.*`, `.Client.<Head>`. Retail may rename any of these.

## 21. Conflicts, decisions and owner questions

### 21.1 Conflicts

| Ref | Conflict | Proposed resolution (PROPOSED) |
|---|---|---|
| C-01 | CLAUDE.md "do not add Protobuf tooling" and "JSON via `IWireCodec`" versus owner direction to add gRPC and everything | Resolved by doc 90 (PR #927): the rule is lifted for the gRPC module and the Protobuf codec module only (`Bazaar.Server.Transport.Grpc`, `Bazaar.Client.Transport.Grpc`, `Bazaar.Wire.Codec.Protobuf`); Core, Contracts and the JSON path stay free of Protobuf; the gRPC module hard-depends on the Protobuf codec and nothing else does (architecture test, doc 90 section 2). JSON over WebSocket stays the floor. The CLAUDE.md edit belongs to doc 90's docs PR. Nothing further here |
| C-02 | "No web client, ever" versus Retail "runs on any device" | Retail heads are Avalonia heads (Windows, Android, Linux, and macOS or iOS if Avalonia ships them). A browser till is not offered. Owner decides (Q3) |
| C-03 | Resibo Auth is passwordless only versus a till PIN | A PIN is a local device credential only, never a sign-in to the server; it unlocks a till session that is already authenticated by a passkey or a device credential. Authentication floor unchanged |
| C-04 | "Server does all compute" versus peripheral relays on the till | Add peripherals to the client-side exceptions list (R-017) |
| C-05 | Resibo Consumer has audit retention default 3650 days for its domain; Retail operators in other places | Retention is a per-tenant setting with a jurisdiction pack floor; no default is hard-coded in the shared library |
| C-06 | Bazaar's Resource Economy REJECTED versus a money type in shared libs; and the owner's exact-amounts decision (PR #872) versus a commerce money type | No conflict with the rejection: agent budgets versus commerce arithmetic (R-049). No conflict with the amounts decision: `Cdax.Money` follows it (R-043); the only open point is where the shared numeric core lives (doc 40) |

### 21.2 Decisions taken in this document (PROPOSED, not questions)

Following the owner's rule "over-engineer; each option its own module":

- Every device, payment, terminal, tax-jurisdiction, export and anchor option is its own module under a contract; no option is dropped for being uncommon.
- Wire transports and codecs are modules, designed in doc 90; Retail reuses them and adds none.
- Every Retail fiscal fact is append-only and journalled; corrections are new records.
- `Cdax.Money` is exact (arbitrary precision, disk is the limit, per the PR #872 decision), with named rounding.
- Stock is a movement ledger; balances are derived.
- All provider modules have a deterministic fake in the testing kit.

### 21.3 Owner questions

1. **May peripherals be driven from the till (R-017)?** This adds an item to the client-side exceptions list in CLAUDE.md. Recommended: yes, as a device relay process. The alternative, a server-side driver reaching devices over the network, fails for USB and serial devices.
2. **What counts as "any device" for Retail?** Recommended: Avalonia heads only (Windows, Android, Linux first; macOS and iOS if Avalonia supports them under the OSI-only rule). A browser till breaks the no-web-client rule.
3. **Offline number block behaviour when a till runs out (R-030): stop, or continue with a provisional prefix?** Recommended: stop by default, provisional as an operator setting, because gapless numbering is a legal question in many places.
4. **Offline card approval (R-033): off by default?** Recommended: off, operator-enabled, with a limit; the risk of chargebacks sits with the operator.
5. **Where does the Retail repo's data layer live: the same Postgres model as Bazaar, or per-tenant SQLite on small installs?** Aludel is multi-database already. Recommended: Postgres first, with the tenant store contract (R-036) written so a SQLite provider can follow.

## 22. Work packages

Dependency order. None of them is built now. Each package is its own PR with its test, and states `Docs section this implements: 85, <section>`.

| WP | Content | Rows | Depends on |
|---|---|---|---|
| WP1 | Docs PR: this document; README index row | all | none |
| WP2 | `Principal.onBehalfOf` and `Scope` sub-path contracts with serialisation tests | R-005, R-006 | none |
| WP3 | Manifest fields `family`, `dataAuthority`; architecture rule R-002 | R-001, R-002, R-038 | WP2 |
| WP4 | `Cdax.Money` contracts and pure logic, property tests for allocation and rounding; storage tiers per R-043 | R-043 to R-046 | PR #872 numerics (or doc 40 decision on the shared core) |
| WP5 | Tabula named chains and retention per class | R-053, R-055 | WP2 |
| WP6 | Aludel key purposes and non-exporting signer | R-076, R-078 | WP2 |
| WP7 | Rubedo view `surface` | R-020 | none |
| WP8 | Ouroboros maintenance window and skew policy | R-079, R-080 | none |
| WP9 | Multi-active provider set in Core | R-009 | WP3 |
| WP10 | Mandatory card-pattern redaction and forbidden-field scan | R-084 | none |
| WP11 | Wire transport and codec modules | R-050 to R-052 | Owned by doc 90's work packages (build order in doc 90); no Retail package |
| WP14 | `Bazaar.Client.OfflineSync` extraction | R-028, R-031 | WP2 |
| WP15 | `Bazaar.Server.Webhooks` | R-012 | none |
| WP16 | `Bazaar.Installer` and `InstallPayload` | R-003, R-082 | WP3 |
| WP17 | Device fakes in the testing kit | R-025, section 18 | none |
| WP18 | `Bazaar.Conduit.Lan` | R-040 | none |
| WP19 | `Bazaar.Identity.Method.Pin` (with Badge and Biometric stubs) | R-071 | none |
| WP20 | Tabula anchor sinks and standalone verifier | R-061, R-062 | WP5 |

Retail-side packages (R-008 to R-027, R-091 to R-094, R-030 to R-033, R-047 to R-048, R-056 to R-068, R-072 to R-074, R-087, R-088) are not scheduled here; they wait for the Retail repo.

## 23. Overlaps with existing docs

- **Doc 84** (exchange ledger): it routes Consumer's features. This doc does not repeat any row; R-016, R-055, R-060, R-070 and R-075 cite its modules (notifications candidate, export framework, groups, account guardian).
- **Doc 49 section 4**: R-028 to R-034 extend its merge rules with Retail data classes (numbered documents, stock movements) and extract its queue into a module. The doc 49 rules themselves are not reopened.
- **Doc 72** (access grants): R-039 and R-073 reuse it.
- **Doc 73** (content screening): R-086 reuses it for webhook and scan input.
- **Doc 74** (hardware probe and resource ledger): R-018, R-025 and R-026 reuse the device key and the lease shape.
- **Doc 75** (watchdog): R-025 and R-034 add device and clock attention.
- **Doc 76** (export): R-060 adds formats.
- **Doc 78** (testing kit): R-002, R-084 and section 18 add rules and fakes.
- **Doc 79** (account guardian): R-075 and R-090.
- **Doc 81** (audit vocabulary and retention): R-053 and R-055 add named chains and per-class retention; the vocabulary gains Retail kinds when written.
- **Docs 71, 77, 80, 82, 83** are used by reference only (dependency watcher, status, MCP, job classes, client patterns).
- **Doc 90** (wire transports and codecs, PR #927): section 12 and R-035, R-050 to R-052 point to it and add no design.
- **Doc 02, 39 and the CLAUDE.md wire line**: R-035 and R-050 to R-052 reuse doc 90, which carries the change; C-01 is resolved there.
