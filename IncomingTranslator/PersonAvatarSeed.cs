using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace IncomingTranslator;

// A decorative, device-local identity—not a voice matcher or an inferred portrait.
// The versioned seed is frozen after the first confirmed voice example so later
// corrections, model switches and statistics cannot keep changing someone's face.
internal static class PersonAvatarSeed
{
    internal static string? Canonical(string? seed) => seed is { Length: 64 } && seed.All(Uri.IsHexDigit)
        ? seed.ToLowerInvariant() : null;

    internal static string Resolve(SpeakerPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);
        return Canonical(person.VoiceAvatarSeed) ?? FromPersonId(person.Id);
    }

    internal static string FromPersonId(string personId)
    {
        if (string.IsNullOrWhiteSpace(personId)) throw new ArgumentException("An avatar needs a saved person identity.", nameof(personId));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText(hash, "SayWhat/person-avatar/id/v1");
        AppendText(hash, personId);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static string FromVoice(string personId, string modelSha, IReadOnlyList<float> embedding)
    {
        if (string.IsNullOrWhiteSpace(personId)) throw new ArgumentException("An avatar needs a saved person identity.", nameof(personId));
        ArgumentNullException.ThrowIfNull(modelSha);
        string model = SpeakerFeedbackStore.NormalizeModelSha(modelSha);
        ArgumentNullException.ThrowIfNull(embedding);
        if (embedding.Count is < 1 or > 4096 || embedding.Any(value => !float.IsFinite(value)))
            throw new ArgumentException("An avatar needs a valid confirmed voice example.", nameof(embedding));
        double normSquared = embedding.Sum(value => (double)value * value);
        if (Math.Abs(normSquared - 1) > .01)
            throw new ArgumentException("An avatar needs a normalized confirmed voice example.", nameof(embedding));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText(hash, "SayWhat/person-avatar/voice/v1");
        // Different local person IDs need not display identical avatars even if
        // their confirmed embeddings happen to match. Names never enter this hash.
        AppendText(hash, personId);
        AppendText(hash, model);
        byte[] quantized = new byte[embedding.Count * sizeof(short)];
        try
        {
            double scale = 32767 / Math.Sqrt(normSquared);
            for (int index = 0; index < embedding.Count; index++)
            {
                short value = (short)Math.Clamp(Math.Round(embedding[index] * scale, MidpointRounding.ToEven), -32767, 32767);
                BinaryPrimitives.WriteInt16LittleEndian(quantized.AsSpan(index * sizeof(short)), value);
            }
            hash.AppendData(quantized);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { CryptographicOperations.ZeroMemory(quantized); }
    }

    private static void AppendText(IncrementalHash hash, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
