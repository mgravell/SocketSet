#!/usr/bin/env bash
# Same-session A/B of two commits, measured back to back in ISOLATED git worktrees.
#
# The Linux counterpart of Compare-Commits.ps1, ported 2026-08-02. It exists because the Windows session
# of 2026-08-01 landed a SHARED-code fix (the PooledBufferWriter high-water capacity, a264998) that
# io_uring, epoll and managed all reach through OutboundConnection.Flush, and Linux had NO rig that could
# ask "did that change help HERE". run-tls-sizes.sh sweeps legs of ONE binary; it cannot compare commits,
# and RESULTS.md forbids subtracting a fresh number from a recorded one.
#
# WHY WORKTREES, AND NOT `git checkout <sha> -- <paths>`.
# Carried over verbatim from the .ps1 because the failure it describes is not platform-specific: that
# form updates the INDEX as well as the working tree, so an unrelated `git commit` in the same checkout
# while the rig runs will commit a revert of the very change under test. The A/B then compares the old
# code with itself and reports a plausible few-percent "regression". Clean status, passing build, wrong
# number. A worktree gets its own checkout AND its own index, so it cannot do that.
#
# WHAT THIS RIG STILL CANNOT DISTINGUISH, stated up front because a rig is not neutral about what it can
# see: it measures ONE leg shape per invocation. A change on an opt-in path measures as exactly nothing
# unless EXTRA_ARGS puts you on that path, and the null result then looks like a property of the change
# rather than of the leg. That is precisely the trap for the flush fix, whose whole mechanism is that
# --classic pays the out-of-band path and plaintext --byo skips it: run BOTH, or you have not tested it.
#
# AND THE SAME TRAP APPLIES TO **BACKEND**, which cost a full 20-minute run on 2026-08-02. The
# identical-binary guard below CANNOT catch it: it proves the two builds differ, not that the differing
# code is REACHABLE from the leg you chose. The flush fix lives in OutboundConnection, and only
# WindowsConnection (IOCP/RIO) and EpollConnection derive from it -- IoUringConnection and
# ManagedConnection derive from Connection directly and have their own send paths. So BACKEND=io-uring
# measured a perfect, believable, meaningless null. Before trusting a null result here, confirm by
# READING THE TYPE HIERARCHY that the chosen backend reaches the changed code.
#
# Usage:
#   BEFORE=a264998~1 AFTER=a264998 bench/compare-commits.sh
#   BEFORE=a264998~1 AFTER=a264998 EXTRA_ARGS="--classic --tls" SIZES="262144 1048576" bench/compare-commits.sh
#   BACKEND=epoll BRIDGED=0 bench/compare-commits.sh          # bare responder (SmokeTest --http)
set -uo pipefail

BEFORE=${BEFORE:-HEAD~1}
AFTER=${AFTER:-HEAD}
SIZES=${SIZES:-"16384 262144 1048576"}
CONNECTIONS=${CONNECTIONS:-64}
SHARDS=${SHARDS:-12}
DURATION=${DURATION:-5s}
# Per-MEASUREMENT warm-up load, discarded. Distinct from discarding pass 1: every measurement starts a
# FRESH server process, so each one pays its own JIT and pool-fill transient, not just the first. Without
# this the .ps1's per-side spread at 256KB was 6-10% against run-tls-sizes' 2.2% on the same leg.
WARMUP=${WARMUP:-3s}
# First pass per side is discarded as warm-up, so this is scored passes + 1. AGENTS.md rule 4 asks for
# SIX scored passes at 256KB and above (three consecutive passes can span 1.2% when the true spread is
# 9-17%), so the default is 7. Drop it only for a quick look, and say so if you quote the result.
REPS=${REPS:-7}
# PORT_BASE must sit BELOW the ephemeral range and must not repeat between back-to-back runs. Both halves
# were learned the hard way on 2026-08-02, from three EADDRINUSE (errno 98) NOSTARTs in one session:
#   * 41000 (carried over from Compare-Commits.ps1) is INSIDE Linux's default ip_local_port_range
#     32768-60999, so bombardier's own client sockets can hold the exact port the next server wants. On
#     Windows the same constant is safe -- the dynamic range there starts at 49152 -- which is exactly why
#     a straight port of the rig inherited a bug the original does not have.
#   * A fixed base makes two legs run back to back reuse the same ports, and 64 keep-alive connections
#     leave that local port in TIME_WAIT after the server is killed.
# A NOSTART is not neutral: it silently drops a scored pass, and the drops were ALL on one side.
PORT_BASE=${PORT_BASE:-$(( 20000 + (RANDOM % 8000) ))}
BACKEND=${BACKEND:-io-uring}          # io-uring | epoll | managed
BRIDGED=${BRIDGED:-1}                 # 1 = AspNetDemo through Kestrel; 0 = bare SmokeTest --http responder
EXTRA_ARGS=${EXTRA_ARGS:-}            # applied to BOTH sides, e.g. "--classic --tls"

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${OUT:-$REPO/bench/results}"
BOMB="$REPO/bench/.tools/bombardier"

