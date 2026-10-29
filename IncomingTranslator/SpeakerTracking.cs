using System.Runtime.InteropServices;
using NAudio.Wave;

namespace IncomingTranslator;

internal readonly record struct SpeakerAttribution(string Label, double Confidence, bool IsMixed, double Pan)
{
    public bool CanLearn { get; init; } = true;
    public double SpeechSeconds { get; init; }
    public DateTimeOffset EvidenceStart { get; init; }
    public DateTimeOffset EvidenceEnd { get; init; }
    public string Diagnostic { get; init; } = "";
    public string EvidenceId { get; init; } = "";
    public string? PersonId { get; init; }
    public string? SuggestedPersonId { get; init; }
    public bool PersonConfirmed { get; init; }
    public string SessionVoiceKey { get; init; } = "";
}

// This tracker supplies accurately timed audio and conservative voice/overlap hints. Actual learned voice
// identity belongs to AiSpeakerIdentifier; position/pitch alone must never become a person's identity.
internal sealed class SpeakerTracker
{
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private readonly object _gate = new();
    private readonly List<AudioFrame> _audioFrames = new();
    private float[] _left = Array.Empty<float>();
    private float[] _right = Array.Empty<float>();
    private int _frameFill;
    private int _sampleRate;
    private int _frameSamples;
    private DateTimeOffset _frameStart;
    private DateTimeOffset _nextSampleAt;
    private DateTimeOffset _lastAudibleAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastVoiceLikeAt = DateTimeOffset.MinValue;

    public DateTimeOffset LastAudibleAt { get { lock (_gate) return _lastAudibleAt; } }
    public DateTimeOffset LastVoiceLikeAt { get { lock (_gate) return _lastVoiceLikeAt; } }
    public string LastDiagnostic { get; private set; } = "Waiting for clear voice audio.";

    public double VoicedSeconds(DateTimeOffset start, DateTimeOffset end)
    {
        lock (_gate)
            return _audioFrames.Where(frame => frame.VoiceLike && frame.End > start && frame.Start < end)
                .Sum(frame => Math.Max(0, ((frame.End < end ? frame.End : end) -
                    (frame.Start > start ? frame.Start : start)).TotalSeconds));
    }

    public void Observe(byte[] buffer, int count, WaveFormat format)
    {
        if (count <= 0 || format.Channels < 1 || format.SampleRate <= 0) return;
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            format is WaveFormatExtensible extensibleFloat && extensibleFloat.SubFormat == FloatSubFormat;
        bool isPcm = format.Encoding == WaveFormatEncoding.Pcm ||
            format is WaveFormatExtensible extensiblePcm && extensiblePcm.SubFormat == PcmSubFormat;
        int bytes = format.BitsPerSample / 8;
        if (!(isFloat && bytes == 4) && !(isPcm && bytes is 2 or 3 or 4)) return;
        int availableFrames = Math.Min(count, buffer.Length) / (format.Channels * bytes);
        if (availableFrames == 0) return;

        lock (_gate)
        {
            EnsureBuffers(format.SampleRate);
            DateTimeOffset blockStart = DateTimeOffset.UtcNow.AddSeconds(-(double)availableFrames / format.SampleRate);
            if (_nextSampleAt == default || Math.Abs((_nextSampleAt - blockStart).TotalMilliseconds) > 180)
            {
                _nextSampleAt = blockStart;
                _frameFill = 0;
            }
            DateTimeOffset sampleBlockStart = _nextSampleAt;
            for (int frame = 0; frame < availableFrames; frame++)
            {
                int index = frame * format.Channels * bytes;
                float left = ReadSample(buffer, index, bytes, isFloat);
                float right = format.Channels > 1 ? ReadSample(buffer, index + bytes, bytes, isFloat) : left;
                if (_frameFill == 0) _frameStart = _nextSampleAt;
                _left[_frameFill] = float.IsFinite(left) ? left : 0;
                _right[_frameFill] = float.IsFinite(right) ? right : 0;
                _frameFill++;
                _nextSampleAt = sampleBlockStart.AddSeconds((frame + 1.0) / _sampleRate);
                if (_frameFill == _frameSamples)
                {
                    AnalyzeFrame(_frameStart);
                    _frameFill = 0;
                }
            }
        }
    }

