#!/usr/bin/env bash
set -euo pipefail

# Offline shell regression: source only the bootstrap's package helper functions.
# apt-get/dpkg are shell fixtures; no WSL, packages, network or models are used.
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
bootstrap=${1:-"$script_dir/../scripts/bootstrap-voxtral-wsl.sh"}
bash -n "$bootstrap"
fixture_root=$(mktemp -d "${TMPDIR:-/tmp}/localtalk-package-fixture.XXXXXXXX")
trap 'rm -f -- "$fixture_root"/*; rmdir -- "$fixture_root"' EXIT
helpers="$fixture_root/helpers.sh"
awk '/^# Begin package transaction helpers\./ { copy=1; next } /^# End package transaction helpers\./ { copy=0 } copy' "$bootstrap" > "$helpers"
[[ -s "$helpers" ]]
source "$helpers"
apt_progress=(-q -o APT::Status-Fd=1 -o Dpkg::Use-Pty=0 -o DPkg::Lock::Timeout=120)
package_log="$fixture_root/packages.log"
package_output="$fixture_root/output"
trace="$fixture_root/trace"
count_file="$fixture_root/count"
checks=0

check() {
    if ! "$@"; then echo "FAIL: $*" >&2; exit 1; fi
    checks=$((checks + 1))
}
reset_case() {
    : > "$package_log"; : > "$trace"; printf '0\n' > "$count_file"
    apt_mode=success
    repair_result=0
}
apt-get() {
    local count
    count=$(< "$count_file"); count=$((count + 1)); printf '%s\n' "$count" > "$count_file"
    printf 'apt-get %s\n' "$*" >> "$trace"
    if IFS= read -r unexpected; then echo 'Unexpected inherited package stdin.' >&2; return 70; fi
    echo 'dlstatus:1:25.0000:Downloading signed fixture package'
    echo 'fixture package stderr' >&2
    case "$apt_mode" in
        interrupted)
            if [[ "$count" == 1 ]]; then
                echo "E: dpkg was interrupted, you must manually run 'dpkg --configure -a' to correct the problem." >&2
                return 100
            fi ;;
        interrupted-again)
            echo "E: dpkg was interrupted, you must manually run 'dpkg --configure -a' to correct the problem." >&2
            return 100 ;;
        failed)
            echo '/usr/sbin/update-ca-certificates: 130: echo: echo: I/O error' >&2
            return 100 ;;
        locked)
            echo 'E: Could not get lock /var/lib/dpkg/lock-frontend. It is held by another process.' >&2
            return 100 ;;
    esac
    echo 'pmstatus:fixture:100.0000:Installed fixture package'
}
dpkg() {
    printf 'dpkg %s\n' "$*" >> "$trace"
    if IFS= read -r unexpected; then echo 'Unexpected inherited dpkg stdin.' >&2; return 70; fi
    echo 'Configuring interrupted fixture packages.'
    return "$repair_result"
}
expect_result() {
    local expected=$1 result=0
    run_apt install fixture-package <<< 'must not reach a package script' > "$fixture_root/progress" 2> "$fixture_root/error" || result=$?
    check test "$result" -eq "$expected"
}

reset_case
expect_result 0
check grep -Fq 'fixture package stderr' "$package_log"
check grep -Fq 'pmstatus:fixture:100.0000' "$fixture_root/progress"
check grep -Fq 'DPkg::Lock::Timeout=120' "$trace"
check test "$(wc -l < "$trace")" -eq 1
echo 'PASS: headless package stdin, both diagnostic streams, progress and bounded lock wait.'

reset_case
apt_mode=interrupted
expect_result 0
check test "$(wc -l < "$trace")" -eq 3
check test "$(sed -n '2p' "$trace")" = 'dpkg --configure -a'
check grep -Fq 'dpkg was interrupted' "$package_log"
check grep -Fq 'pmstatus:fixture:100.0000' "$package_log"
echo 'PASS: interrupted dpkg is configured once and the original transaction is retried once.'

reset_case
apt_mode=interrupted-again
expect_result 100
check test "$(wc -l < "$trace")" -eq 3
echo 'PASS: a repeated interrupted transaction remains a failure after one repair.'

reset_case
apt_mode=interrupted
repair_result=42
expect_result 42
check test "$(wc -l < "$trace")" -eq 2
echo 'PASS: failed dpkg recovery propagates its status without another install.'

for apt_mode_case in failed locked; do
    reset_case
    apt_mode=$apt_mode_case
    expect_result 100
    check test "$(wc -l < "$trace")" -eq 1
done
echo 'PASS: maintainer-script and lock failures are neither hidden nor generically retried.'

fake_burst() {
    local index
    for ((index=0; index<2000; index++)); do
        printf 'fixture output %04d: enough text to exceed the progress pipe buffer after its reader closes.\n' "$index"
    done
    echo 'Fixture package completed.' >> "$trace"
    echo 'final-package-line'
}
reset_case
run_package fake_burst 2> "$fixture_root/closed-error" | head -n 1 > "$fixture_root/closed-progress"
check grep -Fxq 'Fixture package completed.' "$trace"
check test "$(tail -n 1 "$package_log")" = final-package-line
check test "$(wc -l < "$package_log")" -eq 2001
check test ! -s "$fixture_root/closed-error"
echo 'PASS: a closed progress pipe is quiet and fully drained while packages complete.'

reset_case
run_package fake_burst 1>&- 2> "$fixture_root/closed-error"
check test "$(tail -n 1 "$package_log")" = final-package-line
check test ! -s "$fixture_root/closed-error"
echo 'PASS: an invalid progress handle also leaves package output drained without repeated write errors.'

fake_failed_burst() { fake_burst; return 42; }
reset_case
result=0
run_package fake_failed_burst 2> "$fixture_root/closed-error" | head -n 1 > "$fixture_root/closed-progress" || result=$?
check test "$result" -eq 42
check test "$(tail -n 1 "$package_log")" = final-package-line
check test ! -s "$fixture_root/closed-error"
echo 'PASS: a package failure retains its actual status after the progress reader closes.'

reset_case
package_output="$fixture_root/missing/output"
result=0
run_package apt-get install fixture-package > "$fixture_root/log-failure" 2>&1 || result=$?
check test "$result" -ne 0
check grep -Fq 'Cannot write the package setup transcript' "$fixture_root/log-failure"
echo "PASS: $checks isolated package checks; a transcript write failure is also reported."
