"""Offline tests: no network, inference dependencies, microphones, WSL or GPU."""
from array import array
import json
from pathlib import Path
import random
import tempfile
import unittest

from protocol import FrameConfig, MODELS, Pcm16Resampler, SpeakerParser, validate_start
from prepare import inspect


class ProtocolTests(unittest.TestCase):
    def test_frame_config_uses_trained_values(self):
        config = FrameConfig.read({"target_sample_rate": 24000, "speech_tok_compress_ratio": 3200,
                                  "chunk_frames": 22, "lookahead_frames": 4})
        self.assertEqual(config.chunk_samples, 70400)
        self.assertEqual(config.lookahead_samples, 12800)
        self.assertEqual(config.window_samples, 83200)

    def test_reject_offline_or_malformed_checkpoints(self):
        for config in ({}, {"target_sample_rate": 24000},
                       {"target_sample_rate": 24000, "speech_tok_compress_ratio": 3200, "chunk_frames": True, "lookahead_frames": 4},
                       {"target_sample_rate": 16000, "speech_tok_compress_ratio": 3200, "chunk_frames": 22, "lookahead_frames": 4}):
            with self.assertRaises(ValueError):
                FrameConfig.read(config)

    def test_pcm_resampling_packet_boundaries_do_not_change_audio(self):
        source = array("h", [round(20000 * ((i % 321) / 321 - 0.5)) for i in range(16000)]).tobytes()
        whole = Pcm16Resampler()
        expected = whole.feed(source)
        expected.extend(whole.finish())
        fragmented = Pcm16Resampler()
        actual = array("f")
        random.seed(1)
        cursor = 0
        while cursor < len(source):
            count = random.randint(1, 1280) * 2
            actual.extend(fragmented.feed(source[cursor:cursor + count]))
            cursor += count
        actual.extend(fragmented.finish())
        self.assertEqual(len(actual), 24000)
        self.assertEqual(actual, expected)
        self.assertLessEqual(len(fragmented._samples), 1)

    def test_pcm_silence_and_empty_flush(self):
        resampler = Pcm16Resampler()
        self.assertEqual(len(resampler.finish()), 0)
        result = resampler.feed(b"\0\0" * 160)
        result.extend(resampler.finish())
        self.assertEqual(len(result), 240)
        self.assertTrue(all(value == 0 for value in result))

    def test_pcm_rejects_incomplete_samples(self):
        with self.assertRaises(ValueError):
            Pcm16Resampler().feed(b"\0")

    def test_multiple_speakers_and_continuations(self):
        parser = SpeakerParser()
        self.assertEqual(parser.parse("Speaker 0: Hello. Speaker 1: 你好。"),
                         [{"voice_id": "0", "text": "Hello."}, {"voice_id": "1", "text": "你好。"}])
        self.assertEqual(parser.parse("继续。"), [{"voice_id": "1", "text": "继续。"}])
        self.assertEqual(parser.parse(" More. Speaker 0: はい。"),
                         [{"voice_id": "1", "text": "More."}, {"voice_id": "0", "text": "はい。"}])

    def test_session_rollover_does_not_reuse_names(self):
        parser = SpeakerParser()
        parser.parse("Speaker 2: hello")
        parser.reset()
        self.assertEqual(parser.parse("Unlabelled continuation"), [{"voice_id": None, "text": "Unlabelled continuation"}])
        self.assertEqual(parser.parse("Speaker 2: new voice"), [{"voice_id": "2", "text": "new voice"}])

    def test_labels_without_spaces_and_nonenglish_content(self):
        parser = SpeakerParser()
        self.assertEqual(parser.parse("Speaker0: 안녕하세요.\nSpeaker 01: Bonjour.<|text_chunk_end|>"),
                         [{"voice_id": "0", "text": "안녕하세요."}, {"voice_id": "1", "text": "Bonjour."}])
        self.assertEqual(parser.parse(""), [])
        self.assertEqual(parser.parse("Speaker 3:"), [])

    def test_start_validation(self):
        value = {"type": "session.start", "protocol": 1, "sample_rate": 16000, "format": "pcm_s16le"}
        self.assertEqual(validate_start(value), {"context_info": None, "max_tokens": 256})
        for change in ({"sample_rate": 24000}, {"channels": 2}, {"format": "float32"}, {"max_tokens": 0},
                       {"max_tokens": True}, {"context_info": "x" * 513}, {"protocol": 2}, {"type": "start"}):
            with self.assertRaises(ValueError):
                validate_start(value | change)

    def test_inspection_fails_without_receipt_or_weights(self):
        with tempfile.TemporaryDirectory(prefix="saywhat-groups-test-") as directory:
            result = inspect(directory, next(iter(MODELS)), directory)
            self.assertFalse(result["prepared"])
            self.assertIn("preprocessor_config.json", result["missing"])
            self.assertFalse((Path(directory) / "venv").exists())


if __name__ == "__main__":
    unittest.main()