    public SpeakerAttribution Identify(DateTimeOffset start, DateTimeOffset end, bool exactInterval = false)
    {
        lock (_gate)
        {
            List<AudioFrame> all = exactInterval
                ? _audioFrames.Where(frame => frame.End > start && frame.Start < end).ToList()
                : SelectLatestVoiceTurn(start, end);
            List<AudioFrame> voiced = all.Where(frame => frame.VoiceLike).ToList();
            double speechSeconds = voiced.Sum(frame => exactInterval
                ? Math.Max(0, ((frame.End < end ? frame.End : end) - (frame.Start > start ? frame.Start : start)).TotalSeconds)
                : (frame.End - frame.Start).TotalSeconds);
            double weight = voiced.Sum(frame => frame.Energy);
            double pan = weight > 0 ? voiced.Sum(frame => frame.Pan * frame.Energy) / weight : 0;
            int overlapFrames = voiced.Count(frame => frame.ConflictingVoices);
            bool overlappingVoices = overlapFrames >= 8 && overlapFrames >= voiced.Count * 0.25;
            bool usable = speechSeconds >= 0.55 && !overlappingVoices;
            string label = overlappingVoices ? "Overlapping/uncertain voices" : "Unidentified speaker";
            DateTimeOffset evidenceStart = all.Count > 0 ? exactInterval && start > all[0].Start ? start : all[0].Start : start;
            DateTimeOffset evidenceEnd = all.Count > 0 ? exactInterval && end < all[^1].End ? end : all[^1].End : end;
            LastDiagnostic = $"Voice audio={speechSeconds:0.0}s, debugPan={pan:0.00}, concurrentVoiceFrames={overlapFrames}, canLearn={usable}.";
            return new SpeakerAttribution(label, 0, overlappingVoices, pan)
            {
                CanLearn = usable, SpeechSeconds = speechSeconds, EvidenceStart = evidenceStart, EvidenceEnd = evidenceEnd,
                Diagnostic = LastDiagnostic
            };
        }
    }

    public float[] GetMonoAudio16k(DateTimeOffset start, DateTimeOffset end, bool exactInterval = false)
    {
        lock (_gate)
        {
            if (end <= start || _sampleRate <= 0) return Array.Empty<float>();
            // A short, current voice window is both more accurate for speaker matching and cheaper to embed.
            if (end - start > TimeSpan.FromSeconds(6)) start = end.AddSeconds(-6);
            List<AudioFrame> frames = exactInterval
                ? _audioFrames.Where(frame => frame.End > start && frame.Start < end).ToList()
                : SelectLatestVoiceTurn(start, end);
            if (frames.Count == 0) return Array.Empty<float>();
            start = start > frames[0].Start ? start : frames[0].Start;
            end = end < frames[^1].End ? end : frames[^1].End;
            int length = Math.Max(0, (int)Math.Round((end - start).TotalSeconds * 16000));
            if (length == 0) return Array.Empty<float>();
            var output = new float[length];
            double leftEnergy = frames.Sum(frame => frame.LeftEnergy);
            double rightEnergy = frames.Sum(frame => frame.RightEnergy);
            double pan = (Math.Sqrt(rightEnergy) - Math.Sqrt(leftEnergy)) /
                Math.Max(1e-9, Math.Sqrt(rightEnergy) + Math.Sqrt(leftEnergy));
            // Keep one channel for the entire window rather than switching channels at every 40 ms frame.
            // This preserves voice timbre and avoids joining different spatial speakers into one fingerprint.
            bool leftDominant = pan < -0.12;
            bool rightDominant = pan > 0.12;
            foreach (AudioFrame frame in frames)
            {
                int firstOutput = Math.Max(0, (int)Math.Ceiling((frame.Start - start).TotalSeconds * 16000));
                int lastOutput = Math.Min(length, (int)Math.Floor((frame.End - start).TotalSeconds * 16000));
                for (int outputIndex = firstOutput; outputIndex < lastOutput; outputIndex++)
                {
                    double sourcePosition = ((start - frame.Start).TotalSeconds + outputIndex / 16000.0) * _sampleRate;
                    int first = Math.Clamp((int)sourcePosition, 0, frame.Left.Length - 1);
                    int second = Math.Min(frame.Left.Length - 1, first + 1);
                    double fraction = Math.Clamp(sourcePosition - first, 0, 1);
                    double left = frame.Left[first] * (1 - fraction) + frame.Left[second] * fraction;
                    double right = frame.Right[first] * (1 - fraction) + frame.Right[second] * fraction;
                    output[outputIndex] = (float)(leftDominant ? left : rightDominant ? right :
                        frame.PhaseCancellation ? frame.LeftEnergy >= frame.RightEnergy ? left : right : (left + right) * 0.5);
                }
            }
            // Missing/capture-silent spans stay silent at their real position, instead of being spliced out.
            return output;
        }
    }

