using System.IO;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IncomingTranslator;

internal sealed class AppSettings
{
    internal const int DefaultSentencePauseMilliseconds = 1200;
    // Interface language is independent of all spoken/translation language choices.
    public string UiLanguage { get; set; } = UiLanguagePreference.ReadInstallDefault(AppContext.BaseDirectory);
    public string UiThemeId { get; set; } = "coral-night";
    public string CustomThemeBackground { get; set; } = "#191817";
    public string CustomThemeTextPalette { get; set; } = "auto";
    public string CustomThemeImagePath { get; set; } = "";
    public double CustomThemeImageOpacity { get; set; } = .16;
    public bool UseAiSpeakerRecognition { get; set; } = true;
    public string SpeakerModelPath { get; set; } = AppPaths.DefaultSpeakerModelPath;
    public double SpeakerMatchThreshold { get; set; } = 0.50;
    public string TranslatorBaseUrl { get; set; } = "http://127.0.0.1:1234/v1";
    public string TranslatorModel { get; set; } = "tencent/hy-mt2-1.8b";
    public string OwnTranslatorModel { get; set; } = "tencent/hy-mt2-1.8b";
    public string TranslationQuality { get; set; } = "Fast";
    public string ActiveTranslationProfileId { get; set; } = TranslationProfiles.NormalId;
    public List<TranslationProfile> CustomTranslationProfiles { get; set; } = new();
    public string IncomingSpeechEngine { get; set; } = "voxtral";
    public string IncomingNormalSpeechEngine { get; set; } = "voxtral";
    public string IncomingNormalSourceLanguage { get; set; } = "Automatic";
    public int SubtitlePreviewStableMilliseconds { get; set; } = 500;
    public bool IncomingSendEarlyPreviews { get; set; } = false;
    public string VibeVoiceModelId { get; set; } = "microsoft/VibeVoice-ASR-Streaming-1.5B";
    public string VibeVoiceEndpoint { get; set; } = "http://127.0.0.1:8082";
    public string IncomingTargetLanguage { get; set; } = "English";
    public string OwnVoiceTargetLanguage { get; set; } = "Chinese";
    public string OwnVoiceSourceLanguage { get; set; } = "English";
    public string OwnRecognitionEngine { get; set; } = "nemotron";
    public string OwnMicrophoneDeviceId { get; set; } = "default";
    public bool OwnVoicePauseWhenVrchatMuted { get; set; } = true;
    public string IncomingSourceLanguage { get; set; } = "Automatic";
    public bool IncomingChineseOnly { get; set; } = false;
    public bool OwnVoiceAdditionalLanguagesEnabled { get; set; } = false;
    public List<string> OwnVoiceAdditionalTargetLanguages { get; set; } = new();
    public string OverlayMode { get; set; } = "Sentence bubbles";
    public int HistoryLinesPerSpeaker { get; set; } = 12;
    public List<string> SourceLanguages { get; set; } = new() { "Chinese" };
    public bool DetectAnySourceLanguage { get; set; } = true;
    public int IncomingSettleMilliseconds { get; set; } = DefaultSentencePauseMilliseconds;
    public int RecognitionDelayMilliseconds { get; set; } = 240;
    public int OwnVoiceEndPauseMilliseconds { get; set; } = DefaultSentencePauseMilliseconds;
    public bool OwnVoiceSendEarlyPreviews { get; set; } = false;
    public int VisibleSpeakerRows { get; set; } = 8;
    public string PlaybackDeviceId { get; set; } = "auto-vrchat";
    public bool NormalizeQuietPlayback { get; set; } = true;
    public bool CaptureVrChatOnly { get; set; } = true;
    // An empty value migrates the former VRChat-only/desktop preference on read.
    public string IncomingAudioMode { get; set; } = "";
    public string IncomingApplicationPath { get; set; } = "";
    public string IncomingApplicationName { get; set; } = "";
    public bool IncomingSinglePerson { get; set; } = false;
    public string IncomingPersonId { get; set; } = "";
    public bool FinishCapturedSentenceOnMute { get; set; } = true;
    public int MaxSpeakerProfiles { get; set; } = 2;
    public bool ShareTranslationModel { get; set; } = true;
    public bool UseManagedTranslator { get; set; } = AppPaths.IsPackagedApplication;
    public string OwnManagedModelId { get; set; } = "hy-mt2-1.8b-q4_k_m";
    public string IncomingManagedModelId { get; set; } = "hy-mt2-1.8b-q4_k_m";
    public string ManagedRuntimeId { get; set; } = "llama-vulkan";
    public string OwnTranslatorBaseUrl { get; set; } = "http://localhost:1234/v1";
    public string Glossary { get; set; } = "";
    public bool PauseOnMinimize { get; set; } = false;
    public bool FirstRunSetupCompleted { get; set; } = false;
    public bool FirstRunIntroductionCompleted { get; set; } = false;
    public bool SubtitleGuidePromptSeen { get; set; } = false;
    public string CloseButtonBehavior { get; set; } = "Ask";
    public bool SubtitlesAlwaysOnTop { get; set; } = true;
    public int SubtitlesVisibleMessages { get; set; } = 3;
    public double SubtitlesFontSize { get; set; } = 16;
    public double SubtitlesBubbleWidth { get; set; } = 420;
    public double SubtitlesBackgroundOpacity { get; set; } = .65;
    public bool SubtitlesShowOriginalText { get; set; } = true;
    public bool CheckForUpdatesOnLaunch { get; set; } = true;

