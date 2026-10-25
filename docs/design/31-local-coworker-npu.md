# Local Co-Worker (On-Device Inference)

Status: DRAFT for owner review (2026-10-25) · Owner questions: 13
Depends on: 02-wire-protocol-and-sync.md (escalation message), 05-model-gateway.md (cloud escalation target), 15-security-taint-and-approvals.md (untrusted text), 34-testing-strategy.md
Used by: 27-client-architecture.md (chips, cleanup pipeline), 28-windows-client.md, 29-android-client.md, 30-linux-client-and-packaging.md

## Purpose
The Local Co-Worker is a small model that runs inside each native client to do cheap language tasks: collapsing action logs and threads into chips, cleaning up voice dictation, expanding and rewriting prompts, and UI assistance. It uses the platform NPU when one is present, falls back to GPU or CPU, and sends no content off the device. Heavy reasoning stays on cloud models reached through the server.

## Decided inputs
- Summaries and cleanup run **client-side, never server-side**; the server has no NPU. (Bazaar.Windows §4, Bazaar.Android §4)
- Action-log chips are collapsed and expandable, generated locally. (Bazaar.Windows §4)
- Voice pipeline: raw transcription, then on-device cleanup, then text, "entirely on-device". (Bazaar.Android §3)
- Windows: Intel NPU only; the NPU path is OpenVINO via Windows ML, and DirectML is not an NPU path. (Bazaar.Windows §3, corrected by research)
- Android: Qualcomm/Snapdragon NPU only, for now. ONNX Runtime is the common model format. (Bazaar.Android §4)
- ONNX Runtime is a .NET library capability, not an Avalonia feature. (Bazaar.Windows §2)
- GCs are append-only; derived summaries never replace messages. (CLAUDE.md, Bazaar.md)
- Money budgets are REJECTED; local inference is not metered. (CLAUDE.md)

## Design
**Components**
- **Local runtime** (shared client code) [PROPOSED]: model registry, task queue, output guards, chip labels. Proposed project `Bazaar.Client.LocalInference` (new name, needs owner approval per CLAUDE.md).
- **Platform providers** (heads only) [PROPOSED]: `WindowsMlProvider` (Windows ML catalog, OpenVINO EP), `QnnProvider` (Android, ORT Android QNN through a Java binding), `CpuProvider` (ORT CPU, all platforms, the universal fallback).
- **Task kinds**: `ChipSummary`, `VoiceCleanup`, `PromptRewrite`, `UiAssist`. Each has a fixed prompt template, an input cap and an output cap.
- **Escalation adapter** [PROPOSED]: sends a request to the server over the wire protocol (doc 02). Never automatic.

**Selection chain** [PROPOSED]

| Platform | Chain | Notes |
|---|---|---|
| Windows | OpenVINO NPU, then OpenVINO GPU, then ORT CPU | Intel only; other vendors go to CPU |
| Android | QNN HTP (NPU), then QNN GPU, then ORT CPU | Snapdragon only |
| Linux | ORT CPU | No NPU decision yet (question 3) |

Each step runs probe, load, placement check, warm-up. Any failure moves to the next step, and the step used is shown in the UI.

**Placement verification** [PROPOSED]: after compile, read the EP's placement report. The NPU tier is accepted only if at least 95 percent of graph nodes by count run on the NPU and no attention or KV-cache op runs on CPU. Otherwise the session is marked `Degraded` and the UI shows the real backend. The threshold is set by benchmark. This prevents silent CPU fallback from looking like a working NPU.

**Model packaging and delivery** [PROPOSED]
- A signed manifest per model version: id, version, SPDX licence, per-file SHA-256, size per backend, minimum runtime version.
- Models download on first enable from an operator-configured mirror. The installer may pre-fetch (question 9).
- Compiled NPU blobs are cached locally and keyed on (model hash, EP, driver version, runtime version). A key mismatch means recompile, never reuse. Blobs are never distributed, because OpenVINO documents that blob compatibility across driver and runtime versions is not guaranteed.

**Update policy** [PROPOSED]: model versions are pinned per client release. Model updates are opt-in. A driver or runtime change rebuilds local blobs only.

**Output guards** [PROPOSED]
- Cleanup must keep every number, name and URL from the input. If not, the UI shows the raw transcript, one tap away.
- Output caps per task kind; over-cap output is truncated and marked.
- Chip labels are deterministic (verb, counts, file types, e.g. "Read 15 files") and need no model. The model writes only an optional one-line prose summary. Chips work with the Co-Worker off.

