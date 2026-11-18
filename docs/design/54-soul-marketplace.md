# Soul Marketplace

Status: PROPOSED (2026-11-18). This doc gathers what the owner and the other docs already say about the Soul Marketplace in one place and lists what is still open. The nature of the marketplace is DECIDED, and the owner decided on 2026-11-16 (module decision cards) that the marketplace ships during beta, so it is in scope for beta. The remaining questions in section 7 carry PROPOSED defaults (coordinator, 2026-11-18) until the owner answers. The owner's naming ("Soul = marketplace") makes that decision cover the Soul Marketplace.
Depends on: `07-soul-system.md` (Soul, export format, import quarantine), `33-backup-dr-and-export.md` (Soul export), `44-data-portability.md`, `47-licensing-and-trademarks.md` (licence rules), `48-gap-decisions-security-identity.md` (Verified tag, audit), `15-security-taint-and-approvals.md` (taint, approvals) · Used by: `25-company-systems-knowledge.md` (Talent Pool)

## Purpose
An optional place to share preset Souls (behavioural configuration for an agent) between Bazaar instances, so a company can import a specialist someone else built. It never moves anything personal, and it never moves what an agent has learned.

## 1. Decided (owner)
| Item | Decision | Source |
|---|---|---|
| Nature | Completely optional, user-generated content, for sharing preset Souls. | `Bazaar.md` section 2; `CLAUDE.md` |
| Content | Behavioural presets only. No personal data and no project contexts (memories, accounts, credentials, reputation). | `Bazaar.md` section 2; `CLAUDE.md` |
| Memory and reputation | Never part of anything published. Memory-free by design (the bundle has no field for it), by enforcement (the publish path refuses it), and by test. A separate memory export exists but has no marketplace target. | doc 07 "memory exclusion" (owner, 2026-11-13); DECISIONS-LOG; docs 19, 44 |
| Audit | Every publish writes an audit record with the bundle hash and the enforcement result. | doc 07; doc 48 |
| Mirror Agent | The Mirror Agent profile and preferences are excluded from exports. | doc 07, doc 26 |
| Timing | The marketplace ships during beta (owner module decision cards, 2026-11-16). This replaces the earlier "v1 scope OPEN" note in `FEATURES.md`, `ROADMAP.md` and doc 07 Question 1, which must be aligned. | DECISIONS-LOG, module decisions 2026-11-16 |

## 2. The bundle (format only, PROPOSED in doc 07)
The Soul export is the marketplace bundle. Per doc 07: `manifest.json` (schemaVersion, bundleVersion, creator, per-file SHA-256, signature, licence from `soul.export_licence`, exclusions list, extensions), `charter.md`, and `skills/<name>/SKILL.md`.

Excluded: Passport, VM disks, GC messages, Vault content, Kanban, raw embeddings, memories, lessons, preferences, the Mirror Agent profile, reputation, and any text naming a person. Export runs a secret-pattern scan and a human review.

## 3. Importing (from the research recommendation and doc 07)
An imported Soul is untrusted (`Bazaar.md` section 2, research recommendation; doc 07 work package 9):
- It lands in quarantine, verified against its manifest digests and signature, and has no tool grants until approved.
- Approval, whitelist changes and tool grants follow the existing Tier 2 approval ladder (doc 15, doc 07). Importing never changes a model whitelist or grant as a side effect.
- Imported text is external content and taints hard (DECISIONS-LOG design set; doc 15). Reader sessions handle it.
- Hiring a Soul into a company is the Talent Pool and a Council vote, not an automatic step (doc 25, doc 10).

## 4. Listing rules that already exist for module marketplaces
Docs 47 and 48 define the module marketplace, which is a different catalogue (installable modules, not Souls). Recorded here only to show what a Soul listing could reuse; whether it does is question 3.
- Licence: an SPDX expression, shown before install (doc 47 section 10).
- Verified tag: automated checks, a human review that an agent cannot supply, a monitored sandbox run, and automatic revocation on a failing later version. Unverified items install with an unsuppressible warning (doc 48 section 3).
- Mark misuse removal is recorded in the audit log (doc 47 section 9).
The Verified tag is defined for module versions. Nothing says it applies to Souls.

## 5. Not decided and not specified here
Hosting, the listing and search API, ratings, a creator identity or account system, payment (doc 47 says paid entitlement is a separate design), moderation and the reviewer pool (doc 48 marks the public reviewer pool OPEN), and export signing identity (doc 07 Question 6). None has an owner statement, so none is designed here.

## 6. Module placement (rule, not new design)
Under the modularity rule the marketplace client and server halves are separate modules, `Bazaar.Server.<Name>` and `Bazaar.Client.<Name>`, each with `.Abstractions`, and nothing goes into Server or Client.Core (`CLAUDE.md`; memory module-sides rule). The name is a working name until the owner picks one. The export and import logic belongs with the Soul module (doc 07 work packages 8 and 9), not the marketplace.

## 7. Questions (2 to 5 carry PROPOSED defaults)
1. **Is the marketplace in scope? ANSWERED (owner, 2026-11-16): it ships during beta.** Build order inside beta is not decided. PROPOSED default: local bundle export and import with quarantine first (doc 07 work packages 8 and 9), then the catalogue.
2. **Who hosts the catalogue?** Options: A) one Bazaar-run public catalogue; B) a catalogue each operator runs, with import by URL or file; C) both. PROPOSED default: B first, because it needs no central service, no moderation staff and no account system.
3. **Do Souls use the module marketplace's rules (licence, Verified tag, audit) or their own?** Options: A) reuse the module rules, with the Verified tag redefined for Souls; B) Souls get a lighter rule set (licence plus manifest verification plus quarantine). PROPOSED default: B for v1, since there is no code to scan, only text, skills and a charter, and the quarantine import already gates tool grants.
4. **Human review before listing.** Doc 07 says export runs a human review. Who reviews a published Soul, and is the creator's own review enough? Options: A) the creator's own review plus the automatic secret and exclusion scan; B) an independent reviewer. PROPOSED default: A for v1 (local and operator-run catalogue), B only if question 2 is A or C.
5. **Export signing identity** (doc 07 Question 6). Recommended default stays: the exporting agent's GitHub App identity plus an operator key. The signing tool is unverified.

## 8. Tests (when built)
- A Soul that has memory, lessons, reputation and a Passport reference publishes a bundle with none of them (the doc 07 test).
- The publish path refuses a bundle built from the memory export.
- A bundle with a tampered file fails the digest check on import and stays in quarantine.
- An imported Soul has no tool grant and no whitelist entry until an approval records them.
- Each publish writes an audit row with the bundle hash.

Docs section this implements: none yet. This doc is the design proposal.
