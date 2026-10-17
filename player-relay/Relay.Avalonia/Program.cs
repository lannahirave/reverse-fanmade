using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;

namespace ReVerse.Relay.Desktop;

internal static class Program
{
    private static readonly TimeSpan InstanceWait = TimeSpan.FromSeconds(5);

    [STAThread]
    public static void Main(string[] args)
    {
        var instanceName = OperatingSystem.IsWindows() ? "Local\\ReVersePlayerRelay" : "ReVersePlayerRelay";
        using var singleInstance = new Mutex(false, instanceName);
        if (!AcquireInstance(singleInstance))
            return;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            singleInstance.ReleaseMutex();
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();

    private static bool AcquireInstance(Mutex singleInstance)
    {
        if (TryAcquire(singleInstance, InstanceWait))
            return true;
        if (!OperatingSystem.IsWindows())
            return false;
        var others = FindOtherInstances();
        if (others.Count == 0)
            return false;
        try
        {
            if (MessageBox(IntPtr.Zero, LoadTranslation().AlreadyRunningMessage, "ReVerse Relay", MessageBoxYesNo | MessageBoxWarning) != IdYes)
                return false;
            foreach (var other in others)
            {
                try
                {
                    if (!other.HasExited)
                    {
                        other.Kill();
                        other.WaitForExit(InstanceWait);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }
        }
        finally
        {
            foreach (var other in others)
                other.Dispose();
        }
        return TryAcquire(singleInstance, InstanceWait);
    }

    private static bool TryAcquire(Mutex singleInstance, TimeSpan timeout)
    {
        try
        {
            return singleInstance.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static List<Process> FindOtherInstances()
    {
        using var current = Process.GetCurrentProcess();
        return Process.GetProcessesByName(current.ProcessName)
            .Where(process =>
            {
                if (process.Id != current.Id)
                    return true;
                process.Dispose();
                return false;
            })
            .ToList();
    }

    private static ITranslation LoadTranslation()
    {
        var localization = new TranslationService();
        try
        {
            localization.SetLanguage(RelaySettings.Load().LanguageCode);
        }
        catch (Exception)
        {
        }
        return localization.Current;
    }

    private const uint MessageBoxYesNo = 0x4;
    private const uint MessageBoxWarning = 0x30;
    private const int IdYes = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
