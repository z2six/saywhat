"""Loopback-only VibeVoice Groups adapter. No weights/network are loaded on import."""
import argparse
from array import array
import asyncio
from contextlib import asynccontextmanager
import gc
import json
import logging
import os
from pathlib import Path
import time
import uuid

from protocol import FrameConfig, MODELS, Pcm16Resampler, PROTOCOL_VERSION, SpeakerParser, validate_start

LOG = logging.getLogger("saywhat.groups")
MAX_CONTEXT_SECONDS = 450


def create_app(model_path, model_id, owner, device="cuda"):
    # Import the HTTP runtime only in this explicit server-start path.
    from fastapi import FastAPI, WebSocket, WebSocketDisconnect
    from fastapi.responses import JSONResponse

    state = {"status": "unloaded", "error": None, "model": None, "tokenizer": None, "frames": None}
    gpu_lock = asyncio.Lock()
    active = asyncio.Lock()

    def load():
        import torch
        from vibevoice.modular.modeling_vibevoice_asr import VibeVoiceASRForConditionalGeneration
        from vibevoice.processor.vibevoice_asr_processor import VibeVoiceASRProcessor
        local = Path(model_path).resolve(strict=True)
        if not local.is_dir():
            raise ValueError("Groups checkpoint must be a prepared local directory.")
        frames = FrameConfig.read(json.loads((local / "preprocessor_config.json").read_text()))
        if device == "cuda" and not torch.cuda.is_available():
            raise RuntimeError("CUDA is unavailable in this WSL distribution. Groups was not started on CPU automatically.")
        processor = VibeVoiceASRProcessor.from_pretrained(str(local), local_files_only=True)
        if processor.tokenizer.text_chunk_end_id is None:
            raise ValueError("The tokenizer is not a streaming VibeVoice tokenizer.")
        # These calls are the official streaming demo APIs at the pinned revision.
        model = VibeVoiceASRForConditionalGeneration.from_pretrained(
            str(local), dtype=torch.bfloat16 if device == "cuda" else torch.float32,
            device_map=device, attn_implementation="sdpa", local_files_only=True,
        ).eval()
        state.update(model=model, tokenizer=processor.tokenizer, frames=frames)

    async def loading():
        try:
            state["status"] = "loading"
            LOG.info("Loading prepared local checkpoint %s", model_id)
            await asyncio.to_thread(load)
            state["status"] = "ready"
            print(json.dumps({"event": "model_loaded", "backend": device, "ready": True,
                              "sample_rate": state["frames"].sample_rate}), flush=True)
            LOG.info("Groups ready; inference is local, no raw audio is written to disk")
        except Exception as error:
            state.update(status="error", error=str(error))
            print(json.dumps({"event": "error", "code": "VIBEVOICE_STARTUP_FAILED", "backend": device}), flush=True)
            LOG.exception("Groups startup failed")

    @asynccontextmanager
    async def lifespan(_):
        task = asyncio.create_task(loading())
        yield
        # Process termination is the ultimate bounded cleanup if a CUDA call hangs.
        await asyncio.gather(task, return_exceptions=True)
        state.update(status="unloaded", model=None, tokenizer=None)
        gc.collect()
        try:
            import torch
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        except Exception:
            LOG.exception("Groups memory cleanup failed")

    app = FastAPI(title="SayWhat local Groups speech", lifespan=lifespan, docs_url=None, redoc_url=None)

    @app.get("/health")
    @app.get("/healthz")
    async def health():
        frames = state["frames"]
        return JSONResponse({
            "engine": "vibevoice", "protocol": PROTOCOL_VERSION, "owner": owner,
            "status": state["status"], "error": state["error"], "model_id": model_id,
            "input_sample_rate": 16000, "sample_rate": frames.sample_rate if frames else 24000,
            "chunk_ms": round(frames.chunk_samples * 1000 / frames.sample_rate, 3) if frames else None,
            "lookahead_ms": round(frames.lookahead_samples * 1000 / frames.sample_rate, 3) if frames else None,
            "max_session_seconds": MAX_CONTEXT_SECONDS, "word_timestamps": False,
            "active_sessions": 1 if active.locked() else 0,
        })

    @app.websocket("/v1/realtime")
    async def websocket(ws: WebSocket):
        if ws.client and ws.client.host not in ("127.0.0.1", "::1"):
            await ws.close(code=1008, reason="Local clients only")
            return
        await ws.accept()
        if state["status"] != "ready":
            await ws.send_json({"type": "error", "code": "not_ready", "message": state["error"] or "Groups is still loading."})
            await ws.close(code=1013)
            return
        if active.locked():
            await ws.send_json({"type": "error", "code": "busy", "message": "Groups already has an active audio session."})
            await ws.close(code=1013)
            return
        try:
            options = validate_start(json.loads(await asyncio.wait_for(ws.receive_text(), 10)))
        except Exception as error:
            await ws.send_json({"type": "error", "code": "invalid_start", "message": str(error)})
            await ws.close(code=1008)
            return
        async with active:
            session_id = uuid.uuid4().hex
            frames = state["frames"]
            model, tokenizer = state["model"], state["tokenizer"]
            resampler, parser = Pcm16Resampler(), SpeakerParser()
            queue = asyncio.Queue(maxsize=96)
            buffer = array("f")
            epoch = 0
            epoch_start = 0
            consumed = 0
            chunk_index = 0
            queued_input_samples = 0
            failure = None
            stream = None

            def initialize():
                return model.init_streaming_state(tokenizer, context_info=options["context_info"])

            def infer(window):
                import numpy as np
                import torch
                audio = torch.from_numpy(np.asarray(window, dtype=np.float32)).to(next(model.parameters()).device)
                with torch.inference_mode():
                    features = model.encode_speech(audio.unsqueeze(0))
                    text, _ = model.streaming_generate_step(
                        audio_features=features, streaming_state=stream, tokenizer=tokenizer,
                        max_new_tokens=options["max_tokens"], temperature=0.0)
                return text

            async def receiver():
                nonlocal queued_input_samples, failure
                try:
                    while True:
                        message = await asyncio.wait_for(ws.receive(), 30)
                        if message.get("type") == "websocket.disconnect":
                            return
                        pcm = message.get("bytes")
                        if pcm is not None:
                            if not pcm or len(pcm) % 2 or len(pcm) > 32000:
                                raise ValueError("An audio packet must contain at most one second of complete PCM16 samples.")
                            if queued_input_samples + len(pcm) // 2 > 16000 * 8:
                                raise RuntimeError("Groups cannot keep up with incoming audio; its eight-second queue limit was reached.")
                            queue.put_nowait(pcm)
                            queued_input_samples += len(pcm) // 2
                        else:
                            command = json.loads(message.get("text") or "{}")
                            if command.get("type") == "session.end":
                                await queue.put(None)
                                return
                            if command.get("type") != "session.ping":
                                raise ValueError("Unsupported Groups session command.")
                except (WebSocketDisconnect, asyncio.CancelledError):
                    return
                except Exception as error:
                    failure = error
                finally:
                    # Nonblocking wake-up; caller also checks receiver completion.
                    try:
                        queue.put_nowait(None)
                    except asyncio.QueueFull:
                        pass

            async def drain(flush=False):
                nonlocal buffer, stream, epoch, epoch_start, consumed, chunk_index
                while len(buffer) >= frames.window_samples or (flush and buffer):
                    cache = stream.get("past_key_values") if stream else None
                    cache_length = cache.get_seq_length() if cache is not None and hasattr(cache, "get_seq_length") else 0
                    decoder_config = getattr(getattr(model, "config", None), "decoder_config", None)
                    token_limit = min(24576, int(getattr(decoder_config, "max_position_embeddings", 32768)) - 2048)
                    if (consumed - epoch_start) / frames.sample_rate >= MAX_CONTEXT_SECONDS or cache_length >= token_limit:
                        epoch += 1
                        epoch_start = consumed
                        parser.reset()
                        stream = None
                        gc.collect()
                        async with gpu_lock:
                            stream = await asyncio.to_thread(initialize)
                        await ws.send_json({"type": "session.rollover", "session_id": session_id, "epoch": epoch,
                                            "audio_start_ms": consumed * 1000 / frames.sample_rate,
                                            "reason": "context_limit", "word_timestamps": False})
                    available = len(buffer)
                    window = array("f", buffer[:frames.window_samples])
                    window.extend([0.0] * (frames.window_samples - len(window)))
                    start = consumed
                    advance = min(frames.chunk_samples, available)
                    begin = time.monotonic()
                    async with gpu_lock:
                        text = await asyncio.to_thread(infer, window)
                    del buffer[:advance]
                    consumed += advance
                    chunk_index += 1
                    await ws.send_json({"type": "transcript.chunk", "session_id": session_id, "epoch": epoch,
                        "chunk_index": chunk_index, "audio_start_ms": start * 1000 / frames.sample_rate,
                        "audio_end_ms": consumed * 1000 / frames.sample_rate,
                        "raw_text": text, "segments": parser.parse(text), "word_timestamps": False,
                        "inference_ms": round((time.monotonic() - begin) * 1000, 2)})

            receive_task = None
            try:
                async with gpu_lock:
                    stream = await asyncio.to_thread(initialize)
                await ws.send_json({"type": "session.started", "session_id": session_id, "epoch": 0,
                    "protocol": PROTOCOL_VERSION, "chunk_ms": frames.chunk_samples * 1000 / frames.sample_rate,
                    "lookahead_ms": frames.lookahead_samples * 1000 / frames.sample_rate,
                    "word_timestamps": False})
                receive_task = asyncio.create_task(receiver())
                while True:
                    item = await asyncio.wait_for(queue.get(), 35)
                    if item is None:
                        if failure:
                            raise failure
                        buffer.extend(resampler.finish())
                        await drain(flush=True)
                        await ws.send_json({"type": "session.done", "session_id": session_id, "epoch": epoch,
                                            "total_chunks": chunk_index})
                        break
                    queued_input_samples -= len(item) // 2
                    buffer.extend(resampler.feed(item))
                    await drain()
                    if failure:
                        raise failure
            except (WebSocketDisconnect, asyncio.CancelledError):
                pass
            except Exception as error:
                print(json.dumps({"event": "error", "code": "VIBEVOICE_SESSION_FAILED", "backend": device}), flush=True)
                LOG.exception("Groups session failed")
                try:
                    await ws.send_json({"type": "error", "code": "session_failed", "message": str(error)})
                    await ws.close(code=1011)
                except Exception:
                    pass
            finally:
                if receive_task:
                    receive_task.cancel()
                    await asyncio.gather(receive_task, return_exceptions=True)
                stream = None
                buffer = array("f")
                gc.collect()

    return app


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-path", required=True)
    parser.add_argument("--model-id", choices=MODELS, required=True)
    parser.add_argument("--owner", required=True)
    parser.add_argument("--port", type=int, default=8082, choices=[8082])
    args = parser.parse_args()
    if len(args.owner) != 32 or any(c not in "0123456789abcdef" for c in args.owner):
        parser.error("owner must be a 32-character lowercase hexadecimal ownership token")
    # A prepared local runtime must never retrieve files or call hosted inference.
    os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1", DO_NOT_TRACK="1")
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    print(json.dumps({"event": "startup", "backend": "cuda", "listen": "127.0.0.1", "port": args.port,
                      "server_version": "saywhat-groups-1"}), flush=True)
    import uvicorn
    uvicorn.run(create_app(args.model_path, args.model_id, args.owner), host="127.0.0.1", port=args.port,
                ws_max_size=65536, ws_max_queue=8, timeout_graceful_shutdown=8, access_log=False)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(json.dumps({"event": "error", "code": "VIBEVOICE_" + type(error).__name__.upper()}), flush=True)
        raise
