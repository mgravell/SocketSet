#!/usr/bin/env bash
# Shared kTLS verification for the Linux harnesses. Sourced by run-matrix.sh and run-tls-sizes.sh.
#
# Exports: ktls_tx_count, ktls_available.
#
# WHY /config IS NOT ENOUGH. Every harness here checks /config before measuring, because silent fallback
# is the failure mode this project keeps paying for. But /config reports what the demo was ASKED to do
# and successfully CONFIGURED - it cannot report what the kernel actually did with the socket. A kTLS leg
# whose setsockopt(TCP_ULP, "tls") quietly failed would still report
# `tls=ktls (openssl + kernel offload)` and would still serve HTTPS correctly, in userspace, at userspace
# speed. That is precisely the shape of confounder that produced the retracted io_uring container results.
#
# /proc/net/tls_stat is the kernel's own accounting and settles it:
#
#   TlsTxSw / TlsRxSw          sockets whose TX/RX crypto the KERNEL is doing in software
#   TlsTxDevice / TlsRxDevice  sockets whose crypto the NIC is doing inline
#   TlsCurr*                   the same as live gauges rather than cumulative counters
#
# MEASURED ON THIS HOST 2026-07-28, and it is worth knowing before reading any kTLS number:
# driving traffic through the --ktls leg moves TlsTxSw and leaves TlsRxSw at ZERO. Transmit is offloaded;
# receive is not offloaded at all, because the kTLS path drives receive as io_uring POLL + SSL_read in
# userspace rather than the RECVMSG + TLS_GET_RECORD_TYPE cmsg design in TlsFilter's notes (TODO item 4).
# So "kTLS" here means TX-only offload, and every kTLS figure on file is a half-offloaded path. That is a
# property of our integration, not of kTLS.
#
# TlsTxDevice stays 0 on loopback and always will - there is no NIC to offload to. Do not read that as a
# failure; read it as the reason loopback cannot say anything about kTLS's largest win.

# Cumulative count of sockets the kernel has taken over TLS transmit for. Empty string if unavailable.
ktls_tx_count() {
    [[ -r /proc/net/tls_stat ]] || return 1
    awk '/^TlsTxSw/ { print $2; found = 1 } END { if (!found) exit 1 }' /proc/net/tls_stat
}

# Is the plumbing present at all? Called once at startup so a whole run does not get part-way in before
# discovering the tls module is not loaded.
ktls_available() {
    if [[ ! -r /proc/net/tls_stat ]]; then
        echo "WARNING: /proc/net/tls_stat is unreadable - the 'tls' kernel module is probably not loaded."
        echo "         Run 'sudo modprobe tls' (persist with /etc/modules-load.d/tls.conf). kTLS legs will"
        echo "         be measured WITHOUT being able to confirm the kernel actually took the socket."
        return 1
    fi
    return 0
}
