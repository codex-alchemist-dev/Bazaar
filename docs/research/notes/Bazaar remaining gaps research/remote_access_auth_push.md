# Remote access, client authentication, and push notifications for a home-hosted Bazaar server (as of 2026-10-24)

Scope: how Avalonia clients (Windows, Android foldable, Linux) reach a headless .NET server on the owner's home NUC behind NAT, authenticate, and receive notifications. Research budget was about 21 tool calls, so several points remain unverified; they are listed under Gaps.

Labels: **verified** = primary page read (via WebFetch, so the text is a model summary of the page, not a byte-for-byte copy); **secondary** = search snippet or third-party page; **unverified** = not confirmed in this session.

Recommendation in one paragraph: For v1, reach the NUC over a private overlay (Tailscale, or self-hosted Headscale) and do not port-forward. Authenticate devices with a short-lived pairing code (QR) that mints a per-device, revocable, rotated refresh credential; use the RFC 8628 device-code pattern when pairing a device that cannot easily type or scan. Use passkeys only for the owner's browser sign-in, and only if the owner accepts a sign-in page (see Risks). For Android notifications, send content-free "wake" pushes through FCM (default) or UnifiedPush/ntfy (Google-free option) and fetch the content over the authenticated connection; do not rely on a persistent foreground WebSocket service to stay alive. Revisit Cloudflare Tunnel + Access only if coworkers need access without installing a VPN client.

---

## 1. Reaching a home server from outside

### Takeaway
A private overlay network (Tailscale, or Headscale as a self-hosted control plane) is the lowest-risk way to reach a NAT-bound NUC from home and mobile data, with no inbound ports. Tailscale's free Personal plan fits a single owner but is explicitly "for non-commercial use", which matters if Codex Alchemist or other coworkers use the same tailnet as a business. Cloudflare Tunnel is outbound-only and gives a public HTTPS hostname, but it puts a third party in the TLS path and its WebSocket fit is not confirmed in the docs read. Port forwarding with dynamic DNS is the highest-exposure option and is not recommended.

### Cited Findings
- Tailscale Personal (free) plan: up to 6 users, unlimited devices, up to 3 ACL groups, 50 tagged resources included (extra $1/month each), 1,000 ephemeral-resource minutes/month; "nearly all of Tailscale's features" including ACLs, subnet routers, exit nodes, MagicDNS. **Verified** — [Tailscale pricing](https://tailscale.com/pricing)
- Tailscale paid per-seat pricing: Standard $8 per user per month, Premium $18 per user per month, Enterprise custom; you pay for seats including vacant ones. **Verified** (same page, read 2026-10-24; page shows no explicit date) — [Tailscale pricing](https://tailscale.com/pricing)
- Tailscale Personal is "free for non-commercial use"; tailnets on public email domains are auto-placed on Personal; tailnets on a custom domain are "treated as business use" and start a 14-day Enterprise trial. Non-profit/education special pricing exists. The page states no open-source licensing terms. **Verified** — [Tailscale pricing](https://tailscale.com/pricing)
- Cloudflare Tunnel: `cloudflared` creates outbound connections only; you can block all inbound traffic at the firewall. Connectors per tunnel can be multiple. **Verified** — [Cloudflare Tunnel docs](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/)
- The same Cloudflare Tunnel page says Tunnel can connect "HTTP web servers, SSH servers, remote desktops, and other protocols"; it does **not** mention WebSocket support, free-plan terms, or restrictions on media-heavy traffic. **Verified (absence)** — same page
- Cloudflare Access supports service-token rules ("Any Access Service Token" or a specific Service Token rule requiring the right headers), which do not need an IdP login; the page does not mention WebSockets or .NET clients. **Verified (page read)**, WebSocket compatibility **unverified** — [Cloudflare Access policies](https://developers.cloudflare.com/cloudflare-one/policies/access/)
- Tailscale and Headscale self-hosting community recommendations for 2026: **not verified** (see Gaps).

### Inferences
- A tailnet is per-user seated; if coworkers join as humans, Tailscale's Personal plan (6 users, non-commercial) may stop fitting and Standard at $8/seat becomes the cost line. Headscale avoids seats but moves operations to the owner.
- Tailscale's free tier terms create a legal-review item for any commercial use. The owner should confirm which plan applies before coworkers join. Not legal advice.
- Cloudflare Tunnel without a WebSocket guarantee is a risk for the realtime path; test it before choosing it. Its Access service-token model fits machine-to-machine clients better than a per-human login.
- Port forwarding plus dynamic DNS exposes the NUC's WebSocket port to the internet. Mobile carriers may use CGNAT, which would block inbound connections entirely (carrier-specific, **unverified**).

### Gaps
- Tailscale latency numbers and DERP relay behavior: not read. Direct vs relayed latency is unverified.
- Headscale licence, maturity, and community consensus: not read.
- Whether the Tailscale Android/Windows/Linux apps coexist with a .NET client that opens a WebSocket to a tailnet IP: assumed normal OS networking, **unverified**.
- Cloudflare Tunnel free-plan limits and any ToS restriction on large non-HTML/media traffic: not found in the page read; **unverified**.
- Cloudflare Access free-plan user limit: not stated in the page read.
- WireGuard raw setup, ZeroTier terms, and the 2026 self-hosting community consensus: not researched.

---

## 2. Client authentication (pairing, device flow, passkeys, tokens, TLS)

### Takeaway
Use an operator-initiated pairing flow: the server shows a short-lived code (or QR) on the NUC or status page, the client redeems it once, and the server issues a per-device credential that can be revoked and rotated. RFC 8628 (device authorization grant) is a standard fit for "approve this device from my other device" pairing and explicitly anticipates QR-style `verification_uri_complete`. ASP.NET Core Identity has built-in passkey support, but it is browser-oriented and scoped to authentication, so it is a sign-in tool for the owner's browser, not a native-client credential.

### Cited Findings
- RFC 8628 (Proposed Standard, August 2019): for devices without a suitable browser or text input; the client shows `user_code` and `verification_uri`; the user approves on another device. `verification_uri_complete` embeds the code for QR-style sharing; clients must still display `user_code`. Default poll interval 5 s; `slow_down` increases interval by 5 s. **Verified** — [RFC 8628](https://datatracker.ietf.org/doc/html/rfc8628)
- RFC 8628 security considerations: remote phishing of `user_code` (confirm the device is in the user's possession); rate-limit user-code attempts because short codes have low entropy; device codes must be high-entropy. **Verified** — [RFC 8628](https://datatracker.ietf.org/doc/html/rfc8628)
- ASP.NET Core 10 passkeys (WebAuthn, FIDO2) are built into ASP.NET Core Identity; the page says the implementation "is deliberately scoped to authentication scenarios. It isn't intended as a general-purpose WebAuthn library." Requirements listed include "A modern web browser that supports WebAuthn." Page updated 2026-10-13, monikers include aspnetcore-10.0. **Verified** — [ASP.NET Core passkeys](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/passkeys/?view=aspnetcore-10.0)
- Passkey options: `IdentityPasskeyOptions.ServerDomain` (RP ID; defaults to server origin if null), `AuthenticatorTimeout` default 5 minutes (a browser hint), `ChallengeSize` default 32 bytes. The RP ID is inferred from the Host header if `ServerDomain` is unset; the page says the host header must be validated. **Verified** — same page
- Passkey attestation state is "serialized as JSON without a signature or encryption, so it provides no integrity protection of its own"; apps calling `IPasskeyHandler` directly must store it securely. **Verified** — same page
- The browser enforces that a passkey works only on the domain (and subdomains) where it was registered. **Verified** — same page
- Community sources (not authoritative) disagree on the exact .NET 10 API names (`AddPasskeys`, `MapIdentityApi`). A secondary search snippet says passkey endpoints are added via `AddIdentityApiEndpoints`/`MapIdentityApi`; another says `AddPasskeys()` is a preview-era name. **Conflicting, unverified** — [Duende passkeys in .NET 10 Blazor](https://duendesoftware.com/blog/20251007-passkeys-in-dotnet-10-blazor-apps-with-aspnet-identity) (secondary)
- Android-side and Linux/Windows client token storage, mTLS, and TLS certificate options (Let's Encrypt, Caddy, Tailscale certs): **not verified**.

### Inferences
- Pairing design: the server generates a one-time pairing secret (QR), bound to a short TTL and a single redemption, and mints a device identity plus a rotating refresh credential. Store device tokens in Android Keystore and Windows DPAPI/credential store (platform APIs not verified here).
- Passkeys are a poor fit as the sole native-client credential because the ASP.NET flow assumes a browser (WebAuthn in JavaScript). A native Avalonia client would use the system browser for the owner's sign-in and then receive a server-issued token; the exact mechanism is unverified.
- The "no web client" rule (CLAUDE.md) allows a read-only status page. A passkey sign-in page would be an interactive browser surface. The owner should decide whether a login page counts as allowed or whether sign-in should happen in the native app with a pairing secret only.
- A .NET WebSocket server should authenticate the connection during the HTTP upgrade (token in a header or first message), not via cookies, to keep the design cookie-free. Not verified against a Microsoft source in this session.

### Gaps
- ASP.NET Core bearer-token auth (`AddBearerToken`) and the .NET 10 Identity API endpoint names: not verified (the Microsoft page with `MapIdentityApi`/`AddPasskeys` returned 404 on an earlier URL; the final passkeys page fetch confirmed the scope limits above but not the exact API names).
- WebSocket authentication patterns in ASP.NET Core: not read.
- TLS certificate options (Let's Encrypt via ACME, Caddy, Tailscale-issued certs): not researched.
- mTLS client certificate management tooling: not researched.
- Token rotation and refresh-token reuse-detection best practices (OAuth 2.0 Security BCP): not read.

---

## 3. Multi-user roles and per-GC access

### Takeaway
Not researched in this session beyond the constraints in the design docs. The owner, coworker humans, and agents need at least three principal types with per-GC membership; the policy engine choice (ASP.NET policy-based authorization vs OpenFGA vs Casbin.NET) is unverified.

### Cited Findings
- None verified in this session. The design docs cover the principal model at a high level: `docs/deep-dives/Bazaar.Server.md` and `docs/deep-dives/Bazaar.Android.md` (not re-read for this note; `docs/deep-dives/Bazaar.md` names GC coordination as DECIDED).

### Inferences
- Start with ASP.NET policy-based authorization over a per-GC membership table (principal, GC, role) in Postgres, because the GC log is already append-only and the data layer is Postgres (per `docs/implementation-plan.md`). Introduce a relationship-based engine (OpenFGA) only if per-object sharing grows beyond GC membership.
- DM access requests fit as an append-only request event in the GC log plus an owner decision event, which keeps the audit trail consistent with the GC-as-record rule.

### Gaps
- OpenFGA, Casbin.NET, and ASP.NET policy docs: not read.
- Coworker onboarding and per-GC permission model in the design docs: not verified in this note.

---

## 4. Android push notifications without a web client

### Takeaway
FCM is the default and is required for the stock ntfy.sh Android path, but it depends on Google Play services and sends the wake-up through Google. UnifiedPush with ntfy (self-hosted) is a Google-free alternative. Either way, the push should carry no message content; the app then fetches the @mention or decision over its authenticated connection. Android 15/16 limits make a permanent foreground WebSocket service an unreliable notification strategy.

### Cited Findings
- FCM Android client docs (last updated 2026-10-22 per the page): clients require Android 7.0+ devices with Google Play Store installed (or Google APIs emulator); apps should check for compatible Google Play services; the page says apps can be distributed outside Play. **Verified** — [FCM Android client](https://firebase.google.com/docs/cloud-messaging/android/client)
- The same FCM page says the registration token method is "deprecated. Use Firebase Installation IDs, as this method will be removed in a future release." Token can change on restore to a new device, reinstall, or clearing app data; `onNewToken` fires. **Verified** — same page
- FCM and legacy API shutdown date: not stated on the page read; FCM v1/v2 migration details **unverified** in this session.
- Doze: suspends network access, ignores wake locks, defers alarms (except `setAlarmClock`), doesn't let JobScheduler/WorkManager run until maintenance windows. Maintenance windows become less frequent over time; the system exits Doze when the device is moved, the screen turns on, or a charger connects. **Verified** — [Doze and App Standby](https://developer.android.com/training/monitoring-device-state/doze-standby)
- Doze page: FCM v1 used high-priority messages; v2 uses `RemoteNotification` for user-facing time-sensitive messages and `BackgroundSync` for non-urgent data sync; "We strongly recommend migrating to v2 as v1 is deprecated." In Doze/App Standby, `RemoteNotification` gives temporary network and partial-wakelock access. **Verified** — same page
- Battery optimization exemptions: `ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS` is limited to apps with an acceptable use case; Google Play policy prohibits requesting a direct Doze/App Standby exemption "unless the core function of the app is adversely affected." **Verified** — same page
- Foreground service types: since Android 14, each foreground service must declare a type plus the matching permission; types include `dataSync`, `remoteMessaging`, `specialUse`, and others. **Verified** — [FGS service types](https://developer.android.com/develop/background-work/services/fgs/service-types)
- `dataSync` limit: apps targeting Android 15+ may run `dataSync` and `mediaProcessing` foreground services for a total of 6 hours in a 24-hour period, tracked separately per type and shared by all the app's services of that type. When exhausted, `onTimeout` is called; the service must call `stopSelf()` within a few seconds; the app cannot start another `dataSync` FGS until it is brought to the foreground, otherwise `ForegroundServiceStartNotAllowedException`. **Verified** — [FGS timeout](https://developer.android.com/develop/background-work/services/fgs/timeout)
- Android 16 (API 36), regardless of target API: background jobs started from a foreground service must follow their runtime quotas, including jobs scheduled via JobScheduler, WorkManager, or DownloadManager. The docs recommend user-initiated data transfer (UIDT) jobs for transfers triggered by a user action; UIDT jobs are exempt from ordinary job quotas. **Verified** — [FGS changes](https://developer.android.com/develop/background-work/services/fgs/changes)
- ntfy Android app: uses Firebase only for the main host ntfy.sh; "For other hosts, instant delivery is used and FCM is not involved." FCM publishing (`firebase-key-file`) is optional and only for custom builds; "For a self-hosted instance, it's easier to just not bother with FCM." **Verified** — [ntfy configuration](https://docs.ntfy.sh/config/) (read in part; first 100k of 152k characters)
- ntfy access control: default is open to everyone; `auth-file` (SQLite, or PostgreSQL if `database-url` is set); `auth-default-access` options include `deny-all`, which the page recommends for a private instance; access tokens currently grant "full access to the user account" (granular tokens planned). **Verified** — same page
- ntfy getting-started: topic names are public and should be hard to guess. **Verified** — [ntfy docs](https://docs.ntfy.sh/)
- UnifiedPush Android distributors listed: ntfy (self-hostable), Sunup (autopush-rs, self-hostable), NextPush (Nextcloud app), Conversations (XMPP), gCompat-UP ("(Google)"). Gotify appears only under Linux. The page has no dates. **Verified** — [UnifiedPush distributors](https://unifiedpush.org/users/distributors/)

### Inferences
- Notification payload design: send `{type, gc_id, seq}` (no message text) so the push reveals only that something happened; the app fetches content over the authenticated connection. This matches the GC-log design in `docs/implementation-plan.md`, where clients replay from the log on reconnect.
- Persistent WebSocket: a `dataSync` FGS is capped at 6 h per 24 h when the app is in the background, so it cannot serve as an always-on notification channel. Use push as the wake signal and keep the socket open only while the app is foreground or unfolded (the Android design doc's unfolded ticker is a foreground case; not verified against the doc text here).
- If a Google-free stance is required (the owner's privacy goals are not stated in the docs read), use UnifiedPush with ntfy self-hosted and `deny-all` default access. Note the ntfy access token scope issue: a token grants full account access, so keep the device token separate from the ntfy account if you adopt ntfy.
- `remoteMessaging` is a valid FGS type per the docs ("Transfer text messages from one device to another"), but it is for cross-device message continuity, and Play policy fit for an agent chat is **unverified**. Do not assume it is approved for this use.
- Doze: high-priority FCM is deprecated; use the v2 `RemoteNotification` type for owner-facing @mentions and decisions. Testing: `adb shell dumpsys deviceidle force-idle` (from the Doze page) can simulate Doze on a dev device.

### Gaps
- FCM HTTP v1 requirement and legacy-API shutdown date: not confirmed in the page read (the page did not state them). Earlier knowledge suggests the legacy API was retired in 2024; **unverified here**.
- .NET for Android FCM binding (Plugin.Firebase or the Xamarin.Firebase.Messaging binding) and how Avalonia Android heads register for push: not researched; **unverified**.
- Exact Android 16 Doze/battery changes beyond the FGS quota change: no 2026-specific change found; a secondary article (ProAndroidDev, March 2026) reports OEM battery managers can kill foreground services; **secondary, unverified**.
- UnifiedPush connector library requirements for apps (how an app registers with a distributor): not read.
- Whether ntfy or UnifiedPush works with Android foreground apps on OEM ROMs (e.g., Find N6 with its OEM battery manager): not verified.
- Google Play policy on `specialUse` and FCM usage for agent notifications: not read.

---

## 5. Rate limiting and abuse protection on the WebSocket endpoint

### Takeaway
No primary source was read for ASP.NET rate limiting (the Microsoft rate-limiting page returned 404 in this session). The one verified rule is from RFC 8628: rate-limit user-code/pairing attempts. Concrete limits below are design suggestions, not sourced facts.

### Cited Findings
- RFC 8628 recommends rate-limiting user-code attempts because short codes have limited entropy; attackers who succeed could link a device to their account. **Verified** — [RFC 8628](https://datatracker.ietf.org/doc/html/rfc8628)
- ASP.NET Core rate limiting middleware (limiter types, WebSocket guidance): **unverified** (Microsoft page returned 404 in this session).

### Inferences
- Pairing endpoint: a small attempt budget per IP and per pairing code, lockout after N failures, one-time codes with short TTLs.
- WebSocket endpoint: authenticate before upgrade or within a short deadline; cap concurrent sockets per device; cap inbound message rate per socket; close idle sockets; reject oversized frames.
- Owner-facing server status and admin paths should not share the WebSocket limiter bucket.
- If Cloudflare is used, its WAF and rate-limit products can add edge throttling, but that is a separate product with its own terms (not verified).

### Gaps
- ASP.NET Core rate limiter API names and examples: not verified.
- Recommended concrete thresholds: none sourced.
- WebSocket-specific DoS guidance (for example, ASP.NET WebSocket options and keep-alive limits): not read.

---

## Risks and conflicts with DECIDED items

- **Tailscale non-commercial terms vs future coworkers.** The Personal plan is "for non-commercial use"; a company tailnet with custom domain is treated as business use. Confirm the plan before inviting coworkers. (Source: [Tailscale pricing](https://tailscale.com/pricing))
- **Passkey sign-in page vs "no web client".** CLAUDE.md allows only a read-only server status page. A passkey sign-in flow needs an interactive browser page. This is a potential conflict; the owner should decide whether it is allowed.
- **Cloudflare Tunnel puts a third party in the TLS path.** Tunnel terminates at Cloudflare; this conflicts in spirit with a self-hosted, owner-controlled posture. It is not a DECIDED conflict but should be weighed.
- **Google dependence via FCM.** The stock path requires Google Play services; UnifiedPush avoids this. The DECIDED docs do not state a privacy stance on Google, so this is an owner decision.
- **No 2026 Tailscale/Headscale community survey was read.** The request asked what the self-hosting community recommends in 2026; this was not verified.

## Summary of verification status
- Verified (primary pages read): Tailscale pricing; Cloudflare Tunnel and Access policy pages (partly); ASP.NET Core passkeys page (final fetch); RFC 8628; FCM Android client page (partly); Android Doze page; Android FGS service types, timeout, and changes pages; ntfy config and getting-started pages (partly); UnifiedPush distributors page.
- Secondary only: Duende passkey article (search snippet only); ProAndroidDev OEM battery article (search snippet only).
- Unverified: Headscale; WireGuard; ZeroTier; Cloudflare Tunnel WebSocket fit and free-plan terms; ASP.NET bearer tokens, rate limiting, and passkey API names; TLS certificate options; mTLS; OpenFGA and Casbin.NET; FCM legacy shutdown date; .NET Android push binding; Play policy on FGS types.

## Sources
- https://tailscale.com/pricing
- https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/
- https://developers.cloudflare.com/cloudflare-one/policies/access/
- https://learn.microsoft.com/en-us/aspnet/core/security/authentication/passkeys/?view=aspnetcore-10.0
- https://datatracker.ietf.org/doc/html/rfc8628
- https://developer.android.com/training/monitoring-device-state/doze-standby
- https://developer.android.com/develop/background-work/services/fgs/service-types
- https://developer.android.com/develop/background-work/services/fgs/timeout
- https://developer.android.com/develop/background-work/services/fgs/changes
- https://firebase.google.com/docs/cloud-messaging/android/client
- https://docs.ntfy.sh/config/
- https://docs.ntfy.sh/
- https://unifiedpush.org/users/distributors/
- https://duendesoftware.com/blog/20251007-passkeys-in-dotnet-10-blazor-apps-with-aspnet-identity (secondary)
- Design docs read by grep only: /home/user/Bazaar/docs/deep-dives/Bazaar.md, /home/user/Bazaar/docs/deep-dives/Bazaar.Server.md, /home/user/Bazaar/docs/deep-dives/Bazaar.Android.md, /home/user/Bazaar/docs/implementation-plan.md
