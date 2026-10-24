# Bazaar External Identity: Email, Accounts, Browser Automation, Credential Storage (as of 2026-10-24)

Scope: external identity for Bazaar agents (email addresses, service sign-ups, browser automation, Soul Passport storage, OAuth/MFA, social-media guardrails). Design context from `Bazaar.md` (external email is for external comms only; agents sign up for external services using Soul identity) and `Bazaar.Soul.md` (Soul Passport must live outside the VM disk; host is Windows-only .NET behind a residential connection).

**Method and limits.** Web search hit a rate limit after its first batch in this session, so most claims come from direct fetches of primary pages (Resend pricing, Cloudflare Email Routing docs, Google Workspace bulk-sender help, Google Terms, Microsoft Learn, RFC 8628, GitHub READMEs). Anything marked **[memory]** is from training knowledge and was NOT verified in this session. Anything marked **[secondary]** came from a search snippet or third-party blog. Not legal advice; legal points are flagged for counsel review.

**Domain caveat.** The design docs give several domains (`firev.xx.kg`, `bazaar.firev.xx.kg`). The domain question is still unresolved in the design docs; the recommendation below assumes it gets replaced.

---

## Key Question 1: Self-hosted mail on a Windows host behind residential internet vs. a relay/transactional service. Recommend one.

### Takeaway
Do not run the MTA on the home Windows host. Use a hosted inbound/outbound path: Cloudflare Email Routing (inbound, forwarding to a Worker/webhook) plus Resend (outbound via HTTPS API, free tier first). Port 25 blocking, PTR/reverse DNS, and Gmail's bulk-sender rules make home-hosted outbound SMTP the highest-maintenance option. If self-hosting is still wanted, run Stalwart (or similar) on a cheap VPS, not on the home connection.

