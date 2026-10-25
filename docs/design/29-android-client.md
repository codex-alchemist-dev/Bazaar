# Bazaar.Android (Development Console)

Status: RECONCILED with DECISIONS-LOG (2026-10-25) · Owner questions: 13
Depends on: 01-gc-data-model.md, 02-wire-protocol-and-sync.md, 06-agent-runtime.md, 12-kanban-and-dependencies.md, 15-security-taint-and-approvals.md, 19-constellation-and-git-solar-system.md, 21-github-integration.md, 27-client-architecture.md, 31-local-coworker-npu.md, 32-remote-access-auth-and-privacy.md · Used by: 34-testing-strategy.md, 35-spikes-plan.md

## Purpose
Bazaar.Android is the phone and foldable head of the Bazaar client: a Development Console for staying connected to the company. The owner triages GCs, approves or denies gated actions, replies, reviews and merges PRs, rearranges Kanban, watches agents and captures ideas by voice. It is not a place to write code. This doc records the decided behaviour and the guards that stop it drifting toward desktop parity.

## Decided inputs
- "Stay connected": a Development Console, not a Mobile IDE or companion app (CLAUDE.md; Bazaar.Android §1). No editor, terminal or file-tree widgets.
- Shares the Avalonia renderer (`Bazaar.Client.Core`, doc 27) with thin heads, though its feature set is distinct (CLAUDE.md; Bazaar.Android §2).
- Fold-aware. The server sends layout variants for all postures; the phone switches locally with no round trip and caches the last profile for offline cold start. The cache holds no message bodies (DECISIONS-LOG round 5, C27; doc 27 owns the fallback shell).
- Press-and-hold opens a radial menu whose centre is dictation. Holding the chat input goes straight to dictation (Bazaar.Android §3, clarified 2026-10-24).
- Voice: raw transcription, then on-device NPU cleanup; fully on-device. On-device inference, with summaries and prompt rewrite, is a ratified client-side exception to "the server does all compute" (CLAUDE.md; DECISIONS-LOG round 4, C17).
- Qualcomm NPU only, via ONNX Runtime Mobile with QNN (Bazaar.Android §4). Foldable detection via Jetpack WindowManager bindings through .NET for Android (RESEARCHED).
- Push: content-free. A bare wake hint carries no chat id and no sequence number. Three wake kinds (mention, DM, owner decision) plus opt-in quota alerts. Content is fetched over the authenticated connection (CLAUDE.md; DECISIONS-LOG round 5, C41). FCM and UnifiedPush sit behind one provider interface.
- Transport: WebSocket for realtime; authenticated HTTP for blobs (screenshots), search and pairing (DECISIONS-LOG round 6, C15; doc 02).
- Taint: external content taints hard; agent text taints softly. Tier 2 actions are gated on hard taint. Outward high-impact actions (posting, sending, spending, repo push or merge) need owner approval; routine internal work does not (DECISIONS-LOG rounds 2 and 6, C06 and C18; doc 15 ladder).
- The owner merges from the apps. The client holds no GitHub credential; the server performs the squash merge on the owner's signed approval, with the `Signed-off-by` line (DECISIONS-LOG round 8, C25; credential choice in docs 14 and 21, round 12).
- Done means the PR is merged. Reviewer approval is a visible flag (DECISIONS-LOG round 8, C28).
- Monaco is not supported on mobile (UI framework contingency note, 2026-10-25). Phone diffs need a native surface (see Conflicts).
- Google Play closed test is the first distribution (CLAUDE.md).
- No web client. GCs are append-only with kept edit versions. Kanban is a dependency board (CLAUDE.md).

## Design
### Components
- `Bazaar.Client.Core` (shared, doc 27): views, view models, `UiProfile` rendering, transport, cursor store. No Android-only feature lives here.
- `Bazaar.Client.Android` (head): non-generic `AvaloniaMainActivity` (Avalonia 12 requirement), posture source, push provider, speech and cleanup providers, Keystore-backed device key store, edge-to-edge insets. Avalonia Android is Tier 1 on Android 16 and Tier 2 on 12 to 15. [PROPOSED] minimum API 35, target API 36.
- Posture: `WindowLayoutInfo` maps to a `Posture` record (folded, unfolded, hinge bounds). Posture selects among the layout variants the server already sent. It does not compute layout (DECISIONS-LOG C27).

### Layout [PROPOSED]
| Posture | Layout |
|---|---|
| Folded | Full chat; GC pager; approval cards inline |
| Unfolded | Chat column becomes a ticker of on-device summaries; pager of Diffs / Task Board / Background; bottom drawer for Constellation (read-only first) |
| Half-open | Unfolded layout split at the hinge (Q4) |

### Radial and dictation [PROPOSED sectors]
Press: the radial opens with dictation at the centre, and recording starts. Move into a sector: dictation cancels and that action opens. Release in the centre: the transcript is cleaned and placed in the composer for review. Release in an empty sector: cancel. Sectors: Reply, Approvals, Kanban, Agents, Annotate, Sketch, Start screen. The radial ignores screen-edge bands (Conflicts 2).

### Voice pipeline [PROPOSED]
Capture, then `ISpeechToText` (Whisper-Small on QNN, Tiny as fallback), then `ITranscriptCleaner` (small instruct model; candidate Phi-4-mini, MIT), then the composer. The cleaner must not add facts (Testing). Without an NPU, a CPU path is labelled "slower" in the UI. Dictated text is never sent without review, and dictation never approves or denies anything.

### Approvals [PROPOSED]
- Card: agent, action, target, exact payload (diff summary or arguments), taint sources collapsed with quoted excerpts, expiry (default 24 h), link to the GC. The action's tier follows doc 15's ladder.
- Actions: Approve once, Deny, Approve for this task (1 h, scope shown). No "always allow".
- Approve requires a fresh biometric prompt. The response is signed by a device key in the Android Keystore (signing unverified; spike).
- Edit-then-approve is desktop-only.
- A changed payload invalidates the card. Offline, the request waits; nothing auto-approves.

### Phone-use scope [PROPOSED]
| Action | Folded | Unfolded |
|---|---|---|
| Read, reply, dictate | Yes | Yes |
| Approve or deny gated actions | Yes | Yes |
| Review a PR and merge it after signed approval | Summary; approve, request changes or merge (Q6) | Full diff in Diffs pane; same actions |
| Move Kanban card | List drag (server validates blocks) | Task Board. A PR-tied card goes to Done only through its merge |
| Watch agents | Presence list | Background pane |
| Annotate screenshot | Yes (photo picker; upload over the HTTP blob channel) | Yes |
| Sketch on Constellation | No | Bottom drawer |
| Voice idea | Owner-tagged text to the owner "Ideas" GC (a registry type, doc 01) | Same |

Kanban moves carry the board revision. A stale move is rejected and the board is re-fetched.

### Background delivery [PROPOSED]
The socket is open only while the app is foreground or unfolded. The server sends a bare wake hint (no GC id, no sequence, no content) for an @mention, a DM or an owner decision, coalesced per device (limits in doc 02; wake reasons owned by doc 06). The phone wakes, opens a short authenticated socket, runs `hello` and `subscribe` from its cursors, posts a local notification and closes. Owner-facing wakes use FCM's notification message type, not high-priority data messages (Doze docs; exact API to verify). The lock screen shows only "Bazaar: something needs you". No keep-alive foreground service is planned.

```csharp
interface IPushProvider { string Name { get; } Task<string> RegisterAsync(CancellationToken ct); }
interface ISpeechToText { Task<string> TranscribeAsync(ReadOnlyMemory<short> pcm16k, CancellationToken ct); }
interface ITranscriptCleaner { Task<string> CleanAsync(string raw, CancellationToken ct); }
interface IPostureSource { IObservable<Posture> Changes { get; } }
```

### Guard against desktop parity [PROPOSED]
1. Android `UiProfile` never includes editor, terminal or file-tree widgets. A test asserts it.
2. A new phone feature is added to the scope table above, and only the owner adds rows.
3. The Android PR checklist asks: "Does this make the phone a build tool?"

## Behaviour and edge cases
- Fold and unfold apply locally at once, with no network round trip.
- Process death during dictation discards audio and sends nothing.
- Ordering uses server sequence numbers (doc 02), never device time.
- Doze delays wakes. Cursors make late or repeated wakes harmless.
- A token change (FCM Installation ID or UnifiedPush endpoint) re-registers. The server drops a token after a failed push.
- Dictation requires the foreground; there is no screen-off dictation.
- Keystore loss (device reset) forces re-pairing (doc 32).
- Attachments never travel on the socket (256 KiB frame limit, doc 02); they use the HTTP blob channel.
- A merge request is sent only after a fresh signed approval. If the PR head changed since the card was shown, the approval is invalidated.

## Testing
- Avalonia.Headless: folded and unfolded layouts at fixed sizes, with posture switching between cached variants; approval card; Kanban move with a stale revision; the desktop-parity guard on `UiProfile`; PR review card with merge gated on approval.
- Unit tests: radial state machine (press, move, centre release, empty release, cancel), with no UI.
- Fakes: scripted `ISpeechToText` and `ITranscriptCleaner`, fake push provider, fake posture source, fake HTTP blob channel.
- Cleaner eval: about 50 rambling transcripts. Check filler removal, no added facts (human review) and latency per device class.
- Real-device spike (not CI): Korean, Japanese and Chinese composer input; fold and unfold mid-stream; half-open; Android 16 insets; 120 Hz scroll; `adb shell dumpsys deviceidle force-idle`, then push, fetch and notify; Wi-Fi to mobile switch; confirm QNN runs the graph on the NPU, not the CPU fallback; Play closed-test install.
- Headless does not cover IME, fold events or real push.

## Work packages
1. Android head project: `AvaloniaMainActivity`, manifest, edge-to-edge, CI APK and AAB build. Depends on the doc 27 shell.
2. Posture source over the WindowManager bindings, with a fake and pinned binding versions. Depends on 1.
3. Folded and unfolded shell with pane pager and cached-profile cold start. Depends on 1, 2 and doc 27.
4. Radial state machine and Headless gesture tests. Depends on 3.
5. Speech and cleanup providers: NPU first, CPU fallback, fakes. Depends on 1 and doc 31.
6. Cleaner eval harness and corpus. Depends on 5.
7. Push providers (FCM, UnifiedPush) and bare-wake handler. Depends on doc 02 wake and push packages.
8. Approval card, biometric gate, device-key signing. Depends on doc 15 packages 5 and 6, and doc 32 pairing.
9. Kanban pane with revision-checked moves. Depends on 3 and doc 12.
10. Constellation drawer, read-only. Depends on 3 and doc 19.
11. Screenshot annotation over the HTTP blob channel. Depends on the doc 02 HTTP surface.
12. Device spike checklist run; results recorded in doc 35.
13. Play closed-test track and signing setup. Blocked on the owner's tester list.
14. PR review and merge pane (summary, full diff, request changes, merge request after signed approval). Depends on 8, doc 21 and doc 12.

## Conflicts
1. Hold-anywhere vs platform gestures: long-press selects text in the composer, TalkBack uses long-press, and Android 16 edge gestures can clash at screen edges. The radial must ignore edge bands.
2. `dataSync` cap: the remote-access note reads the FGS timeout page as 6 h per 24 h; the distribution note says no source confirmed it. Not re-verified in this pass. No always-on socket or keep-alive service is planned (DECISIONS-LOG C41), so the design does not depend on it; re-read the page before any foreground-service use.
3. NPU evidence: the STT numbers come from one developer on Snapdragon 8 Elite Gen 5 (SM8850). The OPPO Find N6 SoC is unverified, and no phone-class LLM speed has been measured.
4. Avalonia Android: Korean IME reported unresolved (2024 comment, unverified). The high-refresh and "complex layouts" claims are unverified, so this doc does not rely on them.
5. Phone diff rendering: Monaco is not available on mobile, and no native Android diff surface has been chosen or spiked. The UI contingency note puts a native Android surface third in its spike order (after Windows and Linux WebView spikes). The Diffs pane is review-only in v1; this must be settled in doc 35 before the pane is built.
6. Distribution order: the owner chose a Play closed test; the distribution note recommends signed APK sideloading first. Both can run. The 14-day clock is the critical path.
7. Gemma 4 licence: Apache 2.0 on the model card, but a secondary source cites Gemma Terms. Unresolved, so not a default. Needs counsel.
8. Other docs to align (not this doc's to fix): doc 02 must specify the bare wake payload and the HTTP blob surface; doc 12 must make Done mean merged for PR-tied cards; doc 21 must provide the merge-request path with the `Signed-off-by` line; doc 27 must own the cached offline profile.

## Questions for the owner
1. Radial sectors: Reply, Approvals, Kanban, Agents, Annotate, Sketch, Start. Default: these seven, because more sectors slow the thumb's arc.
2. Dictation output: insert for review, never auto-send. Default: review, so a misheard word never reaches an agent.
3. Holding the composer starts dictation, so text selection moves to double-tap and handles. Default: yes, your decided gesture. Confirm TalkBack is acceptable.
4. Half-open posture: split at the hinge, or folded? Default: unfolded layout whenever width allows, else folded.
5. Full diffs: folded shows a summary only. Default: yes, since review belongs to the unfolded context.
6. Merge on the phone: may a PR be merged from the folded layout, which shows only a summary? Default: yes, after a fresh biometric approval that shows the checks status and the summary; otherwise merge is unfolded only.
7. Biometric on every approval, and dictation never approves. Default: yes to both (see doc 15).
8. Minimum Android version. Default: API 35 minimum, API 36 target, since Tier 1 is Android 16 only.
9. Default push provider. Default: FCM, with UnifiedPush selectable in settings. FCM is simpler but routes wake-ups through Google.
10. Play closed test: recruit 12 testers now for 14 continuous days, and sideload APKs to your own devices meanwhile. Confirm, and say whether testers may be people outside the company.
11. Cleanup model: Phi-4-mini (MIT) pending benchmark; Qwen3 small and Gemma 4 E2B as fallbacks after a licence check. Also confirm your foldable's SoC, since the NPU path depends on it.
12. Voice ideas: keep audio? Default: no, transcript only, audio discarded after cleanup.
13. Constellation sketches: where do they live? Default: owner annotation nodes; an agent may promote them to the Vault, so the phone never commits to the Git-backed Vault.

ANSWERED: screenshot upload uses the authenticated HTTP blob channel specified in doc 02 (DECISIONS-LOG round 6, C15). Whether screenshot annotation ships in v1 remains in the scope table.
