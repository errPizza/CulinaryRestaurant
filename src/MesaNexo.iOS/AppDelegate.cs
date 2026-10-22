using Foundation;
using Avalonia;
using Avalonia.iOS;
using MesaNexo.Client;
namespace MesaNexo.iOS;
[Register("AppDelegate")]
public class AppDelegate:AvaloniaAppDelegate<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder){App.Vault=new IosVault();App.Discovery=new IosDiscovery();return base.CustomizeAppBuilder(builder).WithInterFont();}
}