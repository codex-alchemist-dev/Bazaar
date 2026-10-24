# Windows Server Licensing and Hardware Sizing for a Single-Host Agent VM Fleet

Research date: 2026-10-24. Prices are USD, US locale. Confidence labels: **[PRIMARY]** = fetched from a Microsoft or Intel page this session; **[SECONDARY]** = reseller, consultancy, university or retailer page; **[UNVERIFIED]** = general knowledge or inference that I could not check this session. Several Microsoft pages I needed returned 403/404 or did not contain the rule, so the Gaps sections matter. This is research, not legal advice. Anything marked as a gap needs a Microsoft Licensing contact or an authorized reseller to confirm before you rely on it.

## Windows Server Standard vs Datacenter VM rights, pricing, and minimum cores

### Takeaway
Standard covers 2 Windows Server VMs per fully licensed host, and extra VM pairs need another full set of core licenses. Datacenter covers unlimited Windows Server VMs on a fully licensed host. Both require at least 16 core licenses per physical server, even on a 4-core NUC. At list price the crossover is about 11 VMs; Standard stays cheaper below that.

### Cited Findings
- Microsoft's US list price for a Windows Server 2025 16-core license is **$1,176 Standard** and **$6,771 Datacenter**. The page says prices "may vary from country to country", shows no date, and states "Datacenter and Standard edition pricing is for 16 core licenses." — [Microsoft Windows Server pricing page](https://www.microsoft.com/en-us/windows-server/pricing) **[PRIMARY, fetched 2026-10-24, no page date]**
- Both editions require Windows Server CALs; the page says CALs are "required for every user or device accessing a server." — [Microsoft pricing page](https://www.microsoft.com/en-us/windows-server/pricing) **[PRIMARY]**
- Each physical server, including single-processor servers, must be licensed with at least 16 core licenses. Each processor must be licensed with at least 8 core licenses. Core licenses are sold in packs of 2 and 16. — [Microsoft Windows Server licensing page](https://www.microsoft.com/en-us/licensing/product-licensing/windows-server) **[PRIMARY]**
- Standard includes two Windows Server Hyper-V-isolated OSEs/containers; Datacenter is described as "unlimited virtualization", not applicable to pay-as-you-go subscriptions. — [Microsoft Windows Server licensing page](https://www.microsoft.com/en-us/licensing/product-licensing/windows-server) **[PRIMARY, partial: the fetched text did not spell out "2 VMs per host, stackable" in those words]**
- Standard's 2-VM right is stackable: a fully licensed Standard host runs 2 Windows Server VMs; more VMs need additional Standard licenses covering all cores again (example: 32-core host, 6 VMs, six 16-core packs). Datacenter with a fully licensed host gives unlimited Windows Server VMs. Minimum 16 cores per server. — [Dell PowerEdge KB 000205142](https://www.dell.com/support/kbdoc/en-us/000205142/virtualization-rights-for-windows-server-editions) **[SECONDARY, vendor support article restating Microsoft rules]**
- Licensing by virtual machine: one license per virtual core, subject to a minimum of 8 licenses per VM. — [Microsoft Windows Server licensing page](https://www.microsoft.com/en-us/licensing/product-licensing/windows-server) **[PRIMARY]**. Applied at list price (my arithmetic: $1,176 / 16 = $73.50 per core; 8 cores = **$588 per VM**). Eligibility of this model for a given buying channel is **[UNVERIFIED]**.
- Reseller list for a Standard 16-core pack is $1,176 commercial, with education and nonprofit rates far lower; Virginia Tech lists $243 Standard and $1,394 Datacenter 16-core licenses (education pricing, not general). — [Redress Compliance](https://redresscompliance.com/windows-server-licensing-guide-2026.html), [Virginia Tech software store](https://software.vt.edu/deptsoftware/msproductsconsolidated/MSwindowsserver.html) **[SECONDARY]**
- One consultancy says Datacenter costs about 5.8x Standard at list and cites a crossover near 12 VMs on a 16-core host. — [Redress Compliance](https://redresscompliance.com/mastering-microsoft-windows-server-licensing-for-sam-professionals.html) **[SECONDARY]**

### Inferences
- Ratio check: $6,771 / $1,176 = **5.76x**. Consistent with the 5.8x secondary claim and the Soul doc's "roughly 5.5–6x".
- **Break-even at list price (my arithmetic on the primary prices):** Standard cost for N Windows Server VMs = ceil(N/2) × $1,176. Datacenter = $6,771.
  - N = 10: 5 packs = $5,880 (Standard cheaper)
  - N = 11: 6 packs = $7,056 (Datacenter cheaper)
  - So the crossover is **about 11 VMs**, not the 12 quoted by Redress. Counting only Windows Server guests; Windows client guests and Linux guests don't count toward this.
- For this NUC (4 cores), Standard at list is **$1,176 for a 2-VM host**, and **$6,771 buys unlimited Windows Server VMs** on the same host. A 16-core minimum applies either way.
- Important ceiling: the Soul doc's "Datacenter" plan is only economical for Windows Server guests. If most agents run Linux, Datacenter is not the right tier.

### Gaps
- Microsoft Product Terms (where the binding virtualization and stacking rules live) were not retrievable: the Product Terms page returned "not available on this date" and the Microsoft Learn Windows Server core-licensing page returned 404. The "stackable 2 VMs" rule is supported by Dell and secondary sources, not by a primary Microsoft fetch. **Confirm with Microsoft or a reseller.**
- Volume, OEM, and Software Assurance pricing for a single small host were not found. The $1,176 and $6,771 figures are list, not what a single buyer pays.
- Whether a NUC-class OEM or retail-box Standard license (vs. volume) carries the same VM rights was not verified.

---

## Do agent-only workloads need CALs, and which CAL model applies?

### Takeaway
Microsoft's stated rule is that Windows Server CALs are required "for every user or device accessing a server", with External Connector licenses for external users. No source I found addresses AI agents specifically. The agents are not humans, but whether a VM counts as a "device accessing" the host is not answered in anything I could fetch. The most defensible planning assumption is at least one User or Device CAL for the one human who uses the host, and a reseller opinion before deployment on the agent question.

### Cited Findings
- "Requires Windows Server CAL"; "CALs are required for every user or device accessing a server." — [Microsoft pricing page](https://www.microsoft.com/en-us/windows-server/pricing) **[PRIMARY]**
- Standard and Datacenter "still require Windows Server CALs for every user or device"; servers accessed by external users require External Connector licenses; pay-as-you-go subscriptions do not require CALs (RDS CALs are still required); Remote Desktop Services and AD RMS need additional Access Licenses. — [Microsoft Windows Server licensing page](https://www.microsoft.com/en-us/licensing/product-licensing/windows-server) **[PRIMARY]**
- The Product Terms list exceptions to the CAL rule; these were not retrieved. — same source **[PRIMARY, partial]**
- The CAL price was not on the Microsoft pricing page; this is a gap.

### Inferences
- Human-facing: the one person who connects to the host (RDP, SMB shares, admin tools) is at minimum one User CAL or one Device CAL, depending on how you access it.
- Agent-facing: an agent VM running Windows is its own OS instance, not a client of the host's server roles. Whether it counts as a "device" needs a written answer. If the agents only talk to Hyper-V through the hypervisor, no server role is accessed. If they reach host SMB, RDP, DNS, or AD services, CALs are likely.
- External Connector is the wrong model unless non-employees or external systems access the host. For a single-person setup it is not the likely fit.
- The Soul doc's open CAL question remains open.

### Gaps
- No source addresses AI agents or non-human "users" for CALs.
- CAL price (User or Device) was not found on a Microsoft page this session.
- Product Terms exceptions not retrieved.
- **Needs a Microsoft or reseller answer in writing before production.**

---

## Windows 11 Pro/Enterprise as the host or as guests; Windows 11 Pro as a 24/7 server host

### Takeaway
A Windows 11 license covers one instance on one device; each additional Windows 11 VM generally needs its own Windows 11 license or a VDA-type license. Windows Server guests need Windows Server licenses regardless of the host. Whether Windows 11 Pro may be used as a 24/7 host for server workloads was not verified, and I would treat it as a risk until Microsoft confirms it.

### Cited Findings
- A Windows 11 Pro license covers one instance on one device, physical or virtual; a second virtual instance needs its own license. — [Microsoft Q&A and licensing guidance, cited via Licensing School](https://www.licensingschool.co.uk/wp-content/uploads/2026/05/Windows-11-for-Virtual-Desktops-Licensing-Guide-13-May-2026.pdf) **[SECONDARY, guide quoting Microsoft]**
- Windows 11 VMs on your own server need Windows 11 Enterprise E3/E5 or Windows VDA E3/E5 licenses; each eligible licensed user may access up to four VMs. This guidance covers user access to virtual desktops, not agent VMs. — [Microsoft Windows 11 Licensing for Virtual Desktops](https://microsoft.com/licensing/guidance/Windows-11-Licensing-for-Virtual-Desktops) **[SECONDARY, search snippet]**
- Windows Server licenses (Standard: 2 VMs; Datacenter: unlimited) cover Windows Server guests. They do not cover a Windows 11 guest. — [Microsoft Windows Server licensing page](https://www.microsoft.com/en-us/licensing/product-licensing/windows-server) **[PRIMARY, partial]**

### Inferences
- A Windows 11 Pro host does not give Windows Server guests any rights. Windows Server guests on a Windows 11 host need their own Windows Server coverage, which in practice means a Windows Server license on the host (Standard/Datacenter) or a per-VM license.
- Windows 11 Pro on the NUC as a *server* workload (24/7, many headless VMs) is the kind of use the client EULA is typically not written for. Treat it as **not clearly allowed** until confirmed.
- Windows 11 Pro guests on a Windows 11 host each need their own Windows 11 coverage. An agent fleet of Windows 11 guests therefore scales per VM, not per host.

### Gaps
- Product Terms sections on Windows client as host or guest were not retrieved (the page returned no effective-date content).
- No source checked whether Windows 11 Pro may run 24/7 as a server host for headless guests.
- Whether Hyper-V is available on Windows 11 Pro: **[UNVERIFIED]** from general knowledge (it is, on Pro and above). Not checked this session.

---

## Windows Server evaluation edition: duration and rearm count

### Takeaway
The evaluation period is **180 days** (Microsoft, primary). Microsoft's pages do not state a rearm limit; secondary sources widely report **6 rearms (about 3 years total)** and some say 5. Treat the rearm count as unverified. Using evaluation media to run an ongoing fleet is a licensing question, not just a timing one.

### Cited Findings
- "(Note: This evaluation edition expires in 180 days.)" Evaluation editions must be activated over the internet in the first 10 days, or they shut down automatically. The page says "When you complete your evaluation, you can convert your evaluation versions to retail." No rearm or extension limit is stated. — [Microsoft Evaluation Center, Windows Server 2025](https://www.microsoft.com/en-us/evalcenter/evaluate-windows-server-2025) **[PRIMARY]**
- "Mainstream" rearm count: six rearms, each adding 180 days (~3 years). — [Woshub, Extend Windows evaluation period](https://woshub.com/extend-windows-evaluation-period-rearm/) **[SECONDARY]**; another guide says five rearms (six 180-day periods total) — [Petri](https://www.petri.com/how-to-extend-windows-server-evaluation) **[SECONDARY]**.
- Past the limit, `slmgr /rearm` fails with 0xC004D307 and a reinstall is required. — [Microsoft Q&A and guides, via search](https://learn.microsoft.com/en-us/answers/questions/389493/how-long-can-i-used-windows-server-evaluation-vers) **[SECONDARY]**
- One Microsoft Q&A answer claims 3 rearms adding 10 days each; this conflicts with the 180-day primary page and is probably wrong or about another edition. **[SECONDARY, conflicting]**
- Check the live count with `slmgr /dlv` on the machine. **[UNVERIFIED on this machine]**

### Inferences
- The Soul doc's "180 days, extendable a few times" matches the primary 180-day figure and the secondary rearm reports.
- Using evaluation to run a multi-year production fleet is not something the cited pages endorse. The Evaluation Center page's "convert to retail" wording points toward purchase after the trial, not a long-term evaluation.

### Gaps
- No Microsoft primary source gives the rearm count. The Microsoft Learn evaluation page returned 404.
- Whether the Product Terms restrict evaluation media for production use was not retrieved.

---

## Tiny11-style stripping and EULA risk; is Server Core already minimal?

### Takeaway
Stripping Windows 11 to create a Tiny11-type image is a modified Windows installation. Secondary sources say this likely breaches Microsoft's license terms and that circumventing activation is prohibited; Microsoft does not support such images. Windows Server Core is Microsoft's own no-GUI installation option and is already the lean choice for this use. I could not fetch the Server Core Learn page (404), so the Server Core claim rests on general knowledge.

### Cited Findings
- A license is still required, and "No modification bypasses Windows licensing or activation." — [The Register](https://www.theregister.com/a/5303034) **[SECONDARY, quoting a project author]**
- Modified installations "likely violate" Microsoft's licensing terms, which the article attributes to the EULA (not quoted). — [Windows News / secondary articles on Tiny11](https://windowsnews.ai/article/tiny11-can-a-stripped-windows-11-image-save-old-pcs-after-windows-10-end.419557) **[SECONDARY, low quality]**
- Tiny11 is unsupported by Microsoft; the project recommends building your own images rather than using prebuilt ISOs. — [Windows Forum thread](https://windowsforum.com/threads/tiny11-windows-11-builder-lean-isos-two-modes-25h2-ready.380042/) **[SECONDARY]**

### Inferences
- Server Core is the supported path: it is an official Microsoft edition, not a modification. Stripping a Server image further (Tiny11-style) would add EULA risk without a clear benefit.
- For Windows 11 guests, Tiny11 would add the same modified-install risk. Stick to a stock image.

### Gaps
- Microsoft's EULA text for Windows 11 and Server was not retrieved.
- The Server Core Learn page returned 404; "already minimal, no GUI, no shell" is **[UNVERIFIED]** from general knowledge.

---

## Realistic capacity on 32 GB: concurrent headless Server Core guests, CPU limits, and NUC RAM

### Takeaway
On a 32 GB, 4-core/8-thread NUC, plan for roughly **5–7 concurrently running headless Server Core guests** with 2 vCPU and 3–4 GB each, plus a few paused or saved ones. The 4 physical cores are the tighter constraint under real builds; RAM is the tighter one for many idle guests. The per-VM memory figures below are my estimates, not sourced measurements. Benchmark them on the target box.

### Cited Findings
- i5-1135G7: DDR4-3200 and LPDDR4x-4267 (some sources say 4266), dual-channel, max 64 GB, no ECC; CPU is soldered (BGA1449). — [Notebookcheck comparison](https://www.notebookcheck.net/i5-1135G7-vs-i3-1215U_12238_14098.247596.0.html) and [Intel Core i5-1135G7 spec listings](https://laptopmedia.com/?p=504997) **[SECONDARY; Intel ark pages redirected and were not fetched]**
- Core count: 4 cores / 8 threads, base 2.4 GHz, turbo up to 4.2 GHz (one listing 4.4 GHz, conflicting). — [StorageReview NUC 11 Pro review](https://www.storagereview.com/review/intel-nuc-11-pro-tiger-canyon-review) **[SECONDARY, conflicting on turbo]**. Intel ark page not retrieved.
- NUC 11 Pro (Tiger Canyon) takes **two DDR4 SO-DIMM slots**, **64 GB maximum**, with 32 GB maximum per slot per the LDLC listing; StorageReview's teardown shows 16 GB installed in two DDR4-3200 1.2V SO-DIMMs at the time of review. — [StorageReview](https://www.storagereview.com/review/intel-nuc-11-pro-tiger-canyon-review), [LDLC NUC11TNKI50002 listing](https://www.ldlc.com/en/product/PB00510163.html) **[SECONDARY]**
- Hyper-V dynamic memory and saved-state behavior: not retrieved this session.

### Inferences
- **RAM budget.** Reserve ~4 GB for the Windows host and Hyper-V. That leaves ~28 GB. At 3–4 GB per running Server Core guest with 2 vCPU, that is about 7 running VMs at most, with no headroom. Practical target: **5–6 running at once**, plus paused or saved ones.
- **CPU budget.** 4 physical cores / 8 threads. Five or six VMs at 2 vCPU each oversubscribes the physical cores 2.5–3x; fine for idle agents, slow for simultaneous compiles or Godot exports. Schedule heavy builds rather than running them in parallel.
- **Saved-state.** Hyper-V saved state writes each VM's RAM to disk, so the host needs free disk equal to the saved VMs' memory. Plan disk accordingly. **[UNVERIFIED, general Hyper-V knowledge]**
- **WSL2 inside each Windows guest.** WSL2 runs its own VM. Its default memory cap is a share of the guest's RAM, so each agent's real footprint is higher than the Server Core figure alone. Set `.wslconfig` limits. **[UNVERIFIED]**
- **RAM upgrade.** Upgrading to 64 GB (2 × 32 GB SO-DIMM) doubles agent capacity. Before buying, confirm the NUC's exact SKU and that 32 GB SO-DIMMs are available and supported; the 32 GB-per-slot figure is from a retailer listing, not Intel.
- **CPU upgrade is not possible.** The CPU is soldered, so no CPU swap. Core count is fixed at 4.

### Gaps
- No sourced per-guest RAM or CPU benchmarks for headless Server Core with agent workloads. The 3–4 GB figure is an estimate.
- Intel ark spec page not retrieved (redirected); the 4-core, 64 GB, 3200 figures come from secondary listings. Verify on the box with `systeminfo` / `Get-CimInstance Win32_PhysicalMemory` and Intel's own ark page.
- The user's "about 32 GB" and the CPU "unconfirmed" are still unconfirmed. Check the box before sizing.

---

## Alternatives that avoid Windows Server licensing for agent VMs

### Takeaway
The biggest cost lever is which guests are Windows. Windows guests carry a per-VM or per-host Microsoft license; Linux guests carry none. Linux guests can carry most agents, with Windows VMs only where Roblox Studio or Windows-only tooling is needed. The host OS license is separate and is a choice in its own right.

### Cited Findings
- The Soul doc states Roblox Studio has no official Linux client and that Godot has a headless mode and Luau tools (`luau-analyze`, Selene) run without Studio. I did not verify these in this session; they come from the Soul doc's [RESEARCHED] claims.
- Windows Server licensing is per host (Standard 2 VMs / Datacenter unlimited) only for *Windows Server* guests on a *Windows Server* host; for a non-Windows host, each Windows guest is licensed per VM (per-VM minimum 8 cores, per the Microsoft licensing page). — [Microsoft Windows Server licensing page](https://www.microsoft.com/en-us/licensing/product-licensing/windows-server) **[PRIMARY]**

### Inferences
- **Linux host + Linux guests:** no Microsoft license at all on the host or guests. Lowest cost; fits the Soul doc's Linux path.
- **Linux host + a few Windows guests:** Windows guests each need their own license. At list price, a Windows Server guest on a non-Windows host works out to roughly $588 per VM via the 8-core minimum (my arithmetic; eligibility unverified). Two or three Windows VMs can be cheaper than Datacenter.
- **Windows Server host + Linux guests:** the Windows Server host still needs at least one 16-core license ($1,176 list) even if almost every guest is Linux. Not free, but Datacenter is then not needed.
- **Windows Server host + Windows Server guests (up to 2):** $1,176 list covers both, plus CAL questions.
- **Windows Server host + many Windows Server guests:** Datacenter at $6,771 list is the economic choice once guest count reaches ~11.
- **Windows-only work:** keep a small number of Windows guests (Roblox Studio, GUI tasks) on a Windows host, and move the rest to Linux guests. This shrinks the Windows license count to the handful that actually need Windows.

### Gaps
- No source compared Linux-host economics end to end. The per-VM $588 figure needs eligibility confirmation.
- Hyper-V Server (the free standalone hypervisor) was not checked for current availability; check Microsoft's lifecycle page before relying on it.

---

## Method note

- Microsoft pages fetched: Windows Server pricing (fetched, USD list), Windows Server licensing (partial), Evaluation Center (180 days). Not retrievable: Microsoft Learn Windows Server core-licensing and evaluation pages (404), the Microsoft Product Terms (not available for the date queried), Intel ark (redirected, not followed).
- Search tool hit a rate limit during one query (Windows 11 Pro as host/guest), so that finding is weaker than the others.
- Secondary sources (Dell, Redress Compliance, Licensing School, StorageReview, LDLC, Notebookcheck, Woshub, Petri, The Register) were used where primaries were unavailable, and are labeled as such.
- Arithmetic (break-even, per-core and per-VM cost, RAM budget) is mine, built on the verified list prices; I show it so it can be re-checked.

## Priority items to confirm with Microsoft or a reseller
1. The "2 VMs per host, stackable" and Datacenter VM rights in the current Product Terms.
2. Whether agent VMs trigger CAL requirements, and the CAL price.
3. Whether Windows 11 Pro may run 24/7 as a host for headless guests.
4. Whether evaluation media may be used for an ongoing fleet, and the rearm count.
5. Per-VM licensing eligibility for Windows Server guests on a non-Windows host.
6. Current pricing for the buying channel you would actually use (volume, OEM, or retail).