    public AppSettings Copy() => new()
    {
        UiLanguage = UiLanguage,
        UiThemeId = UiThemeId,
        CustomThemeBackground = CustomThemeBackground,
        CustomThemeTextPalette = CustomThemeTextPalette,
        CustomThemeImagePath = CustomThemeImagePath,
        CustomThemeImageOpacity = CustomThemeImageOpacity,
        UseAiSpeakerRecognition = UseAiSpeakerRecognition,
        SpeakerModelPath = SpeakerModelPath,
        SpeakerMatchThreshold = SpeakerMatchThreshold,
        TranslatorBaseUrl = TranslatorBaseUrl,
        TranslatorModel = TranslatorModel,
        OwnTranslatorModel = OwnTranslatorModel,
        TranslationQuality = TranslationQuality,
        ActiveTranslationProfileId = ActiveTranslationProfileId,
        CustomTranslationProfiles = (CustomTranslationProfiles ?? new()).Where(profile => profile is not null).Select(profile => profile.Copy()).ToList(),
        IncomingSpeechEngine = IncomingSpeechEngine,
        IncomingNormalSpeechEngine = IncomingNormalSpeechEngine,
        IncomingNormalSourceLanguage = IncomingNormalSourceLanguage,
        SubtitlePreviewStableMilliseconds = SubtitlePreviewStableMilliseconds,
        IncomingSendEarlyPreviews = IncomingSendEarlyPreviews,
        VibeVoiceModelId = VibeVoiceModelId,
        VibeVoiceEndpoint = VibeVoiceEndpoint,
        IncomingTargetLanguage = IncomingTargetLanguage,
        OwnVoiceTargetLanguage = OwnVoiceTargetLanguage,
        OwnVoiceSourceLanguage = OwnVoiceSourceLanguage,
        OwnRecognitionEngine = OwnRecognitionEngine,
        OwnMicrophoneDeviceId = OwnMicrophoneDeviceId,
        OwnVoicePauseWhenVrchatMuted = OwnVoicePauseWhenVrchatMuted,
        IncomingSourceLanguage = IncomingSourceLanguage,
        IncomingChineseOnly = IncomingChineseOnly,
        OwnVoiceAdditionalLanguagesEnabled = OwnVoiceAdditionalLanguagesEnabled,
        OwnVoiceAdditionalTargetLanguages = (OwnVoiceAdditionalTargetLanguages ?? new()).ToList(),
        OverlayMode = OverlayMode,
        HistoryLinesPerSpeaker = HistoryLinesPerSpeaker,
        SourceLanguages = (SourceLanguages ?? new()).ToList(),
        DetectAnySourceLanguage = DetectAnySourceLanguage,
        IncomingSettleMilliseconds = IncomingSettleMilliseconds,
        RecognitionDelayMilliseconds = RecognitionDelayMilliseconds,
        OwnVoiceEndPauseMilliseconds = OwnVoiceEndPauseMilliseconds,
        OwnVoiceSendEarlyPreviews = OwnVoiceSendEarlyPreviews,
        VisibleSpeakerRows = VisibleSpeakerRows,
        PlaybackDeviceId = PlaybackDeviceId,
        NormalizeQuietPlayback = NormalizeQuietPlayback,
        CaptureVrChatOnly = CaptureVrChatOnly,
        IncomingAudioMode = IncomingAudioMode,
        IncomingApplicationPath = IncomingApplicationPath,
        IncomingApplicationName = IncomingApplicationName,
        IncomingSinglePerson = IncomingSinglePerson,
        IncomingPersonId = IncomingPersonId,
        FinishCapturedSentenceOnMute = FinishCapturedSentenceOnMute,
        MaxSpeakerProfiles = MaxSpeakerProfiles,
        ShareTranslationModel = ShareTranslationModel,
        UseManagedTranslator = UseManagedTranslator,
        OwnManagedModelId = OwnManagedModelId,
        IncomingManagedModelId = IncomingManagedModelId,
        ManagedRuntimeId = ManagedRuntimeId,
        OwnTranslatorBaseUrl = OwnTranslatorBaseUrl,
        Glossary = Glossary,
        PauseOnMinimize = PauseOnMinimize,
        FirstRunSetupCompleted = FirstRunSetupCompleted,
        FirstRunIntroductionCompleted = FirstRunIntroductionCompleted,
        SubtitleGuidePromptSeen = SubtitleGuidePromptSeen,
        CloseButtonBehavior = CloseButtonBehavior,
        SubtitlesAlwaysOnTop = SubtitlesAlwaysOnTop,
        SubtitlesVisibleMessages = SubtitlesVisibleMessages,
        SubtitlesFontSize = SubtitlesFontSize,
        SubtitlesBubbleWidth = SubtitlesBubbleWidth,
        SubtitlesBackgroundOpacity = SubtitlesBackgroundOpacity,
        SubtitlesShowOriginalText = SubtitlesShowOriginalText,
        CheckForUpdatesOnLaunch = CheckForUpdatesOnLaunch
    };

    internal static readonly string[] TranslationLanguages = ["Chinese", "English", "Japanese", "Korean", "Spanish", "French", "German", "Portuguese", "Russian", "Thai"];

    internal static string? CanonicalTranslationLanguage(string? value)
    {
        string? name = value?.Trim().ToLowerInvariant() switch
        {
            "zh" or "zh-cn" or "simplified chinese" => "Chinese", "en" => "English", "ja" => "Japanese", "ko" => "Korean",
            "es" => "Spanish", "fr" => "French", "de" => "German", "pt" => "Portuguese", "ru" => "Russian",
            "th" or "th-th" => "Thai", _ => value?.Trim()
        };
        return TranslationLanguages.FirstOrDefault(language => string.Equals(language, name, StringComparison.OrdinalIgnoreCase));
    }

    internal List<string> NormalizedOwnVoiceAdditionalLanguages() => (OwnVoiceAdditionalTargetLanguages ?? new())
        .Select(CanonicalTranslationLanguage)
        .Where(value => value is not null && !string.Equals(value, CanonicalTranslationLanguage(OwnVoiceTargetLanguage) ?? OwnVoiceTargetLanguage?.Trim(), StringComparison.OrdinalIgnoreCase))
        .Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();

    internal IReadOnlyList<string> EffectiveOwnVoiceTargetLanguages() => OwnVoiceAdditionalLanguagesEnabled
        ? new[] { CanonicalTranslationLanguage(OwnVoiceTargetLanguage) ?? OwnVoiceTargetLanguage }.Concat(NormalizedOwnVoiceAdditionalLanguages()).ToArray()
        : new[] { OwnVoiceTargetLanguage };
}

internal static class AppPaths
{
    public static bool IsPackagedApplication => AppBrand.IsPackagedAssembly(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name);
    public static string Root => IsPackagedApplication
        ? AppBrand.DataRoot
        : @"C:\VRChat";
    public static string SettingsPath => IsPackagedApplication ? Path.Combine(Root,"vrchat-translation-settings.json") : Path.Combine(Root,"FoxTrans-v2.0.0-win-x64","vrchat-translation-settings.json");
    public static string ModelsDirectory => Path.Combine(Root, "models", "speaker-id");
    public static string DefaultSpeakerModelPath => Path.Combine(ModelsDirectory, "3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx");
    public const string SpeakerModelUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx";
    public static readonly SpeakerModelChoice[] SpeakerModels =
    {
        new("CAM++ Mandarin (fast)", "3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx"),
        new("ERes2Net Mandarin (larger)", "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx")
    };
    public static string FoxTransConfigPath => Path.Combine(IsPackagedApplication ? Root : Path.Combine(Root,"FoxTrans-v2.0.0-win-x64"),"config.sentence-mode.jsonc");
    public static string FastConfigPath => Path.Combine(IsPackagedApplication ? Root : Path.Combine(Root,"FoxTrans-v2.0.0-win-x64"),"config.fast-chinese.jsonc");
    public static string ToolRoot {
        get {
            string packaged = Path.Combine(AppContext.BaseDirectory,"tools");
            if(Directory.Exists(packaged)) return packaged;
            var folder=new DirectoryInfo(AppContext.BaseDirectory);
            while(folder is not null) { if(File.Exists(Path.Combine(folder.FullName,"FoxTrans-VRChatMuteSync.ps1"))) return folder.FullName; folder=folder.Parent; }
            return packaged;
        }
    }
    public static void InitializePackagedData() {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ModelsDirectory);
        if(File.Exists(SettingsPath)) return;
        string legacy=Path.Combine(ToolRoot,"vrchat-translation-settings.json");
        if(File.Exists(legacy) && !string.Equals(legacy,SettingsPath,StringComparison.OrdinalIgnoreCase)) File.Copy(legacy,SettingsPath,false);
        string oldProfiles=Path.Combine(Directory.GetParent(ToolRoot)?.FullName ?? ToolRoot,"incoming-ai-speaker-profiles.json");
        string newProfiles=Path.Combine(Root,"incoming-ai-speaker-profiles.json");
        if(File.Exists(oldProfiles) && !File.Exists(newProfiles)) File.Copy(oldProfiles,newProfiles,false);
        SettingsStore.Save(SettingsStore.Load());
    }
}

internal sealed record SpeakerModelChoice(string Name, string FileName)
{
    public string Path => System.IO.Path.Combine(AppPaths.ModelsDirectory, FileName);
    public string Url => "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/" + FileName;
    public override string ToString() => Name;
}

