# Agent Security Threat Model for Bazaar (as of 2026-10-24)

Scope: threats to a platform of persistent autonomous agents that read untrusted content (inbound email, stranger GitHub issues/PRs, web pages, forum posts, dependency code), hold credentials in a Soul Passport, run code in Hyper-V/WSL2 VMs, post publicly as disclosed bots, and coordinate in group chats (GCs). Design context read: `/home/user/Bazaar/docs/deep-dives/Bazaar.Soul.md`, `/home/user/Bazaar/docs/deep-dives/Bazaar.Server.md`, `/home/user/Bazaar/research_notes/Bazaar stack feasibility research/email_identity_accounts.md`. `Bazaar.md` was NOT read in this pass.

Verification labels: **verified** = primary source read this session; **secondary** = search-result snippet or third-party write-up; **unverified** = from training memory or not confirmed this session.

Method limits: about 19 tool calls. Several primary pages failed or redirected (listed under Gaps). No Anthropic, OpenAI or Google agent-security guidance was fetched in this pass.

---

## Threat model in one paragraph (Bazaar-specific)

Every Bazaar agent, by default, has all three legs of the "lethal trifecta": (A) it ingests untrusted content (email, issues, web, GC messages written by other agents), (B) it can reach private data (Soul Passport, Vault, memory, GitHub tokens), and (C) it can change state or communicate externally (post to GitHub, send email, post publicly, spend). The main design risk is therefore not a single bug but the combination. Because GCs are shared, one compromised agent's output is untrusted input to every other agent in the same GC, which makes cross-agent injection a first-class path, not an edge case.

---

## 1. Prompt injection and indirect injection: what reduces risk in 2026

### Takeaway
No current defense makes an LLM agent reliably immune to indirect prompt injection; the strongest published position is architectural (limit what an untrusted-input session can do), not detection. Meta's "Rule of Two" and Simon Willison's "lethal trifecta" give the same test. Filters and classifiers reduce but do not remove risk.

