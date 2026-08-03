#!/usr/bin/env bash
# Does the RECEIVE slab scale with CONNECTIONS, and does a big page multiply it?
#
# WHY THIS EXISTS. RESULTS.md recorded "neither Linux backend has the Windows per-socket
# receive slab", on the strength of an RSS table showing epoll flat (72 -> 73 MB) across a 4KB -> 64KB
# page. Inspection says otherwise: EpollShard._recvBuffer is PinnedWriteBufferPool(_socketsPerShard,
# _bufSize), commented "one per live connection", leased at accept and released at close - i.e. exactly
# the per-SOCKET scaling that took a 12-shard RIO server from 283MB to 3,163MB on Windows.
#
# The table is not wrong, it is under-powered. The slab is one NativeMemory.AllocZeroed - an anonymous
# mmap at that size - so pages fault in on first TOUCH. At -c 64 over 8 shards only 64 of the 4096
# per-socket buffers are ever touched, so resident receive memory is (connections x page), not
# (SocketsPerShard x page). The effect was there; it was ~3.8MB and sat under the noise.
#
# Note also that `--page N` rescales three POOL depths to 4MB/N (SmokeTest/Program.cs) precisely so a big
# page cannot multiply into gigabytes - and the per-socket receive slab is not a pool count, so it is not
# covered by that cap. That is the hole this rig measures.
#
# PRE-REGISTERED PREDICTIONS (write them down before running, so the run can falsify something):
#
#   1. epoll RSS rises with CONNECTIONS x PAGE. Going 64 -> 2048 connections should cost ~8MB at a 4KB
#      page and ~127MB at a 64KB page: a delta-of-deltas of roughly 16x, the page ratio.
#   2. io_uring stays roughly FLAT in connections, because its read pool is per-SHARD
#      (BufferPagesPerShard=256 entries) and does not scale with connection count at all.
#   3. If epoll is flat at 2048 connections, the correction is WRONG and the original claim stands.
#
# Pools are pinned explicitly (AFTER --page, which would otherwise rescale them) so that what moves is
# the page and the connection count, not pool depth. That control is why the previous page/memory
# attribution had to be withdrawn once - see "The co-variation control" in RESULTS.md.
#
# Usage:  ./run-recv-slab.sh
#         CONNS="64 512 2048" PAGES="4096 65536" ./run-recv-slab.sh
set -uo pipefail

CONNS=${CONNS:-"64 2048"}
PAGES=${PAGES:-"4096 65536"}
BACKENDS=${BACKENDS:-"epoll io-uring"}
# Empty = receive follows --page (the historical coupling). A number splits them via
# SocketSetOptions.ReceiveBufferSize, which epoll and io_uring only began honouring on 2026-07-28. Setting
# it to 4096 alongside PAGES=65536 is the FIX ARM: if the slab is what scales, a 64KB send page with a 4KB
# receive buffer should track the p4K row's memory while keeping the p64K row's send behaviour. Before the
# 2026-07-28 change this arm was indistinguishable from the plain p64K one - the flag parsed and did
# nothing, while the banner printed the recvbuf= it had ignored.
RECV_BUFFER=${RECV_BUFFER:-}
SHARDS=${SHARDS:-8}
BODY=${BODY:-512}          # small: this is about the per-connection RECEIVE buffer, not the response
DURATION=${DURATION:-12s}
PORT=${PORT:-5099}         # not 5080: never collide with a size sweep that may still be running
# Pool depths held constant across page sizes. Applied AFTER --page so they win.
POOL_DEPTH=${POOL_DEPTH:-256}

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${OUT:-$REPO/bench/results}"
BOMB="$REPO/bench/.tools/bombardier"
SMOKE="$REPO/SmokeTest/bin/Release/net10.0/SmokeTest"

mkdir -p "$OUT"
STAMP=$(date +%Y%m%d-%H%M%S)
CSV="$OUT/recv-slab-$STAMP.csv"

for tool in jq curl taskset; do
  command -v "$tool" >/dev/null || { echo "missing required tool: $tool"; exit 1; }
done
[[ -x "$BOMB" ]] || { echo "missing $BOMB (run another rig once to fetch it)"; exit 1; }

echo "building SmokeTest (Release)..."
dotnet build "$REPO/SmokeTest/SmokeTest.csproj" -c Release -v q --nologo >/dev/null || { echo "build failed"; exit 1; }

