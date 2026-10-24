using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using MesaNexo.Client;
namespace MesaNexo.Android;

[Application]
public class NativeApplication(nint handle, JniHandleOwnership transfer): AvaloniaAndroidApplication<App>(handle, transfer) {
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) { App.Vault = new AndroidVault(this); App.Discovery = new AndroidDiscovery(this); return base.CustomizeAppBuilder(builder).WithInterFont(); }
}