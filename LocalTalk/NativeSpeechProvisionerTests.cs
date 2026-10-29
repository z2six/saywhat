using System.Text.Json;
using System.Text.RegularExpressions;

namespace IncomingTranslator;

/// <summary>
/// Settings, pinned metadata and rejected/cancelled preparation only. This suite
/// never downloads files, creates its candidate data root, starts an executable,
/// opens an audio device or inspects the user's running models.
/// </summary>
internal static class NativeSpeechProvisionerTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Native speech preparation: " + message);
            checks++;
        }
        void Reject<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { checks++; return; }
            throw new InvalidOperationException("Native speech preparation: " + message);
        }

        var fresh = new AppSettings();
        Check(fresh.OwnVoiceSourceLanguage == "English" && fresh.OwnRecognitionEngine == "nemotron",
            "a new user's voice starts with an explicit English source and native recognition");
        Check(fresh.OwnMicrophoneDeviceId == "default", "a new user follows the Windows default microphone");
        Check(fresh.OwnVoicePauseWhenVrchatMuted, "new preferences pause own-voice capture when the VRChat microphone is muted");
        Check(!fresh.FirstRunSetupCompleted && !fresh.FirstRunIntroductionCompleted,
            "constructing fresh preferences does not skip onboarding");
        Check(!fresh.OwnVoiceAdditionalLanguagesEnabled && fresh.EffectiveOwnVoiceTargetLanguages().Count == 1,
            "new users start with one target, not four parallel translations");

        AppSettings legacy = SettingsStore.ReadSettings("{}");
        Check(legacy.OwnRecognitionEngine == "voxtral" && legacy.OwnVoiceSourceLanguage == "Automatic",
            "old preferences without engine/source fields retain Voxtral and automatic language detection");
        Check(legacy.OwnMicrophoneDeviceId == "default", "missing old microphone preference follows the default device");
        Check(legacy.OwnVoicePauseWhenVrchatMuted, "old preferences missing the mute-following choice retain mute gating");
        AppSettings muteIgnored = SettingsStore.ReadSettings("""{"OwnVoicePauseWhenVrchatMuted":false}""");
        Check(!muteIgnored.OwnVoicePauseWhenVrchatMuted && !muteIgnored.Copy().OwnVoicePauseWhenVrchatMuted &&
            !SettingsStore.ReadSettings(JsonSerializer.Serialize(muteIgnored)).OwnVoicePauseWhenVrchatMuted,
            "an explicit mute-following opt-out survives reading, snapshots and a settings roundtrip");
        Check(SettingsStore.ReadSettings("""{"OwnVoicePauseWhenVrchatMuted":true}""").OwnVoicePauseWhenVrchatMuted,
            "an explicit mute-following opt-in survives reading");
        AppSettings legacyLanguage = SettingsStore.ReadSettings("""
            {"OwnVoiceSourceLanguage":"Chinese","OwnVoiceTargetLanguage":"Korean"}
            """);
        Check(legacyLanguage.OwnRecognitionEngine == "voxtral" && legacyLanguage.OwnVoiceSourceLanguage == "Chinese" &&
            legacyLanguage.OwnVoiceTargetLanguage == "Korean", "migration never overwrites an explicit previous spoken/target language");
        AppSettings selected = SettingsStore.ReadSettings("""
            {"OwnRecognitionEngine":"nemotron","OwnVoiceSourceLanguage":"Japanese","OwnMicrophoneDeviceId":" headset-device "}
            """);
        Check(selected.OwnRecognitionEngine == "nemotron" && selected.OwnVoiceSourceLanguage == "Japanese" &&
            selected.OwnMicrophoneDeviceId == "headset-device", "new explicit engine, source and selected microphone survive reading");
        AppSettings roundTrip = SettingsStore.ReadSettings(JsonSerializer.Serialize(selected));
        Check(roundTrip.OwnRecognitionEngine == selected.OwnRecognitionEngine &&
            roundTrip.OwnVoiceSourceLanguage == selected.OwnVoiceSourceLanguage &&
            roundTrip.OwnMicrophoneDeviceId == selected.OwnMicrophoneDeviceId,
            "serializing and reading new preferences does not remigrate them as a legacy install");
        var copied = selected.Copy();
        Check(copied.OwnRecognitionEngine == selected.OwnRecognitionEngine &&
            copied.OwnVoiceSourceLanguage == selected.OwnVoiceSourceLanguage &&
            copied.OwnMicrophoneDeviceId == selected.OwnMicrophoneDeviceId,
            "settings snapshots include all new own-voice choices");

        var many = new AppSettings
        {
            OwnVoiceTargetLanguage = "Chinese", OwnVoiceAdditionalLanguagesEnabled = true,
            OwnVoiceAdditionalTargetLanguages = new() { "zh", "Chinese", "ja", "Japanese", "ko", "en", "French", "unsupported" }
        };
        Check(many.EffectiveOwnVoiceTargetLanguages().SequenceEqual(new[] { "Chinese", "Japanese", "Korean", "English" }),
            "one primary and three additional targets are canonical, ordered, unique and bounded to four");
        Check(many.NormalizedOwnVoiceAdditionalLanguages().SequenceEqual(new[] { "Japanese", "Korean", "English" }),
            "duplicates of the primary language and aliases do not consume extra target slots");
        var snapshot = many.Copy();
        snapshot.OwnVoiceAdditionalTargetLanguages.Clear();
        Check(many.OwnVoiceAdditionalTargetLanguages.Count == 8, "editing a target snapshot never mutates the original selection");
        many.OwnVoiceAdditionalLanguagesEnabled = false;
        Check(many.EffectiveOwnVoiceTargetLanguages().SequenceEqual(new[] { "Chinese" }),
            "turning extra languages off returns immediately to one output");
        var absentTargets = new AppSettings { OwnVoiceAdditionalTargetLanguages = null!, OwnVoiceAdditionalLanguagesEnabled = true };
        Check(absentTargets.EffectiveOwnVoiceTargetLanguages().Count == 1, "missing extra-language storage is safe");

        Check(OwnRecognitionCatalog.Choices.Select(choice => choice.Id).SequenceEqual(new[] { "nemotron", "voxtral" }),
            "only the shipped native and legacy recognizers are offered, not research candidates");
        OwnRecognitionChoice native = OwnRecognitionCatalog.Find("nemotron"), previous = OwnRecognitionCatalog.Find("voxtral");
        Check(native.SupportsExplicitLanguage && !previous.SupportsExplicitLanguage,
            "the model catalog distinguishes real language pinning from legacy autodetection");
        foreach ((string language, string code) in new[]
        {
            ("Chinese", "zh-CN"), ("English", "en-US"), ("Japanese", "ja-JP"), ("Korean", "ko-KR"),
            ("Spanish", "es-ES"), ("French", "fr-FR"), ("German", "de-DE"), ("Portuguese", "pt-BR"), ("Russian", "ru-RU")
        })
        {
            Check(native.SupportsSource(language) && OwnRecognitionCatalog.LanguageCode(language) == code,
                "native recognition accepts the visible source language with its actual runtime code: " + language);
            Check(!previous.SupportsSource(language), "legacy recognition does not pretend to pin " + language);
        }
        Check(native.SupportsSource("Automatic") && previous.SupportsSource("Automatic") &&
            OwnRecognitionCatalog.LanguageCode("Automatic") == "auto", "automatic remains available for language switching");
        Check(!native.SupportsSource("unsupported") && !previous.SupportsSource("unsupported"),
            "unknown source languages are not advertised as supported");
        Check(AppSettings.CanonicalTranslationLanguage("th-TH") == "Thai" &&
            !native.SupportsSource("Thai") && !native.SupportsSource("th") && !previous.SupportsSource("Thai") &&
            !OwnRecognitionCatalog.SupportedSourceLanguages.Contains("Thai"),
            "Thai translation output never silently becomes unsupported microphone recognition");
        var thai = SettingsStore.ReadSettings("""{"OwnRecognitionEngine":"nemotron","OwnVoiceSourceLanguage":"English","OwnVoiceTargetLanguage":"Thai","OwnVoiceAdditionalLanguagesEnabled":true,"OwnVoiceAdditionalTargetLanguages":["Japanese","Chinese","th","Thai"]}""");
        Check(thai.EffectiveOwnVoiceTargetLanguages().SequenceEqual(new[] { "Thai", "Japanese", "Chinese" }) &&
            thai.Copy().OwnVoiceTargetLanguage == "Thai" && thai.OwnVoiceSourceLanguage == "English",
            "Thai primary and additional outputs persist without changing the English microphone language");
        Check(OwnRecognitionCatalog.LanguageCode("zh") == "zh-CN" && OwnRecognitionCatalog.LanguageCode("ja") == "ja-JP",
            "existing source-language aliases resolve to the correct pinned runtime code");
        Check(ReferenceEquals(OwnRecognitionCatalog.Find(null), native) && ReferenceEquals(OwnRecognitionCatalog.Find("unknown"), native),
            "an unknown new engine preference falls back to the shipped default");

        ProvisionAsset runtime = NativeSpeechProvisioner.RuntimeAsset, model = NativeSpeechProvisioner.ModelAsset;
        Check(runtime.FileName == "nemo-speech-0.2.0-windows-x86_64-vulkan.zip" && runtime.SizeBytes == 19_302_464,
            "the runtime download has an explicit Windows/Vulkan archive and measured size");
        Check(runtime.Sha256 == "edf15a04ba98740aa0ab75ae4681ac7ad0a488b22b35ce2cbebd10590bb1c1e1",
            "the runtime bytes have the reviewed immutable digest");
        Check(runtime.DownloadUrl == "https://github.com/NVIDIA/NeMo-Speech.cpp/releases/download/v0.2.0/" + runtime.FileName,
            "the runtime comes from an exact official release rather than latest or a third-party mirror");
        Check(NativeSpeechProvisioner.ModelRevision == "ea30d66debe3740a08b573244286791d423d6b3e" &&
            model.FileName == "nemotron-3.5-asr-streaming-0.6b.q8_0.gguf" && model.SizeBytes == 742_090_464,
            "the speech model has an immutable revision, streaming identity and measured size");
        Check(model.Sha256 == "3fc991d3badad7277c11030a7519832cddaf2057aafed6d4b25147e953a070b1",
            "the model bytes have the reviewed immutable digest");
        Check(model.DownloadUrl == "https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b/resolve/" +
            NativeSpeechProvisioner.ModelRevision + "/" + model.FileName,
            "the model is pinned to an official NVIDIA repository revision");
        foreach (ProvisionAsset asset in new[] { runtime, model })
        {
            var url = new Uri(asset.DownloadUrl, UriKind.Absolute);
            Check(url.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(url.UserInfo) &&
                string.IsNullOrEmpty(url.Query) && string.IsNullOrEmpty(url.Fragment), "download metadata has no credentials or mutable query: " + asset.FileName);
            Check(Regex.IsMatch(asset.Sha256, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant), "asset verification requires a full SHA-256: " + asset.FileName);
        }

        string[] launch = NativeSpeechProvisioner.ServerArguments(@"C:\fixtures\speech.gguf", 8080);
        Check(launch[0] == "serve" && launch[1] == "--asr-model" && launch[2] == @"C:\fixtures\speech.gguf" &&
            launch.Contains("--asr.enabled=true") && launch.Contains("--tts.enabled=false") && launch.Contains("--nmt.enabled=false"),
            "native launch uses the actual official server CLI and inline boolean syntax");
        Check(launch.Contains("--asr.endpointing.enable=false") && launch.Contains("--asr.batching.enabled=false") &&
            launch.Contains("--asr.streaming.rnnt_right_context=3") && !launch.Contains("false") && !launch.Contains("true"),
            "native recognition does not add a second sentence-finishing gate or pass invalid standalone boolean arguments");
        Check(launch.Contains("--asr.backend.threads=2") && launch[Array.IndexOf(launch, "--threads") + 1] == "2" &&
            launch[Array.IndexOf(launch, "--device") + 1] == "vulkan:0" && launch.Contains("--no-ui"),
            "native launch caps inference and HTTP threads separately, keeps Vulkan explicit, and opens no browser");

        // This random, absent directory is never created. Even accidentally
        // re-running this suite cannot collide with or change real app data.
        string absentRoot = Path.Combine(Path.GetTempPath(), "SayWhat-NativeSpeech-Metadata-" + Guid.NewGuid().ToString("N"));
        var provisioner = new NativeSpeechProvisioner(absentRoot);
        string settingsBefore = JsonSerializer.Serialize(fresh);
        Check(!Directory.Exists(absentRoot) && provisioner.ExecutablePath is null && !provisioner.IsReady(fresh),
            "construction and readiness inspection do not install or start anything");
        Check(provisioner.PendingDownloadBytes(fresh) == runtime.SizeBytes + model.SizeBytes,
            "a fresh preparation shows both known download sizes before any network work");
        Check(provisioner.RuntimeRoot == Path.Combine(absentRoot, "runtimes", "nemo-speech-0.2.0-vulkan") &&
            provisioner.ModelPath == Path.Combine(absentRoot, "models", "own-speech", model.FileName),
            "native files live in dedicated app-owned locations rather than a user's WSL or another speech app");
        Check(!provisioner.IsReady(legacy) && provisioner.PendingDownloadBytes(legacy) == 0,
            "legacy users do not silently schedule a native model download");
        Reject<ModelSelectionUserException>(() => provisioner.BuildStartInfo(fresh, 8083),
            "an unprepared native model must require preparation instead of pulling files at start");
        foreach (int port in new[] { 0, 1023, 65536 })
            Reject<ModelSelectionUserException>(() => provisioner.BuildStartInfo(fresh, port), "invalid port cannot reach runtime construction");
        Reject<SetupUserException>(() => provisioner.PrepareAsync(legacy, null, CancellationToken.None).GetAwaiter().GetResult(),
            "preparing native files requires a deliberate native model choice");
        var unsupported = fresh.Copy(); unsupported.OwnVoiceSourceLanguage = "unsupported";
        Reject<SetupUserException>(() => provisioner.PrepareAsync(unsupported, null, CancellationToken.None).GetAwaiter().GetResult(),
            "unsupported languages are rejected before download or inference");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Reject<OperationCanceledException>(() => provisioner.PrepareAsync(fresh, null, cancelled.Token).GetAwaiter().GetResult(),
            "cancelled preparation exits before files, network or model startup");
        Check(!Directory.Exists(absentRoot) && provisioner.ExecutablePath is null &&
            JsonSerializer.Serialize(fresh) == settingsBefore,
            "all metadata and rejected/cancelled operations leave the root absent and preferences unchanged");
        return checks;
    }
}