internal static partial class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly object SettingsGate = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsPath))
                return ReadSettings(File.ReadAllText(AppPaths.SettingsPath));
        }
        catch { }
        return new AppSettings();
    }

    internal static AppSettings ReadSettings(string contents)
    {
        using var document = JsonDocument.Parse(contents);
        var settings = JsonSerializer.Deserialize<AppSettings>(contents) ?? new AppSettings();
        // Missing engine fields identify legacy installs. Never silently replace
        // their working Voxtral setup or assume an input language on upgrade.
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            !document.RootElement.TryGetProperty(nameof(AppSettings.OwnRecognitionEngine), out _))
        {
            settings.OwnRecognitionEngine = "voxtral";
            if (!document.RootElement.TryGetProperty(nameof(AppSettings.OwnVoiceSourceLanguage), out _))
                settings.OwnVoiceSourceLanguage = "Automatic";
        }
        // Reading this new delivery preference is inert. Only the old default
        // pause migrates; a customized pause or saved new choice is preserved.
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            !document.RootElement.TryGetProperty(nameof(AppSettings.OwnVoiceSendEarlyPreviews), out _) &&
            settings.OwnVoiceEndPauseMilliseconds == 2400)
            settings.OwnVoiceEndPauseMilliseconds = AppSettings.DefaultSentencePauseMilliseconds;
        // Retain the old one-message layout only until the user chooses a count.
        // Afterwards the explicit count owns compact display in every legacy layout.
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            !document.RootElement.TryGetProperty(nameof(AppSettings.SubtitlesVisibleMessages), out _) &&
            settings.OverlayMode == "Latest sentence")
            settings.SubtitlesVisibleMessages = 1;
        // A completed setup is reliable evidence of an existing installation.
        // Do not interrupt those users with the new first-run introduction.
        // Explicit false remains false so a deliberate reset can show it again.
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            !document.RootElement.TryGetProperty(nameof(AppSettings.FirstRunIntroductionCompleted), out _) &&
            settings.FirstRunSetupCompleted)
            settings.FirstRunIntroductionCompleted = true;
        Normalize(settings);
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            !document.RootElement.TryGetProperty(nameof(AppSettings.IncomingChineseOnly), out _))
        {
            bool explicitChinese = document.RootElement.TryGetProperty(nameof(AppSettings.IncomingSourceLanguage), out JsonElement source)
                && source.ValueKind == JsonValueKind.String && settings.IncomingSourceLanguage == "Chinese";
            bool legacyChineseList = settings.IncomingSourceLanguage == "Automatic"
                && document.RootElement.TryGetProperty(nameof(AppSettings.DetectAnySourceLanguage), out JsonElement detectAny)
                && detectAny.ValueKind == JsonValueKind.False
                && document.RootElement.TryGetProperty(nameof(AppSettings.SourceLanguages), out JsonElement languages)
                && languages.ValueKind == JsonValueKind.Array && languages.GetArrayLength() == 1
                && languages[0].ValueKind == JsonValueKind.String
                && AppSettings.CanonicalTranslationLanguage(languages[0].GetString()) == "Chinese";
            settings.IncomingChineseOnly = explicitChinese || legacyChineseList;
        }
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            !document.RootElement.TryGetProperty(nameof(AppSettings.ActiveTranslationProfileId), out _))
            TranslationProfiles.MigrateExistingSettings(settings);
        return settings;
    }

    public static void Save(AppSettings settings) => Save(settings, applyingProfile: false);

    // A deliberate profile selection must apply its editable preferences. Normal
    // saves retain the stale-window protections used by live overlay controls.
    internal static void SaveTranslationProfileSelection(AppSettings settings) => Save(settings, applyingProfile: true);

    internal static void SaveTranslationProfileMetadata(AppSettings settings)
    {
        TranslationProfiles.Normalize(settings);
        SavePreferences(new Dictionary<string, JsonNode>
        {
            [nameof(AppSettings.ActiveTranslationProfileId)] = JsonValue.Create(settings.ActiveTranslationProfileId)!,
            [nameof(AppSettings.CustomTranslationProfiles)] = JsonSerializer.SerializeToNode(settings.CustomTranslationProfiles, Options)!
        });
    }

    private static void Save(AppSettings settings, bool applyingProfile)
    {
        lock (SettingsGate)
        {
            settings.OwnVoiceAdditionalTargetLanguages = settings.NormalizedOwnVoiceAdditionalLanguages();
            Directory.CreateDirectory(AppPaths.Root);
            // Live-window preferences may have changed since the dashboard snapshot.
            // An explicit profile application may replace them; an ordinary save must not.
            if (File.Exists(AppPaths.SettingsPath))
            {
                try
                {
                    var latest = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsPath)) as JsonObject;
                    if (!applyingProfile && latest?[nameof(AppSettings.SubtitlesAlwaysOnTop)] is JsonValue pin && pin.TryGetValue<bool>(out bool pinned))
                        settings.SubtitlesAlwaysOnTop = pinned;
                    if (!applyingProfile && latest?[nameof(AppSettings.UseAiSpeakerRecognition)] is JsonValue recognition && recognition.TryGetValue<bool>(out bool enabled))
                        settings.UseAiSpeakerRecognition = enabled;
                    if (!applyingProfile && latest?[nameof(AppSettings.UiLanguage)] is JsonValue language && language.TryGetValue<string>(out string? code))
                        settings.UiLanguage = UiLanguagePreference.Normalize(code);
                    if (latest?[nameof(AppSettings.FirstRunIntroductionCompleted)] is JsonValue introduction && introduction.TryGetValue<bool>(out bool introduced))
                        settings.FirstRunIntroductionCompleted = introduced;
                    if (latest?[nameof(AppSettings.SubtitleGuidePromptSeen)] is JsonValue guide && guide.TryGetValue<bool>(out bool guideSeen))
                        settings.SubtitleGuidePromptSeen = guideSeen;
                    if (!applyingProfile && latest is not null) PreserveAppearance(settings, latest);
                    if (!applyingProfile && latest is not null) PreserveSubtitleAppearance(settings, latest);
                }
                catch (JsonException) { /* Preserve the existing explicit settings-repair path. */ }
            }
            Normalize(settings);
            WriteIfChanged(AppPaths.SettingsPath, JsonSerializer.Serialize(settings, Options));
            UpdateOwnVoiceConfig(settings, AppPaths.FastConfigPath);
            UpdateOwnVoiceConfig(settings, AppPaths.FoxTransConfigPath);
        }
    }

    internal static void SaveSubtitlePinPreference(bool value) => SaveBooleanPreference(nameof(AppSettings.SubtitlesAlwaysOnTop), value);
    internal static void SaveSpeakerRecognitionPreference(bool value) => SaveBooleanPreference(nameof(AppSettings.UseAiSpeakerRecognition), value);
    internal static void SaveUiLanguagePreference(string value) => SavePreference(nameof(AppSettings.UiLanguage), JsonValue.Create(UiLanguagePreference.Normalize(value))!);
    internal static void SaveFirstRunIntroductionPreference(bool value) => SaveBooleanPreference(nameof(AppSettings.FirstRunIntroductionCompleted), value);
    internal static void SaveSubtitleGuidePromptPreference(bool value) => SaveBooleanPreference(nameof(AppSettings.SubtitleGuidePromptSeen), value);

    internal static void SaveSubtitleAppearancePreference(AppSettings settings)
    {
        var appearance = settings.Copy();
        NormalizeSubtitleAppearance(appearance);
        SavePreferences(new Dictionary<string, JsonNode>
        {
            [nameof(AppSettings.SubtitlesVisibleMessages)] = JsonValue.Create(appearance.SubtitlesVisibleMessages)!,
            [nameof(AppSettings.SubtitlesFontSize)] = JsonValue.Create(appearance.SubtitlesFontSize)!,
            [nameof(AppSettings.SubtitlesBubbleWidth)] = JsonValue.Create(appearance.SubtitlesBubbleWidth)!,
            [nameof(AppSettings.SubtitlesBackgroundOpacity)] = JsonValue.Create(appearance.SubtitlesBackgroundOpacity)!,
            [nameof(AppSettings.SubtitlesShowOriginalText)] = JsonValue.Create(appearance.SubtitlesShowOriginalText)!
        });
    }

    private static void PreserveSubtitleAppearance(AppSettings settings, JsonObject document)
    {
        if (document[nameof(AppSettings.SubtitlesVisibleMessages)] is JsonValue messages && messages.TryGetValue<int>(out int visibleMessages))
            settings.SubtitlesVisibleMessages = visibleMessages;
        if (document[nameof(AppSettings.SubtitlesFontSize)] is JsonValue font && font.TryGetValue<double>(out double fontSize))
            settings.SubtitlesFontSize = fontSize;
        if (document[nameof(AppSettings.SubtitlesBubbleWidth)] is JsonValue width && width.TryGetValue<double>(out double bubbleWidth))
            settings.SubtitlesBubbleWidth = bubbleWidth;
        if (document[nameof(AppSettings.SubtitlesBackgroundOpacity)] is JsonValue opacity && opacity.TryGetValue<double>(out double backgroundOpacity))
            settings.SubtitlesBackgroundOpacity = backgroundOpacity;
        if (document[nameof(AppSettings.SubtitlesShowOriginalText)] is JsonValue original && original.TryGetValue<bool>(out bool showOriginal))
            settings.SubtitlesShowOriginalText = showOriginal;
    }

    internal static void NormalizeSubtitleAppearance(AppSettings settings)
    {
        settings.SubtitlesVisibleMessages = Math.Clamp(settings.SubtitlesVisibleMessages, 1, 10);
        settings.SubtitlesFontSize = double.IsFinite(settings.SubtitlesFontSize) ? Math.Clamp(settings.SubtitlesFontSize, 12, 30) : 16;
        settings.SubtitlesBubbleWidth = double.IsFinite(settings.SubtitlesBubbleWidth) ? Math.Clamp(settings.SubtitlesBubbleWidth, 240, 900) : 420;
        settings.SubtitlesBackgroundOpacity = double.IsFinite(settings.SubtitlesBackgroundOpacity) ? Math.Clamp(settings.SubtitlesBackgroundOpacity, 0, 1) : .65;
    }

    internal static void CopySubtitleAppearance(AppSettings source, AppSettings target)
    {
        target.SubtitlesVisibleMessages = source.SubtitlesVisibleMessages;
        target.SubtitlesFontSize = source.SubtitlesFontSize;
        target.SubtitlesBubbleWidth = source.SubtitlesBubbleWidth;
        target.SubtitlesBackgroundOpacity = source.SubtitlesBackgroundOpacity;
        target.SubtitlesShowOriginalText = source.SubtitlesShowOriginalText;
    }

    internal static void SaveAppearancePreference(AppSettings settings)
    {
        var appearance=settings.Copy();
        AppearanceTheme.Normalize(appearance);
        SavePreferences(new Dictionary<string,JsonNode> {
            [nameof(AppSettings.UiThemeId)]=JsonValue.Create(appearance.UiThemeId)!,
            [nameof(AppSettings.CustomThemeBackground)]=JsonValue.Create(appearance.CustomThemeBackground)!,
            [nameof(AppSettings.CustomThemeTextPalette)]=JsonValue.Create(appearance.CustomThemeTextPalette)!,
            [nameof(AppSettings.CustomThemeImagePath)]=JsonValue.Create(appearance.CustomThemeImagePath)!,
            [nameof(AppSettings.CustomThemeImageOpacity)]=JsonValue.Create(appearance.CustomThemeImageOpacity)!
        });
    }
    private static void PreserveAppearance(AppSettings settings,JsonObject document)
    {
        if(document[nameof(AppSettings.UiThemeId)] is JsonValue theme && theme.TryGetValue<string>(out var id))settings.UiThemeId=id!;
        if(document[nameof(AppSettings.CustomThemeBackground)] is JsonValue background && background.TryGetValue<string>(out var color))settings.CustomThemeBackground=color!;
        if(document[nameof(AppSettings.CustomThemeTextPalette)] is JsonValue palette && palette.TryGetValue<string>(out var selected))settings.CustomThemeTextPalette=selected!;
        if(document[nameof(AppSettings.CustomThemeImagePath)] is JsonValue image && image.TryGetValue<string>(out var path))settings.CustomThemeImagePath=path!;
        if(document[nameof(AppSettings.CustomThemeImageOpacity)] is JsonValue opacity && opacity.TryGetValue<double>(out var value))settings.CustomThemeImageOpacity=value;
        AppearanceTheme.Normalize(settings);
    }

    private static void SaveBooleanPreference(string preference, bool value) => SavePreference(preference, JsonValue.Create(value)!);

    private static void SavePreference(string preference, JsonNode value) => SavePreferences(new Dictionary<string,JsonNode> {[preference]=value});
    private static void SavePreferences(IReadOnlyDictionary<string,JsonNode> preferences)
    {
        // A window-only change must not rewrite live microphone configuration, normalize other
        // preferences, or replace a newer saved language/model choice with an overlay snapshot.
        lock (SettingsGate)
        {
            JsonObject document = ReadPreferenceDocument();
            foreach(var preference in preferences) document[preference.Key] = preference.Value;
            WritePreferenceDocument(document);
        }
    }

    // Call only while SettingsGate is held: read/validate/patch is one operation.
    private static JsonObject ReadPreferenceDocument() => File.Exists(AppPaths.SettingsPath)
        ? JsonNode.Parse(File.ReadAllText(AppPaths.SettingsPath)) as JsonObject
            ?? throw new InvalidDataException("The saved settings aren't a JSON object.")
        : JsonSerializer.SerializeToNode(new AppSettings(), Options)!.AsObject();

    private static void WritePreferenceDocument(JsonObject document)
    {
        Directory.CreateDirectory(AppPaths.Root);
        string temporary = AppPaths.SettingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, document.ToJsonString(Options));
            File.Move(temporary, AppPaths.SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WriteIfChanged(string path, string contents)
    {
        // Starting the second feature must not rewrite the microphone configuration
        // while the capture host is reading it. Real settings changes are applied
        // after stopping capture; an identical snapshot needs no file mutation.
        if (File.Exists(path) && string.Equals(File.ReadAllText(path), contents, StringComparison.Ordinal)) return;
        File.WriteAllText(path, contents);
    }

    internal static AppSettings Normalize(AppSettings settings)
    {
        settings.UiLanguage = UiLanguagePreference.Normalize(settings.UiLanguage);
        AppearanceTheme.Normalize(settings);
        NormalizeSubtitleAppearance(settings);
        IncomingListening.Normalize(settings);
        settings.OwnRecognitionEngine = settings.OwnRecognitionEngine == "voxtral" ? "voxtral" : "nemotron";
        settings.OwnMicrophoneDeviceId = string.IsNullOrWhiteSpace(settings.OwnMicrophoneDeviceId) ? "default" : settings.OwnMicrophoneDeviceId.Trim();
        settings.SourceLanguages ??= new List<string> { "Chinese" };
        settings.OwnVoiceAdditionalTargetLanguages = settings.NormalizedOwnVoiceAdditionalLanguages();
        settings.IncomingSettleMilliseconds = Math.Clamp(settings.IncomingSettleMilliseconds, 600, 2400);
        settings.SubtitlePreviewStableMilliseconds = Math.Clamp(settings.SubtitlePreviewStableMilliseconds, 150, 1200);
        settings.RecognitionDelayMilliseconds = Math.Clamp(settings.RecognitionDelayMilliseconds, 80, 1000);
        settings.IncomingSpeechEngine = TranslationProfiles.CanonicalSpeechEngine(settings.IncomingSpeechEngine);
        settings.IncomingNormalSpeechEngine = TranslationProfiles.CanonicalSpeechEngine(settings.IncomingNormalSpeechEngine);
        if (settings.IncomingNormalSpeechEngine == "vibevoice") settings.IncomingNormalSpeechEngine = "voxtral";
        settings.IncomingSourceLanguage = CanonicalSubtitleSourceLanguage(settings.IncomingSourceLanguage);
        settings.IncomingNormalSourceLanguage = CanonicalSubtitleSourceLanguage(settings.IncomingNormalSourceLanguage);
        if (settings.IncomingSpeechEngine != "vibevoice")
        {
            settings.IncomingNormalSpeechEngine = settings.IncomingSpeechEngine;
            settings.IncomingNormalSourceLanguage = settings.IncomingSourceLanguage;
        }
        settings.VibeVoiceModelId = TranslationProfiles.CanonicalVibeVoiceModel(settings.VibeVoiceModelId);
        settings.VibeVoiceEndpoint = TranslationProfiles.NormalizeVibeVoiceEndpoint(settings.VibeVoiceEndpoint);
        settings.OwnVoiceEndPauseMilliseconds = Math.Clamp(settings.OwnVoiceEndPauseMilliseconds, 900, 3500);
        settings.VisibleSpeakerRows = Math.Clamp(settings.VisibleSpeakerRows, 2, 12);
        settings.HistoryLinesPerSpeaker = Math.Clamp(settings.HistoryLinesPerSpeaker, 3, 40);
        settings.MaxSpeakerProfiles = Math.Clamp(settings.MaxSpeakerProfiles, 2, 8);
        if (settings.OverlayMode == "History") settings.OverlayMode = "Per-speaker history";
        if (settings.CloseButtonBehavior is not ("Ask" or "Quit" or "Tray")) settings.CloseButtonBehavior = "Ask";
        TranslationProfiles.Normalize(settings);
        return settings;
    }

    private static string CanonicalSubtitleSourceLanguage(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "Automatic", StringComparison.OrdinalIgnoreCase)
            ? "Automatic" : AppSettings.CanonicalTranslationLanguage(value) ?? value.Trim();

    private static void UpdateOwnVoiceConfig(AppSettings settings, string path)
    {
        if (!File.Exists(path) && AppPaths.IsPackagedApplication)
        {
            var template = new JsonObject {
                ["version"] = 1,
                ["audio"] = new JsonObject { ["device"] = "default" },
                ["pipeline"] = new JsonObject {
                    ["speech"] = new JsonObject { ["type"]="voxtral-fox", ["baseUrl"]="http://127.0.0.1:8080", ["delayMs"]=240 },
                    ["translation"] = new JsonObject { ["type"]="openai-chat", ["baseUrl"]=settings.OwnTranslatorBaseUrl, ["model"]=settings.OwnTranslatorModel },
                    ["realtime"] = new JsonObject { ["preset"]="responsive", ["newUtteranceAfterMs"]=settings.OwnVoiceEndPauseMilliseconds }
                },
                ["outputs"] = new JsonArray(new JsonObject { ["type"]="vrchat-osc", ["address"]="127.0.0.1:9010", ["typingIndicator"]=true })
            };
            WriteIfChanged(path,template.ToJsonString(Options));
        }
        if (File.Exists(path))
        {
            var documentOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            JsonNode? root = JsonNode.Parse(File.ReadAllText(path), null, documentOptions);
            if (root?["audio"] is JsonObject audio) audio["device"] = settings.OwnMicrophoneDeviceId;
            if (root?["pipeline"]?["speech"] is JsonObject speech)
                speech["delayMs"] = settings.RecognitionDelayMilliseconds;
            if (root?["pipeline"]?["realtime"] is JsonObject realtime)
            {
                realtime["newUtteranceAfterMs"] = settings.OwnVoiceEndPauseMilliseconds;
                if (root["pipeline"]?["translation"] is JsonObject translation)
                {
                    translation["prompt"] = $"Translate the source speech from any language into concise, natural {TargetDescription(settings.OwnVoiceTargetLanguage)} suitable for casual VRChat conversation. Preserve meaning and tone. Output only the translation with no label, explanation, alternatives, or extra text. /no_think";
                    translation["model"] = settings.OwnTranslatorModel;
                    translation["baseUrl"] = settings.OwnTranslatorBaseUrl;
                    if(!string.IsNullOrWhiteSpace(settings.Glossary)) translation["prompt"] = translation["prompt"]!.GetValue<string>() + " Use this glossary only for matching source terms; do not invent missing words: " + settings.Glossary[..Math.Min(2000,settings.Glossary.Length)];
                    if(settings.OwnTranslatorModel.Contains("hy-mt",StringComparison.OrdinalIgnoreCase))
                    {
                        string references = string.IsNullOrWhiteSpace(settings.Glossary) ? "" : "Reference the following translations (only when the source terms occur):\n"+settings.Glossary[..Math.Min(2000,settings.Glossary.Length)]+"\n\n";
                        translation["prompt"] = references+$"Translate the following text into {settings.OwnVoiceTargetLanguage}. Note that you should only output the translated result without any additional explanation:";
                    }
                }
                WriteIfChanged(path, root.ToJsonString(Options));
            }
        }
    }

    public static string TargetDescription(string language) => language == "Chinese" ? "Simplified Chinese" : language;
}

