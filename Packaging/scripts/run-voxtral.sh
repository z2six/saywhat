#!/usr/bin/env bash
set -euo pipefail

server=${1:?server path required}
model=${2:?realtime model path required}
backend=${3:?backend required}
port=${4:?port required}
owner=${5:?owned GUID required}
[[ "$owner" =~ ^[a-f0-9]{32}$ ]] || { echo 'Invalid SayWhat? owner token.' >&2; exit 2; }
case "$backend" in cuda|vulkan|cpu) ;; *) echo 'Unsupported speech backend.' >&2; exit 2 ;; esac
case "$port" in 8080|8081) ;; *) echo 'Invalid SayWhat? speech port.' >&2; exit 2 ;; esac
[[ -x "$server" && -s "$model" ]] || { echo 'Speech executable or realtime model is missing.' >&2; exit 3; }

# Updating an already prepared installation need not run setup again. The
# Windows launcher sends this flag only for an app-owned root/server record;
# the helper additionally verifies the source marker in this running distro.
# No global GUI setting or imported user's Ubuntu installation is changed.
if [[ "${6:-}" == --hide-desktop-integration ]]; then
    helper="$(dirname "$0")/hide-speech-desktop-entries.sh"
    if [[ -f "$helper" ]]; then
        if ! /bin/bash "$helper" --owned-runtime "$server"; then
            echo 'Optional speech desktop entry cleanup failed; translation will still start.' >&2
        fi
    fi
fi

pidroot="${XDG_DATA_HOME:-$HOME/.local/share}/localtalk/pids"
mkdir -p "$pidroot"
pidfile="$pidroot/voxtral-$port-$owner.pid"
if [[ -f "$pidfile" ]]; then
    existing=$(head -n 1 "$pidfile")
    if [[ "$existing" =~ ^[0-9]+$ ]] && kill -0 "$existing" 2>/dev/null; then
        echo "SayWhat? already recorded a live speech process for port $port; stop it before starting another." >&2
        exit 4
    fi
fi

export VOXTRAL_SERVER_BACKEND="$backend"
export LD_LIBRARY_PATH="$(dirname "$server"):/usr/local/cuda-13.1/lib64:/usr/local/cuda/lib64:/usr/lib/wsl/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
server_pid=''
birth=''
server_real=$(readlink -f "$server")
still_owned() {
    local pid=${1:-}
    [[ "$pid" =~ ^[0-9]+$ ]] && kill -0 "$pid" 2>/dev/null || return 1
    if [[ -n "$birth" ]]; then
        [[ $(awk '{print $22}' "/proc/$pid/stat" 2>/dev/null) == "$birth" ]] &&
            [[ $(readlink -f "/proc/$pid/exe" 2>/dev/null) == "$server_real" ]]
    else
        # Covers a cancellation between launch and recording the child's identity.
        # Only this wrapper's own running job may be stopped in that short interval.
        [[ " $(jobs -pr | tr '\n' ' ') " == *" $pid "* ]]
    fi
}
cleanup() {
    trap - EXIT INT TERM
    set +e
    local pid=${server_pid:-}
    if [[ -z "$pid" ]]; then pid=$(jobs -pr | head -n 1); fi
    if still_owned "$pid"; then
        kill -TERM "$pid" 2>/dev/null || true
        for ((attempt=0; attempt<50; attempt++)); do
            still_owned "$pid" || break
            sleep 0.1
        done
        if still_owned "$pid"; then kill -KILL "$pid" 2>/dev/null || true; fi
    fi
    if [[ -f "$pidfile" ]] && [[ $(head -n 1 "$pidfile") == "$pid" ]] &&
        [[ $(tail -n 1 "$pidfile") == "$owner" ]]; then rm -- "$pidfile"; fi
    rm -f -- "$pidfile.new"
}
# Install cleanup before starting a child: marker/identity errors must not orphan it.
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
"$server" --model "$model" --listen 127.0.0.1 --port "$port" --no-auth &
server_pid=$!
birth=$(awk '{print $22}' "/proc/$server_pid/stat")
printf '%s\n%s\n%s\n' "$server_pid" "$birth" "$owner" > "$pidfile.new"
mv "$pidfile.new" "$pidfile"
printf 'LOCALTALK_ASR_PID=%s\n' "$server_pid"
printf 'LOCALTALK_ASR_OWNER=%s\n' "$owner"

set +e
wait "$server_pid"
result=$?
set -e
exit "$result"
