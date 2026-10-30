using System;
using System.IO;
using System.Text.Json;

namespace IncomingTranslator;

// The capture process needs a small live view, not the dashboard's growing
// library of profiles. Legacy standalone roots remain readable when this file
// does not exist. A malformed projection never falls back to stale preferences.
internal static class OwnVoiceRuntimePreferences
{
    internal const string FileName = "own-voice-runtime.json";
    internal const string LegacyFileName = "vrchat-translation-settings.json";
    internal const int MaximumBytes = 65_536;

    internal static JsonDocument Read(string root)
    {
        string projection = Path.Combine(root, FileName);
        string path = File.Exists(projection) || Directory.Exists(projection) ? projection : Path.Combine(root, LegacyFileName);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] bytes = new byte[MaximumBytes + 1];
        int length = 0;
        while (length < bytes.Length)
        {
            int count = file.Read(bytes, length, bytes.Length - length);
            if (count == 0) break;
            length += count;
        }
        if (length > MaximumBytes) throw new IOException("The microphone runtime preferences exceed their safe size limit.");
        int offset = length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        var document = JsonDocument.Parse(bytes.AsMemory(offset, length - offset));
        if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
        document.Dispose();
        throw new JsonException("Microphone runtime preferences must be an object.");
    }
}
