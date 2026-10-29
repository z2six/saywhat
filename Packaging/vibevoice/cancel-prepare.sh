#!/usr/bin/env bash
set -euo pipefail
owner=${1:?owned GUID required}
[[ "$owner" =~ ^[a-f0-9]{32}$ ]] || exit 2
scriptroot=$(cd -- "$(dirname -- "$0")" && pwd -P)
pidfile="/tmp/saywhat-vibevoice-prepare-$owner.pid"
[[ -f "$pidfile" ]] || exit 0
pid=$(head -n 1 "$pidfile")
birth=$(sed -n '2p' "$pidfile")
[[ "$pid" =~ ^[0-9]+$ && "$birth" =~ ^[0-9]+$ && $(sed -n '3p' "$pidfile") == "$owner" ]] || exit 3
running() { [[ -d "/proc/$pid" ]] && [[ $(awk '{print $3}' "/proc/$pid/stat" 2>/dev/null) != Z ]]; }
matches() {
    [[ $(awk '{print $22}' "/proc/$pid/stat" 2>/dev/null) == "$birth" ]] &&
    [[ $(ps -o pgid= -p "$pid" | tr -d ' ') == "$pid" ]] &&
    [[ $(sed -n '4p' "$pidfile") == "$scriptroot/bootstrap-vibevoice.sh" ]] &&
    tr '\0' '\n' < "/proc/$pid/cmdline" | awk -v owner="$owner" -v script="$scriptroot/bootstrap-vibevoice.sh" '
        $0 == script { script_found=1 } $0 == owner { owner_found=1 } END { exit !(script_found && owner_found) }'
}
if ! running; then rm -- "$pidfile"; exit 0; fi
matches || { echo 'Groups setup ownership does not match; no process was stopped.' >&2; exit 4; }
kill -TERM -- "-$pid"
for ((attempt=0; attempt<100; attempt++)); do running || break; sleep 0.1; done
if running && matches; then kill -KILL -- "-$pid"; fi
if running; then echo 'Groups setup has not exited; its marker remains for retry.' >&2; exit 5; fi
[[ -f "$pidfile" && $(head -n 1 "$pidfile") == "$pid" ]] && rm -- "$pidfile"
exit 0