case "$BACKEND" in
  io-uring) XPORT="socketset/io_uring" ;;
  epoll)    XPORT="socketset/epoll" ;;
  managed)  XPORT="socketset/managed" ;;
  *) echo "BACKEND must be io-uring | epoll | managed (got '$BACKEND')"; exit 1 ;;
esac

for tool in jq curl taskset git lscpu; do
  command -v "$tool" >/dev/null || { echo "missing required tool: $tool"; exit 1; }
done
mkdir -p "$OUT" "$(dirname "$BOMB")"
if [[ ! -x "$BOMB" ]]; then
  echo "fetching bombardier..."
  curl -sSL -o "$BOMB" https://github.com/codesenberg/bombardier/releases/download/v1.2.6/bombardier-linux-amd64
  chmod +x "$BOMB"
fi

source "$REPO/bench/cpu-split.sh"

# Say so rather than discovering it as a sporadic NOSTART three passes in.
PORT_TOP=$(( PORT_BASE + 2 * $(wc -w <<<"$SIZES") * REPS + 2 ))
read -r EPH_LO EPH_HI < /proc/sys/net/ipv4/ip_local_port_range
if (( PORT_TOP >= EPH_LO )); then
  echo "WARNING: ports $PORT_BASE-$PORT_TOP overlap the ephemeral range $EPH_LO-$EPH_HI."
  echo "         The load generator can hold a port the next server needs; expect sporadic NOSTART."
fi

STAMP=$(date +%Y%m%d-%H%M%S)
CSV="$OUT/ab-$STAMP.csv"
LOGS="$OUT/logs-ab-$STAMP"
mkdir -p "$LOGS"
echo "rep,size,side,commit,mib_s,status" > "$CSV"

# The banner gate. Trust the banner, not the flag (AGENTS.md rule 1): a flag that parses and is ignored
# measures identically to one that works, and this rig's whole job is to attribute a delta to a commit.
# The TLS and byo fragments are derived from EXTRA_ARGS rather than assumed, so `--classic` that silently
# ran the byo bridge is REFUSED rather than measured.
SCHEME=http
case " $EXTRA_ARGS " in *" --tls "*|*" --ktls "*) SCHEME=https ;; esac
XTLS="off"
case " $EXTRA_ARGS " in
  *" --ktls "*) XTLS="ktls (openssl + kernel offload)" ;;
  *" --tls "*)  XTLS="openssl" ;;
esac
XBYO=""
case " $EXTRA_ARGS " in
  *" --classic "*|*" --no-byo "*) XBYO="byo=off" ;;
  *" --byo "*)                    XBYO="byo=pipe" ;;
esac

WORKTREES=()
cleanup() {
  for w in "${WORKTREES[@]:-}"; do [[ -n "$w" ]] && git -C "$REPO" worktree remove --force "$w" 2>/dev/null; done
  git -C "$REPO" worktree prune 2>/dev/null
}
trap cleanup EXIT INT TERM