source "$REPO/bench/cpu-split.sh"

echo "backend,page,conns,shards,rss_idle_kb,rss_peak_kb,rps,status" > "$CSV"

echo
echo "receive-slab scaling: ${BACKENDS// / } x pages ${PAGES// / } x conns ${CONNS// / }"
echo "  shards=$SHARDS body=$BODY pools pinned at $POOL_DEPTH  server=$SERVER_CPUS client=$CLIENT_CPUS"
echo "  csv: $CSV"
echo

for backend in $BACKENDS; do
  for page in $PAGES; do
    for conns in $CONNS; do
      # --page rescales three pool depths; pin them AFTER it so page is the only thing that varies.
      args=(--http --"$backend" -n "$SHARDS" -z "$BODY" --page "$page"
            --write-buffers "$POOL_DEPTH" --oob-write-buffers "$POOL_DEPTH"
            --buffer-pages "$POOL_DEPTH" --port "$PORT")
      [[ -n "$RECV_BUFFER" ]] && args+=(--recv-buffer "$RECV_BUFFER")
      taskset -c "$SERVER_CPUS" "$SMOKE" "${args[@]}" >/tmp/recv-slab.$$.log 2>&1 &
      pid=$!

      banner=""
      for ((i=0;i<80;i++)); do
        banner=$(grep -m1 'http-bench:' /tmp/recv-slab.$$.log 2>/dev/null) && [[ -n "$banner" ]] && break
        sleep 0.25
      done
      if [[ -z "$banner" ]]; then
        echo "  $backend p$page c$conns: NOSTART"
        echo "$backend,$page,$conns,$SHARDS,,,,NOSTART" >>"$CSV"
        kill $pid 2>/dev/null; wait $pid 2>/dev/null; continue
      fi
      # Trust the banner, not the flag that was passed (bench/README.md rule 5). Check recvbuf too: this
      # rig exists because that field was printed for years by backends that did not honour it.
      want_recv=${RECV_BUFFER:-$page}
      if [[ "$banner" != *"page=$page "* || "$banner" != *"recvbuf=$want_recv "* ]]; then
        echo "  $backend p$page c$conns: BANNER MISMATCH -> $banner"
        echo "$backend,$page,$conns,$SHARDS,,,,MISMATCH" >>"$CSV"
        kill $pid 2>/dev/null; wait $pid 2>/dev/null; continue
      fi

      idle=$(awk '/^VmRSS:/{print $2}' /proc/$pid/status 2>/dev/null)

      # Load first, sample throughout: untouched pages are not resident, so idle RSS says nothing here.
      taskset -c "$CLIENT_CPUS" "$BOMB" -k -o json -p r -c "$conns" -d "$DURATION" -t 10s \
        "http://127.0.0.1:$PORT/" >/tmp/recv-slab-bomb.$$.json 2>/dev/null &
      bomb=$!
      peak=0
      while kill -0 $bomb 2>/dev/null; do
        cur=$(awk '/^VmRSS:/{print $2}' /proc/$pid/status 2>/dev/null)
        [[ -n "$cur" && "$cur" -gt "$peak" ]] && peak=$cur
        sleep 0.2
      done
      wait $bomb 2>/dev/null
      rps=$(jq -r '.result.rps.mean // empty' </tmp/recv-slab-bomb.$$.json 2>/dev/null)

      kill $pid 2>/dev/null; wait $pid 2>/dev/null; sleep 1
      printf "  %-9s p%-6s c%-5s idle %7s KB  peak %8s KB  %9.0f rps\n" \
        "$backend" "$page" "$conns" "${idle:-?}" "$peak" "${rps:-0}"
      echo "$backend,$page,$conns,$SHARDS,${idle:-},$peak,${rps%.*},ok" >>"$CSV"
    done
  done
done
rm -f /tmp/recv-slab.$$.log /tmp/recv-slab-bomb.$$.json

echo
echo "=== peak RSS (KB) under load ==="
awk -F, 'NR>1 && $6!="" {printf "%-9s p%-7s c%-6s %10s KB\n",$1,$2,$3,$6}' "$CSV"
echo
echo "Read it as a DELTA-OF-DELTAS: (epoll c2048 - epoll c64) should be ~16x larger at p64K than at p4K"
echo "if the receive slab is per-socket, and io_uring should barely move in connections at all."
echo "csv: $CSV"
