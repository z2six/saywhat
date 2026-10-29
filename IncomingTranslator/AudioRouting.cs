using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace IncomingTranslator;

internal sealed record PlaybackDeviceChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

internal static class AudioRouting
{
    public static string PlaybackWarning(string choice)
    {
        using MMDevice device = Resolve(choice);
        return MutedPlaybackWarning(device.AudioEndpointVolume.Mute,
            device.AudioEndpointVolume.MasterVolumeLevelScalar, device.FriendlyName);
    }

    internal static string MutedPlaybackWarning(bool muted, float volume, string deviceName) =>
        muted || volume <= 0.001f
            ? $"No incoming audio: Windows playback is muted or at 0% on {deviceName}. Unmute it to translate other people."
            : "";

    public static List<PlaybackDeviceChoice> Choices()
    {
        var choices = new List<PlaybackDeviceChoice>
        {
            new("auto-vrchat", "Automatic — follow VRChat's playback device"),
            new("default", "Windows default playback device")
        };
        using var enumerator = new MMDeviceEnumerator();
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device) choices.Add(new(device.ID, device.FriendlyName));
        }
        return choices;
    }

    public static MMDevice Resolve(string choice, string? preferredId = null)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!string.IsNullOrWhiteSpace(choice) && choice is not "auto-vrchat" and not "default")
        {
            MMDevice selected = enumerator.GetDevice(choice);
            if (selected.State == DeviceState.Active) return selected;
            selected.Dispose();
            throw new InvalidOperationException("The selected playback device is disconnected. Choose Automatic or another device in Options.");
        }
        if (choice != "default")
        {
            var vrchatIds = Process.GetProcessesByName("VRChat").Select(process =>
            {
                using (process) return (uint)process.Id;
            }).ToHashSet();
            string? bestId = null;
            double bestScore = -1;
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    try
                    {
                        SessionCollection sessions = device.AudioSessionManager.Sessions;
                        for (int index = 0; index < sessions.Count; index++)
                        {
                            using AudioSessionControl session = sessions[index];
                            if (!vrchatIds.Contains(session.GetProcessID)) continue;
                            double score = session.State == AudioSessionState.AudioSessionStateActive ? 10 : 0;
                            score += session.AudioMeterInformation.MasterPeakValue;
                            if (device.ID == preferredId) score += 0.02; // Avoid hopping between mirrored outputs.
                            if (score > bestScore) (bestScore, bestId) = (score, device.ID);
                        }
                    }
                    catch { /* A disappearing audio session must not stop capture. */ }
                }
            }
            if (bestId is not null) return enumerator.GetDevice(bestId);
        }
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }
}

// This changes only audio sent to recognition, never Windows/VRChat playback volume.
internal sealed class RecognitionGainSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly Func<bool> _enabled;
    public RecognitionGainSampleProvider(ISampleProvider source, Func<bool> enabled) => (_source, _enabled) = (source, enabled);
    public WaveFormat WaveFormat => _source.WaveFormat;
    public double LastGain { get; private set; } = 1;
    public double LastRms { get; private set; }
    public double LastPeak { get; private set; }
    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        double energy = 0, peak = 0;
        for (int index = offset; index < offset + read; index++)
        {
            energy += buffer[index] * buffer[index];
            peak = Math.Max(peak, Math.Abs(buffer[index]));
        }
        LastRms = Math.Sqrt(energy / Math.Max(1, read));
        LastPeak = peak;
        double desired = _enabled() && LastRms >= 0.00010
            ? Math.Min(Math.Clamp(0.025 / LastRms, 1, 8), 0.90 / Math.Max(0.00001, peak))
            : 1;
        LastGain = Math.Clamp(desired, 1, 8);
        for (int index = offset; index < offset + read; index++)
            buffer[index] = (float)Math.Clamp(buffer[index] * LastGain, -0.98, 0.98);
        return read;
    }
}
