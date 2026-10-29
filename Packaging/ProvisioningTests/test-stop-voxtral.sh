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
fixture_port=25347

is_alive() {
    [[ -d "/proc/$1" ]] && [[ $(awk '{print $3}' "/proc/$1/stat" 2>/dev/null) != Z ]]
}
owned_dummy() {
    local candidate=$1 expected_birth=$2
    [[ "$candidate" =~ ^[0-9]+$ && "$expected_birth" =~ ^[0-9]+$ ]] &&
    [[ $(readlink -f "/proc/$candidate/exe" 2>/dev/null) == /usr/bin/bash ]] &&
    [[ $(awk '{print $22}' "/proc/$candidate/stat" 2>/dev/null) == "$expected_birth" ]] &&
    tr '\0' '\n' < "/proc/$candidate/cmdline" |
        awk -v port="$fixture_port" 'previous == "--port" && $0 == port { found=1 } { previous=$0 } END { exit !found }'
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
    ' localtalk-stop-test "$mode" "$ready" --port "$fixture_port" &
    pid=$!
    birth=$(awk '{print $22}' "/proc/$pid/stat")
    fixture_pids+=("$pid"); fixture_births+=("$birth")
    for index in $(seq 1 100); do [[ -f "$ready" ]] && break; sleep 0.02; done
    check test -f "$ready"
    check owned_dummy "$pid" "$birth"
    marker="$fixture_root/localtalk/pids/voxtral-$fixture_port-$owner.pid"
    fixture_files+=("$marker")
}
write_marker() { printf '%s\n%s\n%s\n' "$pid" "$1" "$2" > "$marker"; }
expect_refusal() {
    local expected=$1 server=$2 code=0 output="$fixture_root/refusal-$owner.txt"
    fixture_files+=("$output")
    bash "$stop_script" "$server" "$fixture_port" "$owner" > "$output" 2>&1 || code=$?
    check test "$code" -eq "$expected"
    check is_alive "$pid"
    check test -f "$marker"
}
finish_owned_dummy() {
    write_marker "$birth" "$owner"
    bash "$stop_script" /usr/bin/bash "$fixture_port" "$owner"
    wait "$pid" 2>/dev/null || true
    check test ! -d "/proc/$pid"
    check test ! -f "$marker"
}

new_dummy graceful
write_marker "$birth" "$owner"
bash "$stop_script" /usr/bin/bash "$fixture_port" "$owner"
wait "$pid"
check test ! -d "/proc/$pid"
check test ! -f "$marker"
echo 'PASS: graceful TERM exits and removes its marker.'

new_dummy ignored
write_marker "$birth" "$owner"
bash "$stop_script" /usr/bin/bash "$fixture_port" "$owner"
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

new_dummy graceful
write_marker "$birth" "$owner"
wrong_port=$((fixture_port + 1))
wrong_marker="$fixture_root/localtalk/pids/voxtral-$wrong_port-$owner.pid"
fixture_files+=("$wrong_marker")
cp -- "$marker" "$wrong_marker"
code=0
bash "$stop_script" /usr/bin/bash "$wrong_port" "$owner" > /dev/null 2>&1 || code=$?
check test "$code" -eq 4
check is_alive "$pid"
check test -f "$marker"
check test -f "$wrong_marker"
rm -- "$wrong_marker"
finish_owned_dummy
echo 'PASS: a different selected port cannot stop or adopt the fixture process.'

for invalid in 0 1023 65536 -1 +25347 025347 '25347;echo' '25347 ' 99999999999999999999; do
    code=0
    bash "$stop_script" /usr/bin/bash "$invalid" 0123456789abcdef0123456789abcdef > /dev/null 2>&1 || code=$?
    check test "$code" -eq 2
done
for valid in 1024 65535; do
    bash "$stop_script" /usr/bin/bash "$valid" 0123456789abcdef0123456789abcdef
    checks=$((checks + 1))
done

echo "PASS: $checks isolated stop checks; all owned dummy processes reaped."