git -C "$REPO" worktree prune 2>/dev/null
ROOT="${TMPDIR:-/tmp}/ss-ab"

# Sets the global SIDE_EXE rather than echoing it. Deliberate: an earlier version returned the path via
# `$(new_side ...)`, which runs the function in a SUBSHELL, so its `WORKTREES+=` append was discarded and
# the EXIT trap cleaned up nothing — leaving a worktree per run pinned to the repo.
new_side() { # $1=name $2=commit -> sets SIDE_EXE
  # Separate statements, NOT `local a=$1 b=$ROOT/$a`: bash expands every word of the `local` command line
  # before assigning any of them, so the second would read `name` while it is still unset (and already
  # shadowed), which under `set -u` aborts the run.
  local name="$1"
  local commit="$2"
  local path="$ROOT/$name"
  echo "  worktree $name -> $commit" >&2
  if [[ -e "$path" ]]; then
    git -C "$REPO" worktree remove --force "$path" 2>/dev/null
    rm -rf "$path"
  fi
  git -C "$REPO" worktree add --detach --quiet "$path" "$commit" || { echo "git worktree add failed for $commit" >&2; exit 2; }
  WORKTREES+=("$path")
  if [[ "$BRIDGED" == "1" ]]; then
    dotnet build "$path/AspNetDemo/AspNetDemo.csproj" -c Release -v q --nologo >"$LOGS/build-$name.log" 2>&1 \
      || { cat "$LOGS/build-$name.log" >&2; echo "build failed for $commit" >&2; exit 2; }
    SIDE_EXE="$path/AspNetDemo/bin/Release/net10.0/AspNetDemo"
  else
    dotnet build "$path/SmokeTest/SmokeTest.csproj" -f net10.0 -c Release -v q --nologo >"$LOGS/build-$name.log" 2>&1 \
      || { cat "$LOGS/build-$name.log" >&2; echo "build failed for $commit" >&2; exit 2; }
    SIDE_EXE="$path/SmokeTest/bin/Release/net10.0/SmokeTest"
  fi
  [[ -x "$SIDE_EXE" ]] || { echo "built, but no executable at $SIDE_EXE" >&2; exit 2; }
}