    public void ResetProfiles()
    {
        // Acoustic hints deliberately have no learned identity database. This also clears stale attribution audio.
        lock (_gate) _audioFrames.Clear();
    }

    private List<AudioFrame> SelectLatestVoiceTurn(DateTimeOffset start, DateTimeOffset end)
    {
        // Keep the last speech turn after a real silence gap. Panning changes also happen when the listener
        // rotates their head, so stereo position is never a turn boundary or a learned identity.
        List<AudioFrame> all = _audioFrames.Where(frame => frame.End > start && frame.Start < end).ToList();
        List<AudioFrame> voiced = all.Where(frame => frame.VoiceLike).ToList();
        if (voiced.Count == 0) return all;
        AudioFrame last = voiced[^1];
        DateTimeOffset selectedStart = last.Start;
        DateTimeOffset previousStart = last.Start;
        for (int index = voiced.Count - 2; index >= 0; index--)
        {
            AudioFrame frame = voiced[index];
            if (previousStart - frame.End > TimeSpan.FromMilliseconds(650) ||
                last.End - frame.Start > TimeSpan.FromSeconds(6)) break;
            selectedStart = frame.Start;
            previousStart = frame.Start;
        }
        DateTimeOffset selectedEnd = last.End.AddMilliseconds(120);
        if (selectedEnd > end) selectedEnd = end;
        return all.Where(frame => frame.End > selectedStart && frame.Start < selectedEnd).ToList();
    }

    private static float ReadSample(byte[] buffer, int index, int bytes, bool isFloat)
    {
        if (isFloat) return MemoryMarshal.Read<float>(buffer.AsSpan(index, 4));
        if (bytes == 2) return MemoryMarshal.Read<short>(buffer.AsSpan(index, 2)) / 32768f;
        if (bytes == 4) return MemoryMarshal.Read<int>(buffer.AsSpan(index, 4)) / 2147483648f;
        int value = buffer[index] | buffer[index + 1] << 8 | buffer[index + 2] << 16;
        if ((value & 0x800000) != 0) value |= unchecked((int)0xff000000);
        return value / 8388608f;
    }

    private void EnsureBuffers(int sampleRate)
    {
        if (_sampleRate == sampleRate && _left.Length > 0) return;
        _sampleRate = sampleRate;
        _frameSamples = Math.Clamp(sampleRate / 25, 320, 4096);
        _left = new float[_frameSamples];
        _right = new float[_frameSamples];
        _frameFill = 0;
        _nextSampleAt = default;
        _audioFrames.Clear();
    }

