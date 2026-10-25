# Community and External Platforms

Status: DRAFT for owner review (2026-10-25) · Owner questions: 13
Depends on: 15-security-taint-and-approvals.md, 14-identity-service-and-passport.md, 21-github-integration.md, 22-email-subsystem.md · Used by: 06-agent-runtime.md, 23-observatory-and-usage.md, 28-windows-client.md, 29-android-client.md

## Purpose
Lets Soul-backed agents take part in outside developer communities (GitHub, Stack Overflow, Roblox DevForum, Godot forum, Reddit, developer Discords, RFC lists) as disclosed bot participants. Defines the per-platform policy data, the posting tiers, and the audit and kill-switch rules that keep this inside each platform's terms.

## Decided inputs
- Agents are disclosed bot participants; they sign up only where terms allow, otherwise a person creates the account (owner, 2026-10-24; Bazaar.md).
- Public-posting tiers are operator-configured with shipped defaults (doc 15). Human-only CAPTCHA/phone/ID steps: recommendation, not decided.
- Platform bans are respected. GitHub identity is GitHub Apps (CLAUDE.md).
- All community content is `external` and taints a session hard (doc 15).
- Linux kernel model is the reference for accepted disclosed-bot contributions (research, verified).

## Design
### Community feature set [PROPOSED]
- **Watch** tagged repos, threads and forums (Tier 0, reads only). **Draft** in a GC for sponsor review.
- **Post** issues, PRs, threads or comments (Tier 2); **reply** in human-approved threads (Tier 1, Conflicts 1); **edit or withdraw** own posts (Tier 2).
- **Accounts**: a human creates them; the agent gets the credential as an Identity Service lease (doc 14).

### Approval tiers for community actions [PROPOSED]
Maps onto doc 15's tiers. Operators may change any row per platform.

| Tier | Community actions (shipped default) |
|---|---|
| 0 | Search and read public content; read internal GC; read a repo's contributing rules |
| 1 | Reply in a human-approved thread, with notice in GC; only when the session is clean (Conflicts 1) |
| 2 | Any new issue, PR, forum thread or comment in a new thread; edit or withdraw own post; any new account; outreach email to a stranger; any RFC or mailing-list post |
| 3 | Human performs: CAPTCHA, SMS/phone, ID, passkeys, recovery codes, legal-term acceptance |
| Deny | Platform unverified or banned; Hacker News; CAPTCHA or SMS-rental services; accounts in another person's name; repos whose rules bar AI contributions; blocklisted targets |

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
Platform policy matrix (data, seeded from research; status labels as in the research notes: verified = primary text read through a summary; secondary = search snippet or news; unverified = not read):

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

### Rate limits, audit and takedown
- **Quotas** (enforced in ToolDispatcher, doc 15): 5 new public items per day per platform; 1 new issue or PR per repo per day; no more than 3 new repos per week per agent; GitHub content writes 30/min per install, below the 80/min secondary limit. Breach denies, then freezes that agent on that platform and notifies the sponsor.
- **Audit**: every outward action writes one `community_action` row and an audit_log entry (doc 15). Rows are append-only; a withdrawal adds a row. Shows in the Observatory and in the GC where the draft was made.
- **Kill switch**: per platform (stops all community writes there); per target (objection or moderator ban adds to `target_blocklist`); per agent (doc 15). A blocklisted target is not reopened without an owner decision.
- **Withdraw**: closing or deleting own posts where the platform allows, Tier 2.

## Behaviour and edge cases
- Approval body-hash mismatch denies; any edit after approval needs a new approval.
- `checked_on` older than 30 days on an active platform drops posting to Deny until re-checked [PROPOSED].
- A moderator or platform notice that bot or AI posting is barred sets the platform to Banned and triggers its kill switch.
- Idempotency key per approval; search the platform before any retry.
- Sponsor offline: posts wait. Nothing auto-approves.
- GitHub: installation tokens expire after 1 hour. Issue creation on an uninstalled public repo and PRs from org forks are unverified (Conflicts 4).

## Testing
- Fake adapters with recorded HTTP fixtures; no live accounts in CI.
- Policy loader: Unverified and Banned resolve to Deny; Hacker News is Deny at every tier.
- Tier engine on real Postgres: tainted session turns Tier 1 into Tier 2; new account is Tier 2; blocklisted target is Deny.
- Golden tests: footer and profile label on every body; `Assisted-by` on PRs, no agent `Signed-off-by`.
- Altered body or expired approval denies. Poisoned-issue replay yields no community action.
- Quota, kill-switch and audit-completeness tests. Avalonia.Headless test of the draft-review card.
- Opt-in live GitHub spike on a throwaway public repo without the App installed; not in CI.

## Work packages
1. `platform_policy` schema, loader and seed from the research matrix. No deps.
2. Bot profile model and footer builder. No deps.
3. `community_action` table and audit linkage. Dep: doc 15 WP 7.
4. `CommunityAction` tier mapping into ToolPolicyEngine. Dep: doc 15 WP 4.
5. `target_blocklist` and kill-switch hooks. Dep: 4.
6. Quality-gate checks (duplicate search, example evidence, rule freshness). Dep: 1.
7. GitHub adapter (issues, PRs, trailers) behind App identity. Dep: 4, doc 21.
8. Draft-review card contracts and Avalonia UI. Dep: 3, doc 15 WP 6.
9. Observatory community log view. Dep: 3.
10. Community quota buckets. Dep: doc 15 WP 4.
11. Opt-in live GitHub spike (throwaway repo). Dep: 7.
12. Other adapters (forums, Discord, Reddit). Blocked until each platform is verified and the owner decides.

## Conflicts
1. **Reply autonomy vs taint.** Reading a thread is an `external` read, so the session becomes Tainted(hard). Doc 15 then turns Tier 1 into Tier 2. A Tier 1 reply cannot be autonomous in the session that read the thread. Options: approve all replies in v1, or build the reader/actor split (doc 15, later option) first.
2. **New accounts.** Owner default is approval for each new account. Doc 15 lets whitelisted sign-ups run autonomously with notice. This doc follows the owner default; doc 15 needs amending.
3. **Unverified platforms.** Doc 15 says no record means Tier 2. This doc proposes Deny for posting. Stricter; owner decides (Q3).
4. **GitHub reach.** Installation-auth docs say tokens cover only granted repos; a 2025 community answer says any actor can open an issue on a public repo. The Forks API requires the App on the source account. The fork-and-PR flow is unverified and may fail.
5. **Godot.** Primary CONTRIBUTING.md has no AI rule; a secondary report says June 2026 bans autonomous agents. Treated as Deny until resolved.
6. **Sign-up flow.** Bazaar.md says agents sign up to GitHub and Stack Overflow; GitHub ToS says a human must create the account. The human creates; the agent gets a lease. Owner rule unchanged.
7. **EU AI Act Article 50.** Reported in force from 2 Aug 2026 (today is 25 Oct). If so, the labels here are required, not optional.

Legal points for counsel (research, not advice):
- EU AI Act Art. 50: provider vs deployer role; the unconfirmed December 2026 grace period; Art. 50(4) for AI text on public-interest matters.
- California BPC 17941 (commercial or election deception); AB 410 status.
- Philippines RA 10173 s.6 reaches PH residents' data, including maintainer contact details in threads.
- CAN-SPAM ($53,088 per email) and GDPR/PECR for outreach email (GDPR/PECR not researched).
- Sponsor liability when a platform bans the account.

## Questions for the owner
1. New accounts: approval every time, overriding doc 15's whitelist autonomy? Default: yes (Tier 2).
2. v1 replies: approve every reply until the reader/actor split exists? Default: yes.
3. Unverified platforms: no posting until a verified record exists? Default: yes (Deny).
4. Reddit: any agent posting? Default: no; sponsor-manual only until the primary API terms are read.
5. Godot forum and repos: Deny until the contributing rules and forum are read? Default: yes.
6. Stack Overflow: Deny until the gen-AI policy is read? Default: yes.
7. Quotas: 5 new public items/day/platform; 1 new issue or PR per repo per day; 3 new repos/week? Default: as stated.
8. Footer and display name wording (placeholder above)? Default: "(AI agent)" suffix and the one-line footer.
9. Sponsor: one named human per agent, required before any account? Default: yes.
10. Code contributions: `Assisted-by` plus sponsor `Signed-off-by`, with no `Co-Authored-By` in other projects? Default: yes. (This repo keeps CLAUDE.md's trailers.)
11. Objections: stop and blocklist at once; withdrawal of own posts needs approval? Default: yes.
12. Cold outreach email to maintainers and RFC or mailing-list posts in v1? Default: no cold email; replies only in threads the agent opened; each list post Tier 2.
13. Counsel: engage before the first live post? Default: yes.
