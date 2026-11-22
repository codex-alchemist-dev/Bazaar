# 92. Endpoint selection: choosing the best model endpoint per call

Status: PROPOSED (2026-11-22). Owner brief, 2026-11-22: any agent may have several endpoints selected, and the choice is dynamic, fast and "just gives the best in general"; borrow from the open-source routers; over-engineer. Nothing here is DECIDED.
Depends on: 05-model-gateway.md (routes, policy, quota, health), 91-claude-code-cli-provider.md, 45-onboarding.md section 1.15 (first agent).
Used by: 06-agent-runtime.md, 23-observatory-and-usage.md.
DECIDED rule kept: a route's fallback never leaves the agent's allowed set or its model family (doc 05). This doc is PROPOSED in full and widens nothing.

## Purpose

Doc 05 gives a route an ordered candidate set and three fixed strategies (`priority`, `round_robin`, `least_loaded`). This doc replaces "fixed order" with a **selector**: given the agent's allowed endpoints, the call's needs and what the gateway has measured, it ranks the endpoints and the gateway walks that ranking as its fallback chain. Policy still comes first: the selector only ever sees endpoints the agent's policy allows (doc 05), never widens it, and never uses a subscription login the gateway must not hold.

## Where it sits

`Bazaar.Server.Gateway.Selection` (engine) with `.Abstractions` (contracts). A pure library with no reference to Bazaar.Server, usable in any .NET project (extraction rule). The gateway adapts its provider, route and health records to the selector's own types. The route's `strategy` value gains `adaptive`; the three old values keep working.

## Pipeline per call

1. **Hard filters**, each with a reason kept for the trace: excluded by the caller, missing a capability the call needs (tools, vision, thinking, caching), context window too small, breaker open or health `Down`, blocked by quota until a time in the future.
2. **Tiers.** `priority` is a tier: the best non-empty tier wins, lower tiers are the fallback order. A profile may make tiers soft, so a much better lower-tier endpoint can win.
3. **Score** each survivor from normalized factors, weighted by the profile:
   - latency: peak-EWMA of recent latency, plus a penalty per in-flight call (the Finagle and Linkerd idea);
   - reliability: success rate with a Beta prior, so a new endpoint is neither trusted nor condemned on one sample;
   - quota headroom: how much of the rate or token window is left, so the selector backs off before a 429;
   - cost: display prices as a ranking signal only. Money budgets stay REJECTED; this never blocks a call;
   - identity order: `native` before `pooled` by the setting `gateway.claude.order`, as doc 05 says;
   - cache affinity: a bonus for the endpoint that served the same affinity key last, so prompt caching keeps paying;
   - quality prior per task class (an operator value, 0 to 1; no benchmark is built in).
4. **Strategy** turns scores into an order: `best` (highest score), `power_of_two` (draw two by weight, keep the better; spreads load without a herd), `thompson` (sample each endpoint's success from its Beta, so exploration shrinks as evidence grows), `weighted_random`. A small `explore_fraction` sends a call to the second choice so a recovered endpoint is noticed.
5. **Decision.** The full ranking, each score's breakdown, each rejection's reason, and an optional **hedge plan** (start the second endpoint after the top endpoint's p95 for calls marked safe to repeat). The trace goes to the gateway's usage row as a short code, never a prompt.

## Feedback

The gateway reports each call's outcome (success, error class, latency, tokens) to a stats tracker. It keeps decayed counters per endpoint (time-based, so an idle endpoint's old failures fade), a peak-EWMA latency, a p95 estimate, and an in-flight count. Quota headroom comes from the adapters' rate-limit observations (doc 05). The tracker is in memory; a restart starts from priors. Nothing it holds is a secret or a prompt.

## Profiles

`balanced` (default), `latency`, `reliability`, `cost`, `quality`. A profile is a set of weights plus a strategy; an operator may add more. The first agent defaults to `balanced` over every Claude endpoint the gateway lists (doc 45 section 1.15).

## Per-agent selection

An agent's policy lists the endpoints it may use (doc 05); the wizard and admin panel let the operator tick several and pick a profile. The selector ranks within that set per call, so the choice is dynamic while the allow-list stays the owner's.


## Gateway seam (how it plugs in)

The gateway resolves a route against policy first, which fixes the candidate list. A new optional port, `ICandidateRanker`, then **reorders** that list and may omit candidates; it can never add one. The gateway checks that every ranked entry was in the resolved list and ignores a ranker that returns anything else (fail safe: original order). So the same-family rule and per-agent policy hold by construction, and the selector needs no knowledge of either.

Which routes use it: a binding table (route id to profile name) held by the selector module, configured through settings. No new `gw_route.strategy` value is stored, so no migration is needed now (the column has a CHECK on three values and `common/` is frozen); an `adaptive` strategy value is a later migration that follows doc 04's rule for core-owned tables. Routes with no binding keep today's order.

Outcome feedback: the gateway reports start, first token, finish and failure code of every attempt, and the selector's tracker keeps its own counters. Breaker and quota facts stay owned by the gateway (doc 05) and are passed in as signals; the selector never duplicates them.

Hedging: a hedge plan is advice. v1 builds the plan; the gateway acts on it only after a later change adds concurrent attempts (doc 05 says no fallback once any event reached the agent, which the hedge must respect: first event wins, the loser is cancelled).

## Wizard contract (first agent)

The first-run wizard (doc 45 section 1.15) creates agent "Claude" with: a list of **source ids**, which are gateway provider ids (for example `anthropic-direct`, `claude-code`, and the FreeLLMAPI provider id), empty meaning every Claude-family source the gateway has; a **profile name**, default `balanced`. The server turns the source ids into the policy whitelist (every available `provider:model` of the Claude family on those sources), creates the route `claude` over them, and binds `claude` to the profile. Adding a source later extends the whitelist by an explicit operator edit only (doc 05: new models never join a whitelist automatically). The picker calls `IAgentSelectionSetup` (in `Bazaar.Server.Gateway.Selection.Abstractions`): `BindAsync(routeId, profileName)` and `ListProfilesAsync()`.

## What other routers contributed

Peak-EWMA and power-of-two choices (Finagle, Linkerd, Envoy), health and cooldown with fallback (LiteLLM, OmniRoute), weighted and latency-based routing (Portkey, LiteLLM), task-class quality priors (RouteLLM-style routing, as an operator prior only), Thompson sampling (bandit routers). Concepts only: no code or configuration is imported.

## Open questions

1. Task classes: a fixed list (chat, code, summarise, embed) or operator-defined. Default: fixed, extendable by setting.
2. Hedged calls cost twice. Default: off; per-call opt-in for safe, repeatable calls only.
3. Persisting the stats across restarts. Default: not persisted.

## Testing

Pure unit tests with an injected clock and random source: filters and reasons, tier order, each factor moves the score the right way, strategies are deterministic under a seed, power-of-two never picks the worse of its pair, Thompson favours evidence, stats decay, no endpoint ever appears that the caller did not pass in.
