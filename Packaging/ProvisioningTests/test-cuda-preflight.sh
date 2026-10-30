#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
source "$root/scripts/preflight-voxtral-backend.sh"
fixture=$(mktemp -d)
trap 'rm -f -- "$fixture/nvidia-smi" "$fixture/result"; rmdir -- "$fixture"' EXIT
cat > "$fixture/nvidia-smi" <<'SH'
#!/usr/bin/env bash
case "$1" in
    --query-gpu=driver_version) printf '%s\n' "$FIXTURE_DRIVER" ;;
    --query-gpu=compute_cap) printf '%s\n' "$FIXTURE_CAPABILITY" ;;
    *) exit 19 ;;
esac
SH
chmod +x "$fixture/nvidia-smi"
checks=0
check_case() {
    local expected=$1 result=0
    export FIXTURE_DRIVER=$2 FIXTURE_CAPABILITY=$3
    saywhat_check_cuda_driver "$fixture/nvidia-smi" > "$fixture/result" 2>&1 || result=$?
    [[ "$result" == "$expected" ]] || { cat "$fixture/result"; exit 1; }
    ((checks+=1))
}
check_case 0 580.88 7.5
check_case 0 591.44 8.9
check_case 0 621.01 12.0
check_case 0 $'591.44\n591.44' $'8.9\n12.0'
check_case 5 579.99 8.9
check_case 5 591.44 6.1
check_case 5 591.44 7.0
check_case 5 591.44 $'8.9\n6.1'
check_case 5 '' 8.9
check_case 5 'driver unavailable' 8.9
check_case 5 591.44 'N/A'
check_case 5 591.44 ''
echo "PASS $checks CUDA preflight fixtures; no GPU, WSL, package manager or model was used."
