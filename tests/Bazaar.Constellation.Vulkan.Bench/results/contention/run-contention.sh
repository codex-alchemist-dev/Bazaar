#!/bin/bash
# Section 10.12 matrix. Each load keeps one bgload process alive while the variants run.
cd "$(dirname "$0")/.." || exit 1
EXE=bin/Release/net11.0/Bazaar.Constellation.Vulkan.Bench.dll
OUT=results/contention
mkdir -p $OUT
STOP=/v/git/Bazaar-cull/bg.stop

run_load() {  # name cpuThreads gpu(0|1) variants...
  local name=$1 cpu=$2 gpu=$3; shift 3
  rm -f $STOP
  if [ "$name" != "none" ]; then
    dotnet $EXE bgload $STOP $cpu $gpu > /dev/null 2>&1 &
    BG=$!
    sleep 6
  fi
  for v in "$@"; do
    local prio="" args=""
    case $v in
      base) ;;
      prio-high) prio=high ;;
      prio-realtime) prio=realtime ;;
      parts2) args="parts=2" ;;
      parts4) args="parts=4" ;;
      fif2) args="fif=2" ;;
      fif3) args="fif=3" ;;
      prio-high+fif3) prio=high; args="fif=3" ;;
    esac
    echo "== $name $v"
    BAZAAR_BENCH_BOOST=1 BAZAAR_BENCH_GPU_PRIORITY=$prio dotnet $EXE topology-contention $OUT/$name-$v.csv $args 2>&1 | grep -E "^contention"
  done
  if [ "$name" != "none" ]; then
    touch $STOP
    wait $BG 2>/dev/null
  fi
  rm -f $STOP
}

run_load none 0 0 base
run_load cpu2 2 0 base
run_load cpu4 4 0 base
run_load cpu8 8 0 base
run_load gpu 0 1 base prio-high prio-realtime parts2 parts4 fif2 fif3
run_load both 2 1 base prio-high prio-realtime parts2 parts4 fif2 fif3 prio-high+fif3
echo ALLDONE