### Cited Findings
- Simon Willison (2025-06-16) names three capabilities that combine into exfiltration risk: access to private data, exposure to untrusted content, and the ability to communicate externally; he says the only safe course is to avoid the combination, and calls guardrail products unreliable ("95% of attacks" is still a failing grade) — [Simon Willison, lethal trifecta](https://simonwillison.net/2025/Jun/16/the-lethal-trifecta/) (**verified**, primary blog read).
- Meta's "Agents Rule of Two" (published 2025-10-31): until prompt injection can be reliably detected and refused, an agent must satisfy no more than two of: [A] processing untrustworthy inputs, [B] access to sensitive systems or private data, [C] changing state or communicating externally. If a task needs all three, start a new session with fresh context; if that is impossible, the agent must not run autonomously and needs supervision such as human approval — [Meta AI blog, practical AI agent security](https://ai.meta.com/blog/practical-ai-agent-security/) (**verified**, fetched; date from fetched page).
- CaMeL ("Defeating Prompt Injections by Design", arXiv:2503.18813, submitted 2025-03-24, v2 2025-06-24, Debenedetti, Shumailov, Tramèr et al.): a protective layer around the LLM that extracts control flow from the trusted query, keeps untrusted retrieved data from changing program flow, and uses capabilities to block private data from flowing over unauthorized paths. Reported on AgentDojo: 77% of tasks solved with provable security vs 84% for an undefended system — [arXiv abstract](https://arxiv.org/abs/2503.18813) (**verified**, abstract read via fetch; the privileged/quarantined-LLM split is NOT confirmed from the fetched text).
- "Design Patterns for Securing LLM Agents against Prompt Injections" (arXiv:2506.08837, submitted 2025-06-10, latest v3 2025-06-27; Beurer-Kellner, Debenedetti, Tramèr et al.): proposes principled design patterns with "provable resistance to prompt injection" and analyzes utility/security trade-offs. The fetched abstract page did not list the pattern names — [arXiv](https://arxiv.org/abs/2506.08837) (**verified** for metadata only; pattern names **unverified**, need the full paper).
- Spotlighting (arXiv:2403.14720, Hines, Lopez, Hall, Zarfati, Zunger, Kıcıman, Microsoft, submitted 2024-03-20): transforms untrusted input so the model gets a reliable signal of its origin; the abstract reports attack success above 50% reduced to below 2% on GPT-family models with little task-quality loss — [arXiv](https://arxiv.org/abs/2403.14720) (**secondary**: numbers from search-result summary of the abstract, not fetched). A later Microsoft/Azure blog says Spotlighting is part of Prompt Shields in Azure AI Foundry (**secondary**, search snippet: [Microsoft Tech Community](https://techcommunity.microsoft.com/blog/azure-ai-foundry-blog/-/4458404)).
- OWASP Top 10 for Agentic Applications for 2026 was published 2025-12-09 (**verified**, date from [OWASP GenAI resource page](https://genai.owasp.org/resource/owasp-top-10-for-agentic-applications-for-2026)). The page itself does not list the risk names. The names below come from one secondary write-up: ASI01 Agent Goal Hijack; ASI02 Tool Misuse and Exploitation; ASI03 Identity and Privilege Abuse; ASI04 Agentic Supply Chain Vulnerabilities; ASI05 Unexpected Code Execution; ASI06 Memory & Context Poisoning; ASI07 Insecure Inter-Agent Communication; ASI08 Cascading Failures; ASI09 Human-Agent Trust Exploitation; ASI10 Rogue Agents — [Kratikal](https://kratikal.com/blog/top-ten-owasp-risk-for-agentic-applications/) (**secondary**; check the official PDF before citing wording).
- Microsoft MSRC's July 2025 indirect-prompt-injection defense post could not be fetched (URL redirected to the MSRC blog index). Its content is **unverified** in this session.

### Cited Findings: what helps (ranked by evidence in this pass)
1. Architectural separation (Rule of Two / trifecta; CaMeL-style control/data separation) — **verified** as design guidance from Meta and Willison; CaMeL result **verified** from abstract.
2. Human approval for consequential actions — recommended by Meta (above) and by Invariant Labs for the GitHub MCP case (see Section 2; **secondary**).
3. Provenance marking (spotlighting/datamarking) — reduces success in reported experiments (**secondary**); not a guarantee.
4. Detection filters — Willison calls them unreliable (**verified** via his post).

### Inferences (for Bazaar)
- Most Bazaar agents are [A]+[B]+[C] by default: they read email and issues (A), hold the Soul Passport and GitHub App tokens (B), and post to GitHub, email and public sites (C). Under Rule of Two, that combination must be split across sessions, or gated by a human.
- A practical split: a "reader" session that touches untrusted content but holds no secrets and no outbound write tools (it emits structured, schema-validated summaries); and an "actor" session that holds credentials and write tools but never receives raw untrusted text. A human or a policy engine sits between them. The raw text must not be fed back into the actor's context.
- Enforce this in Bazaar.Server (tool-policy and taint tracking on each session), not in the system prompt. Prompt-level rules are the thing injection is designed to override.
- Filters such as Prompt Shields may be added as a cheap extra layer; do not count them as the control.

### Gaps
- Anthropic, OpenAI and Google published agent-security guidance was not fetched in this pass. Needs a direct read before the threat model is final.
- OWASP LLM Top 10 2025 LLM01 page (`genai.owasp.org/llmrisk/llm012025-prompt-injection/`) returned HTTP 404 in this pass. Its contents are **unverified**.
- Full CaMeL paper and the Design Patterns paper pattern list not read.
- No 2026 measured benchmark for current frontier models was gathered (the MCPTox figure below is secondary).

---

## 2. Real incidents: coding agents, MCP tools, malicious repos, rules-file backdoors

### Takeaway
Incidents from 2025 to mid-2026 match the trifecta: a poisoned input (issue, rules file, tool description, package) steers an agent that holds private data and an outbound channel. The most-cited concrete cases are the GitHub MCP issue injection (May 2025), the Rules File Backdoor (March 2025), and the Nx "s1ngularity" malware that abused local AI CLIs (August 2025). Many MCP CVEs in 2026 exist per secondary reports, but the records must be checked against NVD before use.

### Cited Findings
- **Rules File Backdoor (Pillar Security, published 2025-03-18):** hidden Unicode in rule files for Copilot and Cursor made the assistant insert an attacker-controlled script tag, with instructions to hide the change from the user; poisoned rules persist across sessions and forks, and the hidden characters were not visible in PR review. Cursor said in March 2025 that this was not a vulnerability on its side; GitHub said users must review suggestions, then on 2025-05-01 added a warning on github.com for hidden Unicode text — [Pillar Security](https://www.pillar.security/blog/new-vulnerability-in-github-copilot-and-cursor-how-hackers-can-weaponize-code-agents) (**verified**, primary vendor-research blog read).
- **GitHub MCP prompt injection (Invariant Labs, reported May 2025; Simon Willison post dated 2025-05-26):** a malicious public issue, read by an agent working on a repo, led the agent to leak private-repo content into a public PR/issue. Invariant said this is architectural (the MCP server works as designed), so no simple patch exists; suggested mitigations were one repository per session, least-privilege tokens, and human approval (noting users can choose "always allow") — [Simon Willison](https://www.simonwillison.net/2025/May/26/github-mcp-exploited) and [DevClass coverage](https://devclass.com/?p=7568) (**secondary**, search-result summaries; not fetched). One write-up (UpGuard) says the flaw is not limited to Claude; not confirmed here. Current patch status **unverified**.
- **s1ngularity / Nx supply-chain attack (malicious versions published to npm 2025-08-26):** the first documented malware abusing developer AI CLIs. The payload invoked installed Claude, Gemini and Q CLIs with auto-approval flags (`--dangerously-skip-permissions`, `--yolo`, `--trust-all-tools`) to search for and collect secrets (wallets, GitHub/npm tokens, SSH keys, env secrets). Data was encoded and uploaded to attacker-created GitHub repos. Root cause per secondary reports: a maintainer npm token leak, or a vulnerable GitHub Action. Scale figures differ by source — [Wiz](https://www.wiz.io/blog/s1ngularity-supply-chain-attack) and [StepSecurity](https://www.stepsecurity.io/blog/supply-chain-security-alert-popular-nx-build-system-package-compromised-with-data-stealing-malware) (**secondary**, search summaries only; primary Nx advisory not fetched).
- **MCP CVE wave (2026, secondary):** a CSA research note (May 2026) reports at least seven confirmed high or critical CVEs across MCP-integrated platforms including MCP Inspector, LiteLLM, Cursor, LibreChat and Windsurf — [CSA MCP security crisis note](https://labs.cloudsecurityalliance.org/wp-content/uploads/2026/05/CSA_research_note_MCP_security_crisis_20260504-csa-styled.pdf) (**secondary**, snippet only). A July 2026 CSA note says Cursor, Claude Code, Gemini CLI, GitHub Copilot and Amazon Q auto-execute project-defined MCP servers with developer privileges, cites MCPoison (CVE-2025-54136) as a shared-repo variant, and cites MCPTox reporting a 36.5% average tool-poisoning success rate across 20 models — [CSA tool-poisoning note, 2026-07-01](https://labs.cloudsecurityalliance.org/research/csa-research-note-mcp-tool-poisoning-auto-execution-20260701/) (**secondary**, snippet only).
- **CVE-2026-30623** (MCP STDIO command injection, disclosed by OX Security 2026-04-15, per a DEV Community post; vendor response reported as "won't fix") — [DEV Community](https://dev.to/gentic_news/mcps-cve-2026-30623-anthropic-wont-fix-stdio-command-injection-mh6) (**secondary**, blog; verify against OX advisory and NVD).
- **Conflicting record:** a HOL Guard page labels CVE-2026-25536 as an MCP "tool poisoning" issue, but its described impact is a cross-client leak in the MCP TypeScript SDK. Treat as unreliable until checked against NVD — [HOL Guard](https://hol.org/guard/security/cves/CVE-2026-25536) (**secondary**, conflicting).
- **Not verified this pass:** CVE-2025-53773 (Copilot prompt-injection to settings change and code execution) is widely cited from memory; the NVD fetch returned only the page title. Treat as **unverified**.

### Inferences (for Bazaar)
- The GitHub MCP case is the direct analogue of "a stranger's GitHub issue read by an agent that holds private repos and can post": Bazaar's agents will hit this on day one unless each session is scoped to one repo and the GitHub App token is repository-scoped.
- Repo-supplied config (MCP server definitions, rules/instruction files, `CLAUDE.md`-style files) is executable intent. Bazaar should never auto-load MCP servers or instruction files from a repo that an agent did not write, and should review changes to them as code.
- The s1ngularity case shows that an agent CLI installed on a host with secrets is a credential-theft tool for any package that runs install scripts. Agent CLIs should run in VMs without host secrets, with no auto-approve flags.

### Gaps
- No primary advisory (NVD, GitHub Security Advisory, vendor PSIRT) was read for any 2026 MCP CVE in this pass.
- Current patch status for the GitHub MCP issue in each client: unverified.
- No incident data for Hyper-V/WSL2-based agent environments (none expected to exist).

---

## 3. Sandbox and VM isolation, egress, credential brokering, audit

### Takeaway
Hyper-V gives a hardware-enforced guest boundary, but the agent's own in-VM network path and any mounted folder are the realistic leak routes. Enforce egress and secret handling outside the guest. Credentials should be short-lived and brokered; GitHub App installation tokens are the right primitive but their lifetime and scope behaviour must be verified before the design relies on them.

### Cited Findings
- Microsoft's Virtual Secure Mode (VSM) page (TLFS): VSM lets root and guest partitions create isolated memory regions, and access is controlled solely by the hypervisor, which is in the trusted computing base — [Microsoft Learn TLFS VSM](https://learn.microsoft.com/ro-ro/virtualization/hyper-v-on-windows/tlfs/vsm) (**secondary**, search snippet; not fetched in full).
- Shielded VMs (Microsoft Inside Track): Host Guardian Service issues attestation; a shielded VM starts only on attested hosts — [Microsoft Inside Track](https://www.microsoft.com/insidetrack/blog/using-shielded-virtual-machines-to-help-protect-highvalue-assets/) (**secondary**). Primary Learn shielded-VM documentation was not retrieved.
- GitHub App authentication docs were fetched (`docs.github.com/.../about-authentication-with-a-github-app`), but the fetched text did not state installation-token lifetime, scoping at creation, or key-rotation guidance; it only says an app uses an installation access token to act as an installation — [GitHub docs](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/about-authentication-with-a-github-app) (**verified** for the statement quoted; the lifetime is not in the fetched text). From memory, installation tokens expire after about one hour and can be narrowed to selected repositories and permissions at creation — **unverified**; confirm in the REST API docs before relying on it.
- The Soul doc records that Bazaar's Hyper-V design gives no clipboard sharing, no host drive passthrough beyond one scoped mounted folder, and enhanced session mode off — `/home/user/Bazaar/docs/deep-dives/Bazaar.Soul.md` section 2 (**verified**, design doc read).
- The Soul doc says the Server Core + WSL2 design runs a real Linux kernel inside the Windows guest, and that nested VMs are not suited to performance-sensitive workloads — same file, section 2 (**verified**, design doc read; the nested-performance statement is attributed there to Microsoft and was not re-verified here).
- The email/identity note says Playwright should use a persistent per-agent profile stored on the host, because the profile holds session tokens — `/home/user/Bazaar/research_notes/Bazaar stack feasibility research/email_identity_accounts.md`, Key Question 3 (**verified**, note read; Playwright API details flagged there as unverified).

### Inferences (for Bazaar)
- **Egress must be enforced outside the guest.** A WSL2 instance inside the Windows guest, or any guest process with root, can change in-guest firewall rules. Put default-deny on the hypervisor virtual switch or on the host, and send all HTTP(S) through a host-side egress proxy with a per-agent domain allowlist. Block direct DNS and direct TCP to arbitrary hosts.
- **Exfiltration channels to block:** rendered markdown images and links, unfurled URLs in posts, webhooks, pastebins, and GitHub comments or gists created by the agent. A proxy allowlist plus "no auto-rendered remote images in agent-authored content" covers most of these.
- **Mounted folder is a leak route.** The Soul doc says Soul data is mounted into the VM as one scoped folder. The Passport, the audit log and any key material must never sit under any mounted path. This is a conflict to fix in the design: Section 1 says the Passport lives outside the VM disk, Section 2 says Soul data is mounted in; the rule must be "Passport and audit: never mounted; memory: mounted read-only where possible."
- **Browsing belongs in a sandbox, not on the host.** The email note's Playwright plan runs a persistent profile on the host. Browsing untrusted pages on the host with session cookies is high risk. Recommend: browser runs in an agent VM with no Passport access; the host-side broker injects a cookie or token for one service at a time and logs each use.
- **Credential brokering:** agents never receive raw API keys, passwords, TOTP seeds or refresh tokens. The host-side Identity Service issues per-action, per-service short-lived tokens. The Soul doc already leans this way (Identity Service, OPEN in the design docs).
- **Audit:** an append-only log written by the host, hash-chained, with the agent having no write access. Log every token issuance, decrypt, egress request (domain, method, size), and each tool call with its trust tag.

### Gaps
- Hyper-V shielded VM primary documentation and the TLFS page were not read in full.
- GitHub App installation token lifetime and per-request scope: not confirmed in this pass.
- No measured comparison of WSL2 egress behaviour inside a nested Hyper-V guest (the Soul doc's benchmark is still pending).
- No data on Windows Server Core + WSL2 firewall behaviour.

---

## 4. Cross-agent contamination: shared memory, Vault, group chats

### Takeaway
Because agents read each other's GC messages, one compromised agent can inject instructions into others. OWASP's agentic list names this directly (ASI06 Memory & Context Poisoning; ASI07 Insecure Inter-Agent Communication; ASI08 Cascading Failures — secondary source). The mitigation is provenance tags with trust levels, plus rules that untrusted-sourced content never grants authority.

### Cited Findings
- OWASP agentic 2026 list includes memory/context poisoning, insecure inter-agent communication and cascading failures (names from [Kratikal](https://kratikal.com/blog/top-ten-owasp-risk-for-agentic-applications/), **secondary**).
- Rules File Backdoor persisted across sessions and forks (see Section 2; **verified**).
- The Bazaar GC design is append-only and never purged (`/home/user/Bazaar/docs/deep-dives/Bazaar.Server.md` and `Bazaar.md`; Bazaar.md not read this pass; the "append-only, never purged" statement is from the task brief and `CLAUDE.md`, **verified** in CLAUDE.md).

### Inferences (for Bazaar)
- **Append-only GCs make poisoning permanent.** A poisoned message in a GC is in context forever. Mitigate with per-message trust tags and a rule that messages from agent authors are data, never instructions, for other agents.
- **Quarantine state.** When an agent is flagged (anomaly, injection suspected, kill switch), its messages stay visible but are tagged quarantined. Other agents' context builders drop or summarize quarantined content, and nothing it wrote is promoted to shared memory or the Vault.
- **Memory writes need provenance.** Each memory or Vault entry records source session, source trust level, and whether any untrusted content was in context when it was written. Entries derived from untrusted input stay in a low-trust tier until a human or a higher-trust process promotes them.
- **No inter-agent authority.** An agent's approval or request is never accepted as a human approval. Approval gates check a human identity signed by the platform, not any agent message.
- **Cascading failure control.** Rate limits and per-GC message budgets stop one agent's loop from flooding others.

### Gaps
- No published evaluation of GC-style multi-agent chat contamination was gathered. This is inference from OWASP naming plus the Rules File Backdoor persistence finding.
- Vault and memory design (Constellation) not read in this pass.

---

## 5. Governance: approval tiers, kill switch, rate limits, immutable audit

### Takeaway
The email/identity note already proposes a four-tier trust ladder (Tier 0 autonomous, Tier 1 notice, Tier 2 approval, Tier 3 human-only) and forbids CAPTCHA solving and scam-baiting. The threat model adds: tiers must map to capabilities enforced by the server, a global and per-agent kill switch, and an audit log the agents cannot write.

### Cited Findings
- Trust ladder and forbidden actions: `/home/user/Bazaar/research_notes/Bazaar stack feasibility research/email_identity_accounts.md`, Key Question 3 (**verified**, note read; the ladder is marked "proposed; for Boss approval").
- Meta: human approval or an equivalent validation step when an agent cannot be split (**verified**, see Section 1).
- Invariant Labs: human approval helps, but users may choose "always allow" (**secondary**, see Section 2).
- Google Terms: prohibit automated access and bypassing protective measures; the email note cites these for CAPTCHA and sign-up rules (**secondary** via the note; Google Terms fetched by the note's author on 2026-10-24).

### Inferences (for Bazaar)
- **Tiers as server policy:** each tool is tagged with a tier (read, post, spend, create-account, grant-scope, merge, deploy). The server refuses a higher-tier call without a signed human approval. Prompts cannot change this.
- **Approval fatigue is a real risk.** "Always allow" settings were a documented failure mode. Bazaar should not offer per-agent blanket allow for Tier 2 actions; approvals are per-action or time-boxed with a visible scope.
- **Kill switch:** (1) freeze one agent: revoke its brokered tokens, stop its VM, mark its GC presence offline; (2) freeze the platform: stop all egress and all write tools. Test both on a schedule.
- **Rate limits and budgets:** per-agent caps on outbound posts, emails, GitHub writes, and spend. Breaches auto-freeze and notify the owner.
- **Immutable audit:** host-side, append-only, hash-chained, with periodic export. The agent must not have write access. The Soul doc's "auditable reasoning" standing ask should log the trust tags that influenced each decision.
- **Public posting:** every post by a bot carries the bot label (GitHub `[bot]` suffix); posts that quote untrusted content should mark the quote.

### Gaps
- Kill-switch latency and token-revocation behaviour for GitHub App installations: not verified.
- Legal review of agent-authored public posts and approval logs: not researched; needs counsel.

---

## 6. Supply chain: dependencies agents add, NuGet confusion, package policy

### Takeaway
NuGet has a real dependency-confusion control (package source mapping, NuGet 6.0+), but it is limited to restore/install/update and is bypassed when a package is already in the global packages folder. Agent package additions must go through a human-gated policy, not the agent's own tool calls.

### Cited Findings
- NuGet Package Source Mapping (Microsoft Learn, ms.date 2026-07-16): filters per package which source(s) NuGet searches; added in NuGet 6.0; every package including transitive ones must match a pattern once mapping is defined; a `*` pattern defines a default source; the most specific pattern wins — [Microsoft Learn](https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping) (**verified**, primary doc read).
- Same page: mapping applies only to restore, install and update; `dotnet package add` and `dotnet list package --outdated/--deprecated/--vulnerable` send package IDs to all configured sources, including public ones (**verified**).
- Same page: "When the requested package already exists in the global packages folder, no source look-up will happen and the mappings will be ignored." It recommends a per-repo global packages folder (**verified**).
- Same page: mapping is opt-in and requires a nuget.config; a single root nuget.config is a best practice (**verified**).
- Rules File Backdoor and s1ngularity show that repo and dependency content can carry instructions and credential-stealing code (**verified** and **secondary** respectively; see Section 2).
- OWASP ASI04 "Agentic Supply Chain Vulnerabilities" (name only, **secondary**).

### Inferences (for Bazaar)
- **Agents must not run `dotnet add package` directly.** Route all new packages through a human-approved request (Tier 2), then add them under central package management (the project convention in `CLAUDE.md`).
- **Enforce mapping in CI:** a repo-level nuget.config with `<clear />`, a `*` pattern pointing only at nuget.org (or a private feed), and a per-repo global packages folder so mapping is not bypassed. Check that CI fails if the global packages folder is shared.
- **Lock and audit:** packages require a reviewed lock state. Lock-file support and NuGet vulnerability audit are expected to exist (**unverified** from this pass; check the NuGet docs before relying on them).
- **Install scripts and build tasks are code execution.** Restore/build runs in a no-secrets VM with egress allowlisted to the feed. This blocks the s1ngularity pattern (install-time code reads host secrets).
- **Typosquatting** (near-name packages) is not covered by source mapping; it needs a reviewed allowlist of package IDs for agent-proposed additions.

### Gaps
- Signed-package verification in NuGet (author/repository signing) and its enforcement options: not checked in this pass (**unverified**).
- Typosquatting incident data for NuGet specifically: not gathered.
- No data on whether agents' own dependency-add behaviour (for example, model-proposed package names that do not exist — "slopsquatting") occurs for C#/.NET; not searched this pass.

---

## 7. Prioritized mitigations for this architecture

### Takeaway
Ranked by the gap they close for Bazaar's specific design, not by generic severity. P0 items are needed before any agent reads email or stranger issues in production, and before any agent holds a real credential.

### Cited Findings
- Rule of Two and trifecta: the two source-backed principles behind the ranking (**verified**, see Section 1).
- GitHub MCP case: repo-scoped sessions and least-privilege tokens were the suggested mitigations (**secondary**).
- Credential exposure in s1ngularity: secrets in reach of an agent CLI were the loss (**secondary**).

### Inferences: prioritized list

**P0 (before production reading of untrusted content or holding real credentials)**
1. **Session taint and Rule of Two enforcement in Bazaar.Server.** Each session is tagged when it ingests untrusted input (email, stranger issues/PRs, web, GC messages from other agents). Tainted sessions cannot call Tier 1+ tools (secrets, post, send, spend) without a human approval. Enforce in the tool-dispatch layer.
2. **Reader/actor split** for email, issues and web. The reader session outputs schema-validated fields only, no free text into the actor.
3. **No secrets in any agent VM or in any mounted folder.** Passport, audit log and key material stay host-side; the Identity Service issues short-lived, per-action credentials. Verify GitHub App token lifetime before relying on it.
4. **Default-deny egress** enforced outside the guest (hypervisor switch or host firewall), through a host-side proxy with a per-agent domain allowlist. No auto-rendered remote content in agent-authored posts.
5. **Browser sandbox:** browsing runs in a no-Passport VM; cookies or tokens are injected per service by the broker.
6. **Human-only tiers:** CAPTCHA, phone/ID, passkeys, recovery codes, and any public post on a new platform (as in the email note's ladder).
7. **Immutable, host-side audit log** with trust tags on every tool call and every egress request.
8. **Kill switch** (per agent and global), tested.

**P1 (before multi-agent GCs go live with agents acting on each other's output)**
9. **Provenance and trust tags on every GC message, memory write and Vault entry;** untrusted-derived memory stays low-trust until promoted by a human.
10. **Quarantine state** for a flagged agent: its output is tagged and excluded from others' context and from shared memory.
11. **No inter-agent approval authority;** human approvals are signed by a platform identity.
12. **Rate limits and per-GC message budgets,** with automatic freeze and owner notice on breach.
13. **Repo config as code:** never auto-load MCP servers, rules files or agent instruction files from repos an agent did not author; review changes to them.

**P2 (before public release or open-sourcing of the agent platform)**
14. **NuGet policy:** source mapping with per-repo global packages folder; agents cannot add packages without human approval; install-time code runs in a no-secrets sandbox; typosquat allowlist.
15. **Spotlighting or datamarking** of untrusted input as a defense-in-depth layer, with its effect measured on Bazaar's own tasks.
16. **Scam and phishing triage** as already decided in the email note: quarantine, do not reply.
17. **Regular red-team runs** using a published agent benchmark (AgentDojo was named in the CaMeL result; not evaluated here).

### Gaps
- None of the P0-P2 items has been tested against Bazaar's actual code (none exists yet for the agent loop).
- Relative priority is an inference from the design docs and cited sources, not from a measured risk model.

---

## Conflicts and open items for the owner
- **Mounted Soul folder vs Passport location:** `Bazaar.Soul.md` section 1 says the Passport lives outside the VM disk; section 2 mounts Soul data into the VM as one scoped folder. Decide that the Passport and audit log are never under any mounted path.
- **Playwright on the host vs browser sandboxing:** `email_identity_accounts.md` puts the persistent browser profile on the host. Running untrusted pages with session cookies on the host conflicts with the isolation goal. Recommend moving browsing into a no-Passport VM with broker-injected tokens.
- **Gateway key exposure:** `Bazaar.Server.md` says the server holds provider routing for many upstreams, including Anthropic API keys. Model keys should live in the server's secret store and never be given to an agent VM. (Inference; the doc does not say the agent sees them.)
- **Nested WSL2 networking:** the Soul doc's default is Windows Server Core + WSL2. Egress controls must not depend on in-guest firewalls; the nested-VM benchmark should include an egress test.

## Items verified vs not verified (summary)
- Verified (primary read): Willison trifecta post; Meta Rule of Two post; CaMeL arXiv abstract; Design Patterns arXiv metadata; OWASP agentic 2026 page date; Pillar Rules File Backdoor post; Microsoft NuGet package source mapping doc; GitHub App authentication doc (for the statements quoted only); the three Bazaar design docs.
- Secondary: OWASP ASI names; spotlighting results; GitHub MCP incident; s1ngularity; 2026 MCP CVE waves; CVE-2026-30623; Hyper-V VSM and shielded-VM statements; Prompt Shields.
- Unverified or not fetched: OWASP LLM01 2025 text; Anthropic, OpenAI, Google guidance; MSRC indirect-injection post; CVE-2025-53773 and any NVD record; GitHub App installation token lifetime; Hyper-V shielded VM primary docs; CaMeL privileged/quarantined split; NuGet package signing; NuGet typosquatting data; slopsquatting data.
