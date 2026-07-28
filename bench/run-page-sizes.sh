#!/usr/bin/env bash
# Page size x payload, on the BARE SocketSet HTTP responder - no Kestrel, no bridge, no pipes.
#
# WHY THIS EXISTS, AND WHY IT IS NOT run-tls-sizes.sh WITH AN EXTRA FLAG.
#
# Two separate questions got tangled together and this rig exists to untangle them.
#
#  1. "Is BufferPageSize the right default?" It is ONE global constant shared by every backend, swept only
#     on Windows, where RIO wanted 64KB (4.68x at 256KB) and IOCP did not care (+-2%). It cannot move until
#     io_uring and epoll have been swept too. That is the blocker on TODO item 0.
#
#  2. "Why does goodput FALL from 64KB to 256KB?" Measured 2026-07-28 through AspNetDemo: every SocketSet
#     leg collapses at 256KB (epoll -36%, iouring -24%, both TLS legs -52%) while both Kestrel controls
#     RISE (+24%, +16%) in the same passes. Goodput falling as payload grows is a defect signature.
#
# Question 2 is why this runs the BARE responder. The AspNetDemo sweep has the Kestrel bridge in the path -
# two Pipes, a copy per write, a thread hop - so it cannot say whether the cliff is in the transport or in
# the bridge. Same client, same pinning, same payload, minus the bridge: the difference is the answer.
#
# PRE-REGISTERED, so the result can falsify something. TODO said both Linux backends should be roughly
# page-INSENSITIVE, because io_uring already dispatches one writev over an OutChain of segments (the same
# scatter-gather shape that made IOCP page-insensitive) and epoll sends directly from the TLS output
# buffer with no page copy at all. The 256KB collapse argues against that. So:
#   * If a bigger page removes the cliff, the prediction was WRONG, the Linux backends are page-sensitive
#     after all, and the shared default cannot be decided on the Windows evidence alone.
#   * If a bigger page does NOT remove it, page quantisation is not the mechanism, and the next suspects
#     are the Pending queue, segment management, or the out-of-band flush path - not buffer geometry.
#   * If the cliff is absent here but present through AspNetDemo, it is the BRIDGE and not the transport,
#     and TODO item 1 has been chasing the wrong component.
#
# THE CO-VARIATION TRAP, which already confounded one page sweep. `--page N` does not only set the page:
# SmokeTest rescales THREE pool depths to 4MB/N, to hold pinned memory roughly constant -
# WriteBuffersPerShard, OutOfBandWriteBuffersPerShard and BufferPagesPerShard. So a bare sweep varies page
# size AND pool depth together, and by a lot: 1024 buffers at a 4KB page against 64 at 64KB, a 16x swing.
# On Windows depth was shown not to matter at 256KB (2,387.7 vs 2,387.2 MiB/s, 0.02% apart) - but that is a
# Windows result at one payload. Set FIXED_POOL_DEPTH to pin all three and separate the two variables.
#
# WHICH BACKEND ACTUALLY NEEDS THE CONTROL (checked 2026-07-28, not assumed):
#   * io_uring reads ALL THREE - BufferPagesPerShard is its receive pool, WriteBuffersPerShard and
#     OutOfBandWriteBuffersPerShard its send pools. Its page sweep is confounded and needs this control.
#   * epoll reads NONE of them; EpollShard takes only BufferPageSize. So `--page` moves exactly one thing
#     for epoll, its sweep was never confounded, and its "insensitive at every page" result stands as-is.
# An earlier FIXED_WRITE_BUFFERS knob pinned only one of the three, so it was not a complete control.
#
# Usage:  ./run-page-sizes.sh
#         PAGES="4096 16384 65536 262144" SIZES="262144" ./run-page-sizes.sh
#         FIXED_POOL_DEPTH=1024 ./run-page-sizes.sh        # control for the co-variation above
#         BACKENDS="epoll" REPS=6 ./run-page-sizes.sh
set -uo pipefail

PAGES=${PAGES:-"4096 16384 65536"}
SIZES=${SIZES:-"512 16384 262144"}
BACKENDS=${BACKENDS:-"epoll io-uring"}
SHARDS=${SHARDS:-8}
DURATION=${DURATION:-8s}
WARMUP=${WARMUP:-2s}
CONNECTIONS=${CONNECTIONS:-64}
# First pass is discarded as host warm-up, so this is scored passes + 1.
REPS=${REPS:-4}
PORT=${PORT:-5081}
FILTER=${FILTER:-}
# Empty = let --page rescale the pools (the shipped behaviour). A number pins ALL THREE for every page
# size, which is what makes the sweep a controlled comparison. FIXED_WRITE_BUFFERS is the old name for
# this knob, kept working because TODO.md and one commit message cite it - but it pinned only one pool.
FIXED_POOL_DEPTH=${FIXED_POOL_DEPTH:-${FIXED_WRITE_BUFFERS:-}}
# Empty = receive buffer follows the page. A number splits them, as SocketSetOptions.ReceiveBufferSize
# does on Windows - where coupling them cost 3.0GB resident at a 64KB page because the receive slab is
# per-SOCKET. NOTE: honoured only by IOCP/RIO today, so on Linux this is currently inert. Kept so the
# harness is ready the moment the Linux backends learn it, and so a run records that it asked.
RECV_BUFFER=${RECV_BUFFER:-}

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${OUT:-$REPO/bench/results}"
BOMB="$REPO/bench/.tools/bombardier"
SMOKE="$REPO/SmokeTest/bin/Release/net10.0/SmokeTest"

