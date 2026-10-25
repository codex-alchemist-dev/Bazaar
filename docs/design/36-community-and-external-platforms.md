# Community and External Platforms

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 11
Depends on: 01-gc-data-model.md, 04-postgres-schema-and-migrations.md, 14-identity-service-and-passport.md, 15-security-taint-and-approvals.md, 21-github-integration.md, 22-email-subsystem.md · Used by: 06-agent-runtime.md, 23-observatory-and-usage.md, 27-client-architecture.md, 28-windows-client.md, 29-android-client.md

## Purpose
Lets Soul-backed agents take part in outside developer communities (GitHub, Stack Overflow, Roblox DevForum, Godot forum, Reddit, developer Discords, RFC lists) as disclosed bot participants. Defines the per-platform policy data, the posting tiers, and the audit and kill-switch rules that keep this inside each platform's terms.

## Decided inputs
- Agents are disclosed bot participants. They sign up only where terms allow; otherwise a person creates the account (owner, 2026-10-24; CLAUDE.md, Bazaar.md).
- Platform bans are respected. GitHub identity is GitHub Apps (CLAUDE.md).
- All community content is `external` and taints a session hard. Agent text taints softly; Tier 2 actions are gated on hard taint; a fresh-session path with an approved summary exists (DECISIONS-LOG round 2, C06).
- Reader sessions handle untrusted input and hand over summaries; actor sessions do not read external content directly. The approval gate applies to outward high-impact actions such as posting (owner statement, round 4, C18; Claude's reading, to confirm, see Q1).
- Approval ladder is doc 15's. Community defaults are stricter: new posts, new accounts and outreach need approval; unverified platforms are Deny. Operators may change thresholds (round 7, C21).
- Shared limits sit in one quota table in doc 15; this doc owns its community-specific limits (round 10, C36).
- Fork-and-PR is built as decided; the owner accepts the GitHub-docs risk (round 3, C13).
- Account credentials reach agents as Identity Service leases; the server holds provider and GitHub App keys (round 2, C05).
- Linux kernel model is the reference for accepted disclosed-bot contributions (research, verified).

## Design
### Community feature set [PROPOSED]
- **Watch** tagged repos, threads and forums (Tier 0, reads only). **Draft** in a GC for sponsor review.
- **Post** issues, PRs, threads or comments (Tier 2); **reply** in human-approved threads (Tier 1, see Conflicts 1); **edit or withdraw** own posts (Tier 2).
- **Accounts**: a human creates them; the agent gets the credential as an Identity Service lease (doc 14).

### Reader and actor flow [PROPOSED, pending Q1]
- **Reader session**: reads watched content. It is hard-tainted, has no write tools, and returns a schema-validated summary.
- **Actor session**: never reads raw community content. It receives the approved summary through doc 15's hand-over format, then drafts and posts.
- A hard-tainted session posts nothing. A Tier 1 reply is allowed only from a clean actor session, in a thread the human approved.

### Approval tiers for community actions [PROPOSED]
Maps onto doc 15's ladder (Tier 0 autonomous, 1 notice, 2 owner approves, 3 human performs, Deny). Operators may change any row per platform.

| Tier | Community actions (shipped default) |
|---|---|
| 0 | Search and read public content; read internal GC; read a repo's contributing rules |
| 1 | Reply in a human-approved thread, with notice in GC; only from a clean actor session (see Q1) |
| 2 | Any new issue, PR, forum thread or comment in a new thread; edit or withdraw own post; any new account; outreach email to a stranger; any RFC or mailing-list post |
| 3 | Human performs: CAPTCHA, SMS/phone, ID, passkeys, recovery codes, legal-term acceptance |
| Deny | Platform unverified or banned; Hacker News; CAPTCHA or SMS-rental services; accounts in another person's name; repos whose rules bar AI contributions; blocklisted targets; platforms with no verified record |

### Bot profile conventions [PROPOSED]
- Display name carries "(AI agent)" where the platform allows it. Bio names the sponsor and a contact address. Links to an operator-owned disclosure page (placeholder URL).
- One named human **sponsor** per agent. The sponsor holds or created the account and answers for its posts.
- Never claims to be human. No human photos or names.
- Every outbound body ends with a one-line footer (placeholder: `Posted by an AI agent (Bazaar), sponsor <name>, contact <address>.`). Where a platform forbids footers, the profile carries the disclosure and the post is Tier 2 with a note.
- Code: `Assisted-by: <tool> <model>` trailer. The sponsor adds their own `Signed-off-by`. Agents never add `Signed-off-by`.

### Quality gates before any Tier 2 post [PROPOSED]
1. Duplicate search done; the draft links existing threads it relates to.
2. Bug reports include a minimal example the agent ran in its VM, with versions and actual output. No example, no report.
3. Target rules checked within 30 days and no AI-contribution bar found.
4. Sponsor approves the exact body hash (doc 15 approval flow).
5. No personal data, credentials, internal links or thread-derived contact details.
6. Short, specific text; no filler.

### Data and interfaces
Platform policy matrix (data, seeded from research; labels as in the research notes: verified = primary text read through a summary; secondary = search snippet or news; unverified = not read):

| Platform | Status | Agent rule as read | Disclosure | Checked, label |
|---|---|---|---|---|
| Hacker News | Banned | No generated or AI-edited text; no automated posting | n/a | 2026-10-24, verified |
| Linux kernel (reference) | Allowed, human owner | Human reviews, certifies, submits | `Assisted-by` | 2026-10-24, verified |
| GitHub | Apps allowed; bot-made accounts barred | Humans create accounts; AUP bars bulk automation; no AI-content rule found | App identity | 2026-10-24, verified summary |
| Stack Overflow | Unknown; historic gen-AI ban | Not read (blocked) | Unknown | unverified |
| Reddit | API access reportedly ends March 2027 | Bot rules unread | Unknown | secondary (end date) |
| Discord | Unknown | Developer Policy returned 403 | Unknown | unverified |
| Roblox DevForum | No rule found | Only user requests for AI-spam reports | Unknown | unverified |
| Godot | Split | CONTRIBUTING.md has no AI rule (verified); June 2026 ban reported (secondary) | Unknown | conflict |
| X, Bluesky, Mastodon, RFC lists, Discords | Not researched | n/a | n/a | unverified |
| Google (email sign-ups) | Automated access and fake accounts barred | No automated sign-up clause found | n/a | verified summary, terms effective 2026-07-30 |
| QEMU, NetBSD, OpenJDK, Ghostty, Debian, llama.cpp | Vary | Maintainer policies | Varies | secondary or unverified |

Sources: newsguidelines.html; docs.kernel.org coding-assistants; GitHub ToS and AUP; Stack Overflow gen-AI policy (blocked); TechCrunch 2026-09-30 and Reddit Responsible Builder Policy (403); Discord Developer Policy (403); Roblox DevForum threads; godotengine/godot CONTRIBUTING.md; policies.google.com/terms.

```csharp
enum PolicyStatus { Verified, Secondary, Unverified, Banned }
record PlatformPolicy(string Platform, PolicyStatus Status, IReadOnlySet<CommunityAction> Allowed,
                      string DisclosureRule, Uri SourceUrl, DateOnly CheckedOn);
enum CommunityAction { Read, ReplyApprovedThread, NewPost, NewIssue, NewPullRequest,
                       EditOwn, WithdrawOwn, CreateAccount, OutreachEmail }
interface ICommunityAdapter {
    string Platform { get; }
    // ApprovalId is mandatory; the dispatcher verifies it before this is reachable.
    Task<PostReceipt> PostAsync(OutboundPost post, ApprovalId approval, CancellationToken ct);
}
```
```sql
platform_policy(platform pk, status, allowed_actions jsonb, disclosure_rule, source_url, checked_on, checked_by)
bot_profile(agent_id, platform, display_name, sponsor_id, contact, disclosure_url, pk(agent_id, platform))
community_action(id, agent_id, sponsor_id, platform, action, target_url, body, body_sha256,
                 approval_id, taint_sources jsonb, external_id, status, created_at)
target_blocklist(platform, target_ref, reason, added_by, added_at, pk(platform, target_ref))
```
This doc owns these four tables (doc 04 keeps conventions only). `taint_sources` uses doc 15's provenance enum. `body` and `contact` may name third parties, so they are encryptable per-person fields (round 2, C01) [PROPOSED; doc 04 to confirm].

### Rate limits, audit and takedown
- **Quotas**, enforced in ToolDispatcher (doc 15): 5 new public items per day per platform; 1 new issue or PR per repo per day; no more than 3 new repos per week per agent. The platform-wide public-post cap and GitHub write rate (30/min per install, below the 80/min secondary limit) live in doc 15's shared table. Breach denies, then freezes that agent on that platform and notifies the sponsor.
- **Audit**: every outward action writes one `community_action` row and an audit_log entry (doc 15). Rows are append-only; a withdrawal adds a row. Shows in the Observatory (doc 23) and in the GC where the draft was made.
- **Kill switch**: per platform (stops all community writes there); per target (objection or moderator ban adds to `target_blocklist`); per agent (doc 15). A blocklisted target is not reopened without an owner decision.
- **Withdraw**: closing or deleting own posts where the platform allows, Tier 2.

## Behaviour and edge cases
- Approval body-hash mismatch denies; any edit after approval needs a new approval.
- `checked_on` older than 30 days on an active platform drops posting to Deny until re-checked [PROPOSED].
- A moderator or platform notice that bot or AI posting is barred sets the platform to Banned and triggers its kill switch.
- Idempotency key per approval; search the platform before any retry.
- Sponsor offline: posts wait. Nothing auto-approves.
- GitHub: installation tokens expire after 1 hour (verified in doc 14). Issue creation on an uninstalled public repo and PRs from org forks are unverified (Conflicts 2).

## Testing
- Fake adapters with recorded HTTP fixtures; no live accounts in CI.
- Policy loader: Unverified and Banned resolve to Deny; Hacker News is Deny at every tier.
- Tier engine on real Postgres: a hard-tainted reader session posts nothing; a clean actor session may post Tier 1 replies only in approved threads; a new account is Tier 2; a blocklisted target is Deny.
- Reader-to-actor test: injected text in a thread yields a summary but no community action in the actor session.
- Golden tests: footer and profile label on every body; `Assisted-by` on PRs, no agent `Signed-off-by`.
- Altered body or expired approval denies. Poisoned-issue replay yields no community action.
- Quota, kill-switch and audit-completeness tests. Avalonia.Headless test of the draft-review card.
- Opt-in live GitHub spike on a throwaway public repo without the App installed (spike S22); not in CI.

## Work packages
1. `platform_policy` schema, loader and seed from the research matrix. No deps.
2. Bot profile model and footer builder. No deps.
3. `community_action` table and audit linkage. Dep: doc 15 WP 7.
4. `CommunityAction` tier mapping into ToolPolicyEngine. Dep: doc 15 WP 4.
5. `target_blocklist` and kill-switch hooks. Dep: 4.
6. Quality-gate checks (duplicate search, example evidence, rule freshness). Dep: 1.
7. GitHub adapter (issues, PRs, trailers) behind App identity, host-side pushes per doc 21. Dep: 4, doc 21, spike S22 for the issue and fork paths.
8. Draft-review card contracts and Avalonia UI. Dep: 3, doc 15 WP 6.
9. Observatory community log view. Dep: 3.
10. Community quota buckets, using doc 15's shared table. Dep: doc 15 WP 4.
11. Reader and actor hand-over for community content. Dep: doc 15 reader-session primitive.
12. Opt-in live GitHub spike (throwaway repo). Dep: 7.
13. Other adapters (forums, Discord, Reddit). Blocked until each platform is verified and the owner decides.

## Conflicts
1. **Reply path vs taint (remaining).** Rounds 2 and 4 keep the reader/actor split in v1 and forbid an autonomous reply from the session that read the thread. Doc 15 must still specify the hand-over format and the fresh-session path. Until Q1 is confirmed, every reply is Tier 2.
2. **GitHub reach (remaining risk).** The owner keeps fork-and-PR as decided (round 3, C13). Issue creation on uninstalled public repos and org-fork PRs remain unverified. Spike S22 runs before WP 7's fork path; fallback is installed repos only, with the owner posting drafts elsewhere.
3. **Godot (remaining).** The primary CONTRIBUTING.md has no AI rule; a secondary report says June 2026 bans autonomous agents. Deny until resolved (Q3).
4. **Sign-up wording in Bazaar.md (cross-doc).** Bazaar.md's community line (its line 42, per the earlier Conflict 6) implies agents sign up to GitHub and Stack Overflow. Doc 14 and CLAUDE.md say a human creates the account and the agent gets a lease. Bazaar.md needs the same wording.
5. **Doc 15 shipped defaults (cross-doc).** Doc 15 must list new accounts as Tier 2 and unverified platforms as Deny in its shipped defaults (round 7, C21), and must design the reader hand-over (Conflicts 1).
6. **Doc 04 encryptable fields (cross-doc).** `community_action.body` and `bot_profile.contact` need the per-person encryption rule (round 2, C01); C01's doc list does not include this doc.
7. **EU AI Act Article 50 (unverified).** A secondary report says it is in force from 2 Aug 2026. If so, the labels here are required, not optional. Counsel to confirm.

Legal points for counsel (research, not advice):
- EU AI Act Art. 50: provider vs deployer role; the unconfirmed December 2026 grace period; Art. 50(4) for AI text on public-interest matters.
- California BPC 17941 (commercial or election deception); AB 410 status.
- Philippines RA 10173 s.6 reaches PH residents' data, including maintainer contact details in threads.
- CAN-SPAM ($53,088 per email) and GDPR/PECR for outreach email (GDPR/PECR not researched).
- Sponsor liability when a platform bans the account.

## Questions for the owner
1. v1 replies: does the reader/actor split (round 4, C18; Claude's reading, to confirm) make Tier 1 replies from a clean actor session autonomous in approved threads? Default: no; approve every reply (Tier 2) until the hand-over is built and tested.
2. Reddit: any agent posting? Default: no; sponsor-manual only until the primary API terms are read.
3. Godot forum and repos: Deny until the contributing rules and forum are read? Default: yes.
4. Stack Overflow: Deny until the gen-AI policy is read? Default: yes.
5. Quota values: 5 new public items/day/platform; 1 new issue or PR per repo per day; 3 new repos/week? Default: as stated (community-specific; platform-wide caps stay in doc 15's table).
6. Footer and display name wording? Default: "(AI agent)" suffix and the one-line footer.
7. Sponsor: one named human per agent, required before any account? Default: yes.
8. Code contributions to other projects: `Assisted-by` plus sponsor `Signed-off-by`, with no `Co-Authored-By`? Default: yes. (This repo keeps CLAUDE.md's trailers.)
9. Objections: stop and blocklist at once; withdrawal of own posts needs approval? Default: yes.
10. Cold outreach email to maintainers and RFC or mailing-list posts in v1? Default: no cold email; replies only in threads the agent opened; each list post Tier 2.
11. Counsel: engage before the first live post? Default: yes.

ANSWERED: New accounts need owner approval every time, Tier 2, overriding doc 15's whitelist autonomy (DECISIONS-LOG round 7, C21).
ANSWERED: Unverified platforms are Deny until a verified record exists (DECISIONS-LOG round 7, C21; 36's stricter default adopted).
