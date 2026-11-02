# DeepSeek Harness, dsh-superagent and TokenEconomizer: reference review

Status: RESEARCHED (2026-11-01). Reference only. Nothing is ported, copied, or plugged in. The ideas below are candidates for the PROPOSED docs when those are next revised (06, 08, 13, 17, 23). No DECIDED item changes.

## Sources and what was verified

| Source | What was checked | Confidence |
|---|---|---|
| github.com/deepseek-ai/dsh-superagent-prototype (README, `docs/003-execution.md`, `docs/007-architecture.md`) | Primary source for dsh-superagent. Read in full through the summaries of those docs. The README calls it a research prototype, "for reference only, not an official DeepSeek product", and says some article designs are not implemented. MIT. | Verified from the repo's own docs |
| github.com/deepseek-ai/deepseek-harness (README, MIT) | The DeepSeek Harness (dsh) framework: Cordis plugin base, developer preview, local web UI. | Verified for what the page says |
| MarkTechPost article (2026-11-01) | Blocked (403). Covered only by secondary write-ups. | Not checked |
| Secondary write-ups on dsh-superagent | Used only for two figures (below), which the primary repo does not contain. | Unverified |
| github.com/microsoft/TokenEconomizer (README, Apache-2.0) | Mechanisms and benchmark conditions, as summarised from the README and paper. | Self-reported by the authors |

Two earlier claims in this note are now corrected:
- The "dsh-superagent" router and worker design is no longer unverified. It is described in the primary repo, and the design below comes from that.
- The three DSH presets (Standard, Code/PTC, Minimal) came from vendor blogs. The repo's execution doc names `standard` and `dsh-superagent-router` and `dsh-superagent-worker` presets. Code/PTC and Minimal were not confirmed in the primary repo.

## dsh-superagent: what the primary repo describes

It is a proactive coding agent for one GitHub repository. It listens for triggers from GitHub, and optionally Feishu. When an allowed person asks for work, it opens a work item (a Ticket), runs it in its own session and directory, and replies in the original conversation, for example with a pull request. The guiding principle is to take as little human attention as possible.

**Router (the main Session).**
- It assesses proactive candidates, then accepts, attaches, stops or ignores them. Accepting creates the Ticket, its mandate, its first assignment and its subscriptions in one transaction.
- It has 14 tools, including route, eval, reply, settings and policy sync. It has no shell. A route call makes 1 to 20 ordered decisions.
- Eval context is bounded. Trigger text is capped at 4,000 characters. The thread window is five opening messages plus five around the trigger. Most messages are capped at 400 characters, the two next to the trigger at 1,200, and the rest appear as 50-character stubs that expand by id.
- An eval expires twice, then is marked unavailable on the third expiry.

**Engine and dispatch.**
- The engine is the only writer of triggers, threads, decisions, tickets and assignments. Commands run in single transactions, and external work goes through durable effect rows (launch, delivery).
- `WorkerSlots` caps concurrency at `maxWorkers`. A human message may use one extra reserved slot, and wake-ups take priority over new launches. Queued launches wait in a visible `working / queued` state. A slow prepare step delays only its own ticket.

**Workers (Executors).**
- Each ticket gets its own session (`worker-<ticket id>`) and its own directory. The directory is created by a configured prepare command, which in the typical setup is `git worktree`. Without a prepare command the worker starts in an empty directory.
- Workers have six tools: read, ack, report, two thread-relevance tools and a trigger handback. They cannot ask a person directly, change profiles, schedule work, message other sessions, or edit external documents.
- Context per worker is the assignment inline: title, up to 1,500 characters of text, and the source trigger card. Input cards are capped at 600 characters.
- Lifecycle: a failed turn gets continuation messages at 15, 30 and 60 seconds. Provider or network failures retry three times. On restart, interrupted assignments resume, oldest ticket first.

**Subscriptions.** Threads carry subscriptions. A worker can bind a thread, follow one for context, or mark one unrelated. A ticket ends when its followed threads close, or after a ready or dropped report.

**Oversight and results.**
- Progress stays on the ticket. A question for a person is relayed straight away. Worker reports up to 600 characters are steered into the router.
- The router can request a worker's status, add notes, retry or stop it. A health board runs every two seconds. The doc does not describe a dedicated watchdog or a per-worker timeout.
- Outgoing messages back off from 5 seconds up to 300 seconds, and a message is reported as a system problem after three consecutive failures.

