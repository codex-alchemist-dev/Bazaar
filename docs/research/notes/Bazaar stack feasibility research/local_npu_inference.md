# Local NPU Inference for the Bazaar Co-Worker Model (as of Oct 2026)

Scope: practical runtime stack per platform, candidate small models, on-device STT, CPU/GPU fallback, and the server-side "Skimmer Bouncer" question. Sources were searched and fetched in Oct 2026. Items marked **[UNVERIFIED]** come from secondary sources, a single developer, or vendor marketing and were not confirmed against a primary page.

Design-doc references: `/tmp/claude-0/-home-user-Bazaar/c63e21e3-9139-50bc-9f48-e82954f68a61/scratchpad/files-zip-new/Bazaar.Windows.md` (line 39 says "DirectML vs QNN"), `/tmp/claude-0/-home-user-Bazaar/c63e21e3-9139-50bc-9f48-e82954f68a61/scratchpad/files-zip-new/Bazaar.Android.md` (lines 31, 38, 41 record the ONNX Runtime Mobile + QNN decision).

---

## Key Question 1: Windows with an Intel NPU from .NET (OpenVINO EP vs DirectML vs Windows ML)

### Takeaway
On Intel silicon the NPU is reached through the **OpenVINO execution provider (EP)**, not DirectML. The design doc's "DirectML vs QNN" framing is wrong for Intel. Microsoft's own table maps NPU to OpenVINO (Intel), QNN (Qualcomm) and VitisAI (AMD). DirectML is listed only as a legacy, included EP for the GPU row. For .NET, the cleanest route is Windows ML's EP catalog (Windows-certified, auto-updated), with bring-your-own ORT + OpenVINO EP as the version-pinned alternative.

### Cited Findings
- Windows ML pairs the ONNX Runtime with hardware EPs. Silicon-to-EP mapping: NPU = OpenVINO (Intel), QNN (Qualcomm), VitisAI (AMD); GPU row includes "DirectML (included - legacy)"; CPU = OpenVINO or ORT CPU EP. [Microsoft Learn: Accelerate AI models with Windows ML](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/accelerate-ai-models)
- Windows ML EPs are downloaded on demand via `ExecutionProviderCatalog` APIs. They are available on Windows 11 24H2 (build 26100) or later, "depending on device and driver compatibility." Windows ML 2.x uses `Microsoft.WindowsAppSDK.ML` or `Microsoft.Windows.AI.MachineLearning` 2.x. Windows ML 1.8.x also lists OpenVINO (EpName `OpenVINOExecutionProvider`). [Microsoft Learn: Windows ML execution providers](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/supported-execution-providers)
- Windows ML recommends pre-compiling models for an EP and caching the result, because cold starts can take seconds to minutes. Bring-your-own EPs are the option for offline, managed, or strict version-pinning cases. [Microsoft Learn: Accelerate AI models with Windows ML](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/accelerate-ai-models)
- The built-in OpenVINO EP inside the ONNX Runtime repo is **deprecated**. The docs recommend the standalone Plugin EP (for example `pip install onnxruntime-ep-openvino`). The page lists ORT 1.24.1 with OpenVINO 2025.4.1 as the newest pairing. `device_type` accepts `NPU`, NPU precision is FP16, and unsupported ops fall back to CPU. [ONNX Runtime: OpenVINO EP](https://onnxruntime.ai/docs/execution-providers/OpenVINO-ExecutionProvider.html)
- Intel NPU support in Windows ML: Intel Core Ultra Series 1 (Meteor Lake) or newer, "with latest driver." The Intel page gives no minimum driver version, no minimum Windows build, and no OpenVINO pairing. [Intel: Windows ML support](https://www.intel.com/content/www/us/en/developer/articles/guide/windows-ml-support.html)
- Latest Intel NPU driver for Windows is **32.0.100.5540** (listed 8/28/2026, for Windows 11 25H2/24H2/23H2). Intel's description says it supports OpenVINO 2026.3.1.0. A TechSpot listing says 2026.2. Intel's own page is the more authoritative of the two, but the conflict is unresolved. [Intel NPU Driver for Windows](https://www.intel.com/content/www/us/en/download/794734/intel-npu-driver-windows.html); [TechSpot listing](https://www.techspot.com/drivers/driver/file/information/18372/)
- Driver floors cited elsewhere: Google LiteRT asks for NPU driver 32.0.100.4724+. OpenVINO GenAI on NPU troubleshooting says to update to 32.0.100.3104 or newer for execution failures. [Google AI Edge LiteRT Intel guide](https://developers.google.com/edge/litert/next/intel) (listed by search; not fetched, **[UNVERIFIED]** for the exact floor text); [OpenVINO GenAI on NPU](https://docs.openvino.ai/2026/openvino-workflow-generative/inference-with-genai/inference-with-genai-on-npu.html)
- OpenVINO release notes say that with certain Intel NPU PV drivers (32.0.100.4300, 32.0.100.4509, 32.0.100.4512), Windows apps could crash unexpectedly on Core Ultra Series 3. The fix is in later drivers, and 32.0.100.5540 or later is recommended. [OpenVINO 2026 release notes](https://docs.openvino.ai/2026/about-openvino/release-notes-openvino.html)
- OpenVINO documentation warns that compiled NPU blob compatibility across OpenVINO / NPU driver versions is **not guaranteed**. Recompile cached blobs whenever either changes. [OpenVINO GenAI on NPU](https://docs.openvino.ai/2026/openvino-workflow-generative/inference-with-genai/inference-with-genai-on-npu.html) (secondary search summary; the blob warning was in search snippets, **[UNVERIFIED]** wording)
- Windows 11 AI APIs: Phi Silica (Microsoft's on-device SLM) runs on the NPU on Copilot+ PCs and on GPU on other Windows 11 devices through the Windows App SDK. It is a Limited Access Feature (unlock token request), not available in China, and is a fixed Microsoft model, not one Bazaar can ship. [Microsoft Learn: Phi Silica](https://learn.microsoft.com/en-us/windows/ai/apis/phi-silica). Claim that Phi Silica runs on Intel Lunar Lake NPU comes only from a secondary news article **[UNVERIFIED]**.

### Inferences
- For .NET on Windows, the most maintainable path is: Windows ML 2.x (`Microsoft.WindowsAppSDK.ML` or `Microsoft.Windows.AI.MachineLearning`) plus the OpenVINO EP from the catalog, with pre-compiled and cached models per device. Fall back to bring-your-own ORT + OpenVINO plugin EP only if version pinning is required.
- The EP choice should be made per device at runtime, not hard-coded. The Avalonia head project's "NPU execution-provider selection" (Bazaar.Windows.md line 39) should pick OpenVINO on Intel, QNN on Snapdragon X, and CPU otherwise. DirectML is not needed for the NPU on Intel.
- Pin the driver and OpenVINO version together, and treat compiled-blob caches as keyed on both.

### Gaps
- No NuGet package name for the ORT OpenVINO plugin EP from .NET was verified. Only `Microsoft.ML.OnnxRuntime.QNN` (Windows) was confirmed, and only as a pre-built package in the QNN EP docs.
- No official Intel driver-to-OpenVINO compatibility matrix was found. The 2026.2 vs 2026.3.1 conflict is unresolved.
- No official Windows build floor for the Intel OpenVINO EP path was found beyond the Windows ML 24H2 (26100) line.

---

## Key Question 2: Android with Snapdragon (ORT Mobile + QNN from .NET, or alternatives)

### Takeaway
The ORT Mobile + QNN route is viable and has a working public example, but it is Java/Maven-first. No Android .NET NuGet for QNN was found. From .NET for Android, the realistic options are (a) Java binding of `onnxruntime-android-qnn` and `qnn-runtime`, or (b) P/Invoke into the native libraries. Two alternatives may be simpler for Android: **LiteRT (TFLite) with the Qualcomm NPU path** (Google Play AI Packs, ahead-of-time compile) and **Qualcomm GenieX** (Kotlin/Java SDK and a C SDK, with LLM support). The design doc's choice of ORT Mobile + QNN is reasonable, but it is not the only proven path.

### Cited Findings
- QNN EP is documented for "Android and Windows devices with Qualcomm Snapdragon SOCs." Built and tested with "QNN 2.22.x" per the Qualcomm/ORT doc (older). Quantization tooling is x86_64 only, so quantize on an x64 host. [ONNX Runtime: QNN EP](https://onnxruntime.ai/docs/execution-providers/QNN-ExecutionProvider.html). The Android AAR/Maven packaging is not covered on that page.
- Microsoft's Android artifact is `com.microsoft.onnxruntime:onnxruntime-android-qnn`, which pulls Qualcomm's QNN runtime as a separate Maven dependency. The official .NET NuGet for QNN (`Microsoft.ML.OnnxRuntime.QNN`) is Windows-only. [Search summary of ORT QNN docs and Qualcomm AI Developer Workflow](https://docs.qualcomm.com/doc/80-70029-15B/topic/run-an-onnx-model-using-ort.html)
- Working reference build (single developer, anecdotal): `onnxruntime-android-qnn` **1.29.0**, `com.qualcomm.qti:qnn-runtime` **2.45.0** matching the export's build metadata, Hexagon `htp_arch` **81** (Snapdragon 8 Elite Gen 5, SM8850). Whisper-Tiny and Whisper-Small ran on the NPU. [GitHub: itsallgoody/onnxruntime-qnn-snapdragon-8-elite-gen5](https://github.com/itsallgoody/onnxruntime-qnn-snapdragon-8-elite-gen5)
- Android-specific requirements from the same write-up: declare `libcdsprpc.so` and `libOpenCL.so` as `<uses-native-library>` (Android 12+); set `ADSP_LIBRARY_PATH` before creating any session; set `useLegacyPackaging = true` so the DSP loader can read native libs from disk (larger install); pin `qnn-runtime` to the exact QAIRT version used for export, or context-binary loading fails with error 5000. [GitHub: itsallgoody/onnxruntime-qnn-snapdragon-8-elite-gen5](https://github.com/itsallgoody/onnxruntime-qnn-snapdragon-8-elite-gen5)
- LiteRT with the Qualcomm NPU: AOT compilation is optional but recommended for large models. AOT output ships as a Google Play AI Pack; on-device compilation takes a `.tflite` in `assets/` and compiles at run time. Create the model with NPU preferred and GPU fallback. Snapdragon 8 Elite Gen 5 is the named target; "over 80% of recent Qualcomm SoCs" include an NPU (no full list). [Google Developers Blog: LiteRT on Qualcomm NPU](https://developers.googleblog.com/unlocking-peak-performance-on-qualcomm-npu-with-litert/)
- LiteRT vendor numbers on Snapdragon 8 Elite Gen 5 (Google's claims): FastVLM-0.5B int8 weights / int16 activations: 0.12 s time-to-first-token on 1024x1024 images, >11,000 tok/s prefill, >100 tok/s decode. **[UNVERIFIED]** vendor-reported; vision model, not a text-only SLM. [Google Developers Blog](https://developers.googleblog.com/unlocking-peak-performance-on-qualcomm-npu-with-litert/)
- Qualcomm GenieX (Developer Preview, BSD-3-Clause, subject to Qualcomm Terms of Use): runs on Snapdragon only. Backends: `llama_cpp` (GGUF on NPU/GPU/CPU) and `qairt` (pre-compiled AI Hub bundles, NPU only). Platforms: Windows ARM64, Android (Snapdragon 8 Elite and 8 Elite Gen 5), Linux ARM64. Bindings: CLI, Python, a Kotlin/Java Android SDK (`com.qualcomm.qti:geniex-android:0.4.0`), an OpenAI-compatible local server, and a C SDK (`sdk/include/geniex.h`). No .NET binding is listed. Listed models include Gemma 4 E4B, Qwen3.5-2B, Qwen3-4B, Qwen2.5-VL-7B, Qwen3-0.6B. No tok/s numbers published. [GitHub: qualcomm/GenieX](https://github.com/qualcomm/GenieX)
- Qualcomm AI Hub Models repo lists deployable generative models including Gemma 4 (E2B, E4B), Phi-3.5 Mini, Phi-4 Mini, and Qwen3 / Qwen3.5 / Qwen2.5-VL variants. NPU supports FP16, INT16, INT8; some older chipsets cannot run FP16 on the NPU. [GitHub: qualcomm/ai-hub-models](https://github.com/qualcomm/ai-hub-models)
- Blog-level claims, **[UNVERIFIED]**: standard GGUF models (Ollama/llama.cpp) do not run on the Qualcomm NPU at all, and Qualcomm tooling supports models up to about 7B. GenieX's `llama_cpp` backend is described as running GGUF on the NPU, which conflicts with the first claim. [GenieX README](https://github.com/qualcomm/GenieX) vs. search-summary blogs; treat as unresolved.

### Inferences
- The Bazaar design choice (ORT Mobile + QNN) keeps one ONNX model format across Windows and Android, which the design doc prefers. The cost is hand-rolled .NET-for-Android interop around the Maven AAR and native DSP loader setup.
- If the .NET-for-Android binding becomes painful, the lowest-risk fallback is a thin Kotlin/Java module that wraps either GenieX (for LLM) or ORT QNN (for STT), exposed to .NET through a Java binding or a bound service. This is an inference, not verified.
- Foldable device: the OPPO Find N6 SoC was not verified in this research. Snapdragon 8 Elite Gen 5 is the best-documented NPU target that matches the native-library examples above **[UNVERIFIED]** for the Find N6 itself.

### Gaps
- No Android .NET (MAUI / .NET for Android) NuGet or sample for ORT QNN was found.
- No ABI vs non-ABI guidance was fetched; the QNN EP page only shows C++ and Python usage examples.
- Exact QAIRT/QNN version compatibility with ORT 1.29.0 is not in an official matrix that was reached. The 2.45.0 pairing is from one developer's build.

---

## Key Question 3: Small language models for summarisation, rewriting and dictation cleanup on an NPU (2026)

### Takeaway
Viable candidates for the cheap local tier are in the 0.6B to 4B range: Qwen3 (0.6B / 1.7B / 4B), Phi-4-mini (3.8B, MIT), and Gemma 4 E2B / E4B (Apache 2.0 per Hugging Face cards). All three have NPU paths via Qualcomm AI Hub bundles and GenieX. On Intel NPU, OpenVINO documents NPU LLM support with static shapes and a 1024-token default prompt limit. No official Intel NPU tokens/sec figure for a 2026 Core Ultra was found; the only numbers found are old or anecdotal.

### Cited Findings
- Phi-4-mini: 3.8B parameters, MIT license, per secondary comparisons. [Labellerr: best SLMs under 10B (2026)](https://www.labellerr.com/blog/best-small-language-models-under-10b-parameters/); [Tech Insider: Gemma 4 vs Phi-4 Mini vs Qwen3.5](https://tech-insider.org/gemma-4-vs-phi-4-mini-vs-qwen3-5-2026/) **[UNVERIFIED]** (secondary; HF card not fetched).
- Qwen3 small tiers (0.6B, 1.7B, 4B): reported as Apache 2.0, with support for more than 100 languages. Secondary sources. [Labellerr](https://www.labellerr.com/blog/best-small-language-models-under-10b-parameters/) **[UNVERIFIED]** for exact tier licensing.
- Gemma 4 E2B and E4B (released 2 April 2026): E2B = 2.3B effective / 5.1B total; E4B = 4.5B effective / 8B total. Hugging Face cards for `google/gemma-4-E4B` and `google/gemma-4-E4B-it` list **Apache 2.0**. Google's model card lists five sizes (E2B, E4B, 12B, 26B A4B, 31B). One secondary source says Gemma Terms of Use instead of Apache 2.0, so confirm on the official card before shipping. [Hugging Face: google/gemma-4-E4B](https://huggingface.co/google/gemma-4-E4B); [Gemma 4 model card](https://ai.google.dev/gemma/docs/core/model_card_4); [Hugging Face: gemma-4-E4B-it-qat-q4_0-gguf](https://huggingface.co/google/gemma-4-E4B-it-qat-q4_0-gguf)
- Qualcomm AI Hub has pre-compiled NPU bundles for Gemma 4 E2B/E4B, Phi-3.5 Mini, Phi-4 Mini, and Qwen3 variants. [GitHub: qualcomm/ai-hub-models](https://github.com/qualcomm/ai-hub-models)
- Intel: OpenVINO 2026.4 notes mention Gemma 4 visual-token and long-context work on Core Ultra Series 3, and speculative decoding on NPU came in OpenVINO 2026.0. No throughput figures given. [OpenVINO 2026 release notes](https://docs.openvino.ai/2026/about-openvino/release-notes-openvino.html)
- Intel NPU LLM guidance: recommended `transformers==5.0.0` with OpenVINO 2026.3; NPU pipeline uses static shapes and by default supports prompts up to 1024 tokens. [OpenVINO GenAI on NPU](https://docs.openvino.ai/2026/openvino-workflow-generative/inference-with-genai/inference-with-genai-on-npu.html)
- Only community tok/s numbers found for Intel NPU: a DEV Community test on a Core Ultra 7 155U reported roughly 9 tok/s for a 1.5B model, converted from words/s (rough). A Medium post lists 6.10 tok/s for an INT4 model with no model or settings given, and is about 547 days old. Both **[UNVERIFIED]**. [DEV Community](https://dev.to/mr1azl/i-tried-running-llms-on-intels-npu-heres-what-actually-happened-5h17)
- Qualcomm NPU decode numbers for LLMs were not found in the Qualcomm docs reached. The only figure is the LiteRT vision-model claim above.

### Inferences
- For dictation cleanup and thread summaries, a 1.5B to 4B instruct model at INT4 / INT8 is the likely sweet spot on Intel NPU. Quality for "restructure rambling speech" is probably better at 3.8B to 4B, but latency on NPU is unmeasured for those sizes.
- Phi-4-mini (MIT) is the simplest licence for a company-wide open-source project. Qwen3 small tiers and Gemma 4 E2B are the next choices.
- Benchmark needed: run each candidate on the target Intel NPU and Snapdragon device with the actual cleanup prompts, and record tok/s, time-to-first-token, and error rate on filler removal. No number from this research should be used for the budget.

### Gaps
- No measured tok/s on Intel Core Ultra 2026 NPUs from an official source.
- No measured Snapdragon 8 Elite Gen 5 tok/s for a text-only 1.5B to 4B LLM from an official source (GenieX gives none).
- Exact quantisation recipe for Intel NPU (INT4 vs INT8 per model) was not confirmed against a model card.

---

## Key Question 4: On-device speech-to-text for the voice pipeline

### Takeaway
On Android the best-verified option is **Whisper-Small (or Tiny) on Snapdragon via ORT QNN**, with a real-time factor of about 0.09 to 0.13 in one developer's test. On Windows, no verified NPU path for Whisper or Moonshine on Intel was found. Moonshine is MIT-licensed for its code and streaming models and is a credible CPU fallback, but NPU execution for it is unverified. Windows built-in speech (Windows.Media.SpeechRecognition or Windows AI Speech) was not researched in this pass (gap).

### Cited Findings
- Whisper-Small on Snapdragon 8 Elite Gen 5 via ORT QNN: aggregate RTF **0.093** for 385 s of audio (34.6 s processing); **0.130** with temperature-fallback retries. Whisper-Tiny encoder 22.0 to 33.7 ms, decoder 1.73 ms per token. CPU log-mel front end took 0.32 to 0.80 s. Single developer, 48 clips, anecdotal. [GitHub: itsallgoody/onnxruntime-qnn-snapdragon-8-elite-gen5](https://github.com/itsallgoody/onnxruntime-qnn-snapdragon-8-elite-gen5)
- Qualcomm AI Hub provides a precompiled Whisper-Tiny export used in that build. [GitHub: itsallgoody/...](https://github.com/itsallgoody/onnxruntime-qnn-snapdragon-8-elite-gen5)
- Moonshine: code and streaming models are MIT; only legacy non-streaming non-English models are under the non-commercial Moonshine Community License. Moonshine's C++ core uses ONNX Runtime; the Python package works on Windows. The Moonshine Medium Streaming (245M) reports 6.65% average WER and Tiny (34M) 12.00% on Moonshine's own Open ASR Leaderboard figures. **[UNVERIFIED]** sizes conflict across sources (Tiny listed as 26 MB, 27M, and 5.8M parameters). [STT Index: Moonshine](https://speechtotext.dev/model/moonshine/); [Moonshine vs Whisper benchmark (2026)](https://modelslab.com/blog/audio-generation/moonshine-vs-whisper-asr-real-time-speech-2026)
- sherpa-onnx runs on ONNX Runtime with CPU, GPU, NPU, mobile, and WASM targets. Its Moonshine NPU support was not verified. [Search summary; not fetched]

### Inferences
- Android STT: ORT QNN + Whisper-Small (or a streaming alternative if latency matters) fits the existing ONNX pipeline and shares the QNN packaging work from Key Question 2.
- Windows STT: run Whisper or Moonshine on the CPU via ORT, or check whether OpenVINO accepts the Whisper encoder on NPU. Not verified. Do not budget NPU STT on Windows until tested.

### Gaps
- Windows built-in speech APIs (Windows AI Speech, Windows.Media.SpeechRecognition) were not researched. They may be the simplest Windows voice path, and that research should be done before choosing.
- No verified OpenVINO NPU path for Whisper or Moonshine on Intel.
- Voice cleanup latency budget (STT + LLM cleanup end-to-end) was not measured by anyone in these sources.

---

## Key Question 5: Fallback when there is no NPU (CPU/GPU path and how the client decides)

### Takeaway
The fallback is the ORT CPU EP (included in Windows ML) or the OpenVINO CPU/GPU device on Intel, and the QNN GPU or CPU path on Snapdragon. The client should probe for hardware at startup, pick the first available device from a fixed priority list, and cache the result per device. The exact probing API is inferred from the Windows ML docs and was not verified in code.

### Cited Findings
- Windows ML includes the ORT CPU EP and DirectML (legacy GPU) by default. OpenVINO (Intel), QNN (Qualcomm), and VitisAI (AMD) are downloaded on demand from the catalog. [Microsoft Learn: Windows ML execution providers](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/supported-execution-providers)
- Windows ML's CPU row lists "OpenVINO (Intel)" as well as the ORT CPU EP as the universal fallback. [Microsoft Learn: Accelerate AI models with Windows ML](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/accelerate-ai-models)
- LiteRT's NPU path already supports GPU fallback (NPU preferred, GPU second) on Android. [Google Developers Blog](https://developers.googleblog.com/unlocking-peak-performance-on-qualcomm-npu-with-litert/)
- ORT's OpenVINO EP falls back unsupported ops to CPU, which can cause large slowdowns. The QNN doc shows the same caveat. [ONNX Runtime: OpenVINO EP](https://onnxruntime.ai/docs/execution-providers/OpenVINO-ExecutionProvider.html); [ONNX Runtime: QNN EP](https://onnxruntime.ai/docs/execution-providers/QNN-ExecutionProvider.html)

### Inferences
- Priority list per platform: Windows = OpenVINO NPU, then OpenVINO GPU, then ORT CPU (INT4 model). Android = QNN HTP NPU, then GPU, then CPU. The client should check that the chosen EP really ran the graph (ops not placed on CPU), since silent CPU fallback can make the NPU look enabled when it is not.
- The client should expose the chosen backend in the UI or logs, so slow cleanup is not blamed on the model.
- Note: the CPU fallback path has its own cost. A 1.5B INT4 model on a laptop CPU is usable for cleanup and short summaries but is not benchmarked here.

### Gaps
- No verified .NET API for enumerating EPs and their device availability. The Windows ML `ExecutionProviderCatalog` APIs are named but not shown in code.
- No CPU-only tok/s figures for candidate models on these laptops.

---

## Key Question 6: Can a cheap small model act as the server-side "Skimmer Bouncer", and local vs cloud

### Takeaway
Yes, a small model can do the Skimmer Bouncer job, but the server host has no NPU, so it would run on CPU or a cheap hosted API. A small model on CPU is fine for a binary relevance gate on short GC messages, but it is not free to run 24/7 per message unless the model is very small. This was not researched in depth (no cloud pricing or CPU latency numbers gathered). Treat this section as design reasoning, not measured.

### Cited Findings
- The Skimmer Bouncer is marked PROPOSED in the design doc: a cheap model skims each GC message unless @mentioned and wakes the expensive agent only when relevant. Source: `/tmp/claude-0/-home-user-Bazaar/c63e21e3-9139-50bc-9f48-e82954f68a61/scratchpad/files-zip-new/Bazaar.md` (line 34 and table at line 79). Not a web source.
- Small instruct models in the 0.6B to 1.7B range (Qwen3 tiers, Gemma 4 E2B) are listed as Apache 2.0 by secondary and HF sources (see Key Question 3). These can run on CPU, but CPU latency and throughput for the server host were not measured.

### Inferences
- Local-on-client skimming is not a fit for the server-side bouncer: the bouncer must see GC messages even when no client is open, so it has to run on the server. A client-side NPU model only helps the client's own cleanup and summary tasks.
- Two server options: (a) a hosted cheap model API (no hardware needed; per-message cost; no verified pricing here), or (b) a self-hosted 0.6B to 1.7B model on server CPU (fixed cost; latency fine for relevance gating; needs a benchmark). A rules or keyword prefilter in front of either is cheaper than both and should be considered first.
- Keep the bouncer decision separate from the client NPU tier: different host, different failure mode.

### Gaps
- No pricing for any cloud small model was gathered. No CPU latency numbers for 0.6B to 1.7B models on a typical server host were gathered.
- No evaluation of bouncer accuracy (false negatives on relevance) was done.

---

## Summary of Corrections to the Design Docs

1. **Intel NPU is OpenVINO, not DirectML.** `Bazaar.Windows.md` line 39 says "NPU execution-provider selection (DirectML vs QNN)." On Intel it should say "OpenVINO EP (NPU), with CPU/GPU fallback." DirectML is legacy in Windows ML and is not in the NPU row. QNN is the Snapdragon-on-Windows EP and is not relevant to Intel.
2. **Windows ML catalog vs ORT OpenVINO plugin EP.** The Windows ML catalog is the Microsoft-supported route for .NET; bring-your-own is for pinning.
3. **Android ORT QNN is viable, but not the only tested route.** GenieX (LLM) and LiteRT QNN (AOT + Play AI Packs) also exist. Decide based on whether one ONNX pipeline is worth the .NET-for-Android interop cost.
4. **Pin driver and OpenVINO together.** Compiled blobs are not guaranteed portable across versions.
5. **Avoid NPU driver builds 32.0.100.4300, 32.0.100.4509, and 32.0.100.4512** (crash fix per OpenVINO release notes); target 32.0.100.5540 or later.

## Source Quality Notes

- Microsoft Learn, ONNX Runtime, OpenVINO docs, Intel, Qualcomm GitHub, and Google Developers Blog are primary sources.
- The Android QNN build details, Whisper RTF numbers, and the Snapdragon 8 Elite Gen 5 setup come from one developer's repo. Treat them as a feasibility signal, not a benchmark.
- Parameter counts and licences for Phi-4-mini, Qwen3, and Moonshine sizes came mostly from secondary blogs and conflict in places. Verify against Hugging Face model cards before choosing.
- Several Intel tok/s figures found are old or converted from words/s. None is usable for budgeting.
- The Windows ML docs page states it was updated 2026-10-07. The Intel driver page lists 8/28/2026. The Google LiteRT blog date was not captured.