internal static class UiLanguagePreference
{
    internal static string Normalize(string? code) => code switch
    {
        "zh-CN" => "zh-CN", "ja" => "ja", "ko" => "ko", _ => "en"
    };

    internal static string ReadInstallDefault(string installationDirectory)
    {
        try
        {
            string path = Path.Combine(installationDirectory, "ui-language.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return "en";
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("uiLanguage", out var language) && language.ValueKind == JsonValueKind.String
                ? Normalize(language.GetString()) : "en";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return "en"; }
    }
}

internal sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly CheckBox _aiSpeaker;
    private readonly TextBox _speakerPath;
    private readonly ComboBox _speakerChoice;
    private readonly Slider _speakerThreshold;
    private readonly ComboBox _translatorModel;
    private readonly ComboBox _ownTranslatorModel;
    private readonly ComboBox _modelCatalog;
    private readonly Button _downloadTranslator;
    private readonly ComboBox _expectedSpeakers;
    private readonly Button _save;
    private readonly CancellationTokenSource _windowCancellation = new();
    private readonly TextBox _translatorUrl;
    private readonly ComboBox _quality;
    private readonly ComboBox _incomingTarget;
    private readonly ComboBox _ownTarget;
    private readonly ComboBox _overlayMode;
    private readonly ComboBox _recognitionDelay;
    private readonly ComboBox _playbackDevice;
    private readonly CheckBox _vrchatOnly;
    private readonly CheckBox _finishCaptured;
    private readonly CheckBox _earlyPreviews;
    private readonly CheckBox _normalizePlayback;
    private readonly Dictionary<string, CheckBox> _languageChecks = new();
    private readonly CheckBox _anyLanguage;
    private readonly Slider _incomingPause;
    private readonly Slider _ownPause;
    private readonly TextBlock _status;
    private readonly Button _download;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public event Action<AppSettings>? Saved;
    public event Action? ResetSpeakersRequested;

