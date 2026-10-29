#!/usr/bin/env bash
set -euo pipefail

# Isolated ownership/termination regression. No server, models, GPU, microphone,
# network listener, or pre-existing PID directory is used by this fixture.
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
stop_script=${1:-"$script_dir/../scripts/stop-voxtral.sh"}
bash -n "$stop_script"
fixture_root=$(mktemp -d /tmp/localtalk-stop-fixture.XXXXXXXX)
[[ $(readlink -f "$fixture_root") == /tmp/localtalk-stop-fixture.* ]] || exit 2
export XDG_DATA_HOME="$fixture_root"
mkdir -p -- "$fixture_root/localtalk/pids"
declare -a fixture_pids=() fixture_births=() fixture_files=()
checks=0

is_alive() {
    [[ -d "/proc/$1" ]] && [[ $(awk '{print $3}' "/proc/$1/stat" 2>/dev/null) != Z ]]
}
owned_dummy() {
    local candidate=$1 expected_birth=$2
    [[ "$candidate" =~ ^[0-9]+$ && "$expected_birth" =~ ^[0-9]+$ ]] &&
    [[ $(readlink -f "/proc/$candidate/exe" 2>/dev/null) == /usr/bin/bash ]] &&
    [[ $(awk '{print $22}' "/proc/$candidate/stat" 2>/dev/null) == "$expected_birth" ]] &&
    tr '\0' '\n' < "/proc/$candidate/cmdline" |
        awk 'previous == "--port" && $0 == "8080" { found=1 } { previous=$0 } END { exit !found }'
}
cleanup() {
    local result=$? candidate index file
    set +e
    # Only PIDs created and retained by this script are eligible, and their
    # executable, birth time and fixture port are rechecked immediately.
    for index in "${!fixture_pids[@]}"; do
        candidate=${fixture_pids[$index]}
        if is_alive "$candidate" && owned_dummy "$candidate" "${fixture_births[$index]}"; then
            kill -KILL "$candidate"
        fi
        wait "$candidate" 2>/dev/null
    done
    for file in "${fixture_files[@]}"; do
        [[ "$file" == "$fixture_root/"* ]] && rm -f -- "$file"
    done
    rmdir -- "$fixture_root/localtalk/pids" "$fixture_root/localtalk" "$fixture_root" 2>/dev/null
    exit "$result"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

check() {
    if ! "$@"; then echo "FAIL: $*" >&2; exit 1; fi
    checks=$((checks + 1))
}
new_dummy() {
    local mode=$1 ready index
    owner=$(tr -d '-' < /proc/sys/kernel/random/uuid)
    [[ "$owner" =~ ^[a-f0-9]{32}$ ]]
    ready="$fixture_root/ready-$owner"
    fixture_files+=("$ready")
    /usr/bin/bash -c '
        if [[ "$1" == graceful ]]; then trap "exit 0" TERM; else trap "" TERM; fi
        printf ready > "$2"
        while :; do sleep 0.1; done
    ' localtalk-stop-test "$mode" "$ready" --port 8080 &
    pid=$!
    birth=$(awk '{print $22}' "/proc/$pid/stat")
    fixture_pids+=("$pid"); fixture_births+=("$birth")
    for index in $(seq 1 100); do [[ -f "$ready" ]] && break; sleep 0.02; done
    check test -f "$ready"
    check owned_dummy "$pid" "$birth"
    marker="$fixture_root/localtalk/pids/voxtral-8080-$owner.pid"
    fixture_files+=("$marker")
}
write_marker() { printf '%s\n%s\n%s\n' "$pid" "$1" "$2" > "$marker"; }
expect_refusal() {
    local expected=$1 server=$2 code=0 output="$fixture_root/refusal-$owner.txt"
    fixture_files+=("$output")
    bash "$stop_script" "$server" 8080 "$owner" > "$output" 2>&1 || code=$?
    check test "$code" -eq "$expected"
    check is_alive "$pid"
    check test -f "$marker"
}
finish_owned_dummy() {
    write_marker "$birth" "$owner"
    bash "$stop_script" /usr/bin/bash 8080 "$owner"
    wait "$pid" 2>/dev/null || true
    check test ! -d "/proc/$pid"
    check test ! -f "$marker"
}

new_dummy graceful
write_marker "$birth" "$owner"
bash "$stop_script" /usr/bin/bash 8080 "$owner"
wait "$pid"
check test ! -d "/proc/$pid"
check test ! -f "$marker"
echo 'PASS: graceful TERM exits and removes its marker.'

new_dummy ignored
write_marker "$birth" "$owner"
bash "$stop_script" /usr/bin/bash 8080 "$owner"
wait "$pid" 2>/dev/null || true
check test ! -d "/proc/$pid"
check test ! -f "$marker"
echo 'PASS: ignored TERM is force-stopped and its marker removed.'

new_dummy graceful
write_marker "$((birth + 1))" "$owner"
expect_refusal 4 /usr/bin/bash
finish_owned_dummy
echo 'PASS: a mismatched PID birth is refused without stopping the fixture.'

new_dummy graceful
foreign_owner=$(tr -d '-' < /proc/sys/kernel/random/uuid)
write_marker "$birth" "$foreign_owner"
expect_refusal 3 /usr/bin/bash
finish_owned_dummy
echo 'PASS: a foreign owner token is refused without stopping the fixture.'

new_dummy graceful
write_marker "$birth" "$owner"
expect_refusal 4 /usr/bin/sleep
finish_owned_dummy
echo 'PASS: a mismatched executable is refused without stopping the fixture.'

echo "PASS: $checks isolated stop checks; all owned dummy processes reaped."
