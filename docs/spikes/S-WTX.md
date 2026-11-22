# Spike S-WTX: Wire transports and codecs benchmark (part 1, codec half, cloud)

Branch: `agent/wire-bench` (harness in `tools/Bazaar.Wire.Bench` and `tools/Bazaar.Wire.Bench.Aot`; this record and `S-WTX-codec-raw.tsv` are the only docs). Design source: `docs/design/90-wire-transports-and-codecs.md` section 10 (status PROPOSED).

## Question
Which wire codec should be the default preference, and which candidates join the wave: bytes (raw, gzip, brotli), encode and decode time, allocations per frame, and AOT publish size and warnings, for json, cbor, msgpack, protobuf and memorypack, against today's JSON baseline? This record covers the codec half only. The transport half is not run (see "Not measured here").

## Pass criteria (fixed before the first run)
Copied from doc 90 section 10. **Correctness is mandatory for every module** (conformance suite green; foreign-client vectors round-trip; fuzz clean). **Performance decides only the default preference order and which candidates join the wave, not whether the five named transports and four named codecs ship.** Report against JSON as the baseline; three process runs per row. Fail consequence: none for shipping; the numbers set the default preference order only.

## Time-box
Part 1 (codecs, cloud container): one session.

## Method
- Harness: `tools/Bazaar.Wire.Bench`, a console app. `--all` starts one new process per codec and run (5 codecs x 3 runs = 15 processes; no state carries over). Each process builds the corpus from the fixed seed, checks that a decode returns the input (page item 200, last snapshot node and edges, last replay message; a mismatch aborts the run), then measures. BenchmarkDotNet is not in `Directory.Packages.props`, so the harness uses `Stopwatch` and `GC.GetAllocatedBytesForCurrentThread`: at least 400 ms of warm-up, then seven timed rounds of about 250 ms each, with a full GC before each round. A run's figure is the median of its seven rounds; the tables show all three runs and their median.
- What one frame costs: encode is `EncodePayload` plus `Encode` of the envelope into a reused `ArrayBufferWriter` (what a server does per frame); decode is `Decode` plus `DecodePayload`. Single thread, no I/O, no transport, no compression in the timed path.
- Codecs, all through `IWireCodec`: `json` (`JsonWireCodec` in Bazaar.Contracts, the baseline), `cbor`, `msgpack`, `protobuf`, `memorypack` (the four optional modules, from the merged branches below). Default decode limits.
- Corpora (**all synthetic, nothing real**):
  - S6-shaped corpus, regenerated here: 10,000 `gc.message` payloads (`WireGcMessage`), seed 6, 60% short chat and 40% long prose with code blocks and non-ASCII text, 20 GCs, 12 authors, 15% with mentions, 20% replies, 5% edited. The S6 generator lives on `spike/S6` and was not reused, so the text is not the same as S6's and **absolute sizes differ from S6.md** (here the JSON replay averages 756 bytes a frame; S6 had 401). Only comparisons inside this record are valid. Page = one `gc.page` response (`GcPage`) with the first 200 messages; replay = 10,000 `gc.message` event frames.
  - Constellation snapshot: `GraphSnapshot` with 5,000 nodes and 20,000 edges, seed 7, **synthetic** (random endpoints, node ids like `vault/<word>/<word>-00042.md`, `objective-00007`, `phase-00001`; edge kinds weighted toward wiki links). Real vault graphs have clustering and different id shapes, so these numbers show the shape of the cost, not a prediction.
- Compression: .NET `GZipStream` and `BrotliStream` at `CompressionLevel.Optimal` over the whole frame (page, snapshot) or over the 10,000 frames concatenated (replay), in run 1 only (raw sizes were identical in all three runs). Per-frame compression is not measured. In .NET, Optimal is not the maximum for brotli, which is why brotli can come out larger than gzip below.
- AOT: `tools/Bazaar.Wire.Bench.Aot` (one tiny app, `-p:Codec=<name> -p:AotPublish=true`) encodes and decodes one `gc.message` and prints the result, so the codec is really used. Published with `dotnet publish -c Release`, linux-x64, Native AOT, with `TreatWarningsAsErrors=false` so warnings are counted and not fatal (the protobuf app was also published once with the repo default, warnings as errors, and succeeded).
- Environment: Linux cloud container (Ubuntu 24.04.5, kernel 6.18.44), 4 vCPU Intel Xeon @ 2.10 GHz, 16 GB RAM (15 GB usable), .NET SDK 11.0.100-rc.3.26522.148 (the preview SDK installed here; `global.json` asks for 11.0.100, overridden locally and not committed), Release build, default workstation concurrent GC. Shared virtualised vCPUs: expect noise, see the spread in the tables. Package versions: MessagePack 3.1.12, Google.Protobuf 3.36.2, MemoryPack.Core 1.22.0, System.Formats.Cbor 11.0.0.
- Branch assembly: started from `origin/agent/wire-quic`, merged `agent/wire-msgpack`, `agent/wire-protobuf` and `agent/wire-memorypack` (no rebase). The merges conflicted in the generator project (two parallel `TypeResolver` classes, the codec-name list, a duplicate diagnostic id `BWG007`) and in the solution, package and doc 90 files. Resolved by keeping both sides, deleting the protobuf branch's static `TypeResolver`, taking the memorypack branch's `CborEmitter`, and renumbering memorypack's `TooManyFields` to `BWG010`. The generator, all four codec test projects and the architecture tests pass on the merged tree (see Correctness).

## Runs
Three process runs per row; run-by-run figures are in the tables, the raw rows (including the fastest and slowest round of each run) in `docs/spikes/S-WTX-codec-raw.tsv` (`codec, run, corpus, metric, value`). Baseline is JSON; ratios are against its median, and below 1.00x is better than JSON.

#### S6 200-message page (one gc.page frame)

Bytes (identical in every run; compression measured in run 1). Ratio is against JSON.

| Codec | Raw bytes | vs JSON | gzip | brotli |
|---|---|---|---|---|
| json | 123,956 | 1.00x | 29,493 | 30,683 |
| cbor | 114,178 | 0.92x | 29,640 | 31,137 |
| msgpack | 115,795 | 0.93x | 29,726 | 31,257 |
| protobuf | 87,240 | 0.70x | 27,098 | 29,030 |
| memorypack | 92,063 | 0.74x | 28,044 | 29,781 |

Time and allocations per frame (microseconds; bytes). Each cell: the three process runs, then the median of those. Each run's figure is the median of seven timed rounds.

| Codec | Encode us, runs 1 / 2 / 3 | Encode median | vs JSON | Decode us, runs 1 / 2 / 3 | Decode median | vs JSON | Encode alloc B | Decode alloc B |
|---|---|---|---|---|---|---|---|---|
| json | 588.1 / 565.3 / 580.9 | 580.9 | 1.00x | 504.7 / 518.5 / 520.3 | 518.5 | 1.00x | 276,650 | 363,822 |
| cbor | 400.8 / 1,072.0 / 833.7 | 833.7 | 1.44x | 510.3 / 702.8 / 678.3 | 678.3 | 1.31x | 573,550 | 736,736 |
| msgpack | 135.8 / 198.4 / 184.7 | 184.7 | 0.32x | 221.0 / 400.8 / 374.1 | 374.1 | 0.72x | 456,284 | 381,080 |
| protobuf | 134.1 / 151.1 / 135.5 | 135.5 | 0.23x | 207.3 / 249.6 / 217.6 | 217.6 | 0.42x | 265,574 | 559,624 |
| memorypack | 44.2 / 47.0 / 49.4 | 47.0 | 0.08x | 103.0 / 105.6 / 86.9 | 103.0 | 0.20x | 92,616 | 275,252 |

#### S6 10,000-frame replay (gc.message frames; per-frame figures)

Bytes (identical in every run; compression measured in run 1). Ratio is against JSON.

| Codec | Raw bytes | vs JSON | gzip | brotli |
|---|---|---|---|---|
| json | 7,559,301 | 1.00x | 1,535,297 | 1,419,125 |
| cbor | 6,784,198 | 0.90x | 1,541,168 | 1,448,325 |
| msgpack | 6,698,814 | 0.89x | 1,534,993 | 1,439,317 |
| protobuf | 5,227,742 | 0.69x | 1,399,675 | 1,381,499 |
| memorypack | 5,724,248 | 0.76x | 1,518,001 | 1,444,592 |

Time and allocations per frame (microseconds; bytes). Each cell: the three process runs, then the median of those. Each run's figure is the median of seven timed rounds.

