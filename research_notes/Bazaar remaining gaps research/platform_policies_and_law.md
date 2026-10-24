# Platform Policies and Law for AI Agent Participation (status as of 2026-10-24)

Not legal advice. Items marked **needs counsel** require a lawyer before Bazaar acts on them. Labels used throughout:
- **verified (primary)**: the primary page was fetched in this session (WebFetch returns a model-written summary, so quoted wording should be re-checked against the raw page before publication).
- **secondary**: a search snippet, news article, aggregator, or third-party blog.
- **unverified**: from memory, or not confirmed in this session.

Research budget: about 18 tool calls. Blocked fetches (403, tool refusal, or redirect loops) are listed under Gaps. Do not treat any "allowed" entry as permission; treat it as "no ban found in the text I read."

---

# Platform and Law Matrix

## Key Question 1: Platform policies for AI agent participation (allowed with disclosure, banned, or unclear)

### Takeaway
Only one primary policy in this set speaks directly to AI-authored posts and bots: Hacker News bans AI-generated posts and automated posting, and the Linux kernel allows AI-assisted contributions only with an `Assisted-by` tag and a named human taking responsibility. Godot's CONTRIBUTING.md has no AI section, which conflicts with a secondary report of a June 2026 Godot ban. Stack Overflow, Reddit, Discord, and Roblox could not be checked against current primary text. Treat every platform not marked "verified (primary)" as blocked until read.

### Cited Findings

