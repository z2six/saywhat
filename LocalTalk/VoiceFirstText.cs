using System.Globalization;

namespace IncomingTranslator;

// Voice-first copy is deliberately separate from transcript/person text. Values
// here are UI labels only, rebuilt when the interface language changes.
internal static class VoiceFirstText
{
    private static readonly IReadOnlyDictionary<string, string[]> Catalog = Build();

    internal static string T(string english)
    {
        int index = UiText.Language switch { "zh-CN" => 0, "ja" => 1, "ko" => 2, _ => -1 };
        return index >= 0 && Catalog.TryGetValue(english, out string[]? translated) ? translated[index] : UiText.T(english);
    }

    internal static string Format(string english, params object?[] arguments) =>
        string.Format(CultureInfo.GetCultureInfo(UiText.Language == "en" ? "en-US" : UiText.Language), T(english), arguments);

    internal static IReadOnlyDictionary<string, string[]> TestCatalog => Catalog;

    private static IReadOnlyDictionary<string, string[]> Build()
    {
        var values = new Dictionary<string, string[]>(StringComparer.Ordinal);
        void Add(string english, string chinese, string japanese, string korean) => values.Add(english, [chinese, japanese, korean]);
        Add("Start translating my voice", "开始翻译我的语音", "自分の音声の翻訳を開始", "내 음성 번역 시작");
        Add("Translate into VRChat chat", "翻译到 VRChat 聊天", "VRChat のチャットに翻訳", "VRChat 채팅으로 번역");
        Add("I speak", "我说的语言", "自分が話す言語", "내가 말하는 언어");
        Add("Translation language hint", "翻译语言提示", "翻訳用の言語ヒント", "번역용 언어 힌트");
        Add("Translate to · choose up to four", "翻译为 · 最多选择四种", "翻訳先 · 4 言語まで", "번역할 언어 · 최대 4개");
        Add("Voxtral detects the spoken language automatically. Choose Nemotron to set it yourself.", "Voxtral 会自动识别所说的语言。选择 Nemotron 可以自行设置。", "Voxtral は話す言語を自動検出します。自分で指定する場合は Nemotron を選んでください。", "Voxtral은 말하는 언어를 자동으로 감지합니다. 직접 지정하려면 Nemotron을 선택하세요.");
        Add("Translations share VRChat’s 144-character limit. More languages leave less room for each message.", "所有翻译共用 VRChat 的 144 字符上限。语言越多，每种语言可显示的内容就越少。", "翻訳はすべて VRChat の 144 文字の上限を共有します。言語が増えると、各言語で表示できる内容が少なくなります。", "모든 번역이 VRChat의 144자 제한을 함께 사용합니다. 언어를 추가할수록 각 언어로 표시할 공간이 줄어듭니다.");
        Add("Speech recognition · hears your words", "语音识别 · 听懂你说的话", "音声認識 · 話した言葉を聞き取る", "음성 인식 · 내 말을 듣기");
        Add("Native Windows. Uses your chosen spoken language.", "直接在 Windows 上运行，使用你选择的语音语言。", "Windows 上で動作。選んだ話し言葉を使用します。", "Windows에서 바로 실행됩니다. 지정한 언어로 말을 인식합니다.");
        Add("Continuous recognition. Detects the language automatically.", "连续识别语音，自动检测语言。", "連続して音声を認識。言語は自動検出します。", "음성을 계속 인식합니다. 언어는 자동으로 감지합니다.");
        Add("Choose Automatic above to use this recognizer.", "请在上方选择“自动”，再使用此识别器。", "この認識モデルを使うには、上で「自動」を選んでください。", "이 인식 모델을 쓰려면 위에서 자동을 선택하세요.");
        Add("Choose Nemotron below to set a spoken language.", "选择下方的 Nemotron 即可设置所说的语言。", "話す言語を指定するには、下の Nemotron を選んでください。", "말하는 언어를 지정하려면 아래에서 Nemotron을 선택하세요.");
        Add("Translation model · translates your words", "翻译模型 · 翻译你说的话", "翻訳モデル · 言葉を翻訳する", "번역 모델 · 내 말을 번역하기");
        Add("HY-MT2 · Fast", "HY-MT2 · 快速", "HY-MT2 · 高速", "HY-MT2 · 빠름");
        Add("HY-MT2 · Larger", "HY-MT2 · 大型", "HY-MT2 · 大型", "HY-MT2 · 대형");
        Add("Liquid · EN/JP", "Liquid · 英语/日语", "Liquid · 英語/日本語", "Liquid · 영어/일본어");
        Add("Small, dedicated translator. Recommended starting point.", "小型专用翻译模型，建议从这里开始。", "小型の翻訳専用モデル。最初におすすめです。", "작은 번역 전용 모델입니다. 처음에 권장합니다.");
        Add("More memory. Try it for difficult wording; it may take longer.", "占用更多内存，可尝试处理复杂表达，但可能较慢。", "メモリを多く使います。難しい表現に試せますが、時間がかかる場合があります。", "메모리를 더 사용합니다. 어려운 표현에 시도해 보세요. 시간이 더 걸릴 수 있습니다.");
        Add("Alternative translator. Needs a chosen spoken language.", "另一款翻译模型，需要先选择所说的语言。", "別の翻訳モデル。話す言語の指定が必要です。", "다른 번역 모델입니다. 말하는 언어를 지정해야 합니다.");
        Add("Very small. Only translates English and Japanese.", "非常小巧，仅支持英语和日语互译。", "とても小型。英語と日本語のみ翻訳します。", "매우 작습니다. 영어와 일본어만 번역합니다.");
        Add("English ↔ Japanese only. Choose those languages above first.", "仅支持英语与日语互译，请先在上方选择这些语言。", "英語と日本語の相互翻訳のみ対応。先に上で言語を選んでください。", "영어 ↔ 일본어만 지원합니다. 위에서 해당 언어를 먼저 선택하세요.");
        Add("Choose the language you speak above first.", "请先在上方选择你所说的语言。", "先に上で自分が話す言語を選んでください。", "위에서 말하는 언어를 먼저 선택하세요.");
        Add("Current translator: {0}", "当前翻译模型：{0}", "現在の翻訳モデル：{0}", "현재 번역 모델: {0}");
        Add("Subtitles use this translator too.", "字幕也使用此翻译模型。", "字幕にもこの翻訳モデルを使います。", "자막도 이 번역 모델을 사용합니다.");
        Add("Subtitles keep their own translator.", "字幕继续使用独立的翻译模型。", "字幕には別の翻訳モデルを使います。", "자막은 별도의 번역 모델을 유지합니다.");
        Add("All models and downloads", "所有模型与下载", "すべてのモデルとダウンロード", "모든 모델 및 다운로드");
        Add("When messages appear", "何时显示消息", "メッセージを表示するタイミング", "메시지를 표시하는 시점");
        Add("How long to wait after I stop speaking", "停止说话后等待多久", "話し終わってから待つ時間", "말을 멈춘 후 기다리는 시간");
        Add("Current wait: {0:0.0} s", "当前等待：{0:0.0} 秒", "現在の待ち時間：{0:0.0} 秒", "현재 대기: {0:0.0}초");
        Add("Show translations while I’m still speaking", "我还在说话时也显示翻译", "話している途中にも翻訳を表示", "말하는 중에도 번역 표시");
        Add("On: chat updates as you speak. Off: send only after the pause.", "开启：说话时逐步更新聊天。关闭：停顿后才发送。", "オン：話しながらチャットを更新。オフ：待ち時間の後に送信。", "켬: 말하는 동안 채팅을 갱신합니다. 끔: 대기 후에만 전송합니다.");
        Add("Pause when my VRChat mic is muted", "VRChat 麦克风静音时暂停", "VRChat のマイクがミュート中は一時停止", "VRChat 마이크 음소거 중 일시 중지");
        Add("When off, SayWhat can capture and send translations while your VRChat mic is muted.", "关闭后，即使 VRChat 麦克风已静音，SayWhat 仍可采集语音并发送翻译。", "オフの場合、VRChat のマイクがミュート中でも SayWhat は音声を取得し、翻訳を送信できます。", "끄면 VRChat 마이크가 음소거되어 있어도 SayWhat이 음성을 수집하고 번역을 보낼 수 있습니다.");
        Add("For your voice to appear in chat, turn on OSC in VRChat's Action Menu: OSC → Enabled. You only need to enable it once.", "要让你的语音显示在聊天中，请在 VRChat 动作菜单中开启 OSC：OSC → Enabled。只需开启一次。", "自分の声をチャットに表示するには、VRChat のアクションメニューで OSC → Enabled をオンにしてください。一度だけ有効にすれば使えます。", "내 음성을 채팅에 표시하려면 VRChat 액션 메뉴에서 OSC → Enabled를 켜세요. 한 번만 켜면 됩니다.");
        Add("Optional · live subtitles", "可选功能 · 实时字幕", "追加機能 · ライブ字幕", "선택 기능 · 실시간 자막");
        Add("Read other people’s translated speech in a separate window.", "在独立窗口中阅读他人语音的翻译。", "別のウィンドウで他の人の音声の翻訳を読みます。", "별도 창에서 다른 사람의 음성 번역을 읽습니다.");
        Add("This translator does not support that language.", "此翻译模型不支持该语言。", "この翻訳モデルはその言語に対応していません。", "이 번역 모델은 해당 언어를 지원하지 않습니다.");
        Add("Keep at least one translation language.", "请至少保留一种翻译语言。", "翻訳先の言語を 1 つ以上残してください。", "번역 언어를 하나 이상 남겨 주세요.");
        Add("Remove a language before adding another.", "请先移除一种语言，再添加另一种。", "別の言語を追加するには、先に 1 つ削除してください。", "다른 언어를 추가하려면 먼저 하나를 제거하세요.");
        Add("Click to add or remove a language.", "点击以添加或移除语言。", "クリックで言語を追加・削除します。", "클릭하여 언어를 추가하거나 제거하세요.");
        Add("Windows default microphone", "Windows 默认麦克风", "Windows の既定のマイク", "Windows 기본 마이크");
        Add("Saved microphone · not connected", "已保存的麦克风 · 未连接", "保存済みマイク · 未接続", "저장된 마이크 · 연결되지 않음");
        return values;
    }
}
