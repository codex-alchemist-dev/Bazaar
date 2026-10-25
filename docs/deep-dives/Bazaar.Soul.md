# Bazaar.Soul — Identity System & Agent VM/Environment Architecture

Part of the Bazaar document family. Siblings: **Bazaar** · **Bazaar.Server** · **Bazaar.Windows** · **Bazaar.Android** · **Bazaar.Linux** · **Bazaar.Constellation**

Status legend: **[DECIDED]** / **[PROPOSED]** / **[REJECTED/CHANGED]** / **[RESEARCHED]** / **[OPEN]** — see Bazaar.md for definitions.

---

## 1. The Soul System — identity layer

- **[DECIDED]** Soul System: an agent's complete identity — VM, personality, memory, reputation, history, accounts, learning. "Everything that makes an agent itself." Functionally load-bearing — drives real behavioral/skill differentiation between agents, not decorative flavor.
- **[DECIDED]** Soul Passport: the identity/credential registry inside a Soul — GitHub identity, emails, OAuth grants, browser sessions, API keys, SSH keys, site accounts, cookies, MFA metadata. Closer to an enterprise identity vault than a password file. **Must live outside the agent's VM disk** — see Section 2's differencing-disk gotcha. **[DECIDED 2026-10-24, owner]** Passport data may be mounted into the VM as a per-agent scoped folder (each agent sees only its own). **[RESEARCHED — risk accepted by owner]** The security research recommended never mounting the Passport, because a compromised agent can then read all of its own credentials; the owner chose scoped per-agent mounting.
- **[PROPOSED]** Soul Marketplace — export/import/share Souls between Bazaar instances, trading specialized agents ("Charles from Bazaar.Main").
- **[PROPOSED, OPEN]** Identity Service — a backend service every subsystem routes credential/access requests through instead of each knowing about email/GitHub/OAuth/SSH separately. Not yet confirmed as adopted.
- **[DECIDED — Claude's standing ask]** Personality/Soul-driven judgment calls need their *reasoning* to be as auditable as any other decision, not just the outcome — same principle as Bazaar's general decision-log discipline.

---

## 2. Agent VM / Environment Architecture

*(Replaces the original "each agent just gets a VM" idea with a concrete design.)*

### The core design
- **[CHANGED 2026-10-24, owner]** Each agent gets **two VMs: a Windows VM and a Linux VM**, whichever OS the Bazaar server itself runs on. Both are headless by default; one of them can be configured as a fully headed OS on a more capable server. Files move between an agent's two VMs through a host-mediated SMB folder (proposal; details OPEN). **[RESEARCHED]** Two VMs cost more RAM per agent; nested WSL2 inside Hyper-V is a supported scenario but Microsoft says nested VMs are not suited to performance-sensitive work. How many agent pairs fit on the NUC is a sizing benchmark still to run.
- *[SUPERSEDED by the bullet above; kept for its reasoning]* Each agent's environment defaults to **Windows Server Core + WSL2**, not two separate VMs. Server Core gives Windows tooling with no GUI/shell overhead; WSL2 runs a real lightweight Linux kernel underneath it, giving full Linux tooling from the same virtualized unit. Windows chosen as the default (superseding an earlier "Linux-by-default, Windows-on-demand" proposal) because **Roblox Studio has no official Linux client at all** — Windows isn't just preferred for your actual target work (Roblox, likely Godot too, the client apps themselves), it's close to mandatory.
- **[DECIDED]** Storage efficiency via **differencing/overlay disks**, not independent full VM images per agent: one **golden base image** (Server Core + WSL2 + tooling, installed once), each agent gets a small **child disk** storing only what *that agent* changes (copy-on-write). Analogous to Docker image layers — shared read-only base + a writable layer per container. Mechanism: **Hyper-V differencing VHDs** on a Windows host, **QEMU/KVM with qcow2 backing files** on a Linux host — same architecture either way, which also answers "support both Windows and Linux hosts" without two different designs. **[CHANGED 2026-10-24]** The server build is OS-agnostic (owner): the VM backend is a provider — Hyper-V differencing disks on Windows (first supported host), QEMU/KVM with qcow2 on Linux. The 2026-08-07 "Windows-only for now" note scoped the first supported platform, not the architecture.
- **[DECIDED — real gotcha, designed around]** The golden/parent image can't be modified once children exist without invalidating every child — updating the shared toolchain requires a deliberate rebase, normally wiping each agent's personal in-VM customizations. **Consequence: Soul data (memories, reputation, the Passport, anything durable) must live outside the VM disk entirely**, in the agent's host-side folder/database record, mounted in as one scoped folder — not baked into the differencing disk's delta. Makes toolchain updates routine instead of risky; also reinforces security (credentials shouldn't sit on a disk the agent's own dev tools have full write access to).
- **[DECIDED]** Isolation: standard Hyper-V/KVM hardening — no clipboard sharing, no host drive passthrough beyond the one scoped mounted folder, enhanced session mode off. Zero host access by design, not convention.

### The GUI problem
- **[DECIDED, with reasoning]** Agents fundamentally cannot meaningfully "watch" a live game-engine viewport — even a vision-capable model only gets periodic screenshots (expensive, laggy), not continuous perception the way a human watching a screen does. A full desktop environment buys almost nothing for actual confirmation/testing.
- **[DECIDED]** Consequence: **default agent environment is headless** (Server Core, no GUI). Genuine GUI work (opening Roblox Studio, visually working a Godot scene) happens on **Bazaar.Windows**, where an actual human is looking — a deliberate division of labor, not a missing capability.
- **[PROPOSED, OPEN]** For the rare cases an agent's own VM genuinely needs a GUI session, a small **shared pool of GUI-capable sessions**, checked out on demand and released after — reusing the Work-Life Simulation resource-sharing philosophy. Not yet spec'd (pool size, checkout/queueing mechanics). **[DECIDED 2026-10-24 — owner]** Roblox Studio runs only on the human's Bazaar.Windows desktop; agents do headless Luau static checks and file sync. (Research: Roblox's official Studio MCP server requires an open Studio GUI session, and no source shows Studio running in a headless Server Core VM.) The pool is therefore not needed for Roblox; keep it only if another need appears.
- **[DECIDED]** **Godot** has a genuine `--headless` mode: compile errors, unit tests, even exports, no GUI required.
- **[RESEARCHED]** **Roblox/Luau** headless tooling: `luau-analyze` (Roblox's own official CLI static analyzer/type-checker) and **Selene** (community Luau linter) both catch syntax/type errors with zero Studio involvement. Real limit: anything calling actual Roblox APIs (`workspace`, `Instance`, game services) can't be exercised without the real client — this gap is symmetric with Godot, not Roblox-specific.
- **[RESEARCHED]** Neither WinUI 3 nor native OS-widget-based frameworks generally have a true "headless render to bitmap" story — see **Bazaar.Windows** for the Avalonia-vs-WinUI3 headless-testing comparison, which is related but distinct from this "can an agent watch a game engine" problem.

### Windows licensing
- **[RESEARCHED]** **Windows Server Standard** grants only 2 VMs per fully-licensed host (stackable in pairs). **Windows Server Datacenter**, once all physical cores are licensed, grants **unlimited VMs on that host** — roughly 5.5–6x the per-core cost of Standard, break-even over Standard-stacking generally cited around 5–12 VMs. Given the planned agent roster, Datacenter is very likely the economically correct tier once real, rather than per-VM Standard licensing.
- **[RESEARCHED]** A genuine **180-day free evaluation edition** of Windows Server exists, extendable a few times via rearm — legitimate for v0.001–v0.5 development, explicitly not a permanent production plan.
- **[OPEN]** CAL (Client Access License) requirements for a host mostly used by automated agents rather than human users connecting in — genuinely unclear; needs a direct question to a real Microsoft licensing reseller before real deployment. **[RESEARCHED 2026-10-24]** US list prices: $1,176 (Standard, 16-core) and $6,771 (Datacenter, 16-core), 16-core minimum per physical server; crossover roughly 11 Windows Server VMs. No source addresses how CALs apply to AI-agent VMs; get written answers from Microsoft or a reseller. Linux guests avoid Windows VM licensing.
- **[DECIDED]** Tiny11-style stripping isn't needed for Server Core — it's already Microsoft's own minimal, shell-less edition; the bloat Tiny11 targets in consumer Windows 11 mostly doesn't exist here.
- **[RESEARCHED, OPEN]** No Firecracker-for-Windows exists — Firecracker is hard-limited to direct Linux kernel boot. Closest relative supporting Windows guests: **Cloud Hypervisor** (same Rust-VMM lineage, boots real UEFI, explicitly supports Windows 10/Server 2019 guests, sub-second boot targets) — but multiple sources describe its Windows support as less mature than its Linux support. **[RESEARCHED 2026-10-24]** Cloud Hypervisor's documented hosts are Linux (KVM) or MSHV; it can run Windows guests, but no Windows-host path is documented, and the "sub-second boot" figure has no source. On a Windows host, Hyper-V is the only documented option. Firecracker is confirmed Linux-only.

---

## Open Questions (Soul)

1. **Identity Service** — proposed as the credential/access chokepoint, not confirmed as adopted.
2. **GUI session pool mechanics** — concept proposed, sizing/checkout/queueing logic is not.
3. **Cloud Hypervisor vs. Hyper-V for Windows guests** — needs direct testing before committing.
4. **CAL licensing for agent-only Windows Server usage** — genuinely unresolved, needs a real licensing consult.
5. ~~Roblox Studio placement~~ — resolved 2026-10-24: human desktop only.
6. ~~VM topology~~ — decided 2026-10-24: two VMs per agent (Windows + Linux). Still OPEN: per-agent RAM/CPU sizing and how many pairs fit on the NUC (benchmark), and the SMB sharing details.
