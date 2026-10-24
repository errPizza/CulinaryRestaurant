using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
namespace MesaNexo.Client;

public sealed class App: Application {
    public static string? StartupProbe { get; set; }
    public static Func<string, Task>? InstallUpdate { get; set; }
    public static IDiscoveryProvider? Discovery { get; set; }
    public static ISecureVault? Vault { get; set; }
    public override void Initialize() { RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light; Styles.Add(new FluentTheme()); Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MesaNexo.Client/")) { Source = new Uri("avares://MesaNexo.Client/PrototypeTheme.axaml") }); }
    public override void OnFrameworkInitializationCompleted() {

        var view = new MainView(Vault ?? throw new InvalidOperationException("Almacén seguro de plataforma no configurado"));
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {

            var window = new Window { Title = Brand.Name, Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://MesaNexo.Client/Assets/symbol.png"))), Width = 1360, Height = 900, MinWidth = 360, MinHeight = 600, Content = view };
            desktop.MainWindow = window;
            if (StartupProbe is string report) window.Opened += (_, _) => Avalonia.Threading.DispatcherTimer.RunOnce(() => { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(report))!); System.IO.File.WriteAllText(report, System.Text.Json.JsonSerializer.Serialize(new { nativeStartup = true, framework = "Avalonia", windowTitle = window.Title, width = window.ClientSize.Width, height = window.ClientSize.Height, platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription })); desktop.Shutdown(); }, TimeSpan.FromSeconds(2));

        } else if (ApplicationLifetime is ISingleViewApplicationLifetime mobile) mobile.MainView = view;
        base.OnFrameworkInitializationCompleted();

    }
}