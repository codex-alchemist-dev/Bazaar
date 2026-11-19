# Constellation GPU benchmark regression gate

Status: implementation detail of `constellation-scale-and-lod.md` section 7 (benchmark plan). No budget numbers are decided here.

## What it does
`Bazaar.Constellation.Vulkan.Bench` writes a CSV of GPU timings per scenario. The gate compares a candidate CSV against a stored baseline CSV from the same machine and fails when a scenario got slower.

- A row is identified by `section`, `total_nodes` and `instances_drawn`.
- Both `gpu_p50_ms` and `gpu_p95_ms` are compared.
- A row regresses only when the candidate is slower than the baseline by more than the ratio (default 1.25) **and** by more than the absolute floor (default 0.25 ms). The floor stops sub-millisecond timer noise from failing the gate. Run-to-run spread on the Arc B580 was under 1% at the 10M-instance row (design doc section 10.3 once #524 lands).
- A baseline row the candidate no longer measures fails the gate. A new candidate row is reported and passes.
- Exit codes: 0 passed, 1 regression or missing row, 2 bad input.

## Where it runs
GPU timings only mean something on the same GPU and driver as the baseline. Hosted CI runners have no GPU, so the gate is **not** a required PR check. It runs on a self-hosted runner that has a GPU, with the baseline stored per machine. The runner needs the `gpu` label.

## Workflow
`.github/workflows/gpu-bench.yml` runs the bench and the gate on a self-hosted runner labelled `gpu` (`[self-hosted, windows, gpu]`), on a weekly schedule and on demand, never on pull requests, so untrusted code cannot reach that machine. The baseline lives at `tests/Bazaar.Constellation.Vulkan.BenchGate/baselines/pc-gpu.csv`; with no baseline the job records the run as an artifact and passes.

## Usage
`dotnet run --project tests/Bazaar.Constellation.Vulkan.BenchGate -- baseline.csv candidate.csv [maxRatio] [absoluteFloorMs]`
