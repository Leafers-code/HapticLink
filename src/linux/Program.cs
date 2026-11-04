using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Styling;
using Avalonia.Markup.Xaml.Styling;
using HapticLink.Core;

namespace HapticLink;

internal static class Program
{
    internal static string PipeName => "HapticLink-" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Environment.UserName + Path.GetFullPath(Settings.Dir))))[..20];

    [STAThread]
    public static int Main(string[] args)
    {
        using var singleton = new Mutex(true, PipeName, out var first);
        if (!first || args.Contains("--panic") || args.Contains("--toggle-arm") || args.Contains("--toggle-overlay") || args.Contains("--overlay-interact"))
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipe.Connect(2000);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                writer.WriteLine(args.Contains("--panic") ? "panic" : args.Contains("--toggle-arm") ? "arm" : args.Contains("--toggle-overlay") ? "overlay" : args.Contains("--overlay-interact") ? "overlay-interact" : "show");
                return 0;
            }
            catch { Console.Error.WriteLine("No running HapticLink instance for this user/config folder."); return 1; }
        }
        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
        return 0;
    }
}

public sealed class App : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://HapticLink/")) { Source = new Uri("avares://HapticLink/ClassicTheme.axaml") });
        MenuLayout.Install();
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