    private void AnalyzeFrame(DateTimeOffset start)
    {
        double leftEnergy = 0, rightEnergy = 0, monoEnergy = 0, cross = 0;
        for (int index = 0; index < _frameSamples; index++)
        {
            double left = _left[index], right = _right[index];
            leftEnergy += left * left;
            rightEnergy += right * right;
            cross += left * right;
            monoEnergy += Math.Pow((left + right) * 0.5, 2);
        }
        bool phaseCancellation = monoEnergy < Math.Max(leftEnergy, rightEnergy) * 0.12;
        var mono = new double[_frameSamples];
        double energy = 0, differenceEnergy = 0;
        int zeroCrossings = 0;
        for (int index = 0; index < _frameSamples; index++)
        {
            double value = phaseCancellation ? leftEnergy >= rightEnergy ? _left[index] : _right[index] : (_left[index] + _right[index]) * 0.5;
            mono[index] = value;
            energy += value * value;
            if (index > 0)
            {
                differenceEnergy += Math.Pow(value - mono[index - 1], 2);
                if ((value >= 0) != (mono[index - 1] >= 0)) zeroCrossings++;
            }
        }
        double rms = Math.Sqrt(energy / _frameSamples);
        DateTimeOffset end = start.AddSeconds((double)_frameSamples / _sampleRate);
        if (rms >= 0.0012) _lastAudibleAt = end;
        double zcr = (double)zeroCrossings / _frameSamples;
        double brightness = Math.Sqrt(differenceEnergy / Math.Max(1e-12, energy));
        double pitchConfidence = rms >= 0.0012 ? EstimatePitch(mono, _sampleRate).Confidence : 0;
        bool voiceLike = rms >= 0.0012 && pitchConfidence >= 0.20 && zcr is >= 0.003 and <= 0.32 && brightness < 1.7;
        if (voiceLike) _lastVoiceLikeAt = end;
        double leftRms = Math.Sqrt(leftEnergy / _frameSamples), rightRms = Math.Sqrt(rightEnergy / _frameSamples);
        double pan = (rightRms - leftRms) / Math.Max(1e-9, rightRms + leftRms);
        bool conflictingVoices = false;
        double channelCorrelation = cross / Math.Sqrt(Math.Max(1e-12, leftEnergy * rightEnergy));
        if (voiceLike && Math.Abs(channelCorrelation) < 0.45 &&
            Math.Min(leftEnergy, rightEnergy) > Math.Max(leftEnergy, rightEnergy) * 0.20)
        {
            // A head rotation changes gain/delay but not pitch. Only two concurrently voiced, poorly correlated
            // channels with substantially different pitches are evidence of overlap; pan variance is not.
            var leftPitch = EstimatePitch(Array.ConvertAll(_left, value => (double)value), _sampleRate);
            var rightPitch = EstimatePitch(Array.ConvertAll(_right, value => (double)value), _sampleRate);
            double pitchRatio = Math.Max(leftPitch.Pitch, rightPitch.Pitch) / Math.Max(1, Math.Min(leftPitch.Pitch, rightPitch.Pitch));
            conflictingVoices = leftPitch.Confidence > 0.62 && rightPitch.Confidence > 0.62 &&
                pitchRatio is > 1.25 and < 1.80 && BestChannelCorrelation(_left, _right, _sampleRate) < 0.45;
        }
        _audioFrames.Add(new AudioFrame(start, end, _left.ToArray(), _right.ToArray(), leftEnergy, rightEnergy, energy, pan, voiceLike, phaseCancellation, conflictingVoices));
        DateTimeOffset cutoff = end.AddSeconds(-30);
        int firstKept = _audioFrames.FindIndex(frame => frame.End >= cutoff);
        if (firstKept > 0) _audioFrames.RemoveRange(0, firstKept);
    }

    private static (double Pitch, double Confidence) EstimatePitch(double[] samples, int sampleRate)
    {
        int minLag = Math.Max(1, sampleRate / 360);
        int maxLag = Math.Min(samples.Length / 2, sampleRate / 70);
        double best = 0;
        int bestLag = 0;
        for (int lag = minLag; lag <= maxLag; lag += 4)
        {
            double cross = 0, first = 0, second = 0;
            for (int index = lag; index < samples.Length; index += 3)
            {
                double a = samples[index], b = samples[index - lag];
                cross += a * b;
                first += a * a;
                second += b * b;
            }
            double confidence = cross / Math.Sqrt(Math.Max(1e-12, first * second));
            if (confidence > best) { best = confidence; bestLag = lag; }
        }
        return (bestLag > 0 ? (double)sampleRate / bestLag : 0, Math.Clamp(best, 0, 1));
    }

    private static double BestChannelCorrelation(float[] left, float[] right, int sampleRate)
    {
        double best = 0;
        int maximumDelay = Math.Max(1, sampleRate / 500);
        for (int lag = -maximumDelay; lag <= maximumDelay; lag += 3)
        {
            double cross = 0, leftEnergy = 0, rightEnergy = 0;
            int first = Math.Max(0, -lag), end = Math.Min(left.Length, right.Length - lag);
            for (int index = first; index < end; index += 4)
            {
                double a = left[index], b = right[index + lag];
                cross += a * b; leftEnergy += a * a; rightEnergy += b * b;
            }
            best = Math.Max(best, Math.Abs(cross / Math.Sqrt(Math.Max(1e-12, leftEnergy * rightEnergy))));
        }
        return best;
    }

    private sealed record AudioFrame(DateTimeOffset Start, DateTimeOffset End, float[] Left, float[] Right,
        double LeftEnergy, double RightEnergy, double Energy, double Pan, bool VoiceLike, bool PhaseCancellation, bool ConflictingVoices);
}
