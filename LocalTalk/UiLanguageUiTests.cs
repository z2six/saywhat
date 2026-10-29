using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

internal static class UiLanguageUiTests
{
    internal static int Run(MainWindow window,string directory)
    {
        int checks=0;void Check(bool condition,string message){if(!condition)throw new InvalidOperationException("Interface language check: "+message);checks++;}
        string previousLanguage=UiText.Language;
        string saved=File.ReadAllText(AppPaths.SettingsPath);
        string ownConfig=File.ReadAllText(AppPaths.FastConfigPath),otherConfig=File.ReadAllText(AppPaths.FoxTransConfigPath);
        string incomingLog=Path.Combine(AppPaths.Root,"incoming-translator.log");
        byte[] previousIncomingLog=File.Exists(incomingLog)?File.ReadAllBytes(incomingLog):[];
        try
        {
            var original=SettingsStore.Load();
            Check(original.Copy().UiLanguage==original.UiLanguage,"copy preserves interface language");
            var document=JsonNode.Parse(saved)!.AsObject();document["FutureUnknownPreference"]="kept";
            File.WriteAllText(AppPaths.SettingsPath,document.ToJsonString());
            using(var heldOwn=File.Open(AppPaths.FastConfigPath,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var heldOther=File.Open(AppPaths.FoxTransConfigPath,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                SettingsStore.SaveUiLanguagePreference("ko");
                var changed=JsonNode.Parse(File.ReadAllText(AppPaths.SettingsPath))!.AsObject();
                var expected=document.DeepClone().AsObject();expected[nameof(AppSettings.UiLanguage)]="ko";
                Check(JsonNode.DeepEquals(expected,changed),"picker saves only UiLanguage, preserving unknown and translation settings");
                Check(File.ReadAllText(AppPaths.FastConfigPath)==ownConfig && File.ReadAllText(AppPaths.FoxTransConfigPath)==otherConfig,"language picker never rewrites microphone/translation configuration");
            }
            SettingsStore.Save(original.Copy());
            Check(SettingsStore.Load().UiLanguage=="ko","stale dashboard save cannot undo newer interface language");
            string fixture=Path.Combine(directory,"language-default-fixtures");Directory.CreateDirectory(fixture);
            Check(UiLanguagePreference.ReadInstallDefault(fixture)=="en","no installation preference defaults to English");
            foreach(string language in new[]{"en","zh-CN","ja","ko"})
            {
                File.WriteAllText(Path.Combine(fixture,"ui-language.json"),"{\"uiLanguage\":\""+language+"\"}");
                Check(UiLanguagePreference.ReadInstallDefault(fixture)==language,"tracked installer default supports "+language);
            }
            foreach(string invalid in new[]{"{bad","{\"uiLanguage\":17}","{\"uiLanguage\":\"cloud\"}","[]","null","17","\"ja\""})
            { File.WriteAllText(Path.Combine(fixture,"ui-language.json"),invalid);Check(UiLanguagePreference.ReadInstallDefault(fixture)=="en","invalid installer default fails safely"); }
            var literal=UiLocalization.Literal(new TextBlock{Text="Home"});
            var editable=new TextBox{Text="Settings"};
            var frame=new StackPanel();frame.Children.Add(literal);frame.Children.Add(editable);
            UiText.Language="zh-CN";UiLocalization.ApplyTree(frame);
            Check(literal.Text=="Home" && editable.Text=="Settings","matching user names/spoken text are never translated by traversal");
            var selection=new ComboBox{ItemsSource=new[]{"Chinese","Japanese"},SelectedItem="Chinese"};UiLocalization.TranslateOptionLabels(selection);
            Check((string)selection.SelectedItem=="Chinese","localized option display leaves stable target-language key unchanged");
            var settings=(AppSettings)typeof(MainWindow).GetField("_settings",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            string ownTarget=settings.OwnVoiceTargetLanguage,incomingTarget=settings.IncomingTargetLanguage,ownModel=settings.OwnManagedModelId,incomingModel=settings.IncomingManagedModelId;
            foreach(string language in new[]{"en","zh-CN","ja","ko"})
            {
                window.ChangeInterfaceLanguage(language);
                Check(SetupProgressPresentation.LocalizeMessage("Compiling the speech engine · 18 of 31 build tasks")==
                    UiText.Format("Compiling the speech engine · {0} of {1} build tasks","18","31"),"build task counts stay meaningful in "+language);
                Check(SetupProgressPresentation.LocalizeMessage("Installing GPU build tools · 43%")==
                    UiText.Format("Installing {0} · {1}%",UiText.T("GPU build tools"),"43"),"measured package progress is localized in "+language);
                Check(SetupProgressPresentation.LocalizeMessage("Downloading build tools · fetching file 12 · about 3.1 GB in total")==
                    UiText.Format("Downloading {0} · fetching file {1}",UiText.T("build tools"),"12")+UiText.Format(" · about {0} in total","3.1 GB"),"download counts and sizes are preserved in "+language);
                Check(SetupProgressPresentation.Duration(TimeSpan.FromSeconds(3661))==UiText.Format("{0}h {1}m",1,1),"elapsed setup time is localized in "+language);
                Check(SetupProgressPresentation.LocalizeMessage("[private synthetic raw process output]")=="[private synthetic raw process output]","progress localization never interprets arbitrary process output");
                foreach(string page in MainWindow.PrimaryPages)
                {
                    window.Navigate(page);
                    var content=(FrameworkElement)window.Content;
                    content.Measure(new Size(1160,820));content.Arrange(new Rect(0,0,1160,820));content.UpdateLayout();
                    var title=(TextBlock)typeof(MainWindow).GetField("_pageTitle",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
                    Check(title.Text==UiText.T(MainWindow.PageDisplayName(page)),"page heading is localized without changing routing: "+language+"/"+page);
                    var image=new RenderTargetBitmap(1160,820,96,96,PixelFormats.Pbgra32);image.Render(content);
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
                    using var file=File.Create(Path.Combine(directory,"interface-"+language+"-"+page.ToLowerInvariant()+".png"));encoder.Save(file);
                }
                var picker=(ComboBox)typeof(MainWindow).GetField("_interfaceLanguagePicker",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
                Check(picker.Items.Count==4 && picker.Name=="InterfaceLanguagePicker","four native-language options have a dedicated sidebar control");
                Check(settings.OwnVoiceTargetLanguage==ownTarget && settings.IncomingTargetLanguage==incomingTarget && settings.OwnManagedModelId==ownModel && settings.IncomingManagedModelId==incomingModel,"interface language does not switch models or translation targets");
            }
            Check((File.Exists(incomingLog)?File.ReadAllBytes(incomingLog):[]).SequenceEqual(previousIncomingLog),"localized previews did not add capture or inference activity to prior synthetic test logs");
            var runtime=(RuntimeController)typeof(MainWindow).GetField("_runtime",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            Check(!runtime.OwnRunning && !runtime.IncomingRunning,"all localized previews kept both live translation tools stopped");
            return checks;
        }
        finally
        {
            File.WriteAllText(AppPaths.SettingsPath,saved);
            var settings=(AppSettings)typeof(MainWindow).GetField("_settings",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            settings.UiLanguage=previousLanguage;UiText.Language=previousLanguage;
            window.Navigate("Setup");
        }
    }
}
