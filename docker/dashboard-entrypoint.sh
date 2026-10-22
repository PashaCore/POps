#!/bin/bash
# Decides which peers may set X-Forwarded-For for the dashboard (mod_remoteip, see
# apache-pops.conf), then hands over to the stock php:apache entrypoint.
set -euo pipefail

if [ -z "${POPS_TRUSTED_PROXIES:-}" ]; then
    # Default: only this container's default gateway. Connections that reach the published
    # port from the Docker host itself (e.g. a TLS reverse proxy on the host talking to
    # 127.0.0.1:<port>) arrive from that address. /proc/net/route stores it little-endian.
    hex=$(awk '$2 == "00000000" && $3 != "00000000" { print $3; exit }' /proc/net/route 2>/dev/null || true)
    if [[ "$hex" =~ ^[0-9A-Fa-f]{8}$ ]]; then
        POPS_TRUSTED_PROXIES=$(printf '%d.%d.%d.%d' "0x${hex:6:2}" "0x${hex:4:2}" "0x${hex:2:2}" "0x${hex:0:2}")
    else
        POPS_TRUSTED_PROXIES=127.0.0.1
    fi
fi
export POPS_TRUSTED_PROXIES
echo "pops-dashboard: trusting X-Forwarded-For from: $POPS_TRUSTED_PROXIES" >&2

exec docker-php-entrypoint "$@"
