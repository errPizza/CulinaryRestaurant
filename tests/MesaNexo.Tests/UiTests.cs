using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Themes.Fluent;
using MesaNexo.Client;
using MesaNexo.Core;
namespace MesaNexo.Tests;

public sealed class UiTestApplication: Application { public override void Initialize() { Styles.Add(new FluentTheme()); Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MesaNexo.Client/")) { Source = new Uri("avares://MesaNexo.Client/PrototypeTheme.axaml") }); RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light; } }
public static class UiTests {
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<UiTestApplication>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    public static async Task Run(string root, JsonObject state, NativeSession client, ISecureVault vault, List<JsonObject> evidence) {

        PrototypeMotion.SuppressAnimations = true;
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await ui.Dispatch(async () => {

            var view = new MainView(vault);
            var window = new Window { Content = view, Width = 1440, Height = 1000 };
            window.Show();
            void Set(string key, object value) => typeof(MainView).GetField(key, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, value);
            void Call(string name) => typeof(MainView).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
            void Check(bool ok, string name) { if (!ok) throw new Exception(name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
            foreach (var width in new[] { 360, 834, 1440 }) { window.Width = width; Call("ShowLogin"); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(root, "evidence", $"native-login-{width}.png")); Check(view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == Brand.Name), $"Culinary Restaurant branding visible at {width}px"); }
            Set("state", state);
            Set("user", state.Obj("user"));
            Set("session", client);
            foreach (var width in new[] { 360, 834, 1440 }) {

                window.Width = width;
                window.Height = 1000;
                foreach (var page in new[] { "Resumen del turno", "Mesas y cuentas", "Cocina en vivo", "Bandeja de pendientes", "Catálogo", "Caja", "Reportes", "Equipo y dispositivos", "Restaurante", "Reservaciones", "Evaluaciones", "Promociones", "Soporte", "Historial", "Inventario", "Delivery y clientes", "Arqueo ciego", "Impresión", "Fiscal" }) {

                    Set("page", page);
                    Call("Render");
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    var scrolls = view.GetVisualDescendants().OfType<ScrollViewer>().ToArray();
                    Check(scrolls.All(s => s.Extent.Width <= s.Viewport.Width + 2), $"native layout {page} at {width}px fits horizontal viewport");
                    if (width == 1440 && page == "Mesas y cuentas") {

                        var header = ((Grid)typeof(MainView).GetField("shell", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!).Children.OfType<Border>().First(b => Grid.GetRow(b) == 0);
                        var headerParts = header.GetVisualDescendants().OfType<Control>().Where(x => x is TextBlock or Image).Select(x => J.O("text", (x as TextBlock)?.Text, "type", x.GetType().Name, "bounds", x.Bounds.ToString(), "visible", x.IsVisible, "opacity", x.Opacity, "parents", string.Join(";", x.GetVisualAncestors().Select(a => a.GetType().Name + ":" + a.Bounds + ":" + a.Opacity)))).ToArray();
                        File.WriteAllText(Path.Combine(root, "evidence", "native-header-layout.json"), J.N(headerParts)!.ToJsonString());
                        Check(header.Bounds.Height < 115, "desktop header retains one row at 1440px like graphite prototype");
                        var fonts = view.GetVisualDescendants().OfType<TextBlock>().Where(x => x.Text is "Mesas y cuentas" or "01" or "$0.00").Select(x => J.O("text", x.Text, "declared", x.FontFamily.Name, "resolved", new Typeface(x.FontFamily, x.FontStyle, x.FontWeight).GlyphTypeface.FamilyName, "weight", (int)x.FontWeight)).ToArray();
                        File.WriteAllText(Path.Combine(root, "evidence", "native-fonts.json"), J.N(fonts)!.ToJsonString());
                        Check(fonts.Length > 0 && fonts.All(x => !x.S("resolved").Contains("Times", StringComparison.OrdinalIgnoreCase)), "headings and amounts retain interface typeface inside button templates");

                    }
                    if (true) { using (var warmup = window.CaptureRenderedFrame()) { } Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); if (frame is null) throw new Exception("Native rendering did not produce a frame"); frame.Save(Path.Combine(root, "evidence", $"native-{page.Replace(' ', '-')}-{width}.png")); }

                }

            }
            foreach (var width in new[] { 1280, 1360 }) {

                window.Width = width;
                Set("page", "Mesas y cuentas");
                Call("Render");
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                var header = ((Grid)typeof(MainView).GetField("shell", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!).Children.OfType<Border>().First(b => Grid.GetRow(b) == 0);
                Check(header.GetVisualDescendants().OfType<Button>().All(b => { var at = b.TranslatePoint(new Point(0, 0), view); return at is Point point && point.X >= -1 && point.X + b.Bounds.Width <= width + 1; }), $"desktop header controls stay inside {width}px viewport");
                if (width == 1360) { Check(header.Bounds.Height < 115, "default Windows window retains compact desktop header"); using (var warmup = window.CaptureRenderedFrame()) { } Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(root, "evidence", "native-Mesas-y-cuentas-1360.png")); }

            }
            foreach (var width in new[] { 360, 834, 1440 }) { window.Width = width; Set("page", "Mesas y cuentas"); Set("visit", state.A("visits").Objects().First(v => v.S("status") == "open").S("id")); Call("Render"); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); Check(view.GetVisualDescendants().OfType<ScrollViewer>().All(s => s.Extent.Width <= s.Viewport.Width + 2), $"account drawer at {width}px fits horizontal viewport"); using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(root, "evidence", $"native-account-{width}.png")); Call("ProductDialog"); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); Check(view.GetVisualDescendants().OfType<ScrollViewer>().All(s => s.Extent.Width <= s.Viewport.Width + 2), $"product customization at {width}px fits horizontal viewport"); using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(root, "evidence", $"native-order-product-{width}.png")); Call("CloseModal"); Set("visit", ""); }
            Set("page", "Catálogo");
            Call("Render");
            window.UpdateLayout();
            var button = view.GetVisualDescendants().OfType<Button>().Single(x => Avalonia.Automation.AutomationProperties.GetName(x) == "Nuevo producto");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var modal = (Grid?)typeof(MainView).GetField("modal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view);
            Check(modal is not null, "native product editor opens as focused dialog");
            for (var i = 0; i < 30; i++) { window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null); window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null); Dispatcher.UIThread.RunJobs(); var focused = window.FocusManager?.GetFocusedElement() as Visual; Check(focused is not null && focused.GetVisualAncestors().Contains(modal!), "dialog retains keyboard focus step " + i); }
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Check(typeof(MainView).GetField("modal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view) is null, "Escape dismisses dialog");
            typeof(MainView).GetMethod("EditProduct", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [J.O("name", "Tax fixture", "taxBps", 1600)]);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Check(view.GetVisualDescendants().OfType<TextBox>().Single(x => Avalonia.Automation.AutomationProperties.GetName(x) == "Impuesto incluido %").Text == "16", "native product editor retains configured historical tax rate for editing");
            Call("CloseModal");
            var target = new Border { Width = 120, Height = 60, Background = Brushes.Gold };
            window.Content = target;
            window.UpdateLayout();
            PrototypeMotion.SuppressAnimations = false;
            PrototypeMotion.ReduceMotion = true;
            PrototypeMotion.Enter(target);
            Check(target.Opacity == 1 && target.RenderTransform is null, "reduced motion keeps content visible immediately without transforms");
            PrototypeMotion.ReduceMotion = false;
            if (!PrototypeMotion.Reduced) { PrototypeMotion.Enter(target, 400, 20, 10); Check(target.Opacity == 0, "native entrance starts with reference opacity before first frame"); var samples = new JsonArray(); for (var tick = 0; tick < 4; tick++) { await Task.Delay(30); AvaloniaHeadlessPlatform.ForceRenderTimerTick(1); samples.Add(J.O("opacity", target.Opacity, "y", target.RenderTransform!.Value.M32, "x", target.RenderTransform.Value.M31)); } File.WriteAllText(Path.Combine(root, "evidence", "native-motion.json"), samples.ToJsonString()); Check(samples.Objects().Any(x => x["opacity"]!.GetValue<double>() > 0 && x["opacity"]!.GetValue<double>() < 1 && x["y"]!.GetValue<double>() > 0 && x["y"]!.GetValue<double>() < 10), "native entrance visibly interpolates opacity and translation between frames"); for (var tick = 0; tick < 14; tick++) { await Task.Delay(35); Dispatcher.UIThread.RunJobs(); using var frame = window.CaptureRenderedFrame(); } Check(target.Opacity > .99 && target.RenderTransform is not null && Math.Abs(target.RenderTransform.Value.M32) < .01 && Math.Abs(target.RenderTransform.Value.M31) < .01, "native entrance finishes visible at final layout position"); }
            PrototypeMotion.SuppressAnimations = true;
            window.Close();
            return true;

        }, CancellationToken.None);

    }
    public static async Task RunProvider(string root, JsonObject state, List<JsonObject> evidence) {

        File.WriteAllText(Path.Combine(root, ".lab", "ui-provider.json"), state.ToJsonString());
        await using var ui = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await ui.Dispatch(() => {

            var view = new MainView(new MemoryVault());
            typeof(MainView).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, state);
            typeof(MainView).GetField("user", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, state.Obj("user"));
            var window = new Window { Content = view, Width = 1440, Height = 1000 };
            window.Show();
            foreach (var width in new[] { 360, 834, 1440 }) {

                window.Width = width;
                foreach (var page in new[] { "Portafolio", "Planes y módulos", "Licencias y servicios", "Actualizaciones", "Soporte", "Respaldos", "Historial" }) {

                    typeof(MainView).GetField("page", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, page);
                    typeof(MainView).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    PrototypeMotion.SuppressAnimations = true;
                    var ok = view.GetVisualDescendants().OfType<ScrollViewer>().All(s => s.Extent.Width <= s.Viewport.Width + 2);
                    if (!ok) throw new Exception("Provider layout overflow: " + page);
                    evidence.Add(J.O("test", $"provider layout {page} at {width}px fits horizontal viewport", "result", "passed"));
                    using (var frame = window.CaptureRenderedFrame()) { frame!.Save(Path.Combine(root, "evidence", $"native-provider-{page.Replace((char)32, (char)45)}-{width}.png")); if (page == "Portafolio") frame.Save(Path.Combine(root, "evidence", $"native-provider-{width}.png")); }

                }

            }
            window.Close();
            return true;

        }, CancellationToken.None);

    }
}