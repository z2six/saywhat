#!/usr/bin/env bash
set -euo pipefail
data=${1:?data path required}
model_id=${2:?model id required}
owner=${3:?owned GUID required}
[[ "$owner" =~ ^[a-f0-9]{32}$ ]] || exit 2
case "$model_id" in
    microsoft/VibeVoice-ASR-Streaming-1.5B) size=1.5B ;;
    microsoft/VibeVoice-ASR-Streaming-7B) size=7B ;;
    *) exit 2 ;;
esac
revision=1541f590c7099820f10ea012f48d2399282df69f
if [[ $(id -u) == 0 ]]; then runtime="/opt/saywhat-vibevoice/$revision";
else runtime="${XDG_DATA_HOME:-$HOME/.local/share}/saywhat/vibevoice/$revision"; fi
scriptroot=$(cd -- "$(dirname -- "$0")" && pwd -P)
# Fails without installing, downloading or loading if setup is incomplete.
/usr/bin/python3 "$scriptroot/prepare.py" --check --model-path "$data/models/vibevoice/$size" --model-id "$model_id" --runtime-root "$runtime"
pidroot="${XDG_DATA_HOME:-$HOME/.local/share}/saywhat/pids"
mkdir -p -- "$pidroot"
pidfile="$pidroot/vibevoice-8082-$owner.pid"
[[ ! -e "$pidfile" ]] || { echo 'This Groups ownership token already has a process marker.' >&2; exit 4; }
child=''
birth=''
cleanup() {
    trap - EXIT INT TERM
    set +e
    if [[ "$child" =~ ^[0-9]+$ ]] && {
        [[ -n "$birth" ]] && [[ $(awk '{print $22}' "/proc/$child/stat" 2>/dev/null) == "$birth" ]] ||
        [[ -z "$birth" && " $(jobs -pr | tr '\n' ' ') " == *" $child "* ]];
    }; then
        kill -TERM "$child" 2>/dev/null
        for ((attempt=0; attempt<80; attempt++)); do
            [[ -d "/proc/$child" ]] && [[ $(awk '{print $3}' "/proc/$child/stat" 2>/dev/null) != Z ]] || break
            sleep 0.1
        done
        if [[ -n "$birth" && $(awk '{print $22}' "/proc/$child/stat" 2>/dev/null) == "$birth" ]]; then kill -KILL "$child" 2>/dev/null; fi
    fi
    if [[ -f "$pidfile" ]] && [[ $(head -n 1 "$pidfile") == "$child" ]] && [[ $(sed -n '3p' "$pidfile") == "$owner" ]]; then rm -- "$pidfile"; fi
    rm -f -- "$pidfile.new"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
export HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 HF_HUB_DISABLE_TELEMETRY=1 DO_NOT_TRACK=1
"$runtime/venv/bin/python" -u "$scriptroot/server.py" --model-path "$data/models/vibevoice/$size" --model-id "$model_id" --owner "$owner" --port 8082 &
child=$!
birth=$(awk '{print $22}' "/proc/$child/stat")
printf '%s\n%s\n%s\n%s\n' "$child" "$birth" "$owner" "$scriptroot/server.py" > "$pidfile.new"
mv -- "$pidfile.new" "$pidfile"
printf 'SAYWHAT_GROUPS_PID=%s\nSAYWHAT_GROUPS_OWNER=%s\n' "$child" "$owner"
set +e
wait "$child"
result=$?
set -e
exit "$result"