mkdir -p "$OUT" "$(dirname "$BOMB")"
STAMP=$(date +%Y%m%d-%H%M%S)
CSV="$OUT/page-sizes-$STAMP.csv"
LOGS="$OUT/logs-page-sizes-$STAMP"
mkdir -p "$LOGS"

for tool in jq curl taskset shuf; do
  command -v "$tool" >/dev/null || { echo "missing required tool: $tool"; exit 1; }
done

if [[ ! -x "$BOMB" ]]; then
  echo "fetching bombardier..."
  curl -sSL -o "$BOMB" https://github.com/codesenberg/bombardier/releases/download/v1.2.6/bombardier-linux-amd64
  chmod +x "$BOMB"
fi

echo "building SmokeTest (Release)..."
dotnet build "$REPO/SmokeTest/SmokeTest.csproj" -c Release -v q --nologo >/dev/null || { echo "build failed"; exit 1; }

NCPU=$(nproc)
source "$REPO/bench/cpu-split.sh"

echo "size,page,backend,shards,writebufs,oobwritebufs,readpages,rep,rps,mib_s,lat_p50_us,lat_p99_us,errors,status" > "$CSV"

# The bare responder has no /config endpoint - it announces its geometry on stdout instead, which is why
# SmokeTest's http-bench banner reports page/recvbuf/writebufs and not just the backend. Parse it rather
# than trusting the flags: a --page that was ignored must not be recordable as a page sweep.
measure() { # $1=backend $2=page $3=size $4=rep
  local backend="$1" page="$2" size="$3" rep="$4"
  local log="$LOGS/$backend.p$page.s$size.r$rep.log"
  local args=(--http --$backend -n "$SHARDS" --page "$page" -z "$size" --port "$PORT")
  # All three, or it is not a control: --page rescales all three and io_uring reads all three.
  [[ -n "$FIXED_POOL_DEPTH" ]] && args+=(--write-buffers "$FIXED_POOL_DEPTH"
                                         --oob-write-buffers "$FIXED_POOL_DEPTH"
                                         --buffer-pages "$FIXED_POOL_DEPTH")
  [[ -n "$RECV_BUFFER" ]] && args+=(--recv-buffer "$RECV_BUFFER")

  taskset -c "$SERVER_CPUS" "$SMOKE" "${args[@]}" >"$log" 2>&1 &
  local pid=$! banner="" i
  for ((i=0;i<80;i++)); do
    banner=$(grep -m1 '^http-bench:' "$log" 2>/dev/null) && [[ -n "$banner" ]] && break
    sleep 0.25
  done
  if [[ -z "$banner" ]]; then
    echo "    ${backend} p${page}: NOSTART"
    echo "$size,$page,$backend,$SHARDS,,,,$rep,,,,,,NOSTART" >>"$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
  fi

  # Verify what actually loaded: the backend AND the page. Silent fallback and an ignored flag are
  # different failures and both would otherwise be recorded as a clean data point.
  local want_factory; case "$backend" in
    epoll)    want_factory="EpollFactory" ;;
    io-uring) want_factory="IoUringFactory" ;;
    *)        want_factory="" ;;
  esac
  if [[ -n "$want_factory" && "$banner" != *"backend=$want_factory"* ]] || [[ "$banner" != *"page=$page "* ]]; then
    echo "    ${backend} p${page}: MISMATCH -> $banner"
    echo "$size,$page,$backend,$SHARDS,,,,$rep,,,,,,MISMATCH" >>"$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
  fi
  # Record the pool depths that actually resulted, so the co-variation is visible in the CSV rather than
  # having to be recomputed from the page size later.
  local wb ob rp
  wb=$(sed -n 's/.*[^b]writebufs=\([0-9]*\).*/\1/p' <<<"$banner")
  ob=$(sed -n 's/.*oobwritebufs=\([0-9]*\).*/\1/p' <<<"$banner")
  rp=$(sed -n 's/.*readpages=\([0-9]*\).*/\1/p' <<<"$banner")
  # A control that silently failed to control is worse than no control - it would be written up as one.
  if [[ -n "$FIXED_POOL_DEPTH" ]] &&
     { [[ "$wb" != "$FIXED_POOL_DEPTH" ]] || [[ "$ob" != "$FIXED_POOL_DEPTH" ]] || [[ "$rp" != "$FIXED_POOL_DEPTH" ]]; }; then
    echo "    ${backend} p${page}: POOL NOT PINNED (want $FIXED_POOL_DEPTH, got wb=$wb oob=$ob rp=$rp)"
    echo "$size,$page,$backend,$SHARDS,$wb,$ob,$rp,$rep,,,,,,UNPINNED" >>"$CSV"
    kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
  fi

  local url="http://127.0.0.1:$PORT/"
  taskset -c "$CLIENT_CPUS" "$BOMB" -k -o json -p r -c "$CONNECTIONS" -d "$WARMUP" -t 10s "$url" >/dev/null 2>&1
  local j; j=$(taskset -c "$CLIENT_CPUS" "$BOMB" -k -l -o json -p r -c "$CONNECTIONS" -d "$DURATION" -t 10s "$url" 2>/dev/null)
  kill $pid 2>/dev/null; wait $pid 2>/dev/null; sleep 1

  local rps; rps=$(jq -r '.result.rps.mean // empty' <<<"$j")
  if [[ -z "$rps" ]]; then
    echo "    ${backend} p${page}: no result"
    echo "$size,$page,$backend,$SHARDS,$wb,$ob,$rp,$rep,,,,,,FAILED" >>"$CSV"; return
  fi
  local p50 p99 errs mib
  p50=$(jq -r '.result.latency.percentiles."50"' <<<"$j")
  p99=$(jq -r '.result.latency.percentiles."99"' <<<"$j")
  errs=$(jq -r '.result.others + .result.req4xx + .result.req5xx' <<<"$j")
  mib=$(awk -v r="$rps" -v s="$size" 'BEGIN{printf "%.1f", r*s/1048576}')
  printf "    %-10s p%-7s %9.0f rps  %9s MiB/s  p99 %7.0fus\n" "$backend" "$page" "$rps" "$mib" "$p99"
  echo "$size,$page,$backend,$SHARDS,$wb,$ob,$rp,$rep,${rps%.*},$mib,${p50%.*},${p99%.*},$errs,$([[ $errs -gt 0 ]] && echo ERRORS || echo ok)" >>"$CSV"
}

