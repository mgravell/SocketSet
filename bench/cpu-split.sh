#!/usr/bin/env bash
# Shared CPU pinning split for the Linux harnesses. Sourced by run-matrix.sh and run-tls-sizes.sh.
#
# Exports: SERVER_CPUS, CLIENT_CPUS (taskset -c lists), SERVER_NCPU, CLIENT_NCPU (logical CPU counts),
# DOTNET_PROCESSOR_COUNT, GOMAXPROCS.
#
# WHY THIS IS NOT "lower half / upper half". Both scripts used to split the logical CPU list down the
# middle, with a comment saying siblings live on the upper half "on many hosts" so the naive split is
# safe. On the current bench host (Ryzen 9 7900X, 12C/24T, Pop!_OS) that is exactly wrong: Linux
# enumerates CPUs 0-11 as ONE thread of each of the twelve physical cores and 12-23 as their siblings.
# So `taskset -c 0-11` for the server and `12-23` for the client hands both halves the SAME twelve
# physical cores, and every leg is measured while its own load generator runs on the sibling thread.
# Neither half is isolated and the contention is invisible in the output.
#
# The Windows rigs are not affected and do not need the same fix: Windows enumerates the two threads of
# a core ADJACENTLY, so there the lower half genuinely is a distinct set of physical cores. Splitting by
# core here is what makes the two platforms' rigs mean the same thing.
#
# So: split by PHYSICAL CORE, giving each half every thread of its own cores. On this host that is
# 0-5,12-17 (server) against 6-11,18-23 (client) - six cores and twelve logical CPUs each.

_cpu_split() {
    local parsed
    # lscpu -p emits "CPU,CORE" per online logical CPU, comment lines first.
    parsed=$(lscpu -p=CPU,CORE 2>/dev/null | grep -v '^#' | awk -F, '
        {
            cpu[NR] = $1; core[NR] = $2
            if (!($2 in seen)) { seen[$2] = 1; order[++ncores] = $2 }
        }
        END {
            if (NR == 0 || ncores == 0) exit 1
            half = int(ncores / 2); if (half < 1) half = 1
            for (i = 1; i <= half; i++) srv[order[i]] = 1
            s = ""; c = ""; ns = 0; nc = 0
            for (i = 1; i <= NR; i++) {
                if (core[i] in srv) { s = (s == "" ? cpu[i] : s "," cpu[i]); ns++ }
                else                { c = (c == "" ? cpu[i] : c "," cpu[i]); nc++ }
            }
            if (c == "") { c = s; nc = ns }   # single-core host: both halves are the same CPU
            print s "|" c "|" ns "|" nc
        }')

    if [[ -n "$parsed" ]]; then
        IFS='|' read -r SERVER_CPUS CLIENT_CPUS SERVER_NCPU CLIENT_NCPU <<<"$parsed"
    else
        # No lscpu (or unparseable): fall back to the old halves, and say so rather than pretending.
        local ncpu half
        ncpu=$(nproc); half=$(( ncpu / 2 )); (( half < 1 )) && half=1
        SERVER_CPUS="0-$((half-1))"; CLIENT_CPUS="$half-$((ncpu-1))"
        SERVER_NCPU=$half; CLIENT_NCPU=$(( ncpu - half ))
        (( ncpu < 2 )) && { SERVER_CPUS=0; CLIENT_CPUS=0; SERVER_NCPU=1; CLIENT_NCPU=1; }
        echo "WARNING: lscpu unavailable - falling back to a lower/upper-half CPU split, which does NOT"
        echo "         separate SMT siblings on AMD/Linux enumeration. Treat the numbers accordingly."
    fi

    # Both runtimes size their ThreadPool and GC heaps from the CPU count seen at STARTUP, before
    # affinity is applied, and are otherwise oversubscribed against their own pinning.
    export SERVER_CPUS CLIENT_CPUS SERVER_NCPU CLIENT_NCPU
    export DOTNET_PROCESSOR_COUNT=$SERVER_NCPU
    export GOMAXPROCS=$CLIENT_NCPU
}

_cpu_split