**Scheduling** [PROPOSED]: one session per model per device. Priority: `VoiceCleanup`, `UiAssist`, `PromptRewrite`, `ChipSummary`. New user input cancels background summaries. Results are cached by (source hash, model version).

## Data and interfaces

```csharp
public enum LocalTaskKind { ChipSummary, VoiceCleanup, PromptRewrite, UiAssist }
public enum BackendTier { Npu, Gpu, Cpu, Fake }
public sealed record LocalTaskRequest(LocalTaskKind Kind, string Input, int MaxOutputTokens, string CacheKey);
public sealed record LocalTaskResult(string Text, BackendTier Tier, bool Degraded, TimeSpan Elapsed);

public interface ILocalModelBackend : IAsyncDisposable {
    BackendTier Tier { get; }
    Task<LocalTaskResult> RunAsync(LocalTaskRequest request, CancellationToken ct);
}
public interface IExecutionProviderSelector {
    Task<ILocalModelBackend> SelectAsync(ModelManifest manifest, CancellationToken ct);
}
public interface IPlacementVerifier { PlacementReport Verify(SessionInfo session); }
```

Manifest (sketch):
```json
{ "model": "<id>", "version": "1.0.0", "license": "<SPDX, verify>",
  "files": [{ "name": "model.onnx", "sha256": "<hex>", "bytes": 0 }],
  "minRuntime": "<version>", "signature": "<detached>" }
```

Client local store [PROPOSED]: table `local_summary(cache_key PK, source_hash, model_version, tier, text, created_at)` and table `npu_blob(model_sha256, ep, driver_version, runtime_version, path)`, primary key on the four key fields. Store format is OPEN (the client local DB is not decided). Summaries are derived, not synced, deletable.

## Behaviour and edge cases
- **Cold start:** NPU compile takes seconds to minutes. Warm up at app start only when enabled; show a "warming" state.
- **Input limits:** the documented OpenVINO NPU LLM path uses static shapes and a 1024-token prompt default. Longer inputs are chunked or cover the newest entries only. Chunking rules are PROPOSED.
- **Driver builds:** the NPU tier is refused on Intel driver builds 32.0.100.4300, 4509 and 4512, which OpenVINO release notes tie to crashes on Core Ultra Series 3. Target 32.0.100.5540 or later, re-checked before release.
- **Power:** background summaries pause on battery saver (Android) and on battery (Windows) [PROPOSED].
- **Security:** model output is display text. It never becomes a tool call, command or approval. Thread text is untrusted (doc 15). A prompt rewrite is sent only after the user confirms it.
- **Privacy:** no content leaves the device. The only network use is model and EP download. Telemetry records tier and timing, never text [PROPOSED]. Escalation is an explicit user or agent action through the server (doc 05), and the UI states that content is leaving the device.
- **Failure:** any error returns raw content, and the chip shows its count-only label.

## Testing
- No real model in CI. `FakeLocalBackend` returns scripted text, tiers, degraded flags and errors (timeout, out of memory, blob mismatch).
- Selector tests with a fake hardware probe: every chain order, and a downgrade on each failure.
- Placement tests with canned reports: below threshold gives `Degraded`.
- Blob-cache tests: any key field change triggers recompile.
- Output guard tests: a dropped number or name falls back to raw text.
- Avalonia.Headless: deterministic chip label offline, summary replaces label when present, Degraded badge visible.
- Linux CI: a real ORT CPU run on a tiny generated ONNX fixture, which exercises the real runtime path without an NPU.
- Hardware lane (manual or nightly, not CI): NUC Intel NPU and one Snapdragon Android device. Benchmark harness records time to first token, tokens per second, per-EP placement and error rate on a fixed cleanup corpus. Recorded, not gated, until targets exist.
- Android QNN: instrumented test on a physical device only.

## Model candidates and licences (research, not advice)

| Candidate | Role | Licence per sources | Status |
|---|---|---|---|
| Phi-4-mini (3.8B) | Default text candidate | MIT (secondary sources only) | Verify on the model card |
| Qwen3 0.6B / 1.7B / 4B | CPU tier candidates | Apache 2.0 (secondary) | Verify per size |
| Gemma 4 E2B / E4B | Candidate | Apache 2.0 on the HF card; one source says Gemma Terms | Needs counsel |
| Whisper tiny / small | STT (Android QNN, Windows CPU) | Not covered by the research | Verify |
| Moonshine streaming / tiny | STT candidate (CPU) | Code and streaming MIT; legacy non-English models non-commercial | Exclude legacy |

