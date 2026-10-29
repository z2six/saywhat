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
        Add("Chinese and English. More processing to help with difficult speech.", "支持中文和英语，使用更多处理来帮助听清较难的语音。", "中国語と英語に対応。処理を増やし、難しい音声を聞き取りやすくします。", "중국어와 영어를 지원합니다. 인식에 더 시간을 들여 어려운 말을 알아듣도록 돕습니다.");
        Add("Continuous multilingual recognition. Detects the language automatically.", "连续识别多种语言，并自动检测所说的语言。", "多言語の音声を連続して認識し、言語を自動検出します。", "다국어 음성을 연속으로 인식하고 언어를 자동 감지합니다.");
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
        Add("Apply your subtitle choices below before starting.", "请先在下方应用字幕设置，再启动字幕。", "開始する前に、下の字幕設定を適用してください。", "시작하기 전에 아래에서 자막 설정을 적용하세요.");
        Add("Choose compatible languages or another translator first.", "请先选择支持的语言组合，或换一个翻译模型。", "先に対応する言語の組み合わせ、または別の翻訳モデルを選んでください。", "먼저 지원되는 언어 조합이나 다른 번역 모델을 고르세요.");
        Add("Translation model · translates their words", "翻译模型 · 翻译对方的话", "翻訳モデル · 相手の言葉を翻訳", "번역 모델 · 상대방의 말을 번역");
        Add("Use the same translator as My voice", "与“我的语音”使用同一个翻译模型", "自分の音声と同じ翻訳モデルを使う", "내 음성과 같은 번역 모델 사용");
        Add("Sharing a translator uses less memory. Choosing a different model below gives subtitles their own translator.", "共用翻译模型可节省内存。在下方选择其他模型后，字幕会使用独立的翻译模型。", "翻訳モデルを共有するとメモリを節約できます。下で別のモデルを選ぶと、字幕専用の翻訳モデルになります。", "번역 모델을 공유하면 메모리를 절약합니다. 아래에서 다른 모델을 고르면 자막 전용 번역 모델을 사용합니다.");
        Add("An external translator is selected. Change this in Models to use downloaded translators.", "当前选择了外部翻译服务。如需使用下载的模型，请在“模型”中更改。", "外部の翻訳サービスが選ばれています。ダウンロードしたモデルを使うには「モデル」で変更してください。", "외부 번역기가 선택되어 있습니다. 다운로드한 모델을 쓰려면 모델에서 변경하세요.");
        Add("How long to wait after they stop speaking", "对方停止说话后等待多久", "相手が話し終えてから待つ時間", "상대방이 말을 멈춘 뒤 기다릴 시간");
        Add("A shorter pause shows sentences sooner. A longer pause keeps more of a thought together.", "等待越短，字幕出现越快；等待越长，越能保留完整的意思。", "短くすると字幕が早く表示されます。長くすると一続きの内容をまとめやすくなります。", "짧게 기다리면 자막이 더 빨리 나옵니다. 길게 기다리면 문맥을 더 길게 묶습니다.");
        Add("Recognize saved people by their voices", "通过声音识别已保存的人", "保存した人を声で見分ける", "목소리로 저장된 사람 구별");
        Add("Voice recognition is optional. Voice examples and names stay on this PC.", "声纹识别是可选功能。声音样本和姓名仅保存在此电脑上。", "声での識別は任意です。声のサンプルと名前はこの PC にだけ保存されます。", "목소리 구별은 선택 사항입니다. 음성 예시와 이름은 이 PC에만 저장됩니다.");
        Add("Normal shows words sooner. Groups tries to separate overlapping voices, but needs more memory and adds delay.", "普通模式显示更快。群组模式尝试区分重叠的语音，但需要更多内存，也会增加延迟。", "通常は言葉を早く表示します。グループは重なった声の区別を試みますが、より多くのメモリと待ち時間が必要です。", "일반 모드는 말을 더 빨리 표시합니다. 그룹은 겹치는 목소리를 구분하려 하지만 메모리를 더 쓰고 지연이 늘어납니다.");
        Add("Changing model sharing briefly pauses both translators. Your microphone choices stay the same.", "更改模型共用方式会短暂暂停两种翻译。你的麦克风设置保持不变。", "モデルの共有を変える際は両方の翻訳が短く停止します。マイクの設定は変わりません。", "모델 공유 방식을 바꾸면 두 번역이 잠깐 멈춥니다. 내 마이크 설정은 유지됩니다.");
        Add("Quick is active and skips voice recognition. Choose Normal to recognize saved people.", "当前为快速模式，不进行声音识别。如需识别已保存的人，请选择普通模式。", "クイックが有効なため、声の識別を省略します。保存した人を見分けるには通常を選んでください。", "빠른 모드가 켜져 있어 목소리 구별을 생략합니다. 저장된 사람을 구별하려면 일반을 고르세요.");
        return values;
    }
}
