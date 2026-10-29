using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows;
using Microsoft.Win32.SafeHandles;

namespace IncomingTranslator;

// Inno normally supplies the original user's token. If Setup was explicitly started
// elevated, never silently run the dashboard (or write a profile) as administrator.
// This narrow broker can only reopen this exact executable, idle, in the same user's
// already-logged-in Windows shell. It never accepts an arbitrary command or --start-all.
internal static class InstallerUiLaunch
{
    internal static bool Prepare(string[] arguments, out string[] remaining, out int exitCode)
    {
        remaining=arguments;exitCode=0;
        if(arguments.Length==0 || arguments[0]!="--installer-launch-ui") return false;
        string? language=Parse(arguments);
        if(language is null) { exitCode=2; return true; }
        remaining=["--ui-language",language];
        try
        {
            using var identity=WindowsIdentity.GetCurrent();
            if(!IsElevated(identity.AccessToken)) return false;
            LaunchWithShellToken(language,identity.User?.Value ?? "");
            return true;
        }
        catch(Exception error) when(error is Win32Exception or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            System.Diagnostics.Debug.WriteLine("Installer UI launch: "+error);
            UiText.Language=language;
            MessageBox.Show(UiText.T("SayWhat? was installed, but couldn't open automatically. Open it from the Start menu. No translation tools were started."), AppBrand.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
            exitCode=1;return true;
        }
    }

    internal static string? Parse(IReadOnlyList<string> arguments) => arguments.Count==3 && arguments[0]=="--installer-launch-ui" && arguments[1]=="--ui-language" && arguments[2] is "en" or "zh-CN" or "ja" or "ko" ? arguments[2] : null;
    internal static bool CanUseShellToken(string callerSid,string shellSid,bool shellElevated,int callerSession,int shellSession,string shellExecutable,string windowsDirectory) =>
        callerSid.Length>0 && string.Equals(callerSid,shellSid,StringComparison.Ordinal) && !shellElevated && callerSession==shellSession &&
        string.Equals(Path.GetFullPath(shellExecutable),Path.Combine(Path.GetFullPath(windowsDirectory),"explorer.exe"),StringComparison.OrdinalIgnoreCase);

    private static void LaunchWithShellToken(string language,string callerSid)
    {
        IntPtr window=GetShellWindow();
        if(window==IntPtr.Zero)throw new InvalidOperationException("No interactive Windows shell is available.");
        GetWindowThreadProcessId(window,out uint processId);
        using var shell=Process.GetProcessById(checked((int)processId));
        using var process=OpenProcess(0x1000,false,processId);
        if(process.IsInvalid || !OpenProcessToken(process,0x000B,out var token))throw new Win32Exception(Marshal.GetLastWin32Error());
        using(token)
        {
            using var identity=new WindowsIdentity(token.DangerousGetHandle());
            var path=new StringBuilder(32768);int length=path.Capacity;
            if(!QueryFullProcessImageName(process,0,path,ref length))throw new Win32Exception(Marshal.GetLastWin32Error());
            using var current=Process.GetCurrentProcess();
            if(!CanUseShellToken(callerSid,identity.User?.Value ?? "",IsElevated(token),current.SessionId,shell.SessionId,path.ToString(),Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
                throw new InvalidOperationException("A same-user, unelevated Windows shell could not be verified.");
            if(!DuplicateTokenEx(token,0x000B,IntPtr.Zero,2,1,out var primary))throw new Win32Exception(Marshal.GetLastWin32Error());
            using(primary)
            {
                if(!CreateEnvironmentBlock(out IntPtr environment,primary,false))throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    string executable=Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
                    if(Path.GetFileName(executable)!="SayWhat.exe" || executable.Contains('"') || executable.StartsWith("\\\\",StringComparison.Ordinal))
                        throw new InvalidOperationException("Only the installed SayWhat executable can be opened.");
                    var command=new StringBuilder('"'+executable+"\" --ui-language "+language);
                    var startup=new StartupInfo { cb=Marshal.SizeOf<StartupInfo>() };
                    if(!CreateProcessWithTokenW(primary,1,executable,command,0x400,environment,Path.GetDirectoryName(executable)!,ref startup,out var created))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    CloseHandle(created.Thread);CloseHandle(created.Process);
                }
                finally { DestroyEnvironmentBlock(environment); }
            }
        }
    }
    private static bool IsElevated(SafeAccessTokenHandle token)
    {
        if(!GetTokenInformation(token,20,out int value,sizeof(int),out _))throw new Win32Exception(Marshal.GetLastWin32Error());
        return value!=0;
    }
    internal static int RunOfflineChecks()
    {
        int checks=0;void Check(bool value){if(!value)throw new InvalidOperationException("Installer idle-launch policy check failed.");checks++;}
        foreach(string language in new[]{"en","zh-CN","ja","ko"})
        {
            Check(Parse(["--installer-launch-ui","--ui-language",language])==language);
            Check(LocalTalkLaunchOptions.Parse(["--ui-language",language]) is
                { StartAll: false, DownloadModel: false, TranslationModelId: null, Speech: null, SpeakerModelPath: null });
        }
        Check(Parse(["--installer-launch-ui","--start-all"])is null);
        Check(Parse(["--installer-launch-ui","--ui-language","ja","--start-all"])is null);
        Check(Parse(["--installer-launch-ui","--ui-language","arbitrary"])is null);
        Check(CanUseShellToken("S-1-example","S-1-example",false,1,1,@"C:\Windows\explorer.exe",@"C:\Windows"));
        Check(!CanUseShellToken("S-1-example","S-1-other",false,1,1,@"C:\Windows\explorer.exe",@"C:\Windows"));
        Check(!CanUseShellToken("S-1-example","S-1-example",true,1,1,@"C:\Windows\explorer.exe",@"C:\Windows"));
        Check(!CanUseShellToken("S-1-example","S-1-example",false,1,2,@"C:\Windows\explorer.exe",@"C:\Windows"));
        Check(!CanUseShellToken("S-1-example","S-1-example",false,1,1,@"C:\Temp\explorer.exe",@"C:\Windows"));
        return checks;
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct StartupInfo
    {
        public int cb;public string? reserved,desktop,title;public int x,y,width,height,xCount,yCount,fill,flags;
        public short show,reservedLength;public IntPtr reservedData,input,output,error;
    }
    [StructLayout(LayoutKind.Sequential)]private struct ProcessInformation {public IntPtr Process,Thread;public uint ProcessId,ThreadId;}
    [DllImport("user32.dll")]private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,uint process);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool OpenProcessToken(SafeProcessHandle process,uint access,out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool GetTokenInformation(SafeAccessTokenHandle token,int informationClass,out int information,int length,out int returned);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryFullProcessImageName(SafeProcessHandle process,int flags,StringBuilder name,ref int length);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token,uint access,IntPtr attributes,int level,int type,out SafeAccessTokenHandle primary);
    [DllImport("userenv.dll",SetLastError=true)]private static extern bool CreateEnvironmentBlock(out IntPtr environment,SafeAccessTokenHandle token,bool inherit);
    [DllImport("userenv.dll")]private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token,int logon,string app,StringBuilder command,int flags,IntPtr environment,string directory,ref StartupInfo startup,out ProcessInformation created);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
}
