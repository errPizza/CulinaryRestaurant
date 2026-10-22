using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MesaNexo.Client;
using MesaNexo.Core;
namespace MesaNexo.Tests;
static class ButtonFeedbackTests
{
    public static async Task Run(string root,List<JsonObject> evidence)
    {
        await using var ui=HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await ui.Dispatch(async()=>{
            void Check(bool ok,string text){if(!ok)throw new Exception(text);evidence.Add(J.O("test",text,"result","passed"));Console.WriteLine("PASS "+text);}
            PrototypeMotion.SuppressAnimations=true;var view=new MainView(new MemoryVault());var window=new Window{Width=1360,Height=900,Content=view};window.Show();
            foreach(var width in new[]{360,1360}){
                window.Width=width;window.UpdateLayout();Dispatcher.UIThread.RunJobs();using(var warmup=window.CaptureRenderedFrame()){}using var frame=window.CaptureRenderedFrame();frame!.Save(Path.Combine(root,"evidence",$"buttons-login-{width}.png"));
                var login=view.GetVisualDescendants().OfType<Button>().Single(b=>Avalonia.Automation.AutomationProperties.GetName(b)=="Entrar a la operación");var face=login.GetVisualDescendants().OfType<Border>().Single(b=>b.Name=="PART_Border");
                Check(!login.ClipToBounds&&!face.ClipToBounds&&face.Bounds.Height>=44,$"rounded button surface and shadow have unclipped space at {width}px");
            }
            PrototypeMotion.SuppressAnimations=false;PrototypeMotion.ReduceMotion=false;var calls=0;
            var button=(Button)typeof(MainView).GetMethod("Button",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(view,["Probar botón",(Func<Task>)(()=>{calls++;return Task.CompletedTask;}),true])!;
            button.Width=300;button.HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Center;button.VerticalAlignment=Avalonia.Layout.VerticalAlignment.Center;
            window.Width=500;window.Height=240;window.Content=new Border{Background=Brush.Parse("#F9FAFC"),Padding=new Thickness(24),Child=button};window.UpdateLayout();Dispatcher.UIThread.RunJobs();using(var frame=window.CaptureRenderedFrame()){}
            var surface=button.GetVisualDescendants().OfType<Border>().Single(b=>b.Name=="PART_Border");
            var center=button.TranslatePoint(new Point(button.Bounds.Width/2,button.Bounds.Height/2),window)!.Value;
            async Task Pump(int count){for(var i=0;i<count;i++){await Task.Delay(30);AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);}}
            double Size()=>surface.RenderTransform?.Value.M11??1;
            var layout=button.Bounds;
            window.MouseMove(center);await Pump(7);
            if(!PrototypeMotion.Reduced)Check(Size()>1.02&&Size()<1.03,"button gently enlarges on actual pointer hover");
            window.MouseDown(center,MouseButton.Left);await Pump(5);
            if(!PrototypeMotion.Reduced)Check(Size()<.98,"native pointer press compresses the button");
            window.MouseUp(center,MouseButton.Left);await Pump(12);Check(calls==1,"animated mouse click executes its action exactly once");
            button.Focus();window.KeyPress(Key.Enter,RawInputModifiers.None,PhysicalKey.Enter,null);window.KeyRelease(Key.Enter,RawInputModifiers.None,PhysicalKey.Enter,null);await Pump(12);Check(calls==2,"keyboard click keeps the same single action behavior");
            button.Focus();window.KeyPress(Key.Space,RawInputModifiers.None,PhysicalKey.Space,null);await Pump(5);window.KeyRelease(Key.Space,RawInputModifiers.None,PhysicalKey.Space,null);var spaceFrames=new List<double>();for(var i=0;i<8;i++){await Pump(1);spaceFrames.Add(Size());}Check(calls==3,"space key activates the button exactly once");if(!PrototypeMotion.Reduced)Check(spaceFrames.Any(x=>x>1.03),"space key release retains the same click pop");await Pump(5);
            if(!PrototypeMotion.Reduced){
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));var samples=new JsonArray();for(var i=0;i<12;i++){await Pump(1);samples.Add(J.O("scale",Size()));}File.WriteAllText(Path.Combine(root,"evidence","button-pop-frames.json"),samples.ToJsonString());
                Check(samples.Objects().Any(s=>s["scale"]!.GetValue<double>()>1.035),"click pop visibly grows beyond the hover scale");
                Check(Math.Abs(Size()-1.025)<.002,"click pop returns smoothly to the hover size");
            }
            Check(button.Bounds==layout,"hover and pop never move surrounding controls");
            PrototypeMotion.ReduceMotion=true;window.MouseMove(new Point(5,5));await Pump(2);Check(Math.Abs(Size()-1)<.001,"reduced motion restores normal button size immediately");
            var before=calls;window.MouseMove(center);window.MouseDown(center,MouseButton.Left);window.MouseUp(center,MouseButton.Left);await Pump(2);Check(calls==before+1&&Math.Abs(Size()-1)<.001,"reduced motion keeps clicks responsive without scaling");
            button.IsEnabled=false;window.MouseDown(center,MouseButton.Left);window.MouseUp(center,MouseButton.Left);Check(calls==before+1,"disabled button performs no action or pop");
            PrototypeMotion.ReduceMotion=false;PrototypeMotion.SuppressAnimations=true;button.IsEnabled=true;window.UpdateLayout();using(var warmup=window.CaptureRenderedFrame()){}using(var frame=window.CaptureRenderedFrame())frame!.Save(Path.Combine(root,"evidence","button-rounded-idle.png"));window.Close();return true;
        },CancellationToken.None);
    }
}
