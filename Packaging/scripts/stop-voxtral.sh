#!/usr/bin/env bash
set -euo pipefail
server=${1:?server path required}
port=${2:?port required}
owner=${3:?owned GUID required}
[[ "$owner" =~ ^[a-f0-9]{32}$ ]] || exit 2
[[ "$port" =~ ^[1-9][0-9]{3,4}$ ]] && (( 10#$port >= 1024 && 10#$port <= 65535 )) || exit 2
pidfile="${XDG_DATA_HOME:-$HOME/.local/share}/localtalk/pids/voxtral-$port-$owner.pid"
[[ -f "$pidfile" ]] || exit 0
pid=$(head -n 1 "$pidfile")
birth=$(sed -n '2p' "$pidfile")
[[ $(sed -n '3p' "$pidfile") == "$owner" ]] || { echo 'Owned token mismatch; no process was stopped.' >&2; exit 3; }
[[ "$pid" =~ ^[0-9]+$ && "$birth" =~ ^[0-9]+$ ]] || { echo 'Invalid owned PID marker; no process was stopped.' >&2; exit 3; }
is_running() { [[ -d "/proc/$pid" ]] && [[ $(awk '{print $3}' "/proc/$pid/stat" 2>/dev/null) != Z ]]; }
if ! is_running; then rm -- "$pidfile"; exit 0; fi
matches_owned() {
    [[ $(readlink -f "/proc/$pid/exe" 2>/dev/null) == "$(readlink -f "$server")" ]] &&
    [[ $(awk '{print $22}' "/proc/$pid/stat" 2>/dev/null) == "$birth" ]] &&
    tr '\0' '\n' < "/proc/$pid/cmdline" | awk -v port="$port" 'previous == "--port" && $0 == port { found=1 } { previous=$0 } END { exit !found }'
}
matches_owned || { echo 'PID no longer matches the SayWhat?-owned executable, birth time and port. Nothing was stopped.' >&2; exit 4; }
kill -TERM "$pid" || { is_running && exit 5; }
for _ in $(seq 1 50); do
    is_running || break
    sleep 0.1
done
if is_running && matches_owned; then
    kill -KILL "$pid" || { is_running && exit 5; }
    for _ in $(seq 1 50); do
        is_running || break
        sleep 0.1
    done
fi
if is_running && matches_owned; then
    echo 'The owned speech process has not exited yet. Its marker is retained for a cleanup retry.' >&2
    exit 5
fi
[[ -f "$pidfile" ]] && [[ $(head -n 1 "$pidfile") == "$pid" ]] && rm -- "$pidfile"
exit 0
