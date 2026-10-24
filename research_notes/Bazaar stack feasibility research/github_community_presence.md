# GitHub Identity and Community Presence for Bazaar Agents (status as of 2026-10-24)

Not legal advice. Items marked UNVERIFIED were not confirmed against a primary source. Where a WebFetch result was a model-written summary rather than verbatim text, that is noted; re-check the quoted wording against the raw page before relying on it.

## 1. Machine accounts vs GitHub Apps (ToS and App limits)

### Takeaway
The design doc's "machine accounts are a dead end" is broadly right, and the ToS also bars bots from creating accounts. That second rule matters more for Bazaar than the design doc notes: the agents cannot sign themselves up for GitHub or other services. A human must. The doc's "no paid path" claim was not verified. I found no stated limit on the number of GitHub Apps an org can own.

### Cited Findings
- ToS (Effective date: April 27, 2026, per the fetched page) Section B, Account Terms: "You must be a human to create an Account. Accounts registered by 'bots' or other automated methods are not permitted." — [GitHub Terms of Service](https://docs.github.com/en/site-policy/github-terms/github-terms-of-service) (fetched via summarizer; verify wording)
- ToS defines a machine account as "an Account set up by an individual human who accepts the Terms on behalf of the Account," used "exclusively for performing automated tasks." — same source (summarizer)
- ToS: "One person or legal entity may maintain no more than one free Account," and "You may maintain no more than one free machine account in addition to your free Personal Account." — same source (summarizer). The design doc's "one free machine account per person/legal entity" matches this in substance.
- ToS does not mention GitHub Apps. Section B.5 says third-party apps that integrate with GitHub may have their own terms. — same source
- ToS Section H (API Terms) says abusive or excessive API requests may lead to suspension. — same source
- The GitHub Apps "about" page states that by default only organization owners can manage GitHub App settings in an organization. It does not state any limit on the number of Apps an account or org can register or own, and does not mention the `[bot]` suffix or bot identities. — [About creating GitHub Apps](https://docs.github.com/en/apps/creating-github-apps/about-creating-github-apps/about-creating-github-apps)
- The installation-auth page says API requests made by an installation are "attributed to the app." It does not mention a `[bot]` username. — [Authenticating as a GitHub App installation](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation)

### Inferences
- GitHub Apps are the correct vehicle for distinct, disclosed bot identities. Nothing in the fetched ToS or Apps docs prohibits them. Nothing explicitly endorses them as "allowed" either. Treat "allowed" as inferred from silence, not from explicit text.
- The "no paid path to buy more machine accounts" claim has not been verified against the Terms or pricing pages. Treat it as unverified.
- The ToS rule against bot-created accounts means the Soul design should be changed. Agents should not "sign up" for services. A human creates the account and hands over the App or token.
- The Apps-per-org count is not documented in the pages checked. Assume there is no hard published limit, but plan to confirm with GitHub Support before relying on about 4+ Apps.

### Gaps
- No explicit ToS or docs text says a `[bot]` identity is allowed for AI agents specifically. The ToS only bans bot-created accounts.
- The "[bot]" suffix behaviour and whether an App can be given a custom avatar were not confirmed in the pages fetched.

## 2. Installation tokens: opening issues or PRs on uninstalled public repos; fork-and-PR flow; rate limits; spam risk

### Takeaway
The design doc's central premise conflicts with primary docs. The installation-auth docs say a token "cannot be granted access to repositories that the installation was not granted access to." The only support for opening issues on an uninstalled public repo is a single 2025 community Q&A answer by a user labelled "Maintainer" (not confirmed as GitHub staff). The fork endpoint docs also say the App must be installed on the source account with access to the source repository, which contradicts the design doc's "no installation on the upstream repo required." Treat the whole fork-and-PR flow as unverified until tested.

### Cited Findings
- Installation tokens cover "all repositories that the installation was granted access to" when not narrowed, and "cannot be granted access to repositories that the installation was not granted access to." The page does not say whether public repos outside the grant are reachable. — [Authenticating as a GitHub App installation](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation) (summarizer)
- Installation tokens expire after 1 hour. — same page
- Community claim: on April 28, 2025, a user labelled "Maintainer" (samus-aran) answered that "any actor, including a GitHub App using an installation access token, can open an issue in a public repository," unless Issues is disabled. The answer was marked accepted. No one in the thread reported a 403 or 404. The poster is not identified as GitHub staff. — [community discussion #157656](https://github.com/orgs/community/discussions/157656)
- Fork endpoint `POST /repos/{owner}/{repo}/forks`: "the GitHub App must be installed on the destination account with access to all repositories and on the source account with access to the source repository." Forks can go into an org via the `organization` parameter. Creation is asynchronous and returns 202. — [Forks REST API](https://docs.github.com/en/rest/repos/forks)
- Installation-token rate limit: 5,000 requests per hour baseline. GitHub Enterprise Cloud orgs get 15,000. Other installs scale up by 50 requests/hour for each repo beyond 20 and each org user beyond 20, capped at 12,500/hour. — [Rate limits for the REST API](https://docs.github.com/en/rest/overview/rate-limits-for-the-rest-api) (summarizer)
- Secondary limits: up to 100 concurrent requests; 900 points/min for REST; 2,000 points/min for GraphQL; 90 s of CPU time per 60 s real time. Content-creating requests: "no more than 80 content-generating requests per minute" and 500 per hour, with some endpoints lower. Most POST/PATCH/DELETE cost 5 points. Limits "can change without notice." — same page
- Acceptable Use Policy (Section 4, Spam and Inauthentic Activity) prohibits "automated excessive bulk activity," "inauthentic interactions, such as fake accounts and automated inauthentic activity," and "using our servers for any form of excessive automated bulk activity." It prohibits "monetized or excessive bulk content in issues." — [GitHub Acceptable Use Policies](https://docs.github.com/en/site-policy/acceptable-use-policies/github-acceptable-use-policies) (summarizer)

### Inferences
- Issue creation is the piece most likely to work on a public repo, because issue writes are open to any authenticated actor. Opening a PR from an org fork is the piece most likely to fail, since the fork docs require the App to be installed on the source account. Test both on a throwaway public repo before designing around either.
- Content-creation secondary limits (80/min, 500/hour) are the binding constraint for an agent community, not the 5,000/hour primary limit. A busy GC or a burst of PR reviews could hit them.
- A bot posting bug reports or PRs across many unrelated repos fits the AUP's "excessive automated bulk activity" pattern, even if each action is individually legitimate. Keep volume low and per-repo relevant.

### Gaps
- Whether an installation token can create an issue on a repo outside its installation grant has not been tested. Primary docs and the community claim conflict.
- Whether a PR from `org-fork:branch` to an upstream repo works with an App token is UNVERIFIED. The App would need access to the upstream repo, which the design doc says is not installed.
- Specific GitHub App permission names needed for fork creation were not confirmed (the forks page does not name one).

## 3. Webhook delivery on a Windows NAT host, and signature verification

### Takeaway
Signature verification is clearly documented: HMAC-SHA256 in `X-Hub-Signature-256`, compared in constant time. GitHub can only push webhooks to a publicly reachable HTTPS endpoint, so NAT requires a tunnel or polling. The docs fetched did not cover retries, timeouts, replay protection, or delivery IPs, so those remain gaps.

### Cited Findings
- Signature header `X-Hub-Signature-256` uses HMAC-SHA256 with the value prefixed `sha256=`. The legacy `X-Hub-Signature` (HMAC-SHA1) is "only included for legacy purposes." Use a constant-time comparison; "Never use a plain == operator." — [Validating webhook deliveries](https://docs.github.com/en/webhooks/using-webhooks/validating-webhook-deliveries)
- The validation page does not cover replay protection, delivery retries, redelivery, or source IP ranges. — same page

### Inferences
- Simplest robust option for a NUC behind NAT: a Cloudflare Tunnel (or similar outbound-only tunnel) exposing one HTTPS route, with a strict signature check and a dedupe on the `X-GitHub-Delivery` ID. This avoids opening inbound ports. (Engineering judgement, not sourced.)
- Polling the REST Events or notifications API is a fallback. It costs rate-limit budget and adds latency. It also avoids the public endpoint. (Engineering judgement, not sourced.)
- smee.io-style relays are fine for development. They are a poor production choice because they add a third party in the path. (Engineering judgement, not sourced.)
- Because the tunnel exposes a public endpoint, requests must be verified before any processing, and the handler should only accept POST to one path.

### Gaps
- Delivery timeout, retry policy, and redelivery behaviour were not verified. Check the "Best practices for webhooks" page before designing the queue. (I recall a short timeout, but this is UNVERIFIED.)
- GitHub webhook source IP ranges (via the meta API `hooks` key) were not fetched.
- Whether a Cloudflare Tunnel is acceptable per the operator's policy was not checked.

## 4. LibGit2Sharp: maintenance, version, feature gaps, verdict

### Takeaway
LibGit2Sharp looks actively maintained (latest release v0.32.0, bundling libgit2 v1.8.6, described as a security release). The main concern for Bazaar is feature coverage for worktrees, partial clones, submodules, and LFS, which I could not verify in this session. The verdict below rests on unverified feature gaps and should be treated as provisional.

### Cited Findings
- Latest release v0.32.0, dated "23 Jul" (the page omits the year; presumed 2026, UNVERIFIED), bundling libgit2 v1.8.6, marked "Latest" and described as a security release. Earlier: v0.31.0 (3 Dec, libgit2 v1.8.4, adds SSH via libgit2's OpenSSH support), v0.30.0 (26 Nov, libgit2 v1.7.2, .NET 8 updates), v0.29.0 (22 Jan, libgit2 v1.7.1, proxy options). — [LibGit2Sharp releases](https://github.com/libgit2/libgit2sharp/releases) (summarizer; years not shown on the page)
- The repo page shows open issues, pull requests, and recent activity, but the summarizer did not confirm the maintenance status. — same page

### Inferences
- Version and binding quality look adequate for clone, fetch, branch, commit, and diff. That is the core of Bazaar's use case.
- For many concurrent clones on one server, LibGit2Sharp runs libgit2 in-process. Memory and handle lifecycle must be managed carefully (use `using`/dispose on Repository objects). Network auth and proxy options exist. Fallback to `git` CLI for edge cases is reasonable.
- Verdict (provisional): keep LibGit2Sharp for in-process read/diff/commit. Use the `git` CLI (run as a subprocess with a per-repo lock) for worktrees, partial/shallow clones, LFS, and submodules, unless those are verified to work in the pinned libgit2 version.

### Gaps
- Feature coverage of libgit2 v1.8.x for worktrees, shallow clones, partial clones (`--filter`), submodules, and LFS was NOT verified. Do not rely on any of these without a test.
- Alternatives (Octokit.net for the GitHub API, a shell-out wrapper, other .NET git libraries) were not evaluated in this session.

## 5. Other platforms: rules on AI agents posting

### Takeaway
Reddit has the most restrictive trajectory, with reported API access changes in 2026. Stack Overflow's generative-AI policy was not verified (fetch blocked). Godot's contribution rules were reported as tightened in June 2026, but this came from secondary sources. Roblox DevForum and the Godot forum were not researched.

### Cited Findings
- Reddit (secondary sources only): the Responsible Builder Policy reportedly requires approval before API data access, and reportedly was updated June 5, 2026. The primary page returned HTTP 403. — [Responsible Builder Policy](https://support.reddithelp.com/hc/en-us/articles/42728983564564-Reddit-Responsible-Builder-Policy) (403, not retrieved); secondary: [Reddit Data API Terms & Commercial Use (2026)](https://prowlo.com/blog/reddit-data-api)
- Reddit Data API Wiki (reportedly updated May 11, 2026, per secondary search summary): Reddit "can and will freely throttle or block unidentified Data API users"; bots must use a descriptive User-Agent with a username as contact. — [Reddit Data API Wiki](https://support.reddithelp.com/hc/en-us/articles/16160319875092-Reddit-Data-API-Wiki) (not fetched; from search snippet and a secondary summary)
- TechCrunch (2026-09-30) reports that Reddit is ending public API access, citing AI bots, with the end reported as March 2027. Not verified against Reddit's own announcement. — [TechCrunch](https://techcrunch.com/2026/09/30/reddit-is-killing-rss-feeds-ending-public-api-access-because-of-ai-bots/) (search snippet only)
- Reddit reportedly does not require a site-wide AI label. Subreddit-specific disclosure rules apply. Reddit condemned the 2025 r/changemyview undisclosed-AI experiment as "unethical." — secondary sources only (search results), UNVERIFIED
- Stack Overflow: the policy page could not be fetched (tool blocked). Not verified in this session. My recollection is that Stack Overflow has long banned posting generative-AI output as answers, but this is UNVERIFIED and must be checked before any plan involving SO.
- Godot: a June 2026 update reportedly bans autonomous AI agents and vibe-coded submissions, with accountability for human contributors as the reason. Secondary source only. — [Open Source Projects Are Banning AI Code: What It Means for Developers](https://zenvanriel.com/ai-engineer-blog/open-source-ai-ban-contributor-poker-guide/) (search snippet, secondary aggregator) — UNVERIFIED; the Godot contributing policy itself was not fetched.

### Inferences
- The clearest signal across platforms is a shift toward requiring a named human accountable for every post or PR. Autonomous, unsupervised agent posting is the category most likely to be banned.
- Reddit's public-API trajectory, if confirmed, would remove the plan's Reddit channel entirely, or move it to a paid commercial deal.

### Gaps
- Stack Overflow policy: not verified.
- Roblox DevForum rules: not researched.
- Godot forum (forum.godotengine.org) rules: not researched. Only the contribution policy was reported on, secondhand.
- GitHub's own rules on AI-generated PRs and issues: no GitHub-specific AI-content policy was found in the AUP. The AUP covers spam and bulk activity only.

## 6. Open-source maintainer trends in 2026 on AI-generated contributions

### Takeaway
Maintainers are increasingly banning or restricting AI-generated contributions, and the trend is toward accountability requirements. Some projects have banned outright (Oracle/OpenJDK, QEMU, NetBSD); others allow with disclosure (Linux, Ghostty, Debian). The reputational risk for "agents posting bug reports and PRs" is real and rising.

### Cited Findings
- Oracle reportedly set an interim ban on AI-generated code, text, images, and contributions to OpenJDK, reaching Git repos, PRs, emails, wikis and issues (August 2026 secondary report). — [Oracle Bans AI-Generated Contributions To OpenJDK](https://www.opensourceforu.com/2026/08/oracle-bans-ai-generated-contributions-to-openjdk/) (secondary; UNVERIFIED against Oracle primary)
- QEMU says it will "DECLINE any contributions which are believed to include or derive from AI generated content." NetBSD treats LLM-generated code as "presumed to be tainted code." — reported in [AI Slop Is Flooding Open Source](https://openresource.dev/articles/ai-slop-contribution-policy) (secondary; UNVERIFIED against project primaries)
- Ghostty tightened policy in January 2026 but requires disclosure of all AI use. Linux allows AI-assisted work with disclosure. Debian voted to allow AI contributions with human responsibility. llama.cpp permits AI use "solely in an assistive capacity." — secondary summary, same sources; UNVERIFIED
- An arXiv study (2609.07542) of popular OSS AI policies reports the most common countermeasures to AI contributions as aggressively closing PRs (48.4%), banning or blocking users (20.8%), and disallowing fully autonomous agents (13.8%). — [arXiv 2609.07542](https://arxiv.org/html/2609.07542) (abstract-level summary via search; not read in full)

### Inferences
- An agent that opens issues and PRs across repos under a bot name is the exact pattern maintainers are blocking. The likely outcome is blocked users and closed PRs, not community trust. The "disclosed bot" approach helps with honesty but does not solve the maintainer-trust problem, since maintainers target volume and autonomy.
- Safer pattern: agents act only on repos where a human owner has asked for help or agreed, and every post is signed by a named human sponsor. The "bot posts bug reports everywhere" plan is high-risk.

### Gaps
- The arXiv study was not read in full. Its counts are from a search summary.
- No primary policy documents from Oracle, QEMU, NetBSD, Ghostty, or Godot were fetched.

## Summary of plan-breaking risks (ranked)

1. **Bot-created accounts are barred by ToS.** Agents cannot self-register on GitHub or other services. Fix: the human sponsor creates accounts. (Source: ToS Section B.)
2. **Installation-token scope may not reach uninstalled upstream repos.** This conflicts with the design doc's fork-and-PR assumption. The fork endpoint requires the App on the source account with access to the source repo. Test before building on it. (Sources: installation-auth page; forks page.)
3. **Content-creation secondary limits (80/min, 500/hour)** are the real throughput ceiling for posting. (Source: rate-limit page.)
4. **Reddit public API reportedly ending by March 2027** (secondary; verify), which may remove the Reddit channel.
5. **Maintainer backlash and bans on autonomous agents** in 2026 (secondary sources). The community plan needs human-accountable posting and volume caps.
6. **LibGit2Sharp feature gaps** (worktrees, partial clones, submodules, LFS) are unverified; plan a CLI fallback.

## Recommended tests before design lock-in
- Create a throwaway public repo the App is NOT installed on. Mint an installation token. Try `POST /repos/{owner}/{repo}/issues`. Record the status code.
- Fork a public repo into the org using an installation token. Record the status and any permission error. Then try opening a PR from that fork.
- Confirm with GitHub Support whether App count per org is limited.
- Read the ToS in full (raw page) to confirm the section letters and wording quoted above.