echo
echo "page-size sweep on the BARE responder (no Kestrel bridge)"
echo "  pages : $PAGES"
echo "  sizes : $SIZES"
echo "  back  : $BACKENDS   shards=$SHARDS"
echo "  cpus  : $NCPU  server=$SERVER_CPUS client=$CLIENT_CPUS (split by physical core)"
echo "          DOTNET_PROCESSOR_COUNT=$DOTNET_PROCESSOR_COUNT GOMAXPROCS=$GOMAXPROCS"
echo "  pool  : $([[ -n "$FIXED_POOL_DEPTH" ]] && echo "PINNED at $FIXED_POOL_DEPTH buffers/shard on ALL THREE pools (co-variation controlled)" || echo "all three follow --page (4MB/page) - co-variation NOT controlled")"
echo "  load  : -c $CONNECTIONS -d $DURATION, keep-alive, $REPS passes (pass 1 discarded)"
echo "  csv   : $CSV"
echo

# One cell = one (backend, page) pair at one payload. Reshuffled every pass, as everywhere else here: in a
# fixed order anything that accumulates is indistinguishable from a property of whichever cell runs late.
CELLS=()
for b in $BACKENDS; do for p in $PAGES; do CELLS+=("$b|$p"); done; done

for size in $SIZES; do
  echo "=== payload ${size} bytes ==="
  for ((rep=1; rep<=REPS; rep++)); do
    mapfile -t SHUF < <(printf '%s\n' "${CELLS[@]}" | shuf)
    for cell in "${SHUF[@]}"; do
      IFS='|' read -r b p <<<"$cell"
      [[ -n "$FILTER" && "$b|$p" != *$FILTER* ]] && continue
      measure "$b" "$p" "$size" "$rep"
    done
  done
done

echo
echo "=== goodput MiB/s, median of scored passes (pass 1 discarded); min-max in brackets ==="
awk -F, 'NR>1 && $8>1 && $10!="" { k=$1"|"$3"|"$2; n[k]++; v[k"_"n[k]]=$10+0 }
  END {
    for (k in n) { split(k,q,"|"); sizes[q[1]]=1; cells[q[2]"|"q[3]]=1 }
    printf "%-10s","size"
    for (c in cells) { split(c,d,"|"); printf "%24s", d[1]" p"d[2] }
    printf "\n"
    m = asorti(sizes, ss, "@ind_num_asc")
    for (i=1;i<=m;i++) { s=ss[i]; printf "%-10s", s
      for (c in cells) { k=s"|"c; cnt=n[k]
        if (cnt==0) { printf "%24s","-"; continue }
        for(j=1;j<=cnt;j++) a[j]=v[k"_"j]
        for(x=1;x<cnt;x++)for(y=x+1;y<=cnt;y++) if(a[x]>a[y]){t=a[x];a[x]=a[y];a[y]=t}
        printf "%14.1f [%.0f-%.0f]", a[int((cnt+1)/2)], a[1], a[cnt] }
      printf "\n" }
  }' "$CSV" 2>/dev/null || awk -F, 'NR>1 && $8>1 && $10!="" {printf "%-8s p%-8s %-10s %10s MiB/s\n",$1,$2,$3,$10}' "$CSV"

echo
echo "csv : $CSV"
echo "Report a difference only where the bracketed min-max ranges are DISJOINT."
echo "Compare against AspNetDemo's sweep at matching payloads: bare vs bridged isolates the Kestrel bridge."
