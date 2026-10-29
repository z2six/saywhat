#!/usr/bin/env bash
set -euo pipefail
job_id=${1:?job id required}
[[ "$job_id" =~ ^[a-f0-9]{32}$ ]] || exit 2
pidfile="/tmp/localtalk-build-$job_id.pid"
[[ -f "$pidfile" ]] || exit 0
pid=$(cat "$pidfile")
[[ "$pid" =~ ^[0-9]+$ && -r "/proc/$pid/cmdline" ]] || exit 0
grep -aFq -- "$job_id" "/proc/$pid/cmdline" || { echo 'Setup PID ownership check failed; no process was stopped.' >&2; exit 3; }
[[ $(ps -o pgid= -p "$pid" | tr -d ' ') == "$pid" ]] || { echo 'Setup process group ownership check failed.' >&2; exit 4; }
kill -TERM -- "-$pid"
echo 'Cancellation requested for the SayWhat? setup job only. If package installation was interrupted, rerun setup to repair it.'
