#!/usr/bin/env bash
# Explicit-consent setup only. Never starts an inference engine or downloads weights.
set -euo pipefail
backend=${1:?backend required}
job_id=${2:?job id required}
case "$backend" in cuda|vulkan|cpu) ;; *) echo 'Choose cuda, vulkan or cpu.' >&2; exit 2 ;; esac
[[ "$job_id" =~ ^[a-f0-9]{32}$ && $(id -u) == 0 ]] || { echo 'Setup requires its own valid job ID and Linux root permission.' >&2; exit 3; }
grep -qi 'microsoft.*WSL2' /proc/sys/kernel/osrelease || {
    echo 'This distribution is not running under WSL2. Convert it to WSL2 before installing realtime speech dependencies.' >&2
    exit 3
}

job_file="/tmp/localtalk-build-$job_id.pid"
printf '%s\n' "$$" > "$job_file"
cleanup() {
    local result=$?
    rm -f -- "$job_file"
    [[ -z "${package_output:-}" ]] || rm -f -- "$package_output"
    # Retain failure diagnostics for troubleshooting; successful setup does not
    # leave an accumulating copy of the package manager's normal output.
    if [[ "$result" == 0 && -n "${package_log:-}" ]]; then rm -f -- "$package_log"; fi
    return "$result"
}
trap cleanup EXIT
trap 'echo "SayWhat? setup was cancelled; no inference engine was started." >&2; exit 130' INT TERM
source /etc/os-release
[[ "$ID" == ubuntu && "$VERSION_ID" == 24.04 ]] || {
    echo "Fresh setup is pinned to Ubuntu 24.04, not $ID $VERSION_ID. Use the existing-runtime import for other distributions." >&2
    exit 4
}
available_mb=$(df -Pm /opt | awk 'NR == 2 {print $4}')
required_mb=4096
[[ "$backend" == cuda ]] && required_mb=16384
[[ "$available_mb" -ge "$required_mb" ]] || {
    echo "Speech build requires at least $required_mb MB of free Linux disk space; only $available_mb MB is available." >&2
    exit 4
}
if [[ "$backend" == cuda ]]; then
    /bin/bash "$(dirname "$0")/preflight-voxtral-backend.sh" cuda
fi

echo 'Step 1/5: Installing signed Ubuntu build packages (may download several hundred MB).'
export DEBIAN_FRONTEND=noninteractive
# Newline-delimited status works through a background pipe without a terminal.
# Percentages describe the current package transaction, not the entire setup.
export LC_ALL=C
apt_progress=(-q -o APT::Status-Fd=1 -o Dpkg::Use-Pty=0 -o DPkg::Lock::Timeout=120)
package_log=$(umask 077; mktemp "/tmp/localtalk-packages-$job_id.XXXXXXXX.log")
package_output=$(umask 077; mktemp "/tmp/localtalk-package-output-$job_id.XXXXXXXX")
echo "Package setup transcript: $package_log"

# Begin package transaction helpers. The offline fixture exercises this block.
relay_package_output() (
    # Only this relay ignores SIGPIPE. Package scripts must never inherit a
    # failed WSL/Windows output handle, nor stop halfway through configuration
    # just because its progress consumer went away. Keep draining to the log.
    trap '' PIPE
    local line forwarding=true
    exec 4>> "$package_log" 5> "$package_output" || exit 1
    while IFS= read -r line || [[ -n "$line" ]]; do
        printf '%s\n' "$line" >&4 || exit 1
        printf '%s\n' "$line" >&5 || exit 1
        if "$forwarding" && ! printf '%s\n' "$line" 2>/dev/null; then
            forwarding=false
        fi
    done
)
run_package() {
    local -a results
    if "$@" </dev/null 2>&1 | relay_package_output; then
        results=("${PIPESTATUS[@]}")
    else
        results=("${PIPESTATUS[@]}")
    fi
    if [[ "${results[1]}" != 0 ]]; then
        echo "Cannot write the package setup transcript: $package_log" >&2
        # Do not replace an actual package failure with the relay's status.
        if [[ "${results[0]}" == 0 ]]; then return "${results[1]}"; fi
    fi
    return "${results[0]}"
}
run_apt() {
    local result
    if run_package apt-get "${apt_progress[@]}" "$@"; then return 0; else result=$?; fi
    # Repair only the explicit interrupted-transaction condition. Network,
    # checksum, lock and maintainer-script failures retain their actual result.
    # Never remove package locks or stop another package manager.
    if [[ "$result" == 100 ]] && grep -Fq "dpkg was interrupted, you must manually run 'dpkg --configure -a'" "$package_output"; then
        if run_package dpkg --configure -a; then
            if run_package apt-get "${apt_progress[@]}" "$@"; then return 0; else result=$?; fi
        else
            result=$?
        fi
    fi
    return "$result"
}
# End package transaction helpers.

run_apt update
run_apt install -y --no-install-recommends build-essential cmake ninja-build git curl ca-certificates libboost-json-dev python3-minimal