    public SettingsWindow(AppSettings current)
    {
        _settings = current.Copy();
        AppearanceTheme.Apply(_settings);
        ThemeBackground.Apply(_settings);
        Resources.MergedDictionaries.Add(AppearanceTheme.CreateResources());
        AppearanceTheme.ApplyWindowChrome(this);
        Title = "VRChat Translator Options";
        Width = 620;
        Height = 740;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = AppearanceTheme.Background;
        Foreground = AppearanceTheme.Text;
        Topmost = true;

        var root = new DockPanel { Background = AppearanceTheme.Surface, Margin = new Thickness(22) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var save = _save = Button("Save and apply", 120);
        var cancel = Button("Cancel", 80);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        save.Click += async (_, _) => await SaveAndCloseAsync();
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var form = new StackPanel();
        scroll.Content = form;
        root.Children.Add(scroll);

        form.Children.Add(Heading("Incoming translation"));
        _vrchatOnly = new CheckBox { Content = "Capture VRChat audio only — exclude browsers and other apps", Foreground = AppearanceTheme.Text, IsChecked = _settings.CaptureVrChatOnly, Margin = new Thickness(0, 4, 0, 8) };
        // The full app owns its searchable application picker. This legacy
        // standalone device dialog must not silently replace a chosen app.
        _vrchatOnly.IsEnabled = IncomingListening.SourceMode(_settings) != "application";
        form.Children.Add(_vrchatOnly);
        form.Children.Add(Hint("Music and sound effects inside VRChat are still part of the game's audio."));
        form.Children.Add(Label("Playback device (only used when VRChat-only capture is disabled)"));
        List<PlaybackDeviceChoice> playbackChoices;
        try { playbackChoices = AudioRouting.Choices(); }
        catch { playbackChoices = new() { new("auto-vrchat", "Automatic — follow VRChat's playback device") }; }
        _playbackDevice = new ComboBox { Foreground = AppearanceTheme.Text, Height = 32, Margin = new Thickness(0, 0, 0, 8), ItemsSource = playbackChoices,
            SelectedItem = playbackChoices.FirstOrDefault(choice => choice.Id == _settings.PlaybackDeviceId) ?? playbackChoices[0] };
        form.Children.Add(_playbackDevice);
        _playbackDevice.IsEnabled = IncomingListening.SourceMode(_settings) == "desktop";
        _vrchatOnly.Checked += (_, _) => _playbackDevice.IsEnabled = false;
        _vrchatOnly.Unchecked += (_, _) => _playbackDevice.IsEnabled = true;
        _normalizePlayback = new CheckBox { Content = "Boost quiet voices for recognition (does not change headphone volume)", Foreground = AppearanceTheme.Text, IsChecked = _settings.NormalizeQuietPlayback, Margin = new Thickness(0, 3, 0, 8) };
        form.Children.Add(_normalizePlayback);
        form.Children.Add(Label("Translator API (Bionic / LM Studio compatible)"));
        _translatorUrl = Text(_settings.TranslatorBaseUrl);
        form.Children.Add(_translatorUrl);
        form.Children.Add(Label("Translate other people's voices to"));
        _incomingTarget = LanguageSelector(_settings.IncomingTargetLanguage);
        form.Children.Add(_incomingTarget);
        form.Children.Add(Label("Translate your voice to (VRChat chatbox)"));
        _ownTarget = LanguageSelector(_settings.OwnVoiceTargetLanguage);
        form.Children.Add(_ownTarget);
        form.Children.Add(Label("Model for OTHER people's voices — installed model or model ID"));
        var modelRow = new Grid();
        modelRow.ColumnDefinitions.Add(new ColumnDefinition());
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _translatorModel = new ComboBox { IsEditable = true, Text = _settings.TranslatorModel, Foreground = AppearanceTheme.Text, Height = 32, Margin = new Thickness(0, 0, 8, 8) };
        var refresh = Button("Refresh models", 150);
        refresh.Click += async (_, _) => await RefreshModelsAsync();
        modelRow.Children.Add(_translatorModel);
        Grid.SetColumn(refresh, 1);
        modelRow.Children.Add(refresh);
        form.Children.Add(modelRow);
        form.Children.Add(Label("Model for YOUR voice — independent from incoming translation"));
        _ownTranslatorModel = new ComboBox { IsEditable = true, Text = _settings.OwnTranslatorModel, Foreground = AppearanceTheme.Text, Height = 32, Margin = new Thickness(0, 0, 0, 8) };
        form.Children.Add(_ownTranslatorModel);
        form.Children.Add(Hint("Using the same model for both saves graphics memory. Different models are supported, but loading two large models can slow VRChat."));
        form.Children.Add(Label("Download another local translation model"));
        _modelCatalog = new ComboBox { Foreground = AppearanceTheme.Text, Height = 32, Margin = new Thickness(0, 0, 0, 8), ItemsSource = LocalModelManager.Catalog, SelectedIndex = 0 };
        form.Children.Add(_modelCatalog);
        var modelDescription = Hint("");
        _modelCatalog.SelectionChanged += (_, _) => modelDescription.Text = (_modelCatalog.SelectedItem as ModelDownloadChoice)?.Description ?? "";
        modelDescription.Text = (_modelCatalog.SelectedItem as ModelDownloadChoice)?.Description ?? "";
        form.Children.Add(modelDescription);
        _downloadTranslator = Button("Download selected model", 210);
        _downloadTranslator.Click += async (_, _) => await DownloadTranslatorAsync();
        form.Children.Add(_downloadTranslator);
        form.Children.Add(Hint("Downloading does not replace the running models. After downloading, select it above and save. All translation stays on this PC."));
        form.Children.Add(Label("Translation preset"));
        _quality = new ComboBox { Foreground = AppearanceTheme.Text, Height = 32, Margin = new Thickness(0, 0, 0, 8), ItemsSource = new[] { "Fast", "Balanced", "Quality" }, SelectedItem = _settings.TranslationQuality };
        form.Children.Add(_quality);
        form.Children.Add(Hint("A larger translator may improve quality, but recognition errors still matter. Balanced/Quality allow more context and time, with more latency."));
        form.Children.Add(Label("Speech recognition lookahead"));
        _recognitionDelay = new ComboBox { Foreground = AppearanceTheme.Text, Height = 32, Margin = new Thickness(0, 0, 0, 8), ItemsSource = new[] { 160, 240, 480 }, SelectedItem = _settings.RecognitionDelayMilliseconds };
        form.Children.Add(_recognitionDelay);
        form.Children.Add(Hint("160 ms is fastest. 240/480 ms gives the recognizer more audio context. Changes apply to the next speech session."));
        form.Children.Add(Label("Possible source languages"));
        _anyLanguage = new CheckBox { Content = "Any language (automatic detection)", Foreground = AppearanceTheme.Text, IsChecked = _settings.DetectAnySourceLanguage, Margin = new Thickness(0, 0, 0, 8) };
        form.Children.Add(_anyLanguage);
        var languagePanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (string language in new[] { "Chinese", "Japanese", "Korean", "Spanish", "English" })
        {
            var check = new CheckBox
            {
                Content = language,
                Foreground = AppearanceTheme.Text,
                IsChecked = _settings.SourceLanguages.Contains(language, StringComparer.OrdinalIgnoreCase),
                Margin = new Thickness(0, 0, 18, 6),
                IsEnabled = !_settings.DetectAnySourceLanguage
            };
            _languageChecks[language] = check;
            languagePanel.Children.Add(check);
        }
        form.Children.Add(languagePanel);
        _anyLanguage.Checked += (_, _) => { foreach (CheckBox check in _languageChecks.Values) check.IsEnabled = false; };
        _anyLanguage.Unchecked += (_, _) => { foreach (CheckBox check in _languageChecks.Values) check.IsEnabled = true; };
        form.Children.Add(Hint("These constrain the translator. The current realtime Voxtral server itself only supports automatic language detection."));
        form.Children.Add(Label("Overlay display"));
        _overlayMode = new ComboBox { Foreground = AppearanceTheme.Text, Height = 32, Margin = new Thickness(0, 0, 0, 8), ItemsSource = new[] { "Sentence bubbles", "Latest sentence", "Per-speaker history" }, SelectedItem = _settings.OverlayMode };
        form.Children.Add(_overlayMode);
        form.Children.Add(Hint("Sentence bubbles show translations in time order, newest at the bottom. Revisions update their existing bubble; old speaker boxes are not kept permanently."));

        form.Children.Add(Heading("Speaker identification"));
        _aiSpeaker = new CheckBox { Content = "Use neural voice fingerprints (local CPU)", Foreground = AppearanceTheme.Text, IsChecked = _settings.UseAiSpeakerRecognition, Margin = new Thickness(0, 4, 0, 9), FontSize = 14 };
        form.Children.Add(_aiSpeaker);
        _speakerChoice = new ComboBox { Foreground = AppearanceTheme.Text, Height = 32, Margin = new Thickness(0, 0, 0, 8), ItemsSource = AppPaths.SpeakerModels,
            SelectedItem = AppPaths.SpeakerModels.FirstOrDefault(model => model.Path == _settings.SpeakerModelPath) ?? AppPaths.SpeakerModels[0] };
        form.Children.Add(_speakerChoice);
        _speakerPath = Text(_settings.SpeakerModelPath);
        form.Children.Add(_speakerPath);
        _download = Button(File.Exists(_settings.SpeakerModelPath) ? "Model installed" : "Download speaker model from GitHub", 240);
        _download.IsEnabled = !File.Exists(_settings.SpeakerModelPath);
        _download.Click += async (_, _) => await DownloadSpeakerModelAsync();
        _speakerChoice.SelectionChanged += (_, _) =>
        {
            if (_speakerChoice.SelectedItem is not SpeakerModelChoice choice) return;
            _speakerPath.Text = choice.Path;
            _download.IsEnabled = !File.Exists(choice.Path);
            _download.Content = File.Exists(choice.Path) ? "Model installed" : "Download selected model from GitHub";
        };
        form.Children.Add(_download);
        form.Children.Add(Label("Voice match strictness"));
        _speakerThreshold = Slider(_settings.SpeakerMatchThreshold, 0.40, 0.80, 0.01);
        form.Children.Add(SliderWithValue(_speakerThreshold, "0.00"));
        form.Children.Add(Hint("Voice fingerprints match learned voices, not positions. Unclear voices may remain unidentified. This does not separate voices that talk over each other."));
        form.Children.Add(Label("Maximum learned speakers in this group"));
        _expectedSpeakers = new ComboBox { Foreground = AppearanceTheme.Text, Height = 32, Margin = new Thickness(0, 0, 0, 8), ItemsSource = Enumerable.Range(2, 7).ToArray(), SelectedItem = _settings.MaxSpeakerProfiles };
        form.Children.Add(_expectedSpeakers);
        var resetSpeakers = Button("Reset learned speakers", 180);
        resetSpeakers.Click += (_, _) =>
        {
            ResetSpeakersRequested?.Invoke();
            _status!.Text = "Speaker reset requested. Voice profiles will be relearned.";
        };
        form.Children.Add(resetSpeakers);

        form.Children.Add(Heading("Sentence timing"));
        form.Children.Add(Label("Other people: actual audio pause before completing a sentence (milliseconds)"));
        _incomingPause = Slider(_settings.IncomingSettleMilliseconds, 600, 2400, 50);
        form.Children.Add(SliderWithValue(_incomingPause, "0 ms"));
        form.Children.Add(Label("Your microphone: pause before completing a sentence (milliseconds)"));
        _ownPause = Slider(_settings.OwnVoiceEndPauseMilliseconds, 900, 3000, 100);
        form.Children.Add(SliderWithValue(_ownPause, "0 ms"));
        _earlyPreviews = new CheckBox { Content = "Show translations while I speak", Foreground = AppearanceTheme.Text, IsChecked = _settings.OwnVoiceSendEarlyPreviews, Margin = new Thickness(0, 5, 0, 8) };
        form.Children.Add(_earlyPreviews);
        form.Children.Add(Hint("Updates use the full sentence heard so far. Early wording may change. Turn this off to send only finished sentences."));
        _finishCaptured = new CheckBox { Content = "When I mute VRChat, finish and send speech already captured", Foreground = AppearanceTheme.Text, IsChecked = _settings.FinishCapturedSentenceOnMute, Margin = new Thickness(0, 5, 0, 8) };
        form.Children.Add(_finishCaptured);
        form.Children.Add(Hint("New microphone capture stops immediately on mute. This only permits the sentence already spoken to finish."));

        _status = new TextBlock { Margin = new Thickness(0, 14, 0, 0), Foreground = AppearanceTheme.Accent, TextWrapping = TextWrapping.Wrap };
        form.Children.Add(_status);
        var layers = new Grid();
        layers.Children.Add(ThemeBackground.CreateLayer());
        layers.Children.Add(root);
        Content = layers;
        Loaded += async (_, _) => await RefreshModelsAsync();
        Closed += (_, _) => _windowCancellation.Cancel();
    }

    private async Task RefreshModelsAsync()
    {
        try
        {
            _status.Text = "Checking installed local translator models…";
            string baseUrl = _translatorUrl.Text.Trim().TrimEnd('/');
            var installed = await LocalModelManager.ListInstalledAsync(baseUrl, _windowCancellation.Token);
            var ids = installed.Select(model => model.Key).ToList();
            string current = _translatorModel.Text;
            string ownCurrent = _ownTranslatorModel.Text;
            _translatorModel.ItemsSource = ids;
            _ownTranslatorModel.ItemsSource = ids;
            _translatorModel.Text = current;
            _ownTranslatorModel.Text = ownCurrent;
            _status.Text = ids.Count == 0 ? "No installed chat models were reported." : $"Found {ids.Count} installed chat model(s), including unloaded models.";
        }
        catch (Exception exception) { _status.Text = "Could not read loaded models: " + exception.Message; }
    }

    private void ModelStatus(string message) => Dispatcher.Invoke(() => _status.Text = message);

    private async Task DownloadTranslatorAsync()
    {
        if (_modelCatalog.SelectedItem is not ModelDownloadChoice choice) return;
        if (AppearanceTheme.ShowMessage(this, choice.Description + "\n\nDownload this model now? It will not be loaded until you select it and save.", "Download local model", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _downloadTranslator.IsEnabled = false;
        _save.IsEnabled = false;
        try
        {
            await LocalModelManager.DownloadAsync(choice, ModelStatus, _windowCancellation.Token);
            await RefreshModelsAsync();
            _status.Text = "Downloaded. Choose it separately for your voice and/or other people, then Save and apply.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _status.Text = "Download failed: " + exception.Message; }
        finally { _downloadTranslator.IsEnabled = true; _save.IsEnabled = true; }
    }

    private async Task DownloadSpeakerModelAsync()
    {
        try
        {
            _download.IsEnabled = false;
            _status.Text = "Downloading the local speaker-identification model from GitHub…";
            Directory.CreateDirectory(AppPaths.ModelsDirectory);
            SpeakerModelChoice choice = _speakerChoice.SelectedItem as SpeakerModelChoice ?? AppPaths.SpeakerModels[0];
            string temporary = choice.Path + ".download";
            using HttpResponseMessage response = await _http.GetAsync(choice.Url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using Stream input = await response.Content.ReadAsStreamAsync();
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output);
            if (new FileInfo(temporary).Length < 1024 * 1024) throw new InvalidOperationException("Downloaded model is unexpectedly small.");
            File.Move(temporary, choice.Path, true);
            _speakerPath.Text = choice.Path;
            _status.Text = "Speaker model installed. Enable the checkbox, then save.";
            _download.Content = "Model installed";
        }
        catch (Exception exception)
        {
            _status.Text = "Download failed: " + exception.Message;
            _download.IsEnabled = true;
        }
    }

    private async Task SaveAndCloseAsync()
    {
        _settings.PlaybackDeviceId = (_playbackDevice.SelectedItem as PlaybackDeviceChoice)?.Id ?? "auto-vrchat";
        _settings.CaptureVrChatOnly = _vrchatOnly.IsChecked == true;
        if (IncomingListening.SourceMode(_settings) != "application")
            _settings.IncomingAudioMode = _settings.CaptureVrChatOnly ? "vrchat" : "desktop";
        _settings.FinishCapturedSentenceOnMute = _finishCaptured.IsChecked == true;
        _settings.NormalizeQuietPlayback = _normalizePlayback.IsChecked == true;
        _settings.UseAiSpeakerRecognition = _aiSpeaker.IsChecked == true;
        _settings.SpeakerModelPath = _speakerPath.Text.Trim();
        _settings.SpeakerMatchThreshold = _speakerThreshold.Value;
        _settings.TranslatorBaseUrl = _translatorUrl.Text.Trim().TrimEnd('/');
        _settings.TranslatorModel = _translatorModel.Text.Trim();
        _settings.OwnTranslatorModel = _ownTranslatorModel.Text.Trim();
        _settings.MaxSpeakerProfiles = _expectedSpeakers.SelectedItem is int speakerCount ? speakerCount : 2;
        _settings.TranslationQuality = _quality.SelectedItem?.ToString() ?? "Fast";
        _settings.IncomingTargetLanguage = _incomingTarget.SelectedItem?.ToString() ?? "English";
        _settings.OwnVoiceTargetLanguage = _ownTarget.SelectedItem?.ToString() ?? "Chinese";
        _settings.OverlayMode = _overlayMode.SelectedItem?.ToString() ?? "Sentence bubbles";
        _settings.SourceLanguages = _languageChecks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList();
        _settings.DetectAnySourceLanguage = _anyLanguage.IsChecked == true;
        if (_settings.SourceLanguages.Count == 0) _settings.SourceLanguages.Add("Chinese");
        _settings.IncomingSettleMilliseconds = (int)_incomingPause.Value;
        _settings.RecognitionDelayMilliseconds = _recognitionDelay.SelectedItem is int lookahead ? lookahead : 240;
        _settings.OwnVoiceEndPauseMilliseconds = (int)_ownPause.Value;
        _settings.OwnVoiceSendEarlyPreviews = _earlyPreviews.IsChecked == true;
        if (!Uri.TryCreate(_settings.TranslatorBaseUrl, UriKind.Absolute, out Uri? uri) ||
            !uri.IsLoopback || uri.Scheme != "http")
        {
            _status.Text = "Use a local HTTP address such as http://127.0.0.1:1234/v1.";
            return;
        }
        if (string.IsNullOrWhiteSpace(_settings.TranslatorModel) || string.IsNullOrWhiteSpace(_settings.OwnTranslatorModel))
        {
            _status.Text = "Choose a chat translation model.";
            return;
        }
        if (_settings.UseAiSpeakerRecognition && !File.Exists(_settings.SpeakerModelPath))
        {
            _status.Text = "Download or choose a speaker model first, or turn neural recognition off.";
            return;
        }
        try
        {
            var installed = await LocalModelManager.ListInstalledAsync(_settings.TranslatorBaseUrl, _windowCancellation.Token);
            string[] selectedModels = new[] { _settings.TranslatorModel, _settings.OwnTranslatorModel }.Distinct().ToArray();
            bool needsLoading = selectedModels.Any(key => !installed.Any(model => model.Key == key && model.IsLoaded));
            if (needsLoading && (selectedModels.Length > 1 || installed.Any(model => selectedModels.Contains(model.Key) && model.SizeBytes > 8_000_000_000L)))
            {
                if (AppearanceTheme.ShowMessage(this, "Loading the selected models can use substantial graphics memory and slow VRChat. Using the same model for both directions is lighter.\n\nLoad the selected models and apply?", "Graphics memory warning", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            }
            _save.IsEnabled = false;
            foreach (string modelKey in selectedModels)
                await LocalModelManager.EnsureLoadedAsync(modelKey, _settings.TranslatorBaseUrl, ModelStatus, _windowCancellation.Token);
            bool requestedRecognition = _settings.UseAiSpeakerRecognition;
            _settings.SubtitlesAlwaysOnTop = SettingsStore.Load().SubtitlesAlwaysOnTop;
            SettingsStore.Save(_settings);
            SettingsStore.SaveSpeakerRecognitionPreference(requestedRecognition);
            _settings.UseAiSpeakerRecognition = requestedRecognition;
            Saved?.Invoke(_settings.Copy());
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _status.Text = "Could not apply options: " + exception.Message; }
        finally { _save.IsEnabled = true; }
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 8), Foreground = AppearanceTheme.Text };
    private static TextBlock Label(string text) => new() { Text = text, FontSize = 13, Margin = new Thickness(0, 4, 0, 4), Foreground = AppearanceTheme.Text };
    private static TextBlock Hint(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8), Foreground = AppearanceTheme.Muted };
    private static TextBox Text(string value) => new() { Text = value, Foreground = AppearanceTheme.Text, Background = AppearanceTheme.Raised, Height = 32, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(7, 4, 7, 4) };
    private static Slider Slider(double value, double minimum, double maximum, double tick) => new() { Value = value, Minimum = minimum, Maximum = maximum, TickFrequency = tick, IsSnapToTickEnabled = true, Height = 28, Margin = new Thickness(0, 0, 0, 6) };
    private static Button Button(string text, double width) => new() { Content = text, Foreground = AppearanceTheme.Text, Background = AppearanceTheme.Raised, BorderBrush = AppearanceTheme.Stroke, Width = width, Height = 32, Padding = new Thickness(7, 3, 7, 3), Cursor = System.Windows.Input.Cursors.Hand };
    private static ComboBox LanguageSelector(string selected) => new() { Foreground = AppearanceTheme.Text, Height = 32, Margin = new Thickness(0, 0, 0, 8), ItemsSource = AppSettings.TranslationLanguages, SelectedItem = selected };
    private static FrameworkElement SliderWithValue(Slider slider, string format)
    {
        var panel = new DockPanel();
        var value = new TextBlock { Text = slider.Value.ToString(format), Width = 74, VerticalAlignment = VerticalAlignment.Center, Foreground = AppearanceTheme.Text };
        DockPanel.SetDock(value, Dock.Right);
        panel.Children.Add(value);
        panel.Children.Add(slider);
        slider.ValueChanged += (_, _) => value.Text = slider.Value.ToString(format);
        return panel;
    }
}
