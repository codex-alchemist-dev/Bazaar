#!/bin/bash
# Section 10.13 matrix: D3DKMT scheduling classes under the GPU-only load, then the swapchain cadence and latency runs, alone and under the load.
cd "$(dirname "$0")/../.." || exit 1
EXE=bin/Release/net11.0/Bazaar.Constellation.Vulkan.Bench.dll
OUT=results/present
STOP=$(pwd)/bg.stop

start_load() {  # bgClass
  rm -f $STOP
  BAZAAR_BENCH_D3DKMT_CLASS=$1 dotnet $EXE bgload $STOP 0 1 > $OUT/bgload-$1.log 2>&1 &
  BG=$!
  sleep 6
}
stop_load() { touch $STOP; wait $BG 2>/dev/null; rm -f $STOP; }

echo "== D3DKMT classes, GPU-only load"
start_load ""
for c in none abovenormal high realtime; do
  echo "-- bench class $c"
  cls=$c; [ "$c" = none ] && cls=""
  BAZAAR_BENCH_BOOST=1 BAZAAR_BENCH_D3DKMT_CLASS=$cls dotnet $EXE topology-contention $OUT/d3dkmt-bench-$c.csv 2>&1 | grep -E "^(contention|D3DKMT)"
done
stop_load
for c in belownormal idle; do
  echo "-- background class $c"
  start_load $c
  head -3 $OUT/bgload-$c.log | grep D3DKMT
  BAZAAR_BENCH_BOOST=1 dotnet $EXE topology-contention $OUT/d3dkmt-bg-$c.csv 2>&1 | grep -E "^contention"
  stop_load
done

echo "== swapchain, alone"
BAZAAR_BENCH_BOOST=1 dotnet $EXE topology-present $OUT/present-none.csv 2>&1 | grep -E "^present modes"
echo "== swapchain, GPU-only load"
start_load ""
BAZAAR_BENCH_BOOST=1 dotnet $EXE topology-present $OUT/present-gpu.csv 2>&1 | grep -E "^present modes"
stop_load
echo ALLDONE
