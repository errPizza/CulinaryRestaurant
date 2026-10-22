using Android.App;
using Android.Content.PM;
using Android.Views;
using Avalonia.Android;
namespace MesaNexo.Android;
[Activity(Label="Culinary Restaurant",Theme="@style/MesaTheme",MainLauncher=true,Exported=true,WindowSoftInputMode=SoftInput.AdjustResize,ConfigurationChanges=ConfigChanges.Orientation|ConfigChanges.ScreenSize|ConfigChanges.UiMode)]
public class MainActivity:AvaloniaMainActivity{}