# Bazaar.Android (Development Console)

Status: DRAFT for owner review (2026-10-25) · Owner questions: 13
Depends on: 27-client-architecture.md, 01-gc-data-model.md, 02-wire-protocol-and-sync.md, 12-kanban-and-dependencies.md, 15-security-taint-and-approvals.md, 19-constellation-and-git-solar-system.md, 31-local-coworker-npu.md, 32-remote-access-auth-and-privacy.md · Used by: 34-testing-strategy.md, 35-spikes-plan.md

## Purpose
Bazaar.Android is the phone and foldable head of the Bazaar client: a Development Console for staying connected to the company. The owner triages GCs, approves or denies gated actions, replies, rearranges Kanban, watches agents and captures ideas by voice. It is not a place to write code. This doc records the decided behaviour and the guards that stop it drifting toward desktop parity.

## Decided inputs
- "Stay connected": a Development Console, not a Mobile IDE or companion app (Bazaar.Android §1).
- Functionally distinct from the desktop heads, even though it shares their renderer (Bazaar.Android §2).
- Fold-aware: folded = full chat; unfolded = chat shrinks to a ticker, plus swipeable Diffs / Task Board / Background panes and a bottom-drawer Constellation (§3).
- Press-and-hold anywhere opens a radial menu whose centre is dictation, active if the finger does not move. Holding the chat input goes straight to dictation (§3, clarified 2026-10-24).
- Voice: raw transcription, then on-device NPU cleanup; fully on-device. Action chips generated on-device (§3; Bazaar.Windows §4).
- Qualcomm NPU only, via ONNX Runtime Mobile with QNN (§4).
- Foldable detection via Jetpack WindowManager bindings through .NET for Android (§4, RESEARCHED).
- Background: content-free push wake-ups through one provider interface (FCM and UnifiedPush/ntfy), then fetch over the authenticated connection (§3).
- First distribution: Google Play closed test (§3).
- Shared Avalonia `Bazaar.Client.Core` with thin heads (CLAUDE.md; Bazaar.Windows §3).
- No web client. GCs are append-only with kept edit versions. Kanban is a dependency board (CLAUDE.md).
- Once a session has seen untrusted input, post, send, spend and push need owner approval (15, Decided inputs).

## Design
### Components
- `Bazaar.Client.Core` (shared, doc 27): views, view models, `UiProfile` rendering, transport, cursor store. No Android-only feature lives here.
- `Bazaar.Client.Android` (head): non-generic `AvaloniaMainActivity` (Avalonia 12 requirement), posture source, push provider, speech and cleanup providers, Keystore-backed key store, edge-to-edge insets. Avalonia Android is Tier 1 on Android 16 and Tier 2 on 12 to 15. [PROPOSED] minimum API 35, target API 36.
- Posture: `WindowLayoutInfo` maps to a `Posture` record (folded, unfolded, hinge bounds) that feeds the view model. Pane arrangement is decided on the phone (see Conflicts 1).

### Layout [PROPOSED]
| Posture | Layout |
|---|---|
| Folded | Full chat; GC pager; approval cards inline |
| Unfolded | Chat column becomes a ticker of on-device summaries; pager of Diffs / Task Board / Background; bottom drawer for Constellation (read-only first) |
| Half-open | Unfolded layout split at the hinge (Q4) |

### Radial and dictation [PROPOSED sectors]
Press: the radial opens with dictation at the centre, and recording starts. Move into a sector: dictation cancels and that action opens. Release in the centre: the transcript is cleaned and placed in the composer for review. Release in an empty sector: cancel. Sectors: Reply, Approvals, Kanban, Agents, Annotate, Sketch, Start screen.

### Voice pipeline [PROPOSED]
Capture, then `ISpeechToText` (Whisper-Small on QNN, Tiny as fallback), then `ITranscriptCleaner` (small instruct model; candidate Phi-4-mini, MIT), then the composer. The cleaner must not add facts (Testing). Without an NPU, a CPU path is labelled "slower" in the UI. Dictated text is never sent without review.

### Approvals [PROPOSED]
- Card: agent, action, target, exact payload (diff summary or arguments), taint sources collapsed with quoted excerpts, expiry (default 24 h), link to the GC.
- Actions: Approve once, Deny, Approve for this task (1 h, scope shown). No "always allow".
- Approve requires a fresh biometric prompt. The response is signed by a device key in Android Keystore (signing unverified; spike).
- Dictation never approves or denies. Edit-then-approve is desktop-only.
- A changed payload invalidates the card. Offline, the request waits; nothing auto-approves.

### Phone-use scope [PROPOSED]
| Action | Folded | Unfolded |
|---|---|---|
| Read, reply, dictate | Yes | Yes |
| Approve or deny gated actions | Yes | Yes |
| Review a PR | Summary; approve or request changes | Full diff in Diffs pane |
| Move Kanban card | List drag (server validates blocks) | Task Board |
| Watch agents | Presence list | Background pane |
| Annotate screenshot | Yes (photo picker) | Yes |
| Sketch on Constellation | No | Bottom drawer |
| Voice idea | Owner-tagged text to the owner "Ideas" GC | Same |

Kanban moves carry the board revision. A stale move is rejected and the board is re-fetched.

### Background delivery [PROPOSED]
The socket is open only while the app is foreground or unfolded. The server calls `WakeAsync` for an @mention, DM or owner decision, coalesced to one per 30 s (doc 02). The phone wakes, opens a short socket, runs `hello` and `subscribe` from its cursors, posts a local notification and closes. Owner-facing wakes use FCM's `RemoteNotification` type, not high-priority v1 messages (Doze docs; exact API to verify). The lock screen shows only "Bazaar: something needs you".

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
- Ordering uses server `n`, never device time.
- Doze delays wakes. Cursors make late or repeated wakes harmless.
- A token change (FCM Installation ID or UnifiedPush endpoint) re-registers. The server drops a token after a failed push.
- Dictation requires the foreground; there is no screen-off dictation.
- Keystore loss (device reset) forces re-pairing (doc 32).
- Attachments never travel on the socket (256 KiB frame limit, doc 02).

## Testing
- Avalonia.Headless: folded and unfolded layouts at fixed sizes; approval card; Kanban move with a stale revision; the desktop-parity guard on `UiProfile`.
- Unit tests: radial state machine (press, move, centre release, empty release, cancel), with no UI.
- Fakes: scripted `ISpeechToText` and `ITranscriptCleaner`, fake push provider, fake posture source.
- Cleaner eval: about 50 rambling transcripts. Check filler removal, no added facts (human review) and latency per device class.
- Real-device spike (not CI): Korean, Japanese and Chinese composer input; fold and unfold mid-stream; half-open; Android 16 insets; 120 Hz scroll; `adb shell dumpsys deviceidle force-idle`, then push, fetch and notify; Wi-Fi to mobile switch; confirm QNN runs the graph on the NPU, not the CPU fallback; Play closed-test install.
- Headless does not cover IME, fold events or real push.

## Work packages
1. Android head project: `AvaloniaMainActivity`, manifest, edge-to-edge, CI APK and AAB build. Depends on the doc 27 shell.
2. Posture source over the WindowManager bindings, with a fake and pinned binding versions. Depends on 1.
3. Folded and unfolded shell with pane pager. Depends on 1, 2.
4. Radial state machine and Headless gesture tests. Depends on 3.
5. Speech and cleanup providers: NPU first, CPU fallback, fakes. Depends on 1 and doc 31.
6. Cleaner eval harness and corpus. Depends on 5.
7. Push providers (FCM, UnifiedPush) and wake handler. Depends on doc 02 packages 8 and 11.
8. Approval card, biometric gate, device-key signing. Depends on doc 15 packages 5 and 6, and doc 32 pairing.
9. Kanban pane with revision-checked moves. Depends on 3 and doc 12.
10. Constellation drawer, read-only. Depends on 3 and doc 19.
11. Screenshot annotation. Blocked on the upload channel (doc 02 or 32).
12. Device spike checklist run; results recorded in doc 35.
13. Play closed-test track and signing setup. Blocked on the owner's tester list.

## Conflicts
1. Fold layout vs "server chooses layout": doc 02 has the server choose layout, but fold changes must be instant. [PROPOSED] The server supplies widgets; the phone arranges panes from posture.
2. Hold-anywhere vs platform gestures: long-press selects text in the composer, TalkBack uses long-press, and Android 16 edge gestures can clash at screen edges. The radial must ignore edge bands.
3. Dictation vs approval: a one-tap voice centre tempts voice approval, but doc 15 says a dictated "yes" is not approval. Resolved here: dictation only composes.
4. `dataSync` cap: the remote-access note reads the FGS timeout page as 6 h per 24 h; the distribution note says no source confirmed it. Not re-verified in this pass. No always-on socket is planned, so the design does not depend on it; re-read the page before any foreground-service use.
5. NPU evidence: the STT numbers come from one developer on Snapdragon 8 Elite Gen 5 (SM8850). The OPPO Find N6 SoC is unverified, and no phone-class LLM speed has been measured.
6. Avalonia Android: Korean IME reported unresolved (2024 comment, unverified). The high-refresh and "complex layouts" claims are unverified, so this doc does not rely on them.
7. Distribution order: the owner chose a Play closed test; the distribution note recommends signed APK sideloading first. Both can run. The 14-day clock is the critical path.
8. Gemma 4 licence: Apache 2.0 on the model card, but a secondary source cites Gemma Terms. Unresolved, so not a default. Needs counsel.

## Questions for the owner
1. Radial sectors: Reply, Approvals, Kanban, Agents, Annotate, Sketch, Start. Default: these seven, because more sectors slow the thumb's arc.
2. Dictation output: insert for review, never auto-send. Default: review, so a misheard word never reaches an agent.
3. Holding the composer starts dictation, so text selection moves to double-tap and handles. Default: yes, your decided gesture. Confirm TalkBack is acceptable.
4. Half-open posture: split at the hinge, or folded? Default: unfolded layout whenever width allows, else folded.
5. Full diffs: folded shows a summary only. Default: yes, since review belongs to the unfolded context.
6. Biometric on every approval, and dictation never approves. Default: yes to both (doc 15 Q7).
7. Minimum Android version. Default: API 35 minimum, API 36 target, since Tier 1 is Android 16 only.
8. Default push provider. Default: FCM, with UnifiedPush selectable in settings. FCM is simpler but routes wake-ups through Google.
9. Play closed test: recruit 12 testers now for 14 continuous days, and sideload APKs to your own devices meanwhile. Confirm, and say whether testers may be people outside the company.
10. Cleanup model: Phi-4-mini (MIT) pending benchmark; Qwen3 small and Gemma 4 E2B as fallbacks after a licence check. Also confirm your foldable's SoC, since the NPU path depends on it.
11. Voice ideas: keep audio? Default: no, transcript only, audio discarded after cleanup.
12. Constellation sketches: where do they live? Default: owner annotation nodes; an agent may promote them to the Vault, so the phone never commits to the Git-backed Vault.
13. Screenshot annotation needs an upload path outside the socket frame limit. Default: yes for v1, designed in doc 02 or 32.
