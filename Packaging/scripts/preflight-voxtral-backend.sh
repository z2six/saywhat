#!/usr/bin/env bash
# Metadata-only CUDA prerequisite check. No model, toolkit or driver is loaded.
set -euo pipefail

saywhat_check_cuda_driver() {
    local smi=$1 drivers capabilities line major minor count=0
    [[ -x "$smi" ]] || {
        echo 'WSL cannot see the NVIDIA Windows driver. Update that driver or choose a Windows speech model before retrying.' >&2
        return 5
    }
    drivers=$("$smi" --query-gpu=driver_version --format=csv,noheader,nounits) || {
        echo 'The NVIDIA driver could not be checked. No CUDA toolkit was downloaded.' >&2; return 5;
    }
    while IFS= read -r line; do
        line=${line//[[:space:]]/}
        [[ "$line" =~ ^([0-9]+)\.[0-9]+(\.[0-9]+)?$ ]] || {
            echo 'The NVIDIA driver version could not be verified. No CUDA toolkit was downloaded.' >&2; return 5;
        }
        major=${BASH_REMATCH[1]}
        (( 10#$major >= 580 )) || {
            echo 'This CUDA speech build needs NVIDIA driver branch 580 or newer. Update the Windows driver or choose a Windows speech model.' >&2
            return 5
        }
        ((count+=1))
    done <<< "$drivers"
    ((count > 0)) || return 5
    capabilities=$("$smi" --query-gpu=compute_cap --format=csv,noheader,nounits) || {
        echo 'The GPU compatibility check failed. Update the Windows NVIDIA driver or choose a Windows speech model.' >&2; return 5;
    }
    count=0
    while IFS= read -r line; do
        line=${line//[[:space:]]/}
        [[ "$line" =~ ^([0-9]+)\.([0-9]+)$ ]] || {
            echo 'The NVIDIA GPU compatibility could not be verified. No CUDA toolkit was downloaded.' >&2; return 5;
        }
        major=${BASH_REMATCH[1]}; minor=${BASH_REMATCH[2]}
        # CUDA 13 removed offline compilation for architectures below Turing.
        # The existing native build targets every visible GPU, so check them all.
        (( 10#$major > 7 || (10#$major == 7 && 10#$minor >= 5) )) || {
            echo 'This CUDA speech build needs Turing (compute capability 7.5) or newer GPUs. Choose a Windows speech model for this GPU.' >&2
            return 5
        }
        ((count+=1))
    done <<< "$capabilities"
    ((count > 0)) || return 5
    printf 'CUDA preflight passed: Windows NVIDIA driver %s; GPU compute capability %s. No model is loaded.\n' "$drivers" "$capabilities"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    [[ "${1:-}" == cuda ]] || { echo 'Choose the CUDA metadata check.' >&2; exit 2; }
    saywhat_check_cuda_driver /usr/lib/wsl/lib/nvidia-smi
fi
