using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IncomingTranslator;

// User artwork is read locally and never copied into installation/source payloads.
// Decode once at a bounded display size; weak WPF bindings update every open surface.
internal static class ThemeBackground
{
    internal const long MaximumImageBytes = 20_000_000;
    private sealed class ImageState : INotifyPropertyChanged
    {
        public ImageSource? Source { get; set; }
        public double Opacity { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Refresh() { PropertyChanged?.Invoke(this,new(nameof(Source)));PropertyChanged?.Invoke(this,new(nameof(Opacity))); }
    }
    private static readonly ImageState State = new();
    private static string _cachedPath="";
    private static BitmapSource? _cachedImage;
    internal static FrameworkElement CreateLayer()
    {
        var image=new Image { Name="ThemeBackgroundImage",Stretch=Stretch.UniformToFill,IsHitTestVisible=false,Focusable=false,ClipToBounds=true };
        BindingOperations.SetBinding(image,Image.SourceProperty,new Binding(nameof(ImageState.Source)){Source=State,Mode=BindingMode.OneWay});
        BindingOperations.SetBinding(image,UIElement.OpacityProperty,new Binding(nameof(ImageState.Opacity)){Source=State,Mode=BindingMode.OneWay});
        RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.LowQuality);
        return image;
    }

    // False means optional artwork was unavailable. The solid background still works.
    internal static bool Apply(AppSettings settings,bool reload=false)
    {
        bool available=true;
        State.Source=null;
        State.Opacity=double.IsFinite(settings.CustomThemeImageOpacity)?Math.Clamp(settings.CustomThemeImageOpacity,0,.6):.16;
        if(settings.UiThemeId=="custom" && !string.IsNullOrEmpty(settings.CustomThemeImagePath))
        {
            try
            {
                if(reload || _cachedImage is null || _cachedPath!=settings.CustomThemeImagePath)
                {
                    _cachedImage=ReadImage(settings.CustomThemeImagePath);
                    _cachedPath=settings.CustomThemeImagePath;
                }
                State.Source=_cachedImage;
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.IO.FileFormatException or InvalidOperationException)
            { available=false;_cachedPath="";_cachedImage=null; }
        }
        else { _cachedPath="";_cachedImage=null; }
        State.Refresh();
        return available;
    }

    internal static BitmapSource ReadImage(string path)
    {
        if(string.IsNullOrWhiteSpace(path) || path.Length>32767 || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\",StringComparison.Ordinal) || path.Contains('"') ||
            !System.Text.RegularExpressions.Regex.IsMatch(path,@"^[A-Za-z]:[\\/]"))
            throw new InvalidDataException("A background image must be a file on a local Windows drive.");
        string resolved=Path.GetFullPath(path);
        string extension=Path.GetExtension(resolved).ToLowerInvariant();
        if(extension is not (".png" or ".jpg" or ".jpeg"))throw new InvalidDataException("Choose a PNG or JPEG image.");
        // Do not follow a shortcut/junction into a share or another unexpected location.
        for(string? ancestor=resolved;ancestor is not null;ancestor=Path.GetDirectoryName(ancestor))
            if((File.GetAttributes(ancestor)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked image locations are not supported.");
        using var stream=new FileStream(resolved,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length<8 || stream.Length>MaximumImageBytes)throw new InvalidDataException("The image must be smaller than 20 MB.");
        Span<byte> signature=stackalloc byte[8];stream.ReadExactly(signature);stream.Position=0;
        bool png=signature.SequenceEqual(new byte[]{137,80,78,71,13,10,26,10});
        bool jpeg=signature[0]==255 && signature[1]==216 && signature[2]==255;
        if((extension==".png" && !png)||(extension!=".png" && !jpeg))throw new InvalidDataException("The image file type does not match its contents.");
        var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);
        var frame=decoder.Frames[0];
        if(frame.PixelWidth<=0 || frame.PixelHeight<=0 || (long)frame.PixelWidth*frame.PixelHeight>40_000_000 || frame.PixelWidth>16384 || frame.PixelHeight>16384)
            throw new InvalidDataException("The image dimensions are too large.");
        stream.Position=0;
        var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;
        double scale=Math.Min(1,Math.Min(1920d/frame.PixelWidth,1080d/frame.PixelHeight));
        bitmap.StreamSource=stream;bitmap.DecodePixelWidth=Math.Max(1,(int)Math.Floor(frame.PixelWidth*scale));
        bitmap.DecodePixelHeight=Math.Max(1,(int)Math.Floor(frame.PixelHeight*scale));bitmap.EndInit();bitmap.Freeze();
        return bitmap;
    }
}
