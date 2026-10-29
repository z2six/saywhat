"""Local transport checks using an inert fake model; never import real torch."""
from array import array
from contextlib import nullcontext
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import time
from types import ModuleType, SimpleNamespace
import unittest
from unittest.mock import patch

import server
from protocol import MODELS


@unittest.skipUnless(importlib.util.find_spec("fastapi") and importlib.util.find_spec("numpy"), "HTTP test dependencies absent")
class ServerTests(unittest.TestCase):
    def setUp(self):
        # Preload ordinary HTTP/array dependencies before patch.dict snapshots
        # sys.modules, so restoring it cannot unload binary NumPy extensions.
        import numpy
        import fastapi
        import starlette.testclient

    def setup_fake(self):
        self.modules = {}
        for name in ("torch", "vibevoice", "vibevoice.modular", "vibevoice.processor",
                     "vibevoice.modular.modeling_vibevoice_asr", "vibevoice.processor.vibevoice_asr_processor"):
            self.modules[name] = ModuleType(name)
        torch = self.modules["torch"]
        torch.bfloat16 = "bfloat16"
        torch.float32 = "float32"
        torch.cuda = SimpleNamespace(is_available=lambda: True, empty_cache=lambda: None)
        torch.inference_mode = nullcontext

        class Tensor:
            def __init__(self, values): self.values = values
            def to(self, _): return self
            def unsqueeze(self, _): return self
        torch.from_numpy = Tensor

        class Model:
            @classmethod
            def from_pretrained(cls, path, **kwargs):
                assert kwargs["local_files_only"] is True
                assert kwargs["attn_implementation"] == "sdpa"
                return cls()
            def eval(self): return self
            def parameters(self): yield SimpleNamespace(device="fake")
            def init_streaming_state(self, tokenizer, context_info=None): return {"step": 0}
            def encode_speech(self, audio): return audio
            def streaming_generate_step(self, audio_features, streaming_state, tokenizer, max_new_tokens, temperature):
                streaming_state["step"] += 1
                return ["Speaker 0: Hello.", "Speaker 1: 你好。", "继续。"][(streaming_state["step"] - 1) % 3], streaming_state

        class Processor:
            @classmethod
            def from_pretrained(cls, path, **kwargs):
                assert kwargs["local_files_only"] is True
                return SimpleNamespace(tokenizer=SimpleNamespace(text_chunk_end_id=123))
        self.modules["vibevoice.modular.modeling_vibevoice_asr"].VibeVoiceASRForConditionalGeneration = Model
        self.modules["vibevoice.processor.vibevoice_asr_processor"].VibeVoiceASRProcessor = Processor

    def client(self, directory):
        from starlette.testclient import TestClient
        Path(directory, "preprocessor_config.json").write_text(json.dumps({
            "target_sample_rate": 24000, "speech_tok_compress_ratio": 160,
            "chunk_frames": 2, "lookahead_frames": 1}))
        return TestClient(server.create_app(directory, next(iter(MODELS)), "0" * 32), client=("127.0.0.1", 50000))

    def ready(self, client):
        for _ in range(100):
            health = client.get("/health").json()
            if health["status"] != "loading":
                break
            time.sleep(.01)
        self.assertEqual(health["status"], "ready", health)
        self.assertFalse(health["word_timestamps"])
        self.assertEqual(health["owner"], "0" * 32)

    def test_real_websocket_protocol_fake_inference(self):
        self.setup_fake()
        with tempfile.TemporaryDirectory(prefix="saywhat-groups-http-") as directory, patch.dict(sys.modules, self.modules), self.client(directory) as client:
            self.ready(client)
            with client.websocket_connect("/v1/realtime") as ws:
                ws.send_json({"type": "session.start", "protocol": 1, "sample_rate": 16000, "format": "pcm_s16le"})
                started = ws.receive_json()
                self.assertEqual(started["type"], "session.started")
                ws.send_bytes(array("h", [0] * 640).tobytes())
                first, second = ws.receive_json(), ws.receive_json()
                self.assertEqual(first["segments"], [{"voice_id": "0", "text": "Hello."}])
                self.assertEqual(second["segments"], [{"voice_id": "1", "text": "你好。"}])
                self.assertFalse(first["word_timestamps"])
                self.assertEqual(first["session_id"], second["session_id"])
                self.assertEqual(second["chunk_index"], 2)
                self.assertAlmostEqual(first["audio_end_ms"], second["audio_start_ms"])
                ws.send_json({"type": "session.end"})
                third, done = ws.receive_json(), ws.receive_json()
                self.assertEqual(third["segments"], [{"voice_id": "1", "text": "继续。"}])
                self.assertEqual(done["type"], "session.done")
                self.assertEqual(done["total_chunks"], 3)

    def test_rollover_in_same_connection_changes_epoch(self):
        self.setup_fake()
        with tempfile.TemporaryDirectory(prefix="saywhat-groups-rollover-") as directory, patch.dict(sys.modules, self.modules), patch.object(server, "MAX_CONTEXT_SECONDS", .01), self.client(directory) as client:
            self.ready(client)
            with client.websocket_connect("/v1/realtime") as ws:
                ws.send_json({"type": "session.start", "protocol": 1, "sample_rate": 16000, "format": "pcm_s16le"})
                started = ws.receive_json()
                ws.send_bytes(array("h", [0] * 640).tobytes())
                first, rollover, second = ws.receive_json(), ws.receive_json(), ws.receive_json()
                self.assertEqual(first["epoch"], 0)
                self.assertEqual(rollover["type"], "session.rollover")
                self.assertEqual(second["epoch"], 1)
                self.assertEqual(started["session_id"], second["session_id"])
                self.assertEqual(second["chunk_index"], 2)
                ws.send_json({"type": "session.end"})
                while ws.receive_json()["type"] != "session.done": pass

    def test_invalid_audio_format_has_clear_error(self):
        self.setup_fake()
        with tempfile.TemporaryDirectory(prefix="saywhat-groups-invalid-") as directory, patch.dict(sys.modules, self.modules), self.client(directory) as client:
            self.ready(client)
            with client.websocket_connect("/v1/realtime") as ws:
                ws.send_json({"type": "session.start", "protocol": 1, "sample_rate": 48000, "format": "pcm_s16le"})
                error = ws.receive_json()
                self.assertEqual(error["type"], "error")
                self.assertEqual(error["code"], "invalid_start")


if __name__ == "__main__":
    unittest.main()
