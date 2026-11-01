# DeepSeek Harness and TokenEconomizer: reference review

Status: RESEARCHED (2026-11-01). Reference only. Nothing is ported, copied, or plugged in. The ideas below are candidates for the PROPOSED docs when those are next revised (17, 08, 06, 23). No DECIDED item changes.

## Sources and what was verified

| Source | What was checked | Confidence |
|---|---|---|
| github.com/deepseek-ai/deepseek-harness (README, MIT) | Public landing page only. It describes "DeepSeek Harness" (dsh): a developer-preview agent harness built on the Cordis "everything is a plugin" framework, with a local web UI. | Verified for what the page says |
| "dsh-superagent" router and worker | Not found. The repo's `apps/` path returned 404. Third-party vendor blogs describe three presets: Standard (full coding agent, default), Code/PTC (programmatic tool calling through a TypeScript code-mode SDK), and Minimal (persistent bash plus a string-replace editor). | Unverified; blog-sourced, so treat as hearsay |
| github.com/microsoft/TokenEconomizer (README, Apache-2.0) | Mechanisms and benchmark conditions, as summarised from the README and paper. | Self-reported by the authors |
| MarkTechPost coverage | Not found. | Not checked |

Notes on the claims:

- The owner's "dsh-superagent" name does not appear in the public repo as far as we could reach. Ask the owner which repo or package they meant before anyone treats its design as known.
- The headline figure is real as stated by the authors: 18.4–30.9% lower mean cost across five agent–model combinations on SWE-Bench (277 tasks, six repositories, run in order with nothing learned at the start). It is the range over five runs, not a single result. The range's top end belongs to one combination, and that combination lost 1.0 point of Pass@1. Pass@1 rose in the other four. Within each repository the cost fall was 5–40%. Other benchmarks showed 6–21% lower cost.
- Read the 30.9% as a best case on the authors' setup, not as an expected saving for Bazaar.

## TokenEconomizer: what it does

It learns per repository from finished sessions, stored locally in the user's home directory:

1. **Micro-skills.** It records repeated failures. Every k sessions (default 3), a model reviews recent sessions and proposes facts. A fact is applied once three sessions confirm it, and dropped if a later session contradicts it.
2. **Confirmed facts at session start.** Repo facts are added to context before the first turn.
3. **Loop guard.** When a turn looks stuck, it adds a short redirect note to the agent's context, up to r times per turn (default 1). It never stops the turn itself.
4. **Tool pruning.** Tools unused over enough prompts and sessions are removed from the agent's own settings. A restore command brings them back. Pruning is not available for every agent.
5. **Cost accounting.** Costs use cache pricing and include the plugin's own model calls. The learning overhead is part of the reported saving.

## Ideas worth adopting

Each idea maps onto a Bazaar doc. None is adopted yet; each needs an owner decision or a doc revision first.

### A. Confirmed repo facts, promoted after several sessions (doc 08, doc 17)

Coding agents rediscover the same repo facts every session (build commands, test quirks, a flaky project). Our memory design (08) has scope, tier and decay, but no rule for promoting a fact from one session to a trusted note.

Proposal: a repo fact becomes a Vault note in the agent's project scope only after N independent confirmations (default 3, configurable), and is retired when a later session contradicts it. Each confirmation links to the task and the check run that showed it. This matches our "agent text is soft" taint rule, since promoted facts never bypass review.

### B. Redirect before stop in the loop guard (doc 06, Psych Ward)

Our design sends loop-guard trips to the Psych Ward (DECIDED). TokenEconomizer's cheaper step comes first: one short redirect note, capped at one per turn, before any stop. Adopt as a first tier in the loop guard. The Psych Ward step stays as the second tier, unchanged. The cap (r) becomes a setting with a floor, not a loosening of the stop rule.

### C. Tool pruning from observed use (doc 17)

Our coding harness has a fixed tool set per agent, and it is set by the whitelist (doc 05). Pruning unused tools reduces the tool-schema tokens on every turn.

Proposal, PROPOSED only: an agent's tool list may shrink from observed use, through the server's allowlist, never by editing the agent's settings files. Pruning never removes a Tier 2 class or the approval-gated tools. Every change is audit-logged, and restore is one call. Measure first (see E).

### D. Programmatic tool calling for batch work (doc 17, optional)

The Code/PTC preset lets the model write one short program that calls several tools, instead of making a round trip per tool call. For batch work such as reading many files or running many checks, this can cut turns and context growth.

Proposal: list it as an evaluation candidate in doc 17's benchmark (doc 34), not as a default. Any such program runs in the VM under the same path jail, timeout and approval gates as shell (doc 17), so it adds no new trust path.

### E. Measure cost with cache pricing, and count the overhead (doc 23, doc 06)

Two points carry over directly:

- Report cost with cache pricing, not raw tokens. Our manifest already records ids and hashes, so per-section tokens can be measured per turn (retrieval doc, section 2).
- Count the cost of our own review, learning and summary passes in any saving claim. TokenEconomizer does this, and the claim is weaker without it.

### F. Route through an OpenAI-compatible base URL (doc 05)

DSH's routers work by pointing a base URL at an OpenAI-compatible endpoint. Bazaar's gateway already treats each provider as an interchangeable upstream (doc 05, FreeLLMAPI and direct keys). No change is needed. This only confirms the pattern.

## Not adopted

- **Cordis plugin framework and the Node web UI.** Bazaar is .NET and uses its own provider interfaces (doc 03). Nothing to port.
- **Whole-repo learned state kept on the developer's machine.** Our learned state lives in the Vault and Postgres, under the server's audit and privacy rules, not in a home-directory store.
- **Any savings figure as a target.** Our benchmark (doc 17, "Live periodic eval") measures held-out pass rate, format compliance and tokens. We set no cost target from these authors' numbers.

## Evidence gaps

- The "dsh-superagent" router and worker design could not be verified from a primary source.
- The MarkTechPost article could not be found, so nothing from it is used here.
- The 30.9% figure is the authors' SWE-Bench result. We have no independent replication, and none of the benchmark conditions match Bazaar's work.

## Owner questions

1. Which repo or package is "dsh-superagent"? Send a link so the design can be read directly.
2. Should idea A (repo-fact promotion after N confirmations) be drafted as a PROPOSED change to doc 08? Recommended: yes, after the next memory-doc revision.
3. Should idea C (tool pruning) stay as a candidate until the per-section token metric (E) exists? Recommended: yes.