**Accept modes and safety.**
- Outside auto mode, an inferred accept is only recorded as "would accept". Auto mode needs enabled matching rules.
- Mandates are revalidated against the current policy. A policy change notifies affected tickets.
- Workers run commands without approval, as the operator's OS user. A blocked-commands list exists, and the docs say it "establish[es] defaults, not a security boundary". A worker can still run a program by absolute path.
- The default accept mode is not stated.

**Merging.** Workers never enable auto-merge by default. The default delivery prompt allows it only for a PR opened for the ticket that is ready, has the required approvals on the head commit, and is under 200 changed lines. A worker withdraws auto-merge before pushing a new revision. The docs state that branch protection must enforce approvals, since the instructions govern the agent only.

**Retention.** Old records are not deleted. The docs say they grow without bound.

**Absent from the primary docs:** token budgets or a global context limit, compaction behaviour, specific model names, a default concurrency value, a default eval timeout, and any benchmark. The 30%-more-router-decisions and 6.4-to-1.6-second inference figures appear only in a secondary write-up. They are unverified and not used here.

## TokenEconomizer: what it does

It learns per repository from finished sessions, stored locally in the user's home directory:

1. **Micro-skills.** It records repeated failures. Every k sessions (default 3), a model reviews recent sessions and proposes facts. A fact is applied once three sessions confirm it, and dropped if a later session contradicts it.
2. **Confirmed facts at session start.** Repo facts are added to context before the first turn.
3. **Loop guard.** When a turn looks stuck, it adds a short redirect note to the agent's context, up to r times per turn (default 1). It never stops the turn itself.
4. **Tool pruning.** Tools unused over enough prompts and sessions are removed from the agent's own settings. A restore command brings them back. Pruning is not available for every agent.
5. **Cost accounting.** Costs use cache pricing and include the plugin's own model calls. The learning overhead is part of the reported saving.

Headline result, as the authors report it: 18.4–30.9% lower mean cost across five agent–model combinations on SWE-Bench (277 tasks, six repositories, run in order with nothing learned at the start). Pass@1 rose in four of five. The 30.9% figure is the best of the five, and that combination lost 1.0 point of Pass@1. Within each repository the cost fall was 5–40%. Other benchmarks showed 6–21% lower cost.

## Ideas worth adopting

Each idea maps onto a Bazaar doc. None is adopted yet; each needs an owner decision or a doc revision first.

### A. Bounded, expandable context per item (doc 06, doc 08, retrieval doc 3.4)

dsh-superagent bounds every context piece with a cap, and keeps the full text behind an id: 4,000 characters for the trigger, 400 for most messages, 1,200 for the two next to the trigger, and 50-character stubs elsewhere. Reads are paged with `next_offset`, with defaults and maxima. This is the same shape as our card format (retrieval doc 3.4), and it gives concrete starting defaults for doc 06's section budgets. Adopt the numbers as defaults, not as settings that can loosen our floors.

### B. Reserved slot for the human, and visible queueing (doc 06)

Work launches sit in a visible `queued` state. One slot is reserved for a human message, and wake-ups go before new launches. Our delegation limit (doc 06: depth 2, three concurrent) has no reserved slot. Propose one, so a human request is never queued behind agent work.

### C. Shadow accept mode before automation (doc 13)

Outside auto mode, dsh-superagent records "would accept" and does not act. That is the same idea as our Skimmer shadow mode (doc 13). It is a useful confirmation: shadow first, then enable. Our doc already has it, so this only reinforces it.

### D. Router without a shell, workers with tools (doc 17)

The router has no shell and a small tool set. Workers get the full tool set. Our design already separates the reader and actor sessions (doc 15, doc 17). This confirms the split. It also suggests keeping the router-style coordinator without a shell.

### E. Ticket done when its PRs close (doc 12, doc 17)

A PR belongs to a ticket by branch name, and the ticket is done when none of its PRs remain open. Our Kanban rule (C25) is that a card is done when its PR is merged. The branch-name binding is a concrete way to implement it. Note it as an option for doc 17's `agent/<task>` branches.

### F. Background work with a cap and a kill (doc 17)

Long shell jobs move to the background after 5 seconds, and get a 30-minute cap per turn. They are killed when the assignment ends. Doc 17 has a 15-minute command timeout and a 2-hour task limit, and no background rule. Adopt the background step and the kill-on-end rule, keeping our existing limits as the ceiling.

