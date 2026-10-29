#!/usr/bin/env bash
# Explicit preparation only. Never run automatically on app startup.
set -euo pipefail
mode=${1:?--prepare or --check required}
data=${2:?absolute Windows-data WSL path required}
model_id=${3:?model id required}
case "$mode" in --prepare|--check) ;; *) exit 2 ;; esac
[[ "$data" == /* && "$data" != / && "$data" != /mnt && "$data" != /mnt/c ]] || { echo 'Invalid Groups data directory.' >&2; exit 2; }
case "$model_id" in
    microsoft/VibeVoice-ASR-Streaming-1.5B) size=1.5B ;;
    microsoft/VibeVoice-ASR-Streaming-7B) size=7B ;;
    *) echo 'Unsupported Groups speech model.' >&2; exit 2 ;;
esac
revision=1541f590c7099820f10ea012f48d2399282df69f
if [[ $(id -u) == 0 ]]; then
    runtime="/opt/saywhat-vibevoice/$revision"
else
    runtime="${XDG_DATA_HOME:-$HOME/.local/share}/saywhat/vibevoice/$revision"
fi
scriptroot=$(cd -- "$(dirname -- "$0")" && pwd -P)
model="$data/models/vibevoice/$size"
if [[ "$mode" == --check ]]; then
    # Neither this probe nor the Python probe imports torch or loads any model.
    exec /usr/bin/python3 "$scriptroot/prepare.py" --check --model-path "$model" --model-id "$model_id" --runtime-root "$runtime"
fi

owner=${4:?owned preparation GUID required}
[[ "$owner" =~ ^[a-f0-9]{32}$ ]] || exit 2
[[ $(ps -o pgid= -p $$ | tr -d ' ') == $$ ]] || { echo 'Preparation must run in its app-owned process group.' >&2; exit 2; }
pidfile="/tmp/saywhat-vibevoice-prepare-$owner.pid"
[[ ! -e "$pidfile" ]] || { echo 'This Groups preparation token already has a marker.' >&2; exit 4; }
birth=$(awk '{print $22}' "/proc/$$/stat")
printf '%s\n%s\n%s\n%s\n' "$$" "$birth" "$owner" "$scriptroot/bootstrap-vibevoice.sh" > "$pidfile"
cleanup_prepare() {
    trap - EXIT INT
    trap '' TERM
    set +e
    # This shell is the verified process-group leader; all children belong to
    # this preparation only. A cancelled download cannot remain orphaned.
    kill -TERM -- "-$$" 2>/dev/null
    if [[ -f "$pidfile" ]] && [[ $(head -n 1 "$pidfile") == $$ ]] && [[ $(sed -n '3p' "$pidfile") == "$owner" ]]; then rm -- "$pidfile"; fi
}
trap cleanup_prepare EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

progress() { printf 'SAYWHAT_GROUPS_PROGRESS={"stage":"%s"}\n' "$1"; }
progress 'Preparing Groups speech tools'
if [[ $(id -u) == 0 ]]; then
    export DEBIAN_FRONTEND=noninteractive
    apt-get update
    apt-get install -y --no-install-recommends python3-venv python3-pip git ca-certificates
else
    /usr/bin/python3 -c 'import ensurepip' >/dev/null 2>&1 || { echo 'Python virtual-environment support is missing in this imported Ubuntu. Install python3-venv before preparing Groups; no system packages were changed.' >&2; exit 3; }
    command -v git >/dev/null || { echo 'Git is missing in this speech distribution. Prepare the regular speech tools first.' >&2; exit 3; }
fi
mkdir -p -- "$runtime"
if [[ ! -d "$runtime/source/.git" ]]; then
    git init "$runtime/source"
    git -C "$runtime/source" remote add origin https://github.com/microsoft/VibeVoice.git
    git -C "$runtime/source" fetch --depth 1 origin "$revision"
    git -C "$runtime/source" checkout --detach "$revision"
fi
[[ $(git -C "$runtime/source" rev-parse HEAD) == "$revision" ]] || { echo 'Groups runtime source revision does not match the pinned version.' >&2; exit 3; }
[[ -x "$runtime/venv/bin/python" ]] || /usr/bin/python3 -m venv "$runtime/venv"
python="$runtime/venv/bin/python"
if [[ ! -f "$runtime/saywhat-runtime.json" ]]; then
    progress 'Installing local Groups runtime'
    "$python" -m pip install --upgrade pip
    # CUDA 12.8 wheel supports Blackwell. No driver/system CUDA installation is changed.
    "$python" -m pip install torch==2.8.0 --index-url https://download.pytorch.org/whl/cu128
    "$python" -m pip install transformers==4.57.1 huggingface-hub==0.35.3 fastapi==0.115.12 'uvicorn[standard]==0.35.0' websockets==15.0.1
    "$python" -m pip install -e "$runtime/source"
    "$python" -c 'import importlib.metadata, json, pathlib, sys; pathlib.Path(sys.argv[1]).write_text(json.dumps({"revision":sys.argv[2],"adapter_version":1,"packages":{k:importlib.metadata.version(k) for k in ("torch","transformers","vibevoice","fastapi","uvicorn","huggingface-hub")}},indent=2))' "$runtime/saywhat-runtime.json" "$revision"
fi
progress 'Downloading Groups speech model'
export HF_HUB_DISABLE_TELEMETRY=1 DO_NOT_TRACK=1 HF_HUB_DISABLE_IMPLICIT_TOKEN=1
"$python" "$scriptroot/prepare.py" --model-path "$model" --model-id "$model_id" --runtime-root "$runtime"