### Cited Findings
- Residential ISPs commonly block outbound TCP 25 by default; the usual advice for self-hosted senders is to relay through an authenticated provider on port 587. [secondary] — [Port 25 blocking overview (getmailbird)](https://www.getmailbird.com/isp-blocking-port-25-email-solutions/); [Port 25 blocking diagnosis (captaindns)](https://www.captaindns.com/en/blog/port-25-blocked-diagnosis-solutions). Both are third-party; check the actual ISP policy before deciding.
- Gmail bulk-sender requirements (apply above 5,000 messages/day to Gmail; requirements took effect 2024-02-01 per the page): SPF and DKIM with DMARC alignment; the sending IP needs a PTR record that forward-resolves to the same IP; TLS required; spam rate below 0.30% in Postmaster Tools (guidance says below 0.10%); one-click unsubscribe for marketing/subscribed mail. — [Google Workspace bulk sender guidelines](https://support.google.com/a/answer/81126) (fetched 2026-10-24; page shows "©2026 Google", no separate last-updated date).
- Residential IPs typically have generic or missing PTR records and sit on large dynamic-IP blocklists. Inference from the PTR requirement above; not separately verified for any named ISP. **[memory]**
- Resend pricing (fetched from resend.com/pricing, 2026-10-24): Free $0, 3,000 emails/month, 100/day, 3 domains. Pro $20/mo for 50,000 emails or $35/mo for 100,000 emails, 10 domains, no daily limit. Scale $90/mo for 100,000 emails, rising to $1,150/mo for 2,500,000; 1,000 domains. Pay-as-you-go overage per 1,000 emails. Optional +100 domains add-on $20/mo on Pro/Scale. — [Resend pricing](https://resend.com/pricing). Third-party sites disagree on some tiers; trust the official page only. Pricing is dated 2026-10-24.
- Cloudflare Email Routing (fetched from developers.cloudflare.com/email-routing/limits, "Last updated Sep 25, 2026"): routing rules 200 per domain; destination addresses 200 per account; inbound message size 25 MiB; each rule forwards to one destination or one Worker (multi-destination needs a Worker calling `forward()`). Outbound sending IS supported via REST API, Workers binding, and SMTP, but only from your routing domains. Before a sending domain is onboarded, you can only send to verified destination addresses. Sends to verified destinations are free and do not count toward quota on any plan. New accounts start with conservative daily sending limits that rise over time; increases via a request form. — [Cloudflare Email Routing limits](https://developers.cloudflare.com/email-routing/limits/). The fetched page does not list a price for Email Routing and does not describe catch-all limits; both are **[memory]/unverified**.
- Stalwart Mail Server (GitHub README fetched 2026-10-24): install guides for Linux/macOS/FreeBSD, Windows, and Docker are listed. Dual-licensed AGPL-3.0 and Stalwart Enterprise License v2. The GitHub releases section was empty in the cached fetch, so the latest release date is unknown. — [stalwartlabs/mail-server](https://github.com/stalwartlabs/mail-server). Windows support is documented, but it has not been tested in this session.
- MailKit (fetched 2026-10-24): MIT license; targets .NET 8+ and .NET Framework 4.6.2+; provides SMTP, IMAP4rev1/rev2, and POP3 **clients** only. It has no server component. — [jstedfast/MailKit](https://github.com/jstedfast/MailKit). Use MailKit as the .NET client for IMAP/SMTP against any server; it is not a mail server.
- hMailServer: the homepage fetch returned HTTP 403, so nothing verified. **[memory]** It is a Windows-only, open-source SMTP/IMAP server; its maintenance cadence has been slow historically. Re-verify before relying on it.
- Mailcow: **[memory]** Docker-based and documented for Linux hosts only. Not verified in this session.
- SmtpServer (.NET library) and similar: **[memory]** these are server-side .NET libraries that can accept SMTP; they are not complete mailboxes and carry the same port/PTR/reputation issues. Not verified.

### Inferences
- Recommended architecture (no inbound ports or outbound port 25 on the home host):
  1. **Inbound:** a domain on Cloudflare DNS with Email Routing per agent address (or catch-all) → a Cloudflare Worker that POSTs the message over HTTPS (443) to a Bazaar endpoint on the home host, or to a queue/mailbox the host polls. The home host never accepts inbound SMTP.
  2. **Outbound:** Resend API over HTTPS (443). Start on Free ($0; 100/day cap is fine for "signup/notification" volume). Move to Pro ($20/mo) when volume or daily cap requires it.
  3. **Tooling:** MailKit only if IMAP access to a hosted mailbox is needed; otherwise the Worker/webhook path is enough.
- Self-hosting the full MTA on home Windows would require: working outbound 587 relay or unblocked 25, a PTR record the ISP will set (usually not available on residential), IP reputation maintenance, and ongoing DKIM/DMARC/TLS work. Treat as high maintenance and low deliverability. The one advantage is full control and no per-message cost.
- If the boss insists on self-hosting, put Stalwart (AGPL-3.0; Windows install documented) on a small VPS with PTR control, and keep the home host as a client only.
- Hosted mailboxes (e.g. Fastmail, Migadu, Google Workspace) are a middle option if agents need a real IMAP inbox for account-recovery emails. Prices not verified in this session.

### Gaps
- Cloudflare Email Routing price and catch-all behavior: not on the fetched page. Verify before commit.
- Outbound deliverability for a new domain on Resend: no deliverability benchmark verified. Gmail/Outlook will still apply their own reputation logic to a new domain.
- Whether residential ISPs in the user's region block 25 and whether they will set PTR: depends on the user's ISP; not checked.
- Microsoft Outlook.com sender rules (2025+): not fetched; treat as unverified.

---

## Key Question 2: Is an xx.kg free-subdomain registry viable for mail and OAuth/signup flows? Recommend an alternative.

### Takeaway
No, do not build external identity on xx.kg. The domain is a delegated subdomain under a free registry, its pricing status is contradictory across sources, free registries are heavily abused, and some OAuth/signup flows need a verified, stable domain. Buy a cheap own domain (a ~$10–$15/yr `.com`/`.dev`-class domain is typical; price not verified here) and point its DNS at Cloudflare.

### Cited Findings
- DigitalPlat FreeDomain (operator: EdgeAlphix LLC per secondary source) offers `*.xx.kg` subdomains; the operator's domain page lists them as available and says "Free to start." — [DigitalPlat Domains](https://domain.digitalplat.org/) (search snippet) and [DigitalPlat FreeDomain GitHub](https://github.com/DigitalPlatDev/FreeDomain) (search snippet).
- Pricing conflict: a forum post says the operator provides one-time payment for `*.us.kg` and `*.xx.kg`, which contradicts "free to start." The conflict is not resolved here. — [LowEndTalk thread](https://lowendtalk.com/discussion/208949/open-source-free-domain-for-everyone-by-digitalplatdev) (search snippet; contents not fetched).
- A forum commenter says these are subdomains and cannot be added to Cloudflare as zones; another user reported success with a related extension. Unverified either way. — search snippets only.
- Do not confuse `.xx.kg` with `.xo.kg` (Intent Panel zone). Separate service. — [Intent Panel](https://panel.int.yt/) (search snippet).

### Inferences
- **Mail reputation:** free-registry domains are common in spam and phishing, so receiving MTAs may score them lower and some providers block them outright. Inference; no measured data gathered here.
- **OAuth/signup:** Google's OAuth consent-screen branding and app verification require a domain the developer owns and can verify (via Search Console). A delegated free subdomain can be verified only if the registry lets you publish the required DNS records and the provider accepts it. Unverified for xx.kg specifically. **[memory]** on Google's verification rules; re-check before depending on it.
- **Stability:** a free registry can drop, change terms, or delist a subdomain. Agent identities (email addresses, OAuth redirect URIs, passports) should not depend on a domain you do not control contractually.
- **Recommended alternative:** one cheap own domain registered at a reputable registrar, DNS on Cloudflare (Email Routing inbound, Resend DKIM/SPF outbound), with one sub-zone per Soul if needed (`claude@`, `gemi@` on the same domain; Bazaar's addresses are just mailbox names). Renewal is a single calendar item. Price to be checked at purchase time.

### Gaps
- Actual xx.kg pricing and delegation terms: unresolved. Read the operator's current terms page before deciding, and treat the docs' "free" claim as unverified.
- No direct test of Resend or Cloudflare with a `.xx.kg` delegation.
- No data on Gmail/Outlook acceptance rates for free-registry sending domains.

---

## Key Question 3: Automated sign-ups, CAPTCHA, browser automation, and the trust ladder

### Takeaway
Most large services forbid automated account creation through their terms and anti-abuse systems, and bypassing CAPTCHA or protective measures is generally treated as a violation. Google's current terms (effective 2026-07-30) prohibit "using automated means to access content" and "bypassing our systems or protective measures," and list "creating fake accounts" as deceptive use; they do not have an explicit "no automated sign-up" clause in the fetched text. Recommend: Playwright (Microsoft.Playwright) with a persistent per-agent profile for *normal* sign-ups the human approves; CAPTCHA, phone/ID verification, and ToS click-throughs are handed to the human, never solved by a service. The trust ladder below sets approval gates.

### Cited Findings
- Google Terms (effective July 30, 2026): prohibits "using automated means to access content from any of our services" and ignoring robots.txt-style rules; prohibits "spamming, hacking, or bypassing our systems or protective measures"; lists "creating fake accounts or content" as a deceptive use. No clause specifically on automated account creation was found in the fetched text. — [Google Terms of Service](https://policies.google.com/terms) (fetched 2026-10-24).
- Stack Overflow's public terms could not be fetched (fetch error). Its terms on automated access and machine accounts are **unverified**. Check directly before any Stack Overflow action.
- Microsoft.Playwright: **[memory]** the official .NET package; supports Chromium/Firefox/WebKit; persistent contexts via `BrowserType.LaunchPersistentContextAsync(userDataDir, options)`. The Playwright .NET browser-contexts page fetched in this session did not cover persistent contexts, so this API name is unverified here. Check https://playwright.dev/dotnet/docs/api/class-browsertype before coding.
- Persistent profiles per agent: a persistent `userDataDir` keeps cookies, local storage, and login state between runs. This is the normal way to keep a logged-in session for an agent, but it also means the profile holds session tokens; store the profile on the host, outside the VM disk, consistent with `Bazaar.Soul.md`. **[memory]** on the mechanics.
- Google's Terms and Google's bulk-sender guidance say nothing about CAPTCHA services. Using paid human-solve CAPTCHA services is a separate question; see "Inferences."

### Inferences
- **Captcha solving:** the terms of most target sites treat CAPTCHA bypass as "bypassing protective measures." Paid human-solve or AI-solve services are a likely ToS violation for the target and may raise computer-misuse or contract issues depending on jurisdiction. Not legal advice. Recommendation: do not integrate captcha-solving services. When a CAPTCHA or phone/ID check appears, the agent stops and pings the boss (human-in-the-loop), and the boss completes it.
- **Ethics of sign-ups:** creating accounts that represent a real person or organisation, or that pretend to be human when the platform requires a human, is a clearer violation than an agent account openly labeled as an AI/Bazaar account. Disclose agent identity in the profile and in posts where the community allows it.
- **Trust ladder (proposed; for Boss approval):**
  - **Tier 0, autonomous (no approval):** read-only browsing of public pages; reading own inbox; replying to already-approved threads; internal GC only.
  - **Tier 1, autonomous with notice:** free sign-ups on a whitelist of services whose terms allow accounts (e.g. documentation sites, forums that permit bot-labelled accounts). Agent notifies the boss in the GC after the fact. No payment, no phone number, no ID.
  - **Tier 2, boss approves before action:** any sign-up requiring a phone number, payment, identity documents, acceptance of new legal terms, or OAuth grants with write scopes; any public post or report on a community site; any new domain/service not on the whitelist.
  - **Tier 3, boss performs it:** CAPTCHA, SMS/phone verification, ID verification, passkey enrolment, recovery-code setup, and any account that would represent a real person or company.
  - **Forbidden (no tier):** CAPTCHA-solving services; SMS-verification rental services; sign-ups on services whose current terms forbid automated accounts; creating accounts in another person's name; scam-bait replies that engage real victims (see Key Question 5).

### Gaps
- Platform-specific policies (Reddit, Discord, Stack Overflow, GitHub, Roblox DevForum, Godot Forum) not verified in this session; search was rate-limited and Stack Overflow fetch failed. Each needs a direct check before onboarding.
- Playwright persistent-context API details: unverified (see above).
- Legal status of CAPTCHA solving in the user's jurisdiction: not researched; needs counsel.

---

## Key Question 4: Storing the Soul Passport (secrets outside the VM disk)

### Takeaway
Use envelope encryption: each secret is encrypted with a per-Soul data key; the data key is wrapped by a host master key that is protected by Windows DPAPI (machine scope) or a dedicated secrets service; ciphertext lives in a host-side database outside the VM disk; the agent never reads raw secrets; a separate Identity Service decrypts and injects only what a request needs, with an append-only audit log. DPAPI-NG adds AD-group/web-credential principals and is overkill for a single host. Windows Credential Manager is a per-user store and is not a multi-agent registry. OpenBao is a reasonable self-hosted vault if its Windows support is confirmed.

### Cited Findings
- DPAPI (classic): encrypts data for the current user or the machine; protects against other users on the same machine, not against code running as that same user. [Microsoft Learn: CNG DPAPI](https://learn.microsoft.com/en-us/windows/win32/seccng/cng-dpapi) (fetched 2026-10-24; page dated 2023-06-06, updated 2025-04-15).
- DPAPI-NG (`NCryptProtectSecret`): built on CNG; protects secrets to a set of principals — currently listed as "a group in an Active Directory forest" and "web credentials." Reuses protection descriptors. Introduced in Windows 8. — [Microsoft Learn: CNG DPAPI](https://learn.microsoft.com/en-us/windows/win32/seccng/cng-dpapi). The fetched page did not list descriptor syntax (e.g. `LOCAL=user`); check the Protection Descriptors page before coding.
- Windows Credential Manager: the Microsoft Learn page fetched is about *credential manager providers* (MPR network-provider entry points), not the user-facing vault. It is a per-user credential store; for this design it is an awkward fit (no multi-tenant structure, no audit, tied to user profile). [Microsoft Learn: Credential Manager](https://learn.microsoft.com/en-us/windows/win32/secauthn/credential-manager) (fetched 2026-10-24; page dated 2018-05-31, updated 2025-03-12). Mismatch between page and expected user-facing content noted.
- OpenBao: MPL-2.0 license per the repo page; the README documents `go build -o bin/bao .` and dev-mode server run, with no Windows install section. Windows server support is **not documented on the fetched page**; a Go build on Windows is plausible but unverified. [openbao/openbao](https://github.com/openbao/openbao) (fetched 2026-10-24; no releases section content). HashiCorp Vault and Azure Key Vault pricing not checked in this session. **[memory]** HashiCorp Vault moved to the BSL license in 2023; verify current terms before choosing it.
- age / SOPS: **[memory]** age is a simple file-encryption tool; SOPS supports age, KMS, and PGP backends. Neither was verified in this session.

### Inferences
- **Threat model:** on a single Windows host, DPAPI (machine scope) protects the master key from other OS users and from copying the disk; it does NOT protect against administrator access or code running as the service account. The Identity Service must run as a separate low-privilege account, and agent VMs must have no access to the host folder that holds ciphertext or the key.
- **Envelope design:** per-Soul data key → wrapped by host master key (DPAPI machine scope, or OpenBao transit key if self-hosted) → ciphertext rows in a host-side SQLite/Postgres DB. Rotation = re-wrap data keys, not re-encrypt all rows.
- **Passport fields with extra care:** TOTP seeds and recovery codes are second factors; storing them next to the password defeats MFA against anyone who gets the Passport. Treat them as highest-sensitivity and gate them behind Tier 3 (boss-only) access where possible.
- **Audit:** append-only log of every decrypt/use (who, which Soul, which service, when, why), written to a host-side store the agents cannot write to. This matches the "auditable reasoning" standing ask in `Bazaar.Soul.md`.
- **Do not let agents hold passwords in the VM.** Inject into browser/session through the host-side Identity Service, or give the agent a short-lived token scoped to one service.

### Gaps
- DPAPI-NG descriptor syntax and whether it adds value on one host: not verified.
- OpenBao on Windows: not documented on fetched page; unverified.
- Azure Key Vault and HashiCorp Vault pricing and current licensing: not checked.
- Hyper-V VM-disk copy scenario (who can read a differencing disk) is covered by `Bazaar.Soul.md`; not re-analysed here.

---

## Key Question 5: OAuth device flow, MFA/TOTP, passkeys

### Takeaway
OAuth device flow (RFC 8628) is a good fit for agents running CLI tools (Gemini/GitHub-style auth), but it is a phishing target and must be approved by the boss, not automated. TOTP can be stored by the Identity Service, but the seed is a second factor and should be treated as highly sensitive. Passkeys are bound to a device or authenticator and are generally not exportable; agents cannot hold them safely without a deliberate sync design, so passkeys should stay with the boss or a hardware key.

### Cited Findings
- RFC 8628 device flow: device requests a device code and user code; user approves on a second device; device polls the token endpoint at an interval (default 5 s); `slow_down` requires adding 5 s to the interval; error codes `authorization_pending`, `slow_down`, `access_denied`, `expired_token`. Security section warns of remote user-code phishing and recommends rate-limiting and short code lifetimes. — [RFC 8628](https://www.rfc-editor.org/rfc/rfc8628) (fetched 2026-10-24).
- Google Terms (2026-07-30) prohibit bypassing protective measures — relevant to any MFA workaround. — [Google Terms](https://policies.google.com/terms).

### Inferences
- The device flow's user-code step is exactly where an agent could be tricked into sending a code to a scammer. Require: the agent only starts device flows the boss requests; the code is shown to the boss in the GC; the boss confirms the device.
- TOTP handling: Identity Service generates codes from the seed on demand only for Tier 1–2 services; Tier 3 (boss-only) for anything with money or identity. Never store recovery codes in an agent-readable place.
- Passkeys: the private key sits in a platform authenticator or security key; it cannot be copied into a Passport without a platform-specific export (which many services and platforms do not support). Practical answer: passkeys are boss-held or hardware-key-held; the agent uses password+TOTP only on services that still allow it.

### Gaps
- Which major providers still accept password+TOTP without passkey enrolment in 2026: not verified.
- Whether any platform permits passkey export to a non-human credential store: not verified.

---

## Key Question 6: Legal and ethical guardrails for agent accounts on social media, and scam-baiting

### Takeaway
Agent accounts are feasible if they are disclosed as AI/Bazaar accounts, follow each platform's bot rules, and avoid automation that the platform bans. The scam-baiting plan is the single highest-risk item: it can harm real victims, cause harassment or impersonation problems, and it exposes the agent to malicious content. Recommend dropping autonomous scam-baiting entirely; if kept at all, use a closed, human-approved, read-only triage mode.

### Cited Findings
- Stack Overflow terms: fetch failed; not verified.
- Platform bot rules (Reddit, Discord, GitHub, Roblox DevForum, Godot forum): not verified in this session (search rate-limited). Each requires direct check.
- Google Terms: prohibits "creating fake accounts or content" under deceptive uses. — [Google Terms](https://policies.google.com/terms).

### Inferences
- **Disclosure:** agent accounts should carry a clear AI/Bazaar label in profile and any post, and never claim to be a specific human. This also reduces impersonation risk.
- **Scam-baiting risks (for counsel and the boss):**
  1. **Harm to real victims:** a scam email may come from a real person being exploited; engaging them can cause distress, and the agent may collect their data.
  2. **Harassment and provocation:** replies intended to waste a scammer's time can read as harassment to the recipient, a third party, or the platform.
  3. **Evidence and reporting:** engaging can hamper law-enforcement reporting or give scammers a foothold (e.g. confirming a live address).
  4. **Malicious content:** scam emails carry links, attachments and prompt-injection text. The agent must never open links/attachments and must treat email body as untrusted data (the design should sanitise before any LLM sees it).
  5. **Platform/ISP risk:** a mail provider may suspend the domain for abuse-related traffic even if the agent is "only replying."
- **Recommendation:** no autonomous replies to scam mail. Quarantine and auto-label; report to the provider's spam/phishing channel where appropriate; optionally let the boss read a summary. Any reply is a Tier 2 boss-approved action with a written reason.

### Gaps
- Platform bot-disclosure requirements (e.g., whether a given forum permits bot-labelled accounts): unverified.
- Jurisdiction-specific law on automated replies to scammers and on impersonation: not researched; needs counsel.
- No information gathered on the boss's specific plan beyond the design docs.

---

## Overall recommendation (summary)

| Concern | Recommendation | Cost (dated 2026-10-24) |
|---|---|---|
| Inbound email | Cloudflare Email Routing → Worker → HTTPS webhook to host | Email Routing price not on fetched page; reportedly free (unverified) |
| Outbound email | Resend API (HTTPS 443), Free tier first | Free: 3,000/mo, 100/day. Pro $20/mo (50k) or $35/mo (100k). Scale $90/mo (100k) |
| Domain | Cheap own domain, DNS on Cloudflare, not xx.kg | Price not verified; check registrar |
| Self-hosted MTA | Avoid on home host; if required, Stalwart on a VPS | VPS cost not checked |
| Browser automation | Playwright (.NET) with per-agent persistent profile on host | Free (OSS) |
| Secrets | Envelope encryption; DPAPI machine-scope master key; host-side DB; Identity Service; append-only audit | No cost |
| Vault (optional) | OpenBao if Windows support confirmed; otherwise a DPAPI-based design | Free (OSS), Windows support unverified |
| CAPTCHA/phone/ID | Human-only (Tier 3); no solving services | n/a |
| Scam-baiting | Drop autonomous replies; quarantine and report | n/a |

## Open items to verify before build
1. Read Cloudflare Email Routing pricing and catch-all behavior on current docs.
2. Confirm xx.kg terms, or drop it.
3. Check each target platform's bot/automated-account policy (Stack Overflow, Reddit, Discord, GitHub, Roblox DevForum, Godot forum).
4. Confirm Playwright .NET `LaunchPersistentContextAsync` signature and behavior.
5. Confirm OpenBao or Stalwart Windows support with a test install.
6. Get legal review on: CAPTCHA services, scam-baiting, agent-account disclosure, and the Windows Server CAL question already listed in `Bazaar.Soul.md`.