No Intel NPU tokens-per-second figure from an official source exists. Community numbers are unverified and are not used for budgeting. The Android Whisper-Small real-time factor (about 0.09 to 0.13) is one developer's result. Qualcomm QAIRT and Qualcomm GenieX terms need counsel review before distribution.

## Work packages
1. Contracts, `FakeLocalBackend` and selector interface. No hardware.
2. Manifest, hash verification and local model store. Depends on 1.
3. Output guards and deterministic chip labels, with headless tests. Depends on 1.
4. Scheduler with cancellation and single-flight. Depends on 1.
5. Windows: ORT CPU provider first (runs in Linux CI), then the Windows ML OpenVINO provider behind a flag. Depends on 1, 2.
6. Placement verifier, `Degraded` state and UI badge. Depends on 5.
7. Blob cache keyed on driver and runtime. Depends on 2, 5.
8. Android QNN packaging spike: Java AAR module, manifest entries, `ADSP_LIBRARY_PATH`, QAIRT pin. Depends on 1.
9. Android QNN provider and voice pipeline. Depends on 4, 8.
10. STT backend abstraction and CPU Whisper or Moonshine backend. Depends on 1, 2.
11. Escalation adapter. Depends on doc 02 and doc 05 contracts.
12. Benchmark harness and fixed cleanup corpus. Depends on 1.

## Conflicts
1. **"Server does all compute" (CLAUDE.md) versus client-side inference.** The Co-Worker is an explicit exception. Owner should ratify it into CLAUDE.md.
2. **"Nothing leaves the device" (brief) versus escalation.** Resolved as: local tasks never send content; escalation is a separate, visible action.
3. **ONNX-only format versus the Intel LLM path.** The documented OpenVINO GenAI NPU path uses OpenVINO IR, not ONNX. Running autoregressive LLMs through the ORT OpenVINO EP is unverified. If that spike fails, the single-format decision needs owner review.
4. **Windows ML catalog downloads EPs on demand**, which conflicts with offline use. Installer pre-fetch is needed. Windows ML also needs Windows 11 24H2 (build 26100) or later, which no decision sets.
5. **Bazaar.Windows §3 still reads "DirectML vs QNN".** Fix in the same change (CLAUDE.md rule).
6. **Licences** for Gemma 4 (two sources disagree), Phi-4-mini and Qwen3 (secondary only), and Whisper (not researched) are unresolved.
7. **GenieX claims GGUF on NPU**, which other sources deny. Unresolved; do not rely on it.
8. **Intel driver and OpenVINO versions** conflict (2026.2 versus 2026.3.1 in sources). Pin one pair after a test.
9. **Windows STT NPU path and Windows AI Speech** are unresearched. Windows STT is CPU-only until tested.
10. **Phi Silica** is a Microsoft-fixed model with Limited Access. It cannot ship as the Co-Worker.
11. **Android binding** is hand-rolled: no .NET NuGet for ORT QNN exists. The decision stands, but the cost is real.

## Questions for the owner
1. **Automatic escalation?** Default: no. Only the user or an explicit agent action sends content to the server.
2. **Summary storage?** Default: local cache only, not synced, not in GC logs.
3. **Linux tier?** Default: CPU-only, off until enabled.
4. **Windows without an Intel NPU** (AMD, no NPU, older Intel)? Default: CPU tier as an opt-in.
5. **Minimum Windows build?** Default: 24H2 (26100), the Windows ML floor.
6. **Second model format** (OpenVINO IR) if the ONNX LLM spike fails? Default: yes, after a spike.
7. **Default text model?** Default: Phi-4-mini if its MIT licence is confirmed on the model card, otherwise Qwen3 1.7B.
8. **Default STT?** Default: Whisper-Small on Android QNN; Whisper or Moonshine on Windows CPU until NPU STT is tested.
9. **Delivery?** Default: download on first enable, with the installer pre-fetching the EP on Windows.
10. **Update policy?** Default: opt-in per model version, pinned in releases.
11. **Latency target** for cleanup of a 10-second utterance? Default: under 2 s on the NUC and the Android device. The benchmark may change this.
12. **Cleanup rules?** Default: remove fillers and fix structure only. Never add facts, and always keep the raw transcript.
13. **Hardware?** Which Core Ultra generation is the NUC, and which SoC is the Android device? Default: the owner names them; sources do not verify the Find N6 SoC.
