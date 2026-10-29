"""Prepare/check pinned local Groups weights; importing this file downloads nothing."""
import argparse
import json
import os
from pathlib import Path
import sys
import threading

from protocol import FrameConfig, MODELS, UPSTREAM_REVISION


def inspect(model_path, model_id, runtime_root):
    missing = []
    root = Path(model_path)
    required = ("config.json", "preprocessor_config.json", "tokenizer_config.json", "tokenizer.json",
                "model.safetensors.index.json", "saywhat-model.json")
    for name in required:
        if not (root / name).is_file() or (root / name).stat().st_size == 0:
            missing.append(name)
    if not missing:
        try:
            marker = json.loads((root / "saywhat-model.json").read_text())
            if marker.get("model_id") != model_id or marker.get("revision") != MODELS[model_id]:
                missing.append("pinned model provenance")
            FrameConfig.read(json.loads((root / "preprocessor_config.json").read_text()))
            index = json.loads((root / "model.safetensors.index.json").read_text())
            shards = set(index["weight_map"].values())
            if not shards:
                missing.append("weight shards")
            for shard in shards:
                # Refuse model metadata trying to escape this model's directory.
                if Path(shard).name != shard or not (root / shard).is_file() or (root / shard).stat().st_size == 0:
                    missing.append("weight shard " + shard)
        except Exception as error:
            missing.append("checkpoint validation: " + str(error))
    runtime = Path(runtime_root)
    for relative in ("venv/bin/python", "source/vibevoice/modular/modeling_vibevoice_asr.py", "saywhat-runtime.json"):
        if not (runtime / relative).is_file():
            missing.append("runtime " + relative)
    if (runtime / "saywhat-runtime.json").is_file():
        try:
            marker = json.loads((runtime / "saywhat-runtime.json").read_text())
            if marker.get("revision") != UPSTREAM_REVISION or marker.get("adapter_version") != 1:
                missing.append("runtime provenance")
        except Exception:
            missing.append("runtime provenance")
    return {"prepared": not missing, "model_id": model_id, "model_path": str(root),
            "runtime_root": str(runtime), "revision": MODELS[model_id], "missing": missing}


def download(model_path, model_id):
    # Explicit preparation downloads public files, never uploads captured audio.
    from huggingface_hub import HfApi, snapshot_download
    from tqdm.auto import tqdm

    class DownloadProgress(tqdm):
        def display(self, *args, **kwargs):
            print("SAYWHAT_GROUPS_PROGRESS=" + json.dumps({"stage": "Downloading Groups speech model",
                "item": str(self.desc or "model files"), "completed": self.n,
                "total": self.total, "unit": self.unit}), flush=True)

    destination = Path(model_path)
    destination.mkdir(parents=True, exist_ok=True)
    metadata = HfApi(token=False).model_info(model_id, revision=MODELS[model_id], files_metadata=True)
    selected = [file for file in metadata.siblings if file.rfilename.endswith((".json", ".safetensors", ".txt"))
                or Path(file.rfilename).name.startswith("LICENSE") or file.rfilename == "README.md"]
    total = sum(file.size or 0 for file in selected)
    finished = threading.Event()

    def progress_monitor():
        # HF's snapshot progress measures completed files, so its largest shard
        # can appear frozen. Also report actual complete/in-progress disk bytes.
        while not finished.is_set():
            received = 0
            for file in selected:
                try:
                    received += (destination / file.rfilename).stat().st_size
                except FileNotFoundError:
                    pass
            for partial in (destination / ".cache" / "huggingface" / "download").glob("*.incomplete"):
                try:
                    received += partial.stat().st_size
                except FileNotFoundError:
                    pass
            print("SAYWHAT_GROUPS_PROGRESS=" + json.dumps({"stage": "Downloading Groups speech model",
                "completed": min(received, total), "total": total, "unit": "B"}), flush=True)
            finished.wait(.75)

    monitor = threading.Thread(target=progress_monitor, name="groups-download-progress", daemon=True)
    monitor.start()
    try:
        snapshot_download(repo_id=model_id, revision=MODELS[model_id], local_dir=str(destination), token=False,
            allow_patterns=["*.json", "*.safetensors", "*.txt", "LICENSE*", "README.md"],
            tqdm_class=DownloadProgress, max_workers=2)
    finally:
        finished.set()
        monitor.join(timeout=2)
    (destination / "saywhat-model.json").write_text(json.dumps({
        "model_id": model_id, "revision": MODELS[model_id], "license": "MIT", "adapter_version": 1
    }, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--model-path", required=True)
    parser.add_argument("--model-id", choices=MODELS, required=True)
    parser.add_argument("--runtime-root", required=True)
    args = parser.parse_args()
    os.environ.update(HF_HUB_DISABLE_TELEMETRY="1", DO_NOT_TRACK="1", HF_HUB_DISABLE_IMPLICIT_TOKEN="1",
                      HF_HUB_DISABLE_XET="1")
    if not args.check:
        download(args.model_path, args.model_id)
    result = inspect(args.model_path, args.model_id, args.runtime_root)
    print(json.dumps(result), flush=True)
    return 0 if result["prepared"] else 3


if __name__ == "__main__":
    sys.exit(main())