### G. Policy revalidation on change (doc 15)

Mandates are rechecked against current policy, and a policy change notifies the affected tickets. Our approval design (doc 15) has no rule for stale grants when policy changes. Propose one: a policy change revalidates open approvals.

### H. Retry bounds and a system-problem report (doc 06)

Provider failures retry three times with backoff. Outgoing messages back off to 300 seconds, and three consecutive failures are reported as a system problem. Adopt the three-strike report for channel messages (doc 06 delivery), with our retry counts as the ceiling.

### I. Repo facts promoted after several sessions (doc 08, doc 17)

From TokenEconomizer. A repo fact becomes a Vault note in the agent's project scope only after N independent confirmations (default 3, configurable), and is retired when a later session contradicts it. Each confirmation links to the task and the check run that showed it. This matches our "agent text is soft" taint rule, since promoted facts never bypass review.

### J. Redirect before stop in the loop guard (doc 06, Psych Ward)

From TokenEconomizer. Our design sends loop-guard trips to the Psych Ward (DECIDED). A single short redirect, capped at one per turn, can come first. The Psych Ward step stays as the second tier, unchanged. The cap becomes a setting with a floor, not a loosening of the stop rule.

### K. Tool pruning from observed use (doc 17)

From TokenEconomizer. An agent's tool list may shrink from observed use, through the server's allowlist, never by editing the agent's settings files. Pruning never removes a Tier 2 class or an approval-gated tool. Every change is audit-logged, and restore is one call. Measure first (see M).

### L. Programmatic tool calling for batch work (doc 17, optional)

The Code/PTC preset is not confirmed in the primary repo. The idea itself is that the model writes one short program that calls several tools, not one round trip per call. Evaluate it in doc 17's benchmark (doc 34), not as a default. Any such program runs under the same path jail, timeout and approval gates as shell.

### M. Measure cost with cache pricing, and count the overhead (doc 23, doc 06)

- Report cost with cache pricing, not raw tokens. Our manifest records ids and hashes, so per-section tokens can be measured per turn (retrieval doc, section 2).
- Count our own review, learning and summary passes in any saving claim.

### N. OpenAI-compatible base URL (doc 05)

DSH's routers point a base URL at an OpenAI-compatible endpoint. Bazaar's gateway already treats providers as interchangeable upstreams (doc 05). No change needed. This only confirms the pattern.

## Not adopted

- **Auto-merge by the agent.** dsh-superagent lets a worker auto-merge a small, approved PR by default. Our rule is that the owner squash-merges (CLAUDE.md, C25). The size threshold (under 200 changed lines) is a useful signal for a review-priority hint, but not for merge authority.
- **Blocked-commands list as a safety boundary.** The docs say it is not one. Our design enforces tool classes and path jails in the server (doc 17). Do not copy the blocklist model.
- **Workers running commands without approval as the OS user.** Our routine internal work needs no approval, but the coding VM is isolated and the server enforces the gates (doc 17). Keep that.
- **Retention without bounds.** dsh-superagent lets records grow without limit. Our GC log is append-only and never purged (DECIDED), but we still need a retention rule for telemetry and eval traces. Not a copy target.
- **Cordis plugin framework and the Node web UI.** Bazaar is .NET and uses its own provider interfaces (doc 03).
- **Learned state kept on the developer's machine.** Our learned state lives in the Vault and Postgres, under the server's audit and privacy rules.
- **Any savings figure as a target.** The authors' numbers are not Bazaar's workload. We set no cost target from them.

## Evidence gaps

- The 30%-more-router-decisions and 6.4-to-1.6-second figures are from a single secondary write-up. They are not in the primary repo and are not used.
- The MarkTechPost article was blocked (403), so nothing from it is used except through the secondary write-ups.
- The three DSH presets other than `standard` are not confirmed in the primary repo.
- The 30.9% TokenEconomizer figure is the authors' own SWE-Bench result. We have no independent replication.

## Owner questions

1. Should idea A (bounded context defaults) and idea B (a reserved human slot) go into a PROPOSED change to doc 06? Recommended: yes, as defaults only, with our floors unchanged.
2. Should idea I (repo-fact promotion after N confirmations) be drafted for doc 08 after its next revision? Recommended: yes.
3. Should idea K (tool pruning) wait until the per-section token metric (M) exists? Recommended: yes.
