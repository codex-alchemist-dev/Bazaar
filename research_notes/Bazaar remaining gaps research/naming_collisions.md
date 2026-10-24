# Naming collisions for Bazaar, Codex Alchemist, Fireball Everything and feature names (as of 2026-10-24)

Scope and limits: a knockout-level check using web search and a few page fetches on 2026-10-24. This is NOT a trademark clearance opinion and NOT legal advice. A professional clearance search (USPTO, EUIPO, WIPO Global Brand Database, plus national registries and common-law/web checks) is required before any commercial branding. No trademark registry was successfully queried in this run (see the Gaps in the first section). Source labels: **verified** = primary source page actually fetched and read; **secondary** = search-result snippet or aggregator/blog; **unverified** = background knowledge or not checked in this run.

Overall knockout result: **"Bazaar" is a high-collision name** (an existing VCS named Bazaar/Breezy, an active "bazaar" marketplace term in AI-agent and x402 payments circles, and multiple GitHub/NuGet uses). **"Codex" as a product-name component is the highest-risk element** because OpenAI's Codex is a prominent AI coding agent. Feature names are mostly common words; **Vault** (HashiCorp Vault) and **Constellation** (ShiftinBits Constellation, Constella) are the most visible collisions found. Fireball Everything and Alchemist were not found to be prominent in software, with caveats.

## Bazaar (platform name): existing software, repos, packages

### Takeaway
"Bazaar" is a generic-English word and already names an older VCS (GNU Bazaar, bzr, whose active fork is Breezy, command `brz`). Separately, "Bazaar" is a common term in 2025-2026 AI-agent and x402 payments projects (Agent Bazaar, x402 Bazaar discovery layer, multiple "agent marketplace" repos). Nothing found is a prominent AI coding agent called exactly "Bazaar", but the term is crowded in exactly the AI-agent space Bazaar occupies.

