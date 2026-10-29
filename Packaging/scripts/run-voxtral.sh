#!/usr/bin/env bash
set -euo pipefail

server=${1:?server path required}
model=${2:?realtime model path required}
backend=${3:?backend required}
port=${4:?port required}
owner=${5:?owned GUID required}
[[ "$owner" =~ ^[a-f0-9]{32}$ ]] || { echo 'Invalid SayWhat? owner token.' >&2; exit 2; }
case "$backend" in cuda|vulkan|cpu) ;; *) echo 'Unsupported speech backend.' >&2; exit 2 ;; esac
[[ "$port" =~ ^[1-9][0-9]{3,4}$ ]] && (( 10#$port >= 1024 && 10#$port <= 65535 )) || {
    echo 'Invalid SayWhat? speech port.' >&2; exit 2;
}
[[ -x "$server" && -s "$model" ]] || { echo 'Speech executable or realtime model is missing.' >&2; exit 3; }

# This does not connect to an existing service or load any speech model. A
# Windows-free port can still be occupied inside the WSL network namespace.
[[ -x /usr/bin/python3 ]] || { echo 'Python 3 is missing from the speech tools. Check and repair setup before starting.' >&2; exit 3; }
probe_port() {
    /usr/bin/python3 - "$port" <<'PY'
import errno
import socket
import sys
with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as candidate:
    try:
        candidate.bind(("127.0.0.1", int(sys.argv[1])))
    except OSError as error:
        if error.errno == errno.EADDRINUSE:
            sys.exit(98)
        raise
PY
}
require_available_port() {
    local probe_result=0
    probe_port || probe_result=$?
    if (( probe_result == 98 )); then
        printf 'LOCALTALK_PORT_CONFLICT=%s:%s\n' "$owner" "$port"
        exit 6
    elif (( probe_result != 0 )); then
        echo 'Could not check the local speech port. Check and repair setup before starting.' >&2
        exit 3
    fi
}
require_available_port

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

# HTTP readiness alone cannot identify a WSL listener: a foreign process may
# win a race for the selected port. Prove that this retained child owns the
# exact loopback TCP socket before the controller probes its health endpoint.
owns_listener() {
    local endpoint inode fd
    printf -v endpoint '0100007F:%04X' "$port"
    while IFS= read -r inode; do
        [[ "$inode" =~ ^[0-9]+$ ]] || continue
        for fd in "/proc/$server_pid/fd/"*; do
            if [[ $(readlink "$fd" 2>/dev/null) == "socket:[$inode]" ]]; then return 0; fi
        done
    done < <(awk -v endpoint="$endpoint" '$2 == endpoint && $4 == "0A" { print $10 }' /proc/net/tcp)
    return 1
}
listen_deadline=$((SECONDS + 80))
while :; do
    if ! still_owned "$server_pid"; then
        set +e
        wait "$server_pid"
        result=$?
        set -e
        require_available_port
        echo 'The owned speech process exited before its listener was verified.' >&2
        if (( result == 0 )); then result=4; fi
        exit "$result"
    fi
    if owns_listener && still_owned "$server_pid"; then
        printf 'SAYWHAT_OWNED_LISTENER %s %s\n' "$owner" "$port"
        break
    fi
    if (( SECONDS >= listen_deadline )); then
        require_available_port
        echo 'Timed out verifying the owned speech listener. No other listener was accepted.' >&2
        exit 5
    fi
    sleep 0.1
done

set +e
wait "$server_pid"
result=$?
set -e
exit "$result"