cuda_options=()
vulkan_options=(-DGGML_VULKAN=OFF)
if [[ "$backend" == cuda ]]; then
    [[ -x /usr/lib/wsl/lib/nvidia-smi ]] || { echo 'WSL cannot see the NVIDIA Windows driver. Install/update the Windows driver and WSL kernel first.' >&2; exit 5; }
    /usr/lib/wsl/lib/nvidia-smi --query-gpu=name --format=csv,noheader
    if [[ ! -x /usr/local/cuda-13.1/bin/nvcc ]]; then
        echo 'Step 2/5: Installing the CUDA 13.1 TOOLKIT only (multi-GB download). No Linux display driver is installed.'
        keyring="/tmp/localtalk-cuda-keyring-$job_id.deb"
        curl --fail --location --proto '=https' --tlsv1.2 --output "$keyring" \
            'https://developer.download.nvidia.com/compute/cuda/repos/wsl-ubuntu/x86_64/cuda-keyring_1.1-1_all.deb'
        echo "eea6cc5f0eaeb99082d054b8c05ae206a378e31e88048df0310d59f651dceed2  $keyring" | sha256sum --check --status || {
            echo 'The NVIDIA repository keyring did not match the pinned checksum. It was not installed.' >&2
            exit 8
        }
        run_package dpkg -i "$keyring"
        rm -- "$keyring"
        run_apt update
        run_apt install -y --no-install-recommends cuda-toolkit-13-1
    fi
    export PATH="/usr/local/cuda-13.1/bin:$PATH"
    export LD_LIBRARY_PATH="/usr/local/cuda-13.1/lib64:/usr/lib/wsl/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
    cuda_options=(-DGGML_CUDA=ON -DCMAKE_CUDA_COMPILER=/usr/local/cuda-13.1/bin/nvcc -DCMAKE_CUDA_ARCHITECTURES=native)
elif [[ "$backend" == vulkan ]]; then
    echo 'Step 2/5: Installing Vulkan build tools and probing the actual WSL Vulkan device.'
    run_apt install -y --no-install-recommends libvulkan-dev glslc vulkan-tools
    info=$(vulkaninfo --summary 2>&1) || { echo "$info"; echo 'Vulkan is unavailable in this WSL distribution. No CPU fallback was silently selected.' >&2; exit 6; }
    echo "$info"
    if ! grep -Eq 'PHYSICAL_DEVICE_TYPE_(DISCRETE|INTEGRATED)_GPU' <<< "$info"; then
        echo 'Only a software Vulkan adapter is visible; this is not a realtime GPU setup. NVIDIA users should explicitly choose CUDA.' >&2
        exit 7
    fi
    vulkan_options=(-DGGML_VULKAN=ON)
else
    echo 'Step 2/5: CPU runtime selected explicitly; realtime speed is not guaranteed.'
fi

# The validated toolkit includes optional developer GUIs. They are not user
# applications for SayWhat? and must not clutter Windows app search. This runs
# only for the automatic-setup distro, never an imported/user Ubuntu runtime.
if [[ "${3:-}" == --hide-desktop-integration && "${WSL_DISTRO_NAME:-}" =~ ^(SayWhat|LocalTalk)-Ubuntu(-[a-f0-9]{8})?$ ]]; then
    if ! /bin/bash "$(dirname "$0")/hide-speech-desktop-entries.sh" --owned-setup; then
        echo 'Optional speech desktop entry cleanup failed; the speech runtime build will continue.' >&2
    fi
fi

commit=3d1ed513debc88b4a383ad83e0cd0bcb84889a60
patch_level=localtalk-r2
root="/opt/localtalk-voxtral/$commit-$patch_level"
source_marker="$root/.localtalk-source-verified-$patch_level"
mkdir -p "$root"
if [[ ! -f "$source_marker" ]]; then
    echo 'Step 3/5: Downloading and verifying the pinned MIT-licensed Voxtral source archive.'
    archive="/tmp/localtalk-voxtral-$job_id.tar.gz"
    curl --fail --location --proto '=https' --tlsv1.2 --output "$archive" \
        "https://codeload.github.com/MrShitFox/voxtral.cpp/tar.gz/$commit"
    echo "3c0d31da1289ccf7234278d37cb84eead2cc0fc05471292229a5980c8cf015dd  $archive" | sha256sum --check --status || {
        echo 'The Voxtral source archive did not match the pinned checksum. No unverified source will be built.' >&2
        exit 9
    }
    tar -xzf "$archive" --strip-components=1 -C "$root"
    rm -- "$archive"
    backend_patch="$(dirname "$0")/voxtral-backend-selection.patch"
    recovery_patch="$(dirname "$0")/voxtral-continuous-recovery.patch"
    echo "237b1714a30c7af73adabfe2f98c8ee4f71cadf5973713100649697e1cfffcf3  $recovery_patch" | sha256sum --check --status || {
        echo 'The bundled realtime recovery patch did not match its pinned checksum. No modified source will be built.' >&2
        exit 10
    }
    # A new revision-specific root leaves previous builds intact and prevents an
    # old source-ready marker from silently omitting the validated recovery fix.
    git -C "$root" apply --check "$backend_patch"
    git -C "$root" apply "$backend_patch"
    git -C "$root" apply --check "$recovery_patch"
    git -C "$root" apply "$recovery_patch"
    touch "$source_marker"
fi
grep -Fq 'GIT_TAG        5cecdad692d868e28dbd2f7c468504770108f30c' "$root/CMakeLists.txt" || {
    echo 'The speech source does not declare the validated pinned GGML dependency. Setup stopped without building or starting an engine.' >&2
    exit 11
}
echo 'Step 4/5: Configuring the pinned GGML dependency and building with ONE CPU job. No speech model is loaded.'
cmake -S "$root" -B "$root/build-$backend" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON -DVOXTRAL_BUILD_SERVER=ON \
    -DBUILD_TESTING=OFF -DVOXTRAL_BUILD_EXAMPLES=OFF -DVOXTRAL_NATIVE_OPT=OFF \
    "${vulkan_options[@]}" "${cuda_options[@]}"
cmake --build "$root/build-$backend" --parallel 1
echo 'Step 5/5: Checking executable help without a model. GPU inference remains OFF.'
"$root/build-$backend/voxtral-server" --help
echo "LOCALTALK_VOXTRAL_SERVER=$root/build-$backend/voxtral-server"
echo 'Speech runtime setup finished. Download/import the realtime model separately, then explicitly press Start.'