| Platform | Status for autonomous agent posting | Evidence | Date | Label |
|---|---|---|---|---|
| Hacker News | **Banned** for generated or AI-edited text, and for automated posting | "Don't post generated text or AI-edited text. HN is for conversation between humans." and "please don't automate posting." [HN Guidelines](https://news.ycombinator.com/newsguidelines.html) | Page undated | verified (primary, via summarizer) |
| Linux kernel | **Allowed with disclosure** and a human owner. AI agents must not add `Signed-off-by`; the human submitter reviews, certifies DCO, and takes full responsibility. The assistant must not submit anything itself. Tag format `Assisted-by: LLM [TOOL1]...` | [docs.kernel.org, Coding Assistants](https://docs.kernel.org/process/coding-assistants.html) | Doc version 7.3.0; no date shown | verified (primary, via summarizer) |
| Godot Engine | **Unclear.** The fetched CONTRIBUTING.md has no AI section. A secondary report says a June 2026 update bans autonomous AI agents and vibe-coded submissions. That report is not confirmed and may refer to a different document (e.g., a contributor policy page or forum rule). | [godotengine/godot CONTRIBUTING.md](https://github.com/godotengine/godot/blob/master/CONTRIBUTING.md) (verified, no AI text); [zenvanriel.com aggregator](https://zenvanriel.com/ai-engineer-blog/open-source-ai-ban-contributor-poker-guide/) (secondary, carried over from the GitHub note) | Fetched 2026-10-24 | verified (absence in CONTRIBUTING.md); ban claim: secondary/unverified. **Conflict** between the two. |
| Stack Overflow | **Historically banned** for posting generative-AI output as questions or answers. The current 2026 status is **unverified**. A May 2024 secondary report says the policy was reversed. | Policy URL `https://stackoverflow.com/help/gen-ai-policy` returned a tool refusal ("unable to fetch"). A search returned only 2022-2024 items (Meta post, secondary reports). | Meta post ~Dec 2022; 2024 report | **unverified (current)**; historic ban: secondary |
| Reddit | **Public API ending.** TechCrunch reports Reddit's announcement that "Public API access will also end by March 2027," citing scraping and automated traffic. The primary Reddit announcement was not read. Reddit's Responsible Builder Policy and Data API Terms could not be read (403 earlier; redditinc.com refused). Bot labelling and disclosure on Reddit are unverified. | [TechCrunch, 2026-09-30](https://techcrunch.com/2026/09/30/reddit-is-killing-rss-feeds-ending-public-api-access-because-of-ai-bots/) (secondary; the article is dated and attributes the statement to Reddit's announcement, not a named spokesperson) | 2026-09-30 article; end date March 2027 | End date: secondary. Bot rules: unverified. |
| Discord | **Unverified.** The Developer Policy redirect chain ended in a 403 (`support-dev.discord.com`). Self-bot prohibition and bot rules: **unverified** (from memory only). | [Discord Developer Policy](https://discord.com/developers/docs/policies-and-agreements/developer-policy) (redirects to docs.discord.com then support-dev.discord.com; 403 on final page) | n/a | unverified |
| Roblox DevForum | **Unclear.** No official DevForum rule on AI-written posts was found. Community threads request reporting tools for "mass AI replies" and an "AI spam" report reason; these are user requests, not rules. Roblox generative-AI docs say that when using third-party tools, the creator is responsible for content delivered to users. Community reports say the Studio AI assistant triggered account bans (false positives, anecdotal). | [DevForum: report tools for mass AI replies](https://devforum.roblox.com/t/better-tools-to-report-disruptive-mass-ai-replies-in-the-dev-forum/3904393) (user request, secondary); [Roblox generative AI docs](https://create.roblox.com/docs/en-us/generative-AI.md) (primary, not fully read); [DevForum false-positive ban thread](https://devforum.roblox.com/t/warning-using-roblox-studio-ai-assistant-can-get-your-account-banned-false-positives/4756750) (anecdotal) | 2025-2026 threads | Rules: unverified. Studio ban anecdote: unverified. |
| GitHub | **Allowed for disclosed App identities, with conditions; bot-created user accounts banned.** ToS bars accounts "registered by 'bots' or other automated methods"; machine accounts must be set up by a human. AUP bans "automated excessive bulk activity" and "automated inauthentic activity." No GitHub-specific AI-content rule found. | [GitHub ToS](https://docs.github.com/en/site-policy/github-terms/github-terms-of-service), [GitHub AUP](https://docs.github.com/en/site-policy/acceptable-use-policies/github-acceptable-use-policies) (both per the earlier GitHub note, summarizer-read) | ToS effective 2026-04-27 (per the earlier note) | secondary/verified-summary; carry over from `github_community_presence.md` |
| Google (YouTube, Gmail, Google accounts) | **Banned** for automated access to content, bypassing protective measures, and "creating fake accounts or content" as deceptive use. No explicit clause on automated sign-up text was found. | [Google Terms](https://policies.google.com/terms) (effective 2026-07-30, per the earlier email note) | 2026-07-30 | verified (primary, via earlier fetch) |
| X/Twitter, Bluesky, Mastodon, YouTube (policy-specific) | **Not researched.** | n/a | n/a | gap |

### Inferences
- The clearest 2026 signal is "named human accountable for every post." Linux and the HN rule both support this. Autonomous posting is the category most likely to be banned.
- A Godot-style ban, if real, would apply to PRs, not forum posts. Keep a Godot forum policy as a separate check.
- Bazaar's plan to post disclosed agent identities is consistent with the Linux model (human owner, disclosure tag, human submits) but inconsistent with the HN model (no generated text, no automated posting). Forums with the HN rule must be excluded from autonomous posting.
- Reddit's API end-date, if confirmed, removes the Reddit channel for API bots and leaves only manual or human-operated accounts. Needs primary confirmation before planning.

### Gaps
- Stack Overflow current policy (blocked). Re-check on a second route (Meta Stack Exchange or Help Center).
- Reddit Responsible Builder Policy, Data API Terms, bot label rules, and the primary end-of-API announcement (403/refused).
- Discord Developer Policy text (redirect ended in 403).
- Roblox DevForum guidelines and Community Standards text (search only; no fetch).
- Godot forum (forum.godotengine.org) rules; whether a June 2026 Godot AI ban exists in any primary Godot doc.
- X/Twitter, Bluesky, Mastodon instance rules, YouTube bot/AI disclosure rules.
- arXiv 2609.07542 (policy study, cited in the earlier GitHub note) was not read.

---

## Key Question 2: Laws on bot disclosure, automated impersonation, and automated outbound email

### Takeaway
The EU AI Act Article 50 duty to tell people they are talking to an AI applies from 2 August 2026 (verified on artificialintelligenceact.eu; one secondary source reports a December 2026 grace period, unconfirmed). California's B.O.T. Act (BPC 17940-17943) requires a clear disclosure of bot identity only when the bot is used to deceive for commercial sales or election influence, and its scope excludes large platforms. The Philippine Data Privacy Act reaches processing by entities linked to the Philippines, including outside the country, and carries heavy criminal penalties. CAN-SPAM covers commercial email only, with $53,088 per-email penalties. GDPR and PECR were not verified.

### Cited Findings

**EU AI Act Article 50**
- Art. 50(1): providers must design AI systems so people "are informed that they are interacting with an AI system," unless obvious to a reasonably well-informed person. [artificialintelligenceact.eu, Article 50](https://artificialintelligenceact.eu/article/50/) (verified (primary-text mirror, via summarizer); not the EUR-Lex original)
- Art. 50(2): providers of generative systems must mark synthetic output in machine-readable form. Same source.
- Art. 50(4): deployers publishing AI-generated **text on matters of public interest** must disclose it, unless it has human review or editorial control with a responsible person. Same source. This is directly relevant if Bazaar agents publish public text.
- Art. 50(5): disclosure "at the latest at the time of the first interaction or exposure." Same source.
- Application: the page states Article 50 "Comes into force 2 August 2026, according to Article 113." [artificialintelligenceact.eu](https://artificialintelligenceact.eu/article/50/) (verified (mirror)).
- Digital Omnibus: high-risk Annex III obligations were deferred to 2 December 2027 and embedded-product systems to 2 August 2028 (secondary). Most secondary sources say Article 50 was not deferred. One Parliament-focused source reports an extension to 2 December 2026 for systems already on the market, "unconfirmed" by the author. Council approval reportedly 29 June 2026 (secondary). [Pasquale Pillitteri, EU Parliament delays](https://pasqualepillitteri.it/en/news/5190/digital-omnibus-ai-act-eu-parliament); [CMS, Digital Omnibus key political agreement](https://cms.law/en/cze/legal-updates/eu-ai-act-developments-key-political-agreement-on-the-digital-omnibus-on-ai-implementation-timeline-and-transparency-consultation) (secondary; the omnibus status and Art. 50 timing conflict between sources; primary Official Journal text not read).
- Penalties for Art. 50 breaches: up to EUR 15 million or 3% of global turnover, whichever is higher (secondary, search summary; not verified against the Regulation text).

**California B.O.T. Act (SB 1001, 2018; BPC 17940-17943)**
- Bot definition (§17940, secondary): "an automated online account where all or substantially all of the actions or posts of that account are not the result of a person." [California Business and Professions Code 17941 summary, california.public.law](https://california.public.law/codes/ca_bus_and_prof_code_section_17941) (secondary; leginfo.legislature.ca.gov returned 403).
- §17941(a) (secondary): unlawful to use a bot to deceive a person about its artificial identity "in order to incentivize a purchase or sale of goods or services in a commercial transaction or to influence a vote in an election." A person who discloses the bot identity is not liable under that section. Same source.
- §17941(b) (secondary): disclosure must be "clear, conspicuous, and reasonably designed to inform persons with whom the bot communicates or interacts that the bot is a bot." Same source.
- Scope (secondary, commentary): applies to "online" public-facing communications; excludes service providers of large platforms with more than 10 million average monthly US visitors. [Perkins Coie commentary](https://perkinscoie.com/insights/blog/i-am-robot-californias-new-law-requires-disclosure-use-bots) (secondary; not read in full).
- AB 410 (2025) would add generative-AI definitions and a broader duty not to mislead about bot identity. LegiScan shows it "held under submission" as of 2025-08-29 (secondary). Not confirmed enacted; the 2019 text is presumed operative. [LegiScan AB410](https://legiscan.com/CA/text/AB410/2025) (secondary).
- Other US state AI-disclosure laws (2025-2026): **not researched**.

**Philippines**
- RA 10173, Data Privacy Act of 2012, Section 4 (scope): covers processing of all personal information by any natural or juridical person; includes controllers not established in the Philippines that use equipment located there. [lawphil.net RA 10173](https://lawphil.net/statutes/repacts/ra2012/ra_10173_2012.html) (verified (primary, via summarizer)).
- Section 6 (extraterritorial application): applies to acts inside or outside the Philippines that relate to personal information about a Philippine citizen or resident, where the entity has a Philippine link (contract in PH, central management in PH, PH branch, carrying on business in PH, or collecting through a PH entity). Same source.
- Penalties (Sections 25-32, verified via same source): e.g., unauthorized processing of personal information: 1-3 years and PHP 500,000-2,000,000; unauthorized processing of sensitive personal information: 3-6 years and PHP 500,000-4,000,000; unauthorized disclosure (personal): 1-3 years and PHP 500,000-1,000,000; malicious disclosure: 1.5-5 years and PHP 500,000-1,000,000. Same source.
- Note: the lawphil text's Section 4 proviso cross-refers to Section 5, which concerns journalists; the summary flags it as a likely cross-reference error. Check the official text.
- Cybercrime Prevention Act (RA 10175), any anti-fake-news/AI-disinformation bills, and PH rules on automated posting: **not researched**. Needs counsel.
- NPC (National Privacy Commission) guidance on AI or automated processing: **not researched**.

**CAN-SPAM (US, commercial email)**
- Each separate email in violation is subject to penalties of up to $53,088 (inflation-adjusted, January 2024 edit). Requires accurate headers, a valid physical postal address, a clear opt-out, and honouring opt-outs within 10 business days. Transactional or relationship messages are "largely exempt," but a commercial-sounding subject line or lead content makes the message commercial. [FTC CAN-SPAM Compliance Guide](https://www.ftc.gov/business-guidance/resources/can-spam-act-compliance-guide-business) (verified (primary, via summarizer); page dated August 2023, edited January 2024).

**Gmail/Google bulk-sender rules** (from the earlier email note, verified primary): above 5,000 messages/day to Gmail, SPF and DKIM with DMARC alignment, PTR record, TLS, spam rate under 0.30% (guidance says under 0.10%), one-click unsubscribe. [Google Workspace bulk sender guidelines](https://support.google.com/a/answer/81126) (verified (primary), fetched 2026-10-24, from the email note).

**GDPR and PECR (UK)**: **not verified in this session.** From memory (unverified): GDPR requires a lawful basis and transparency for processing personal data of EU/UK individuals, including email addresses of strangers; PECR restricts unsolicited marketing email to individuals without consent (B2B "soft opt-in" differs). Needs counsel and primary check on ICO and EUR-Lex pages.

### Inferences
- A disclosed agent that sends a bug report or PR comment is probably not a "commercial" message under CAN-SPAM, but any outreach that promotes a product or service (e.g., a Bazaar sales pitch or a paid service) would be. Inference; needs counsel.
- Under EU Article 50(1), a Bazaar agent that interacts with EU natural persons must disclose AI status at first contact. The disclosure label in profiles and in the first message satisfies this on the operator side. Whether the Bazaar platform (provider) or the operator (deployer) carries the duty depends on roles. Needs counsel.
- Under California BPC 17941, a clearly labelled bot is not liable under that section. The safe harbour requires actual disclosure, so label every agent account and every outbound message.
- Under PH Section 6, a PH-based operator that processes a PH resident's email address is in scope regardless of where the server runs. Other operators in other countries face their own laws. The platform should not assume one jurisdiction.
- Penalty exposure for personal-data mistakes (e.g., storing the email addresses of strangers and sending unsolicited email) is largest under PH law (criminal). Needs counsel.

### Gaps
- Official EUR-Lex text of the AI Act Article 50 and the Digital Omnibus amending act (only a mirror and secondary summaries were read). The December 2026 grace-period claim is unconfirmed.
- Leginfo.legislature.ca.gov BPC 17941 text (403); the 2019 operative text is from a secondary site.
- Other US state AI-disclosure and chatbot laws (2025-2026): not researched.
- PH Cybercrime Act (RA 10175), NPC guidance, and PH AI or disinformation bills: not researched.
- GDPR, PECR, and ICO guidance on cold B2C and B2B email: not verified.
- Other countries' bot-disclosure laws (e.g., Utah, China's generative AI labelling rules, South Korea AI Basic Act): not researched. Unverified from memory that several exist; check before relying on them.

---

## Key Question 3: Open-source maintainer rules on AI contributions (2026) and what disclosed bot contributions look like when accepted

### Takeaway
Maintainers split into three groups: (a) allow with disclosure and a human owner (Linux kernel, verified; Debian and Ghostty, secondary); (b) decline or ban AI-generated code (QEMU, NetBSD, Oracle/OpenJDK, all secondary and unverified against primaries); (c) ban autonomous agents (Godot, secondary, contradicted by the primary CONTRIBUTING.md text). An accepted "disclosed bot" contribution, in the one primary example read, is a patch with an `Assisted-by` tag, a human `Signed-off-by` added by the human submitter, and a human who takes responsibility.

### Cited Findings
- Linux kernel: AI tools may be used. Commit trailer `Assisted-by: LLM [TOOL1] [TOOL2]`. AI agents must not add `Signed-off-by`. The human submitter must review, check licensing, add their own DCO sign-off, and "take full responsibility." Bug fixes must be built and verified; the assistant must not send anything itself. [docs.kernel.org](https://docs.kernel.org/process/coding-assistants.html) (verified (primary, via summarizer)).
- QEMU: says it will "DECLINE any contributions which are believed to include or derive from AI generated content." NetBSD: LLM-generated code "presumed to be tainted code." Source: [openresource.dev](https://openresource.dev/articles/ai-slop-contribution-policy) (secondary; from the earlier GitHub note; project primaries not fetched).
- Oracle/OpenJDK: reported interim ban on AI-generated contributions (August 2026). [OpenSourceForU](https://www.opensourceforu.com/2026/08/oracle-bans-ai-generated-contributions-to-openjdk/) (secondary; unverified against Oracle).
- Ghostty (Jan 2026, requires disclosure of all AI use), Debian (allows AI contributions with human responsibility), llama.cpp (AI only "in an assistive capacity"): secondary, from the earlier note; unverified.
- Godot: primary CONTRIBUTING.md has no AI rule (verified, see Q1). A reported June 2026 ban is unconfirmed.
- arXiv 2609.07542: most common countermeasures to AI contributions were aggressively closing PRs (48.4%), banning or blocking users (20.8%), and disallowing fully autonomous agents (13.8%). Secondary (search-summary; not read in full, from the earlier note).

### Inferences
- A disclosed bot contribution that is accepted looks like: an `Assisted-by` (or equivalent) trailer; a named human sponsor who reviewed and certified; a human-signed DCO; and a PR body that says what the agent did and how it was tested. Bazaar should adopt this format for any code contribution.
- Bot identity (`[bot]` GitHub App) is not enough to be accepted by the maintainers in group (b) or (c), and volume is what triggers blocks. Keep posts few, relevant, and tied to a repo where a human maintainer asked for help.
- For issues and bug reports, the same accountability rule applies. An unsigned bot report is the pattern maintainers are closing.

### Gaps
- Primary policies of QEMU, NetBSD, Oracle, Ghostty, Debian, llama.cpp, and Godot's contributing docs or forum rules were not fetched.
- The arXiv study was not read in full.
- Stack Overflow, Reddit, and Discord forum rules (see Q1).

---

## Key Question 4: Practical guardrails (opt-in lists, approval tiers, rate limits, bot labels, contact info)

### Takeaway
The verified rules support a conservative default: label every account as a bot, give a human contact, approve public posts by tier, limit volume per platform, and never post on a platform whose primary rule bans generated text or automation (currently HN). Specific rate limits per platform need the primary pages (GitHub limits are verified only from a summarizer, from the earlier note).

### Cited Findings
- GitHub content-creating secondary limits: about 80 content-generating requests per minute and 500 per hour (secondary, summarizer of the rate-limit page; from the earlier note). [GitHub REST rate limits](https://docs.github.com/en/rest/overview/rate-limits-for-the-rest-api).
- GitHub AUP forbids "automated excessive bulk activity" and "automated inauthentic activity" (secondary, summarizer; earlier note).
- Reddit: the Data API Wiki reportedly says Reddit "can and will freely throttle or block unidentified Data API users" and requires a descriptive User-Agent with a contact username. Secondary/unverified (earlier note; not fetched).
- CAN-SPAM: honour opt-outs within 10 business days; opt-out must work for 30 days after sending (verified (primary, via summarizer)).
- California BPC 17941(b): disclosure must be "clear, conspicuous, and reasonably designed" (secondary).
- EU AI Act Art. 50(5): disclosure at first interaction (verified (mirror)).

### Inferences (proposed guardrails for Bazaar, for owner approval)
1. **Label**: every agent account and profile carries "AI agent, operated by [named human sponsor], contact [address]." Required for EU Art. 50 and California 17941 comfort, and matches the Linux "named human" model.
2. **Opt-in lists**: agents post publicly only on repos, forums, or threads where a maintainer or moderator has asked for help or agreed. Keep a per-platform allowlist (human-approved).
3. **Approval tiers**: (a) read-only and internal GC: autonomous; (b) replies in approved threads: autonomous with after-the-fact notice; (c) any new public post, issue, PR, or outbound email to a stranger: human approves first; (d) CAPTCHA, phone, ID, and legal-term acceptance: human only.
4. **Platform exclusions**: no autonomous posting on Hacker News (rule verified). Do not post to Reddit under the API route unless the primary policy is read and the API still exists. Do not post on Stack Overflow until its current policy is read.
5. **Rate limits**: per-platform caps set below the lowest secondary limit (GitHub content: 80/min, 500/hour); per-repo caps; no bulk filing across unrelated repos.
6. **Outbound email**: no unsolicited commercial email; opt-out handling if any marketing; physical address if commercial (CAN-SPAM); PH DPA review before storing stranger addresses.
7. **Scam and abuse emails**: quarantine; no autonomous replies (see the email note).
8. **Kill switch**: any maintainer or moderator who objects is removed from the outreach list, and the agent stops posting there.

### Gaps
- Per-platform rate limits for Reddit, Discord, Stack Overflow, Roblox, Godot: not verified.
- Whether a platform accepts bot-labelled accounts (Discord bot account vs user account; Reddit "bot" flair; Roblox): not verified.
- Counsel review needed on the approval-tier and opt-out design, on EU Art. 50 role allocation (provider vs deployer), and on PH DPA duties.

---

## Overall gaps and what needs counsel

**Needs counsel (not legal advice):**
- EU AI Act Article 50 role (provider vs deployer), timing (2 Aug 2026 vs possible December 2026 grace period), and public-interest text disclosure (Art. 50(4)).
- California BPC 17940-17943 scope (commercial and election purposes only), AB 410 status, and whether Bazaar's conduct is "commercial."
- Philippine DPA (RA 10173) duties and criminal exposure for processing stranger contact data; RA 10175 cybercrime exposure from automated posting.
- CAN-SPAM and GDPR/PECR for outbound email to strangers.
- Platform ToS on automated accounts (GitHub, Reddit, Discord, Stack Overflow, Roblox) before any live account is opened.

**Top verification tasks before live use:**
1. Read Stack Overflow Help Center gen-AI policy (blocked here).
2. Read Reddit Responsible Builder Policy and the primary API-end announcement (blocked here).
3. Read Discord Developer Policy final page (403 here).
4. Read Roblox DevForum guidelines and Godot forum rules (not fetched).
5. Read EUR-Lex AI Act Article 50 and the Official Journal omnibus text (mirror only).
6. Read leginfo BPC 17941 (403 here).
7. Read PH RA 10175 and NPC guidance.

**Sources blocked or not read:** stackoverflow.com/help/gen-ai-policy (tool refusal); redditinc.com/policies/data-api-terms (tool refusal); support.reddithelp.com Responsible Builder Policy (403, earlier); support-dev.discord.com Developer Policy (403); leginfo.legislature.ca.gov BPC 17941 (403); privacy.gov.ph RA 10173 page (403; lawphil text used instead).
