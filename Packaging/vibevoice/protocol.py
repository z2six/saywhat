"""SayWhat local Groups wire protocol. Pure stdlib; no inference/import side effects."""
from array import array
from dataclasses import dataclass
import math
import re
import sys

PROTOCOL_VERSION = 1
MODELS = {
    "microsoft/VibeVoice-ASR-Streaming-1.5B": "4262d23d8a539a6530cf64fbd0b1751ef9a30853",
    "microsoft/VibeVoice-ASR-Streaming-7B": "60d858b518b4e19d404af3737f848fc185b30177",
}
UPSTREAM_REVISION = "1541f590c7099820f10ea012f48d2399282df69f"
SPEAKER = re.compile(r"Speaker\s*(\d{1,3})\s*:", re.IGNORECASE)


@dataclass(frozen=True)
class FrameConfig:
    sample_rate: int
    chunk_samples: int
    lookahead_samples: int

    @classmethod
    def read(cls, value):
        required = ("target_sample_rate", "speech_tok_compress_ratio", "chunk_frames", "lookahead_frames")
        if any(k not in value for k in required):
            raise ValueError("Not a VibeVoice streaming checkpoint: trained frame configuration is missing.")
        if any(type(value[k]) is not int for k in required):
            raise ValueError("Streaming frame configuration must contain integer values.")
        rate, ratio, chunks, lookahead = (value[k] for k in required)
        if rate != 24000 or ratio <= 0 or chunks <= 0 or lookahead < 0:
            raise ValueError("Unsupported streaming frame configuration.")
        if (chunks + lookahead) * ratio > rate * 10:
            raise ValueError("The checkpoint audio window exceeds the adapter's ten-second bound.")
        return cls(rate, ratio * chunks, ratio * lookahead)

    @property
    def window_samples(self):
        return self.chunk_samples + self.lookahead_samples


class Pcm16Resampler:
    """Continuous 16→24 kHz linear interpolation, retaining phase across packets.

    Upsampling only: no anti-aliasing low-pass is needed. The interpolation has
    one input-sample lookahead. Do not reset at silence or each audio packet.
    """
    def __init__(self, input_rate=16000, output_rate=24000):
        if input_rate != 16000 or output_rate != 24000:
            raise ValueError("Only mono 16 kHz PCM16 input and 24 kHz output are supported.")
        self.input_rate, self.output_rate = input_rate, output_rate
        self._samples = array("f")
        self._base = 0
        self._next_numerator = 0
        self.input_samples = 0
        self.output_samples = 0

    def feed(self, pcm):
        if len(pcm) % 2:
            raise ValueError("PCM16 audio must contain complete two-byte samples.")
        values = array("h")
        values.frombytes(pcm)
        if sys.byteorder != "little":
            values.byteswap()
        self._samples.extend(v / 32768.0 for v in values)
        self.input_samples += len(values)
        return self._drain(False)

    def finish(self):
        return self._drain(True)

    def _drain(self, final):
        result = array("f")
        target = math.ceil(self.input_samples * self.output_rate / self.input_rate)
        while self.output_samples < target:
            index, remainder = divmod(self._next_numerator, self.output_rate)
            local = index - self._base
            if local + 1 >= len(self._samples) and not final:
                break
            if not self._samples or local >= len(self._samples):
                break
            a = self._samples[local]
            b = self._samples[min(local + 1, len(self._samples) - 1)]
            result.append(a + (b - a) * remainder / self.output_rate)
            self._next_numerator += self.input_rate
            self.output_samples += 1
        next_index = self._next_numerator // self.output_rate
        remove = min(max(0, next_index - self._base), max(0, len(self._samples) - 1))
        if remove:
            del self._samples[:remove]
            self._base += remove
        return result


class SpeakerParser:
    def __init__(self):
        self.voice = None

    def reset(self):
        self.voice = None

    def parse(self, text):
        # The model's text is a chunk delta, not a cumulative transcript.
        text = text.replace("<|text_chunk_end|>", "").replace("<|endoftext|>", "")
        matches = list(SPEAKER.finditer(text))
        segments = []
        cursor = 0
        for match in matches:
            prefix = text[cursor:match.start()].strip()
            if prefix:
                segments.append({"voice_id": self.voice, "text": prefix})
            ordinal = int(match.group(1))
            self.voice = str(ordinal) if ordinal <= 255 else None
            cursor = match.end()
        tail = text[cursor:].strip()
        if tail:
            segments.append({"voice_id": self.voice, "text": tail})
        return segments


def validate_start(options):
    if not isinstance(options, dict) or options.get("type") != "session.start":
        raise ValueError("The first message must be session.start.")
    if options.get("protocol") != PROTOCOL_VERSION:
        raise ValueError("Unsupported Groups protocol version.")
    if options.get("sample_rate") != 16000 or options.get("format") != "pcm_s16le":
        raise ValueError("Groups audio must be mono 16 kHz little-endian PCM16.")
    if options.get("channels", 1) != 1:
        raise ValueError("Groups requires one audio channel.")
    tokens = options.get("max_tokens", 256)
    if type(tokens) is not int or not 32 <= tokens <= 512:
        raise ValueError("max_tokens must be between 32 and 512.")
    context = options.get("context_info") or ""
    if not isinstance(context, str) or len(context) > 512:
        raise ValueError("context_info must be text no longer than 512 characters.")
    return {"max_tokens": tokens, "context_info": context or None}