measure_one() { # $1=exe $2=port $3=size $4=side $5=rep $6=commit -> echoes MiB/s or empty
  local exe="$1" port="$2" size="$3" side="$4" rep="$5" commit="$6"
  local log="$LOGS/$side.$size.r$rep.log" url args pid cfg="" i
  if [[ "$BRIDGED" == "1" ]]; then
    args="--$BACKEND --shards $SHARDS --port $port $EXTRA_ARGS"
    url="$SCHEME://127.0.0.1:$port/payload?n=$size"
  else
    args="--http --$BACKEND -n $SHARDS -z $size --port $port $EXTRA_ARGS"
    url="$SCHEME://127.0.0.1:$port/"
  fi

  taskset -c "$SERVER_CPUS" "$exe" $args >"$log" 2>&1 &
  pid=$!

  if [[ "$BRIDGED" == "1" ]]; then
    for ((i=0;i<100;i++)); do
      cfg=$(curl -sk --max-time 3 "$SCHEME://127.0.0.1:$port/config" 2>/dev/null) && [[ -n "$cfg" ]] && break
      sleep 0.4
    done
    if [[ -z "$cfg" ]]; then
      echo "    $side r$rep $size: NOSTART" >&2
      echo "$rep,$size,$side,$commit,,NOSTART" >>"$CSV"
      kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
    fi
    # Refuse to measure a side that silently loaded a different backend / TLS mode / bridge mode.
    local bad=""
    [[ "$cfg" == *"transport=$XPORT"* ]] || bad="transport"
    [[ "$cfg" == *"tls=$XTLS"* ]]        || bad="${bad:+$bad,}tls"
    [[ -z "$XBYO" || "$cfg" == *"$XBYO"* ]] || bad="${bad:+$bad,}byo"
    if [[ -n "$bad" ]]; then
      echo "    $side r$rep $size: MISMATCH ($bad) -> $cfg" >&2
      echo "$rep,$size,$side,$commit,,MISMATCH" >>"$CSV"
      kill $pid 2>/dev/null; wait $pid 2>/dev/null; return
    fi
  else
    sleep 4
  fi

  local json="$LOGS/.bomb.json" phase mib=""
  for phase in "$WARMUP" "$DURATION"; do
    taskset -c "$CLIENT_CPUS" "$BOMB" -k -c "$CONNECTIONS" -d "$phase" -o json -p r "$url" >"$json" 2>/dev/null
  done
  # bombardier has no `errors` field: failures land in req4xx/req5xx/others. A leg that errored is not a
  # slow leg, it is a broken one, and scoring it was a real defect in Run-PipeSched.ps1 (see a264998).
  local rps bad_n
  rps=$(jq -r '.result.rps.mean // empty' "$json" 2>/dev/null)
  bad_n=$(jq -r '[(.result.req4xx//0),(.result.req5xx//0),(.result.others//0)] | add' "$json" 2>/dev/null)
  if [[ -z "$rps" ]]; then
    echo "$rep,$size,$side,$commit,,NORESULT" >>"$CSV"
  elif [[ -n "$bad_n" && "$bad_n" != "0" ]]; then
    echo "    $side r$rep $size: $bad_n failed requests -- NOT scored" >&2
    echo "$rep,$size,$side,$commit,,ERRORS" >>"$CSV"
  else
    mib=$(awk -v r="$rps" -v s="$size" 'BEGIN{printf "%.1f", r*s/1048576}')
    echo "$rep,$size,$side,$commit,$mib,OK" >>"$CSV"
  fi

  kill $pid 2>/dev/null; wait $pid 2>/dev/null
  sleep 2
  echo "$mib"
}

mode_desc=$([[ "$BRIDGED" == "1" ]] && echo bridged || echo bare)
echo "A/B: $BEFORE  vs  $AFTER"
echo "  backend=$BACKEND $mode_desc  extra='$EXTRA_ARGS'  gate: transport=$XPORT tls=$XTLS ${XBYO:+$XBYO}"
echo "  server=$SERVER_CPUS client=$CLIENT_CPUS  -c $CONNECTIONS -d $DURATION shards=$SHARDS reps=$REPS (scored $((REPS-1)))"

new_side before "$BEFORE"; EXE_BEFORE="$SIDE_EXE"
new_side after  "$AFTER";  EXE_AFTER="$SIDE_EXE"

# Guard against the failure this rig exists to avoid: if both sides produced an identical binary, the
# commits differ in nothing that affects it and any delta reported would be pure noise. In bridged mode
# hash SocketSet.dll, not the host exe -- a transport-only change leaves AspNetDemo identical on both
# sides and hashing that would abort a perfectly valid comparison.
for f in "$(dirname "$EXE_BEFORE")/SocketSet.dll" "$(dirname "$EXE_AFTER")/SocketSet.dll"; do
  [[ -f "$f" ]] || { echo "ABORT: no SocketSet.dll at $f -- cannot prove the two sides differ."; exit 3; }
done
HB=$(sha256sum "$(dirname "$EXE_BEFORE")/SocketSet.dll" | cut -d' ' -f1)
HA=$(sha256sum "$(dirname "$EXE_AFTER")/SocketSet.dll" | cut -d' ' -f1)
if [[ "$HB" == "$HA" ]]; then
  echo "ABORT: both sides produced an identical SocketSet.dll -- there is nothing to compare."
  exit 3
fi

