using System.Globalization;

namespace IncomingTranslator;

// UI copy only. Stored language/model keys, subtitles and people's names are
// never translated through this catalog. The dashboard rebuilds on language change.
internal static class SubtitleSpeechText
{
    private static readonly IReadOnlyDictionary<string, string[]> Catalog = Build();
    internal static IReadOnlyDictionary<string, string[]> TestCatalog => Catalog;
    internal static string T(string english)
    {
        int index = UiText.Language switch { "zh-CN" => 0, "ja" => 1, "ko" => 2, _ => -1 };
        return index >= 0 && Catalog.TryGetValue(english, out string[]? values) ? values[index] : UiText.T(english);
    }
    internal static string Format(string english, params object?[] arguments) => string.Format(
        CultureInfo.GetCultureInfo(UiText.Language == "en" ? "en-US" : UiText.Language), T(english), arguments);
    private static IReadOnlyDictionary<string, string[]> Build()
    {
        var values = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Add(string english, string chinese, string japanese, string korean) => values.Add(english, [chinese, japanese, korean]);
        Add("Subtitle speech recognition", "字幕语音识别", "字幕の音声認識", "자막 음성 인식");
        Add("Choose what people are speaking and the model that hears their words. Your microphone settings stay unchanged.", "选择对方说的语言以及用于听清语音的模型。不会更改你的麦克风设置。", "相手が話す言語と、言葉を聞き取るモデルを選びます。自分のマイク設定は変わりません。", "상대방이 말하는 언어와 음성을 인식할 모델을 고르세요. 내 마이크 설정은 바뀌지 않습니다.");
        Add("Groups uses VibeVoice and detects the language automatically. Choosing a model or a specific language below returns to Normal subtitles.", "群组模式使用 VibeVoice 自动检测语言。在下方选择其他模型或指定语言，会切回普通字幕模式。", "グループでは VibeVoice が言語を自動検出します。下で別のモデルや言語を選ぶと、通常の字幕に戻ります。", "그룹은 VibeVoice로 언어를 자동 감지합니다. 아래에서 다른 모델이나 특정 언어를 고르면 일반 자막으로 돌아갑니다.");
        Add("They speak", "对方说的语言", "相手が話す言語", "상대방이 말하는 언어");
        Add("Any language", "自动识别语言", "言語を自動検出", "언어 자동 감지");
        Add("Choose a language when you know it. Any language uses a multilingual recognizer and may mistake short or unclear speech.", "知道对方说什么语言时，请直接选择。自动识别会使用多语言模型，简短或不清晰的语音仍可能识别错误。", "相手の言語が分かる場合は指定してください。自動検出では多言語モデルを使い、短い言葉や不明瞭な音声を誤認する場合があります。", "상대방의 언어를 알면 직접 고르세요. 자동 감지는 다국어 모델을 사용하며 짧거나 불분명한 말은 잘못 인식할 수 있습니다.");
        Add("Speech model", "语音识别模型", "音声モデル", "음성 모델");
        Add("FireRedASR2 · Fast", "FireRedASR2 · 快速", "FireRedASR2 · 高速", "FireRedASR2 · 빠름");
        Add("FireRedASR2 · Accurate", "FireRedASR2 · 精细", "FireRedASR2 · 詳細", "FireRedASR2 · 정밀");
        Add("Multilingual streaming recognition. Choose a language or let it detect one.", "多语言流式识别，可指定语言或自动检测。", "多言語のストリーミング認識。言語の指定も自動検出もできます。", "다국어 스트리밍 인식입니다. 언어를 지정하거나 자동으로 감지할 수 있습니다.");
        Add("Chinese and English. Lighter recognition for quicker subtitles.", "支持中文和英语，使用较轻量的识别方式，字幕更快。", "中国語と英語に対応。軽い処理で字幕を速く表示します。", "중국어와 영어를 지원합니다. 더 가벼운 인식 방식으로 자막을 빠르게 표시합니다.");
        Add("Chinese and English. Uses an extra decoding step for difficult speech.", "支持中文和英语，增加解码步骤以处理难以听清的语音。", "中国語と英語に対応。難しい音声のために追加の読み取り処理を行います。", "중국어와 영어를 지원합니다. 어려운 음성에 추가 인식 단계를 사용합니다.");
        Add("The previous continuous recognizer. Detects the language automatically.", "原有的连续语音识别模型，自动检测语言。", "従来の連続音声認識モデル。言語は自動検出します。", "기존의 연속 음성 인식 모델입니다. 언어를 자동 감지합니다.");
        Add("Groups recognition with separate voice labels. Adds buffering.", "群组语音识别，可区分语音标签，需要额外缓冲。", "声ごとのラベルを付けるグループ認識。バッファ待ちが加わります。", "음성별 표시가 있는 그룹 인식입니다. 버퍼 대기가 추가됩니다.");
        Add("FireRedASR2 recognizes Chinese and English. The selected language guides translation; FireRed can still recognize English words in Chinese speech. It processes short audio sections rather than updating every word.", "FireRedASR2 识别中文和英语。所选语言用于指导翻译，中文语音中的英语词语仍可被识别。它按短音频片段处理，不会逐字更新。", "FireRedASR2 は中国語と英語を認識します。選んだ言語は翻訳に使われますが、中国語の中の英単語も認識できます。単語ごとの更新ではなく、短い音声区間を処理します。", "FireRedASR2는 중국어와 영어를 인식합니다. 선택한 언어는 번역에 반영되며 중국어 문장 속 영어 단어도 인식할 수 있습니다. 단어마다 갱신하지 않고 짧은 음성 구간을 처리합니다.");
        Add("Voxtral detects the language itself. Choose Nemotron or FireRedASR2 to select a spoken language.", "Voxtral 会自行检测语言。如需指定所说的语言，请选择 Nemotron 或 FireRedASR2。", "Voxtral は言語を自動検出します。話す言語を指定する場合は Nemotron または FireRedASR2 を選んでください。", "Voxtral은 언어를 스스로 감지합니다. 말하는 언어를 지정하려면 Nemotron이나 FireRedASR2를 고르세요.");
        Add("Not applied yet", "尚未应用", "まだ適用していません", "아직 적용되지 않음");
        Add("Current selection", "当前选择", "現在の選択", "현재 선택");
        Add("Download needed: {0:0.0} GB. Files stay on this PC and are reused.", "需要下载：{0:0.0} GB。文件保存在此电脑上，可重复使用。", "必要なダウンロード：{0:0.0} GB。ファイルはこの PC に保存して再利用します。", "다운로드 필요: {0:0.0} GB. 파일은 이 PC에 보관되며 다시 사용됩니다.");
        Add("Apply subtitle choices", "应用字幕设置", "字幕の設定を適用", "자막 설정 적용");
        Add("Files download before subtitles briefly pause to apply the change. Your voice translator keeps running.", "会先下载文件，再短暂暂停字幕以应用更改。你的语音翻译会继续运行。", "先にファイルをダウンロードし、字幕だけを短く停止して変更を適用します。自分の音声翻訳は続きます。", "먼저 파일을 다운로드한 뒤 자막만 잠깐 멈춰 변경을 적용합니다. 내 음성 번역은 계속 실행됩니다.");
        Add("Apply these choices, then choose Start subtitles on Home. Models load only when subtitles start.", "应用设置后，在主页选择“启动字幕”。模型仅在启动字幕时加载。", "設定を適用してからホームで字幕を開始してください。モデルは字幕の開始時にだけ読み込みます。", "설정을 적용한 뒤 홈에서 자막 시작을 누르세요. 모델은 자막을 시작할 때만 불러옵니다.");
        Add("Speech model & language", "语音模型与语言", "音声モデルと言語", "음성 모델 및 언어");
        return values;
    }
}
