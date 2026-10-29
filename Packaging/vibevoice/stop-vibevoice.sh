#!/usr/bin/env bash
# Stop only the process whose token, executable, birth time and command match.
set -euo pipefail
owner=${1:?owned GUID required}
[[ "$owner" =~ ^[a-f0-9]{32}$ ]] || exit 2
scriptroot=$(cd -- "$(dirname -- "$0")" && pwd -P)
pidfile="${XDG_DATA_HOME:-$HOME/.local/share}/saywhat/pids/vibevoice-8082-$owner.pid"
[[ -f "$pidfile" ]] || exit 0
pid=$(head -n 1 "$pidfile")
birth=$(sed -n '2p' "$pidfile")
[[ "$pid" =~ ^[0-9]+$ && "$birth" =~ ^[0-9]+$ && $(sed -n '3p' "$pidfile") == "$owner" ]] || exit 3
revision=1541f590c7099820f10ea012f48d2399282df69f
if [[ $(id -u) == 0 ]]; then runtime="/opt/saywhat-vibevoice/$revision";
else runtime="${XDG_DATA_HOME:-$HOME/.local/share}/saywhat/vibevoice/$revision"; fi
running() { [[ -d "/proc/$pid" ]] && [[ $(awk '{print $3}' "/proc/$pid/stat" 2>/dev/null) != Z ]]; }
matches() {
    [[ $(awk '{print $22}' "/proc/$pid/stat" 2>/dev/null) == "$birth" ]] &&
    [[ $(readlink -f "/proc/$pid/exe" 2>/dev/null) == $(readlink -f "$runtime/venv/bin/python") ]] &&
    [[ $(sed -n '4p' "$pidfile") == "$scriptroot/server.py" ]] &&
    tr '\0' '\n' < "/proc/$pid/cmdline" | awk -v owner="$owner" -v script="$scriptroot/server.py" '
        $0 == script { script_found=1 } previous == "--owner" && $0 == owner { owner_found=1 }
        previous == "--port" && $0 == "8082" { port_found=1 } { previous=$0 }
        END { exit !(script_found && owner_found && port_found) }'
}
if ! running; then rm -- "$pidfile"; exit 0; fi
matches || { echo 'Groups process ownership does not match; nothing was stopped.' >&2; exit 4; }
kill -TERM "$pid"
for ((attempt=0; attempt<100; attempt++)); do running || break; sleep 0.1; done
if running && matches; then kill -KILL "$pid"; fi
for ((attempt=0; attempt<50; attempt++)); do running || break; sleep 0.1; done
if running; then echo 'Groups has not exited. Its ownership marker is retained for retry.' >&2; exit 5; fi
[[ -f "$pidfile" && $(head -n 1 "$pidfile") == "$pid" ]] && rm -- "$pidfile"
exit 0
