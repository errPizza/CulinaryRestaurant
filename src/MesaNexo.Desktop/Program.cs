using Avalonia;
using MesaNexo.Client;
namespace MesaNexo.Desktop;

internal static class Program {
    [STAThread] public static void Main(string[] args) { App.StartupProbe = args.SkipWhile(x => x != "--verify-startup").Skip(1).FirstOrDefault(); App.Vault = new DesktopVault(); if (OperatingSystem.IsWindows()) App.InstallUpdate = InstallUpdate; if (OperatingSystem.IsLinux()) App.Discovery = new LinuxDiscovery(); BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
    static Task InstallUpdate(string package) {

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(package) { UseShellExecute = true });
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime life) life.Shutdown();
        return Task.CompletedTask;

    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}