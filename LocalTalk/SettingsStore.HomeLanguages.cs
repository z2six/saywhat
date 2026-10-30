using System.Text.Json;
using System.Text.Json.Nodes;

namespace IncomingTranslator;

// Dashboard-only model catalog validation, sharing the core store's atomic gate.
// The standalone subtitle host does not depend on dashboard provisioner/UI types.
internal static partial class SettingsStore
{
    // The caller owns the running-direction guard. This operation is deliberately
    // independent of a dashboard draft and never rewrites capture-host configs.
    internal static AppSettings SaveHomeTargetLanguagePreference(bool incoming, string language)
    {
        string target = AppSettings.CanonicalTranslationLanguage(language)
            ?? throw new ModelSelectionUserException("Choose a supported translation language.");
        lock (SettingsGate)
        {
            JsonObject document = ReadPreferenceDocument();
            AppSettings latest = JsonSerializer.Deserialize<AppSettings>(document, Options)
                ?? throw new InvalidDataException("The saved settings couldn't be read.");
            PreserveStoredIncomingSpeechDefaults(latest,
                document.ContainsKey(nameof(AppSettings.IncomingSpeechEngine)), document.ContainsKey(nameof(AppSettings.IncomingNormalSpeechEngine)));
            string targetKey = incoming ? nameof(AppSettings.IncomingTargetLanguage) : nameof(AppSettings.OwnVoiceTargetLanguage);
            bool separateIncoming = incoming && !latest.ShareTranslationModel;
            string model;
            if (latest.UseManagedTranslator)
            {
                string modelId = separateIncoming ? latest.IncomingManagedModelId : latest.OwnManagedModelId;
                model = LocalTalkProvisioner.ModelCatalog.FirstOrDefault(item => item.Id == modelId && item.Role == ProvisionModelRole.Translation)?.ApiAlias
                    ?? throw new ModelSelectionUserException("Choose a translation model in Setup first.");
            }
            else
            {
                model = separateIncoming ? latest.TranslatorModel : latest.OwnTranslatorModel;
                if (string.IsNullOrWhiteSpace(model)) throw new ModelSelectionUserException("Choose a translator model first.");
                if (TranslationModelCapabilities.RequiresManagedRuntime(model))
                    throw new ModelSelectionUserException("TranslateGemma requires SayWhat?'s built-in runtime and its compatible translation template. Select the built-in runtime in Settings.");
            }

            List<string> extras = (latest.OwnVoiceAdditionalTargetLanguages ?? new()).ToList();
            bool removedDuplicate = !incoming && extras.RemoveAll(value =>
                string.Equals(AppSettings.CanonicalTranslationLanguage(value), target, StringComparison.Ordinal)) > 0;
            IEnumerable<string> targets = !incoming && latest.OwnVoiceAdditionalLanguagesEnabled
                ? new[] { target }.Concat(extras) : new[] { target };
            if (targets.Any(value => AppSettings.CanonicalTranslationLanguage(value) is null))
                throw new ModelSelectionUserException("Choose supported translation languages before starting this model.");
            string source = incoming ? latest.IncomingSourceLanguage : latest.OwnVoiceSourceLanguage;
            if (TranslationModelCapabilities.Validate(model, source, targets) is { } invalid)
                throw new ModelSelectionUserException(invalid);

            // Keep all unrelated raw keys, profile snapshots and disabled extra
            // choices untouched. Only remove an extra equal to the new primary.
            bool changed = document[targetKey] is not JsonValue oldTarget ||
                !oldTarget.TryGetValue<string>(out string? oldLanguage) || oldLanguage != target;
            document[targetKey] = JsonValue.Create(target);
            if (incoming) latest.IncomingTargetLanguage = target;
            else latest.OwnVoiceTargetLanguage = target;
            if (removedDuplicate)
            {
                document[nameof(AppSettings.OwnVoiceAdditionalTargetLanguages)] = JsonSerializer.SerializeToNode(extras, Options);
                latest.OwnVoiceAdditionalTargetLanguages = extras;
            }
            if (changed || removedDuplicate) WritePreferenceDocument(document);
            // Return the effective targeted update, without normalizing/migrating
            // unrelated keys in the caller's copy either. Merge only changed keys.
            return latest;
        }
    }
}