### Cited Findings
- GNU Bazaar (bzr) is a VCS that Breezy forks. Breezy's infobox lists original authors as "Canonical and Bazaar/Breezy community"; latest stable release shown as 3.3.23, dated 22 October 2026; developers Martin Packman and Jelmer Vernooij — [Wikipedia: Breezy (software)](https://en.wikipedia.org/wiki/Breezy_(software)) (**verified**, page fetched 2026-10-24; the article text itself did not describe Canonical's current role).
- Breezy is described as a fork of GNU Bazaar (bzr), command `brz`, adding Python 3 and Git support — [Wikipedia: Breezy (software)](https://en.wikipedia.org/wiki/Breezy_(software)) (**verified**).
- Breezy's PyPI page calls it a "friendly fork" of Bazaar hosted at bazaar.canonical.com — [PyPI: breezy](https://pypi.org/pypi/breezy) (**secondary**, from search snippet; not fetched in full). Canonical's current involvement was **not confirmed**.
- **Conflict on release date:** an earlier search snippet listed 3.3.21 dated 3 Feb 2026 as stable and PyPI release files for 3.3.22; the Wikipedia fetch shows 3.3.23 dated 22 Oct 2026. Treat the newest version as the one to re-check on PyPI before citing. (**secondary** vs **verified**; the Wikipedia fetch is the newer read.)
- Breezy activity: one aggregator reports GitHub last push 2026-07-21 (about 188 stars, 34 forks); another says 2026-05-03. Conflicting; **unverified** (check github.com/breezy-team/breezy directly) — [gittrend.io](https://gittrend.io/repo/breezy-team/breezy) and [ecosyste.ms summary](https://summary.ecosyste.ms/projects/371553) (**secondary**).
- NuGet search for "Bazaar" (fetched listing, 18 results) shows mostly unrelated or "Cafe Bazaar" (an Iranian Android app store) packages. Relevant hits: `Bazaar.CSharpTrinkets` (owner SynBiotik, "Assortment of C# tools and utilities"); `XploRe.Versioning.Tools.DotNet` (SemVer versioning that supports Bazaar (bzr) as a VCS); `Coflnet.Sky.Bazaar.Client` and `Coflnet.Sky.Bazaar.Flipper.Client` (game-market related). Also several `*Bazaar*` IAP plugins for Cafe Bazaar and Myket — [NuGet search, fetched](https://www.nuget.org/packages?q=Bazaar) (**verified** listing, as seen 2026-10-24).
- NuGet search restricted to package ID prefix `Bazaar.*` returned 13 results; only `Bazaar.CSharpTrinkets` (SynBiotik) has an ID beginning "Bazaar." — [NuGet search, fetched](https://www.nuget.org/packages?q=Id%3ABazaar.*) (**verified** listing). **Implication:** the `Bazaar.` ID prefix is not owned by any visible org, but one personal package already uses it, so the prefix is not reserved by anyone we can see. Whether NuGet's package ID prefix reservation applies to a new `Bazaar.` prefix is **unverified** (not checked in this run).
- GitHub repository search for "bazaar ai agent" (first page of 74) shows: `Agent-Bazaar/Agent-Bazaar` (autonomous AI agent marketplace on Solana, with SDK and MCP server); `The-Swarm-Corporation/agent-bazaar-implementation` (implementation of the paper "Agent Bazaar: Enabling Economic Alignment in Multi-Agent Marketplaces"); `0d1026/Rialto` (x402 facilitator and "Bazaar discovery layer" for Stellar); `sukrutkrdg/x402-bazaar-mcp` (MCP server for "x402 Bazaar" with 131 pay-per-call APIs on Base); `CameronCrow/AI-Bazaar-deprecated` (extension of LLM Economist, agent trading and firm agents) and `CameronCrow/Agent-Bazaar` (continuation of sethkarten/AI-Bazaar); `ashutoshvct/bazaar` ("Autonomous AI Agent Marketplace"); `crazycompanyinc/bazaar` ("Agent marketplace for discovering, evaluating, and hiring AI agents"); `awrsla/agent-bazaar-challenge`; `8bitnand/BazaarAI` (stock-market AI tutorial). Star counts are low (0–4 on the visible ones) — [GitHub search, fetched](https://github.com/search?q=bazaar+ai+agent&type=repositories) (**verified** listing).
- A search for an AI coding agent called "Bazaar" found no such product; the only "Bazaar" hit was an unrelated 2022 ML-platform blog post — [WebSearch result](https://hashnode.com/tag/vcodex) (**secondary**; search-based absence, not exhaustive).
- A search for a "Bazaar AI agent startup trademark" returned nothing about a Bazaar mark — **not a finding of absence** (search engines do not index registries well) — (**secondary**).
- npm search (`bazaar agent`) returned HTTP 403 and could not be read — **unverified**.
- **Trademark registries:** USPTO search host (tuess.uspto.gov) did not resolve in this run. EUIPO eSearch, WIPO Global Brand Database, and IPOPHL were not successfully queried. **No trademark-status finding for "Bazaar" is made.**

### Inferences
- The strongest collision for "Bazaar" is conceptual and in search/SEO, not a single famous product: "Bazaar" is a crowded label in the AI-agent marketplace space (multiple repos and a published paper titled "Agent Bazaar"), plus x402's "Bazaar" discovery layer. Expect search results and package searches to be polluted for "bazaar agent" queries.
- The name collides with the historical bzr world only if Bazaar uses VCS-related terms; Bazaar's Git-style Solar System / VCS ambitions (Bazaar.Constellation's "Git Solar System") would sit close to Breezy's domain. Recommend avoiding VCS-flavored sub-names under "Bazaar".
- For NuGet: the `Bazaar.*` ID space is open in practice (only one visible package), so the prefix is claimable, but NuGet publishing rights and prefix reservation should be checked on nuget.org under the org account before first publish.

### Gaps
- Trademark registration status for "Bazaar" in any class (USPTO, EUIPO, WIPO, IPOPHL, UKIPO, etc.) — not checked (registry hosts not reachable or not queried).
- Canonical's current involvement in bzr/Breezy in 2026 — not confirmed.
- npm scope/package availability — npm search blocked (403).
- GitHub org/repo `codex-alchemist-dev/Bazaar` — not checked (no GitHub org search done for names; `gh` not used).
- Domain availability — deliberately not checked (per task: do not buy or probe purchase flows).

## "Codex" and "Codex Alchemist" in developer tools

### Takeaway
"Codex" is the name of OpenAI's coding agent (Codex CLI in openai/codex, plus the 2025 Codex agent product, distinct from the 2021 Codex model). Using "Codex" in a team or product name for a coding-related company is a high-risk choice: it implies affiliation with OpenAI's product to developers. No Codex-specific trademark policy was found in this run; OpenAI's general brand guidance and enforcement against "GPT" in third-party names are the closest evidence.

### Cited Findings
- openai/codex is the official open-source repo for OpenAI's Codex CLI, a coding agent that runs locally — [GitHub: openai/codex](https://github.com/openai/codex) and [OpenAI Codex open source docs](https://developers.openai.com/codex/open-source) (**secondary**, from search-result snippets; the repo and docs were not fetched in full).
- Wikipedia notes the original 2021 Codex model is separate from the 2025 coding agent, which was introduced under a similar name — [Wikipedia: OpenAI Codex (language model)](https://en.wikipedia.org/wiki/OpenAI_Codex_(language_model)) (**secondary**, search snippet).
- OpenAI's brand page (openai.com/brand/) says its design guidelines help outside partners, developers and publishers handle its trademarks; permission requests go to partnercomms@openai.com and legal inquiries to legal@openai.com — [OpenAI brand page](https://openai.com/brand/) (**secondary**: the fetch returned HTTP 403, so the statement comes from a search snippet only).
- OpenAI has pushed back on third parties using "GPT" in product or company names, saying such use can imply partnership or endorsement — [Slator: OpenAI Hunts Down Companies Adding Trademarked GPT to Brand](https://tech.slator.com/openai-hunts-down-companies-using-trademarked-gpt-in-brand/) (**secondary**, search snippet, article not fetched). This is an analogy for "Codex", not a Codex-specific case.
- Aggregator logo pages describe "OpenAI Codex" as a trademark and advise checking official brand guidelines — [Toolradar: openai codex](https://toolradar.com/logo/openai-codex) (**secondary**, low authority; it cites no filing).
- No Codex-specific trademark guidance or filing status was found in this run.

### Inferences
- "Codex Alchemist" as a dev-team name combined with a platform called Bazaar is likely to be read by developers as an OpenAI-adjacent or OpenAI-affiliated thing. The risk is higher than for generic words because the product category (AI coding agent) is identical.
- The safest pattern: do not use "Codex" alone as a product, package, or public-facing platform name; keep "Codex Alchemist" only as an internal company/team name, or rename before public promotion. Adding a descriptor does not clearly solve implied affiliation.
- Package and repo names with `Codex.` or `codex-` prefixes will be confused with openai/codex in search and npm/NuGet results.

### Gaps
- OpenAI's official Codex naming policy and any USPTO/EUIPO filings for CODEX (or CODEX-formative marks) were not checked (brand page 403; registries unreachable).
- "Alchemist" as a term in software/AI/dev tools was not searched in this run — **unverified**; treat as a gap, not as clear.
- Whether "Codex Alchemist" as a team name has any existing use was not checked.

## Fireball Everything (studio)

### Takeaway
No studio named "Fireball Everything" was found. "Fireball" is a common word and has several unrelated uses (a 1982 Atari 2600 game; a 2026 game "The Legend of Fireball"; a fan-run "Fireball Studios" wiki). Knockout-level risk is low for software, but gaming-sector "Fireball" names are numerous.

### Cited Findings
- No studio called "Fireball Everything" found in search — [WebSearch result, 2026-10-24](https://en.wikipedia.org/wiki/Fireball_(video_game)) (**secondary**, search-based absence).
- Fireball (Atari 2600, 1982) is a Breakout-style game by Scott Nelson, published by Arcadia/Starpath — [Wikipedia: Fireball (video game)](https://en.wikipedia.org/wiki/Fireball_(video_game)) (**secondary**, search snippet).
- "The Legend of Fireball" (2026), a free adventure from QubicGames, listed for Switch, PS4/PS5 and Xbox One — [PlayStation Store concept page](https://store.playstation.com/en-se/concept/10019459) (**secondary**, search snippet).
- A fan-run wiki (fireball-studios.fandom.com) describes a "Fireball Studios" founded June 2010 by Baxter Ewers, with mostly small indie games on GameJolt — [Fireball Studios fandom wiki](https://fireball-studios.fandom.com/wiki/File:Transparent.gif) (**unverified**: community wiki, not checked against primary sources; treat as a lead only).

### Inferences
- "Fireball" is widely used in games, so a "Fireball Everything" studio name would need a clearance check in games/entertainment classes, separate from software.
- The name is unlikely to collide with software tooling directly, but a "Fireball" game studio with a fan wiki presence means the studio name should be checked on Steam/itch/GameJolt before public use.

### Gaps
- Official registry and marketplace checks for "Fireball Everything" and "Fireball Studios" (USPTO, EUIPO, Steam, itch.io, GameJolt, app stores) — not done.
- Whether the Baxter Ewers "Fireball Studios" is the same entity as the Codex Alchemist/Fireball Everything studio is unknown — **gap**.

## Feature names as common words (Soul, Soul Passport, Constellation, Vault, Council, Sidewalk, Observatory, Skimmer Bouncer)

### Takeaway
Most of these are common English words or conventions, so they are weak as trademarks but may still collide in product categories. **Vault** is the most prominent collision found (HashiCorp Vault, a secrets manager). **Constellation** collides with at least one code-intelligence product (ShiftinBits Constellation) and a knowledge-management app ("Constella"). **Soul / SOUL.md** is an emerging informal convention for agent identity files, not a registered format. **Council**, **Sidewalk** and **Observatory** were not searched in this run; from background knowledge they are used by known products (e.g., Amazon Sidewalk, Mozilla HTTP Observatory), but these were **unverified** here and need a search.

### Cited Findings
- **Vault:** HashiCorp Vault is a secrets management product, with a free self-hostable Community Edition and managed HCP Vault; a logo-site says the logo is a HashiCorp trademark — [Toolradar: vault logo](https://toolradar.com/logo/vault) and [Infisical: HashiCorp Vault overview](https://infisical.com/blog/hashicorp-vault-secrets-management.md) (**secondary**, search snippets). Ownership of the Vault mark by HashiCorp (now part of IBM, per one secondary source) was not confirmed against a filing.
- **Constellation (code intelligence):** ShiftinBits' Constellation builds a queryable graph of code structure for AI assistants, listed on Capterra as cloud/SaaS with ChatGPT and Claude integrations — [Capterra: Constellation](https://www.capterra.co.uk/software/1095530/Constellation) (**secondary**, search snippet; vendor page not read).
- **Constella:** a personal knowledge management app with an infinite visual graph, paid plans from $19/month — [futuretools.io: constella](https://futuretools.io/tools/constella) (**secondary**).
- **Constellation (Markdown vault viewer):** a local app for walking Markdown note vaults in 3D on Apple Vision Pro — [drobinin.com Constellation](https://drobinin.com/apps/constellation/) (**secondary**). Also a hobby "Constellations" GitHub project (johndimm) — [scour.ing link to johndimm/Constellations](https://scour.ing/@matmat/p/https://github.com/johndimm/Constellations) (**secondary**).
- **Soul / SOUL.md:** SOUL.md is a markdown identity-file convention for AI agents, with no single formal standard. Sources: [mdskills SOUL.md spec](https://www.mdskills.ai/specs/soul-md) (says created by Aaron Mars and used in the OpenClaw ecosystem), [soul-spec (AntonioTF5), a YAML-frontmatter .soul.md format, via mer.vin](https://mer.vin/2026/05/soul-md-for-ai-agents-30-80-line-identity-blueprint-before-memory-or-tools/), and [Mission Control agent soul system docs](https://www.mintlify.com/builderz-labs/mission-control/advanced/agent-soul-system) (all **secondary**; vendor and community pages, the mdskills and soul-spec format details were not verified against their repos).
- **Council, Sidewalk, Observatory, Skimmer Bouncer:** not searched in this run. (Background knowledge, **unverified**: Amazon Sidewalk is an Amazon wireless network; Mozilla's HTTP Observatory is a web security scanner; "Council" is a common name for AI-agent debate/voting tools.)

### Inferences
- Soul / SOUL.md: the convention is crowded with independent projects using the same file name (`SOUL.md`). That makes the file-format name a weak trademark asset and a risk for confusion with OpenClaw-style agent files. Bazaar's "Soul" is a product-level concept, so a descriptor ("Bazaar Soul" or "Soul System") helps.
- Vault: a dev-tools product named Vault (as a feature name in a platform that handles credentials, per Bazaar.Soul's Soul Passport) will be read as HashiCorp Vault. The owner's design doc already puts credentials in "Soul Passport" and secrets in "Vault"; this is the highest-impact feature rename candidate for public promotion.
- Constellation: already used by a code-intelligence product targeting AI assistants (ShiftinBits) — the closest category overlap with Bazaar's Constellation (knowledge-graph/Git visualization). Recommend a distinctive qualifier.
- Common words such as Sidewalk, Council and Observatory: in a crowded developer-tool space, expect overlap with names from large vendors (checks required).

### Gaps
- No trademark registry checks for any feature name.
- Council, Sidewalk, Observatory, Skimmer Bouncer: no search results verified.
- Whether HashiCorp's "Vault" mark is registered in software classes (and by which entity) — not confirmed against USPTO/IBM filings.
- Whether ShiftinBits' Constellation holds a registered mark — not checked.

## Package, namespace and org naming (NuGet, npm, GitHub, domains)

### Takeaway
On public NuGet, the `Bazaar.` ID space appears open except for one personal package, but NuGet's prefix-reservation rules and the GitHub org/repo namespace were not verified in this run. npm search was blocked. Domain availability was intentionally not checked.

### Cited Findings
- NuGet public search for "Bazaar" and for ID prefix `Bazaar.*` (see the first section for the exact listings) — [NuGet search](https://www.nuget.org/packages?q=Bazaar) and [NuGet ID search](https://www.nuget.org/packages?q=Id%3ABazaar.*) (**verified** listings).
- npm search page returned HTTP 403 — [npm search](https://www.npmjs.com/search?q=bazaar%20agent) (**verified** failure).
- GitHub org `codex-alchemist-dev` and repo `codex-alchemist-dev/Bazaar`: not checked in this run (no GitHub search for existing org/repo names beyond generic "bazaar" queries).

### Inferences
- Use an org-scoped identifier for packages (for example a Bazaar.* prefix owned by codex-alchemist-dev, or a longer `CodexAlchemist.Bazaar.*` prefix) so that the public package names do not overlap the crowded "bazaar agent" space. Confirm NuGet owner verification and prefix reservation in the account settings before first publish (**unverified**: NuGet reservation feature details not checked).

### Gaps
- NuGet prefix-reservation rules and availability of specific IDs — not fully verified.
- npm scope availability — not verified (npm blocked).
- GitHub org name conflicts — not verified.
- Domain availability — deliberately not checked; no purchase or availability probing done.

## Mitigations (practical)

### Takeaway
Keep the public project name with a distinctive descriptor, avoid "Codex" in public product names, rename or qualify "Vault" and "Constellation" for public use, and run a professional clearance search before promotion. Other open-source projects with generic names commonly use a qualifier or org-scoped namespace (examples not verified in this run).

### Cited Findings
- The owner's design doc already says Bazaar will be open source under AGPL-3.0 with Apache-2.0 SDK and contracts, and that Codex Alchemist is the first company built on Bazaar — [Bazaar.md](/home/user/Bazaar/docs/deep-dives/Bazaar.md) (**verified**, local file read).
- The same doc uses "Vault" for knowledge/credential storage and "Soul Passport" for the credential registry — [Bazaar.md](/home/user/Bazaar/docs/deep-dives/Bazaar.md) (**verified**).

### Inferences (recommended options, not decisions)
1. **Descriptor pairing:** public name "Bazaar" always shown with a qualifier (for example "Bazaar — agent company platform") and a distinct repo/package namespace (e.g., `codex-alchemist-dev/bazaar-platform`, `CodexAlchemist.Bazaar.*`). This is a knockout mitigation, not a clearance result.
2. **Codex:** keep "Codex Alchemist" for the internal team only, or rename the team before public release. Do not use "Codex" in package IDs.
3. **Vault and Constellation:** rename or qualify feature names in public material (e.g., "Bazaar Vault", "Constellation Graph") and check for existing trademarks in the software class.
4. **Trademark policy:** if the project is public and promoted, add a trademark/usage policy for the name and logo (a separate doc). Get counsel to run the clearance search before commercial branding, as required by the task.
5. **Namespaces:** claim the `Bazaar.*` NuGet prefix (or a longer prefix) under the org account early, once the repo exists, to prevent squatting.

### Gaps
- Examples of how specific named open-source projects handled generic-name collisions were not searched in this run — **unverified** as a category.
- Trademark policy templates and reservation procedures were not researched.

## Summary of unverified or not-checked items
- USPTO, EUIPO, WIPO, IPOPHL trademark status for all names: NOT CHECKED (one host did not resolve; others not queried).
- OpenAI Codex brand guidance: brand page returned 403; only search snippets.
- npm search: 403.
- Canonical's current role in bzr/Breezy in 2026: NOT CONFIRMED.
- Breezy GitHub activity dates: conflicting secondary sources.
- Council, Sidewalk, Observatory, Skimmer Bouncer, Alchemist, Soul (beyond the SOUL.md convention): NOT SEARCHED.
- Fireball Studios fan-wiki details: UNVERIFIED community wiki.
- Not legal advice. A professional clearance search is required before commercial branding.