# INTERLEAVED, and the side that goes FIRST alternates by pass. Measuring all of `before` then all of
# `after` puts every before-pass earlier in wall-clock than every after-pass, so anything that drifts over
# a run (thermals, a background task, the ephemeral-port pool filling) lands entirely on one side of the
# subtraction and is indistinguishable from the change.
P=$PORT_BASE
for ((rep=1; rep<=REPS; rep++)); do
  echo "pass $rep/$REPS"
  for sz in $SIZES; do
    P=$((P+2))
    if (( rep % 2 == 1 )); then order="before after"; else order="after before"; fi
    for side in $order; do
      if [[ "$side" == "before" ]]; then exe="$EXE_BEFORE"; off=0; commit="$BEFORE"; else exe="$EXE_AFTER"; off=1; commit="$AFTER"; fi
      v=$(measure_one "$exe" $((P+off)) "$sz" "$side" "$rep" "$commit")
      # ONE retry on a well-separated port. A dropped measurement is not neutral -- it silently lowers the
      # scored-pass count for that cell only, and if the drops cluster on one side (they did) the two sides
      # stop being scored over the same passes. The failed attempt stays in the CSV: it is a real event.
      if [[ -z "$v" ]]; then
        echo "      retrying $side/$sz on a clear port" >&2
        v=$(measure_one "$exe" $((P+off+1000)) "$sz" "$side" "$rep" "$commit")
      fi
      printf '    %-6s %8s  %s\n' "$side" "$sz" "${v:-FAILED}"
    done
  done
done

echo
echo "=== goodput MiB/s, $((REPS-1)) scored passes (pass 1 discarded) ==="
# Ranges, not medians alone (AGENTS.md rule 5): a delta is only claimed where the min-max ranges are
# DISJOINT. Printing the median alone is how an overlapping 8.8% gets quoted as a result.
awk -F, -v reps="$REPS" '
  NR>1 && $6=="OK" && $1>1 { key=$2 SUBSEP $3; v[key][++n[key]] = $5+0; sizes[$2]=1 }
  function stats(arr, cnt,   i, tmp, j, t) {
    for (i=1;i<=cnt;i++) tmp[i]=arr[i]
    for (i=1;i<cnt;i++) for (j=i+1;j<=cnt;j++) if (tmp[j]<tmp[i]) { t=tmp[i]; tmp[i]=tmp[j]; tmp[j]=t }
    lo=tmp[1]; hi=tmp[cnt]
    med = (cnt%2) ? tmp[int(cnt/2)+1] : (tmp[cnt/2]+tmp[cnt/2+1])/2
  }
  END {
    printf "  %-10s %25s %25s %9s   %s\n", "payload", "before (med, min-max)", "after (med, min-max)", "change", "verdict"
    m = asorti(sizes, ord, "@ind_num_asc")
    for (k=1;k<=m;k++) {
      s = ord[k]
      bk = s SUBSEP "before"; ak = s SUBSEP "after"
      if (!(bk in n) || !(ak in n)) { printf "  %-10s  (missing data)\n", s; continue }
      stats(v[bk], n[bk]); bmed=med; blo=lo; bhi=hi
      stats(v[ak], n[ak]); amed=med; alo=lo; ahi=hi
      chg = (bmed>0) ? (amed-bmed)/bmed*100 : 0
      # A single scored pass per side makes each "range" a POINT, and two points are disjoint unless they
      # are exactly equal — so an unguarded verdict prints DISJOINT for pure noise. Seen on this rig'"'"'s own
      # shakeout run. Three is the floor to say anything at all; rule 4 wants SIX at 256KB and above.
      if (n[bk] < 3 || n[ak] < 3)
        verdict = sprintf("n=%d/%d -- TOO FEW PASSES to claim anything", n[bk], n[ak])
      else
        verdict = (blo > ahi) ? "DISJOINT (before ahead)" : (alo > bhi) ? "DISJOINT (after ahead)" : "overlapping -- UNPROVEN"
      printf "  %-10s %9.1f %14s %9.1f %14s %8.1f%%   %s\n", s, \
        bmed, sprintf("(%.1f-%.1f)", blo, bhi), amed, sprintf("(%.1f-%.1f)", alo, ahi), chg, verdict
    }
  }' "$CSV"

echo
echo "csv: $CSV"
echo "A change whose ranges OVERLAP is not a result. Two identical builds have measured ~6% apart here."