| Codec | Encode us, runs 1 / 2 / 3 | Encode median | vs JSON | Decode us, runs 1 / 2 / 3 | Decode median | vs JSON | Encode alloc B | Decode alloc B |
|---|---|---|---|---|---|---|---|---|
| json | 1.73 / 1.58 / 1.54 | 1.58 | 1.00x | 3.30 / 3.11 / 3.06 | 3.11 | 1.00x | 4,832 | 2,684 |
| cbor | 3.70 / 3.35 / 3.57 | 3.57 | 2.27x | 4.41 / 4.67 / 4.59 | 4.59 | 1.47x | 7,590 | 7,193 |
| msgpack | 0.71 / 0.69 / 0.71 | 0.71 | 0.45x | 1.29 / 1.37 / 1.35 | 1.35 | 0.43x | 2,154 | 2,211 |
| protobuf | 0.71 / 0.71 / 0.67 | 0.71 | 0.45x | 1.88 / 1.79 / 1.59 | 1.79 | 0.58x | 1,616 | 11,576 |
| memorypack | 0.50 / 0.49 / 0.48 | 0.49 | 0.31x | 0.56 / 0.52 / 0.50 | 0.52 | 0.17x | 792 | 1,576 |

#### Synthetic Constellation snapshot (5,000 nodes, 20,000 edges, one frame)

Bytes (identical in every run; compression measured in run 1). Ratio is against JSON.

| Codec | Raw bytes | vs JSON | gzip | brotli |
|---|---|---|---|---|
| json | 1,626,479 | 1.00x | 275,682 | 245,041 |
| cbor | 1,593,483 | 0.98x | 285,897 | 276,798 |
| msgpack | 1,569,782 | 0.97x | 283,726 | 278,278 |
| protobuf | 1,141,403 | 0.70x | 277,050 | 256,360 |
| memorypack | 1,506,449 | 0.93x | 298,257 | 267,782 |

Time and allocations per frame (microseconds; bytes). Each cell: the three process runs, then the median of those. Each run's figure is the median of seven timed rounds.

| Codec | Encode us, runs 1 / 2 / 3 | Encode median | vs JSON | Decode us, runs 1 / 2 / 3 | Decode median | vs JSON | Encode alloc B | Decode alloc B |
|---|---|---|---|---|---|---|---|---|
| json | 4,482.5 / 4,749.6 / 4,615.4 | 4,615.4 | 1.00x | 14,757.2 / 14,805.7 / 16,470.5 | 14,805.7 | 1.00x | 4,196,041 | 8,297,323 |
| cbor | 13,308.3 / 13,998.3 / 13,051.6 | 13,308.3 | 2.88x | 20,032.1 / 20,215.5 / 19,557.2 | 20,032.1 | 1.35x | 7,385,701 | 15,873,152 |
| msgpack | 2,602.6 / 2,449.8 / 2,418.9 | 2,449.8 | 0.53x | 6,379.3 / 6,491.5 / 6,449.5 | 6,449.5 | 0.44x | 4,194,416 | 6,820,539 |
| protobuf | 4,211.8 / 4,036.9 / 4,177.4 | 4,177.4 | 0.91x | 5,431.3 / 5,998.2 / 5,473.5 | 5,473.5 | 0.37x | 4,099,739 | 9,490,267 |
| memorypack | 2,816.1 / 2,690.7 / 2,705.1 | 2,705.1 | 0.59x | 3,998.1 / 3,652.1 / 3,498.9 | 3,652.1 | 0.25x | 2,623,273 | 5,720,683 |

### AOT publish (linux-x64, Native AOT, one frame encoded and decoded)

| Codec | Executable bytes | vs JSON | Warnings (all categories) | IL warnings | App runs |
|---|---|---|---|---|---|
| json | 3,814,360 | 1.00x | 0 | 0 | yes |
| cbor | 2,100,216 | 0.55x | 0 | 0 | yes |
| msgpack | 2,034,136 | 0.53x | 0 | 0 | yes |
| protobuf | 3,578,072 | 0.94x | 0 | 0 | yes |
| memorypack | 1,968,504 | 0.52x | 0 | 0 | yes |

Caveats: one publish per codec (sizes are deterministic, so not repeated three times). The json app is larger partly because it carries `System.Text.Json` and the source-generated metadata for every payload type, which the other apps do not use; these are app-level sizes, not the cost of the codec alone, and an empty app's size is not subtracted. Windows and Android AOT sizes are not measured.

## Observations (facts from the tables, not decisions)
- **Size.** Protobuf is smallest on every corpus (0.69x to 0.70x of JSON raw). MemoryPack is next on messages (0.74x to 0.76x) but only 0.93x on the snapshot. CBOR and MessagePack save 7% to 11% on messages and 2% to 3% on the snapshot. After gzip or brotli the codecs are close together: on the page, gzip is 27,098 (protobuf) to 29,726 (msgpack) against 29,493 for JSON, so compression removes most of the gap; on the snapshot JSON compresses best (brotli 245,041 against 256,360 to 278,278).
- **Time.** MemoryPack is fastest on every row (replay frame: 0.49 us encode and 0.52 us decode against 1.58 and 3.11 for JSON). MessagePack and Protobuf are next on message frames (about 0.45x JSON encode, 0.43x to 0.58x decode on replay). Protobuf encode of the snapshot is only 0.91x JSON. **The CBOR codec is slower than JSON** on encode (1.44x page, 2.27x replay, 2.88x snapshot) and decode (1.31x to 1.47x), and allocates 1.6x to 2.7x JSON's bytes per frame. That is a finding about this `System.Formats.Cbor`-based module, not about CBOR as a format.
- **Allocations.** MemoryPack allocates least per frame on every row. Protobuf decode of a replay frame allocates 11,576 bytes against 2,684 for JSON (it builds generated message objects and maps them to the records).
- **AOT.** All five apps publish with zero warnings and run correctly. The protobuf app does not show the IL3050 and IL3053 warnings that S6 saw with Google.Protobuf 3.33.0; this tree pins 3.36.2.
- **Noise.** The page rows are the least stable: cbor encode ranged 400.8 to 1,072.0 us over three processes, msgpack decode 221.0 to 400.8 us. A page is one 100 KB frame, so a gen-2 GC or a noisy neighbour on a shared vCPU moves a round a lot. Replay and snapshot rows vary by up to about 20% between runs (protobuf replay decode 1.59 to 1.88 us). Use the replay rows for ranking.

## Correctness
- The harness aborts when a decoded value differs from the input, so every figure above is from a codec that round-tripped this corpus.
- On the merged tree, all pass with zero failures: generator tests 29, CBOR 175, MessagePack 178, Protobuf 127, MemoryPack 129 (these run the shared `Bazaar.Wire.Conformance` suite), architecture tests 56. Foreign-client vectors and fuzzing beyond the conformance suite's mutation tests were not run here.

## Not measured here
- The owner's Windows PC and an Android device: no numbers for either (doc 90 asks for all three platforms before the preference order is final).
- Every transport row: ws/h1, ws/h2, ws/h3, quic, webtransport, grpc (h2 and h3), grpc-web, and the Android Cronet HTTP/3 module against grpc over HTTP/2.
- Connect time (cold and resumed), time to first replay page, throughput at 1,000 msg/s, CPU per 1,000 msg/s.
- Head-of-line behaviour under 1% and 5% loss with 50 ms RTT (`tc netem`), and reconnect after a network change.
- Per-frame compression (only whole-buffer compression), brotli at maximum quality, multi-threaded throughput, server GC mode, Windows and Android AOT sizes, and candidate codecs beyond the five.
- A real Constellation snapshot (this one is synthetic) and the S6 generator's exact text.

## Result
inconclusive (part 1 only, one platform, synthetic data). On Linux in this container: MemoryPack is the fastest and allocates least but gains little on snapshot size; Protobuf is smallest; MessagePack is about 2.2x faster than JSON on replay frames and 7% to 11% smaller; the CBOR codec as built is slower than JSON. Compression narrows the size differences to a few percent. All five codecs publish under Native AOT with zero warnings. No codec failed correctness. The Windows and Android runs and every transport row remain open, and performance only sets the default order, so no module is dropped on these numbers.

## Consequence
- No DECISIONS-LOG change: doc 90 section 10 stays PROPOSED until the Windows, Android and transport rows exist; the preference order is to be set from the replay rows here plus the other platforms.
- Follow-up for the CBOR module: encode and decode are slower than JSON and allocate more; profile before the preference order is written down.
- Follow-up for the generator: two parallel type-resolver families now coexist (`WireTypeResolver` in `WireModel.cs` used by the CBOR and MemoryPack emitters, `TypeResolver` in `WireTypeModel.cs` used by the MessagePack and Protobuf emitters), and the diagnostic id clash (BWG007) was fixed here by renumbering. The wave branches should be reconciled into one resolver.
