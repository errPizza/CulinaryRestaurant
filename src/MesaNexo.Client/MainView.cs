using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MesaNexo.Core;
namespace MesaNexo.Client;

public sealed partial class MainView: UserControl {
    static readonly IBrush Graphite = Brush.Parse("#242E3B"), Amber = Brush.Parse("#F2B93B"), Canvas = Brush.Parse("#E7E9ED"), Muted = Brush.Parse("#596575"), Line = Brush.Parse("#CDD3DC");
    readonly ISecureVault vault; readonly Grid shell = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Background = Canvas }; readonly ContentControl content = new(); readonly TextBlock status = new() { Text = "Conexión sin comprobar", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 8), Foreground = Graphite };
    readonly List<Action> clockRefresh = []; readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) }; NativeSession? session; JsonObject? state; JsonObject? user; JsonArray draft = []; string page = "Resumen del turno", visit = "", station = "Todas"; bool busy, polling, narrow; Grid? modal; ConnectionProfile? profile; string error = ""; Control? restoreFocus;
    public MainView(ISecureVault vault) {

        this.vault = vault;
        FontFamily = Brand.InterfaceFont;
        Content = shell;
        Grid.SetRow(content, 1);
        Grid.SetRow(status, 2);
        shell.Children.Add(content);
        shell.Children.Add(status);
        SizeChanged += (_, e) => { var width = e.NewSize.Width; var bucket = width < 381 ? 0 : width < 651 ? 1 : width < 901 ? 2 : width < 1201 ? 3 : width < 1300 ? 4 : width < 1360 ? 5 : width < 1551 ? 6 : 7; if (bucket != layoutBucket) { layoutBucket = bucket; narrow = width <= 650; if (state is not null) Render(); else if (lockedLicense is not null) RenderLicenseLock(lockedLicense); else ShowLogin(); } };
        timer.Tick += async (_, _) => { if (session is null || busy || polling || modal is not null) return; foreach (var refresh in clockRefresh.ToArray()) refresh(); polling = true; try { var next = await session.State(); if (state is null || next.Obj("restaurant").L("revision") != state.Obj("restaurant").L("revision") || !Warnings(next).Select(x => x.S("message")).SequenceEqual(Warnings(state).Select(x => x.S("message")))) { state = next; Render(); } Status(); } catch (Exception e) { status.Text = e.Message; status.Foreground = Brushes.DarkRed; } finally { polling = false; } };
        DetachedFromVisualTree += (_, _) => timer.Stop();
        _ = RestoreMotion();
        ShowLogin();

    }
    // These change with the calendar, not with a commit, so the revision alone would not redraw them.
    static IEnumerable<JsonObject> Warnings(JsonObject state) => new[] { state.Obj("connection").Obj("auditAlert"), state.Obj("restaurant").Obj("license").Obj("warning"), state.Obj("connection").Obj("certificate") }.Concat(state.Obj("connection").A("server").Objects()).Where(x => x.Count > 0);
    async Task RestoreMotion() { try { PrototypeMotion.ReduceMotion = await vault.Read("motion.reduced") == "true"; } catch { } }
    async Task RefreshLive() { if (session is null || busy || polling || modal is not null) return; polling = true; try { state = await session.State(); Render(); Status(); } catch (ApiFailure e) when (e.Status == 423) { await ShowLicenseLock(); } finally { polling = false; } }
    static TextBlock Text(string text, double size = 14, IBrush? color = null, bool bold = false) => new() { Text = text, FontFamily = Brand.InterfaceFont, FontSize = size, Foreground = color ?? Graphite, FontWeight = bold ? (size >= 24 ? FontWeight.Bold : FontWeight.SemiBold) : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
    static StackPanel Stack(params Control[] children) { var s = new StackPanel { Spacing = 12 }; foreach (var c in children) s.Children.Add(c); return s; }
    static WrapPanel Wrap(params Control[] children) { var w = new WrapPanel { Orientation = Orientation.Horizontal }; foreach (var c in children) { c.Margin = new Thickness(0, 0, 8, 8); w.Children.Add(c); } return w; }
    static Border Card(Control child, bool dark = false) => new() { Child = child, Background = dark ? Brush.Parse("#273241") : Brushes.White, Padding = new Thickness(24), CornerRadius = new CornerRadius(13), BoxShadow = BoxShadows.Parse("0 4 0 0 #CBD1D9, 0 12 26 0 #0B202C3A"), BorderBrush = Line, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12) };
    Button Button(string label, Func<Task> action, bool primary = false) { var b = new Button { Content = label, Classes = { "prototype" }, MinHeight = 44, FontSize = 13, FontWeight = FontWeight.SemiBold, FontFamily = Brand.InterfaceFont, Padding = new Thickness(16, 11), Background = primary ? Amber : Brushes.White, Foreground = primary ? Brush.Parse("#29210C") : Brush.Parse("#354256"), CornerRadius = new CornerRadius(11), ClipToBounds = false, BorderBrush = primary ? Brush.Parse("#D9A22A") : Line, BorderThickness = new Thickness(1) }; if (primary) b.Classes.Add("primary"); if (label.EndsWith(" ↗")) b.Content = Horizontal(Text(label[..^2], 13, b.Foreground, true), Brand.Icon("arrow-up-right", b.Foreground, 13)); PrototypeMotion.Button(b); AutomationProperties.SetName(b, label); b.Click += async (_, _) => await Run(action); return b; }
    async Task Run(Func<Task> action) {

        if (busy) return;
        busy = true;
        status.Text = "Procesando en el servidor local…";
        var locked = false;
        try { await action(); error = ""; } catch (ApiFailure e) when (e.Status == 423) { error = ""; locked = true; } catch (Exception e) { error = e.Message; status.Text = (session is not null && await session.HasPending() ? session.OperationState + " · " : "") + error; status.Foreground = Brushes.DarkRed; } finally { busy = false; if (error == "") Status(); }
        // A locked license replaces whatever was on screen with the lock screen.
        if (locked) await ShowLicenseLock();

    }
    void Status() { status.Foreground = Graphite; if (session is null) { status.Text = error == "" ? "HTTPS · confianza local aprovisionada · sin navegador" : error; return; } var connection = state?.Obj("connection"); if (user?.S("role") == "dueno") { status.Text = "Central disponible · Espejo de sucursal · Origen " + DateLabel(state?.Obj("analytics").S("sourceAt") ?? "") + " · No es una conexión LAN operativa"; return; } status.Text = $"Servidor local disponible · {session.OperationState}   ·   {connection?.L("outboxPending") ?? 0} eventos pendientes{connection?.S("outboxAlert") switch { "size" => " (outbox grande: revisar la central)", "age" => " (más de un día sin confirmar)", "blocked" => " (un evento detiene la sincronización: llamar a soporte)", "auth" => " (la central no acepta el certificado)", "held" => " (hay eventos apartados por soporte)", _ => "" }}   ·   Internet: sin comprobar · Central: {connection?.S("central", "unverified")}"; }
    IEnumerable<JsonObject> Items(string key) => state?.A(key + "s").Objects() ?? [];
    JsonObject Item(string kind, string id) => Items(kind).FirstOrDefault(x => x.S("id") == id) ?? new JsonObject();
    string EntityName(string kind, string id) => id == "" && kind == "table" ? "Servicio rápido" : Item(kind, id).S("name", id);
    static string Money(long cents) => (cents / 100m).ToString("C", CultureInfo.GetCultureInfo("es-MX"));
    TextBox Input(string caption, string initial = "", bool password = false) { var input = new TextBox { Text = initial, MinHeight = 44, PlaceholderText = caption, PasswordChar = password ? '●' : '\0', HorizontalAlignment = HorizontalAlignment.Stretch }; AutomationProperties.SetName(input, caption); return input; }
    // Money above the threshold: the second manager types their own PIN in the same form.
    Field[] ApprovalFields => [new("approverId", "Gerente que autoriza", "select", "", [new("", "Sin segunda autorización"), .. Items("user").Where(x => x.S("role") == "manager" && x.B("active") && x.B("hasPin") && x.S("id") != user?.S("id")).Select(x => new Option(x.S("id"), x.S("name")))]), new("passcode", "PIN del gerente que autoriza", "password")];
    async Task Authorized(string type, JsonObject p) {

        var approver = p.S("approverId");
        var passcode = p.S("passcode");
        p.Remove("approverId");
        p.Remove("passcode");
        if (approver == "") { await Act(type, p); return; }
        var reply = await Act("authorize.pin", J.O("approverId", approver, "passcode", passcode, "action", J.O("type", type, "payload", p)));
        if (reply["ok"] is not null && !reply.B("ok")) throw new InvalidOperationException(reply.S("error"));

    }
    async Task<JsonObject> Act(string type, JsonObject p) { var r = await session!.Execute(type, p); state = await session.State(); Render(); return r.Obj("result"); }
    async Task OpenVisit(string id) { visit = id; draft = await session!.LoadDraft(id); page = "Mesas y cuentas"; Render(); }
    static void Preparation(StackPanel panel, JsonObject unit, bool dark = false) {

        var tags = Wrap();
        if (unit.S("cooking") != "") { var cook = Badge("held", unit.S("cooking")); cook.Background = Brush.Parse(dark ? "#E6C57F" : "#354357"); if (cook.Child is TextBlock text) text.Foreground = dark ? Brush.Parse("#36290E") : Brushes.White; tags.Children.Add(cook); }
        foreach (var extra in unit.A("extras")) { var label = extra is JsonObject item ? item.S("name") : extra?.ToString() ?? ""; tags.Children.Add(new Border { Background = Brush.Parse(dark ? "#344155" : "#F3F5F8"), BorderBrush = Brush.Parse(dark ? "#6A7A91" : "#CDD3DC"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5), Child = Text("+ " + label, 11, dark ? Brushes.White : Brush.Parse("#344256"), true) }); }
        if (tags.Children.Count > 0) panel.Children.Add(tags);
        if (unit.S("notes") != "") panel.Children.Add(new Border { Background = Brush.Parse(dark ? "#493C23" : "#FFF0C8"), BorderBrush = Brush.Parse(dark ? "#F2B93B" : "#C78F22"), BorderThickness = new Thickness(3, 0, 0, 0), CornerRadius = new CornerRadius(0, 8, 8, 0), Padding = new Thickness(12, 9), Child = Stack(Text("Nota del pedido", 10, Brush.Parse(dark ? "#EACB8D" : "#775823")), Text(unit.S("notes"), 13, Brush.Parse(dark ? "#FFDDA0" : "#503913"), true)) });

    }
    Task AccountOptions() {

        var v = Item("visit", visit);
        var rows = Stack();
        rows.Children.Add(Button("Transferir mesero", () => Form("Transferir responsabilidad y avisos", [new("waiterId", "Nuevo responsable", "select", "", Options("user", x => x.S("role") == "mesero" && x.B("active")))], async p => { p["id"] = visit; await Act("visit.transfer", p); })));
        rows.Children.Add(Button("Cambiar de mesa", () => Form("Mover cuenta", [new("tableId", "Mesa destino", "select", "", Options("table", x => x.S("status") == "free"))], async p => { p["id"] = visit; await Act("visit.move", p); })));
        rows.Children.Add(Button("Dividir por unidades / persona", () => Form("Selecciona las unidades que pasarán a otra cuenta", [new("unitIds", "Unidades", "multi", "", Items("unit").Where(x => x.S("visitId") == visit).Select(x => new Option(x.S("id"), "Persona " + x.L("person") + " · " + x.S("name") + " · " + Label(x.S("status")))).ToArray())], async p => { p["id"] = visit; await Act("visit.split", p); })));
        rows.Children.Add(Button("Unir con otra cuenta", () => Form("Unir sin duplicar cocina", [new("otherId", "Cuenta origen", "select", "", Items("visit").Where(x => x.S("status") == "open" && x.S("id") != visit).Select(x => new Option(x.S("id"), EntityName("table", x.S("tableId")) + " · " + Money(x.L("balance")))).ToArray())], async p => { p["id"] = visit; await Act("visit.merge", p); })));
        rows.Children.Add(Button("Solicitar descuento", () => Form("Solicitar autorización de descuento", [new("kind", "Tipo", "select", "percent", [new("percent", "Porcentaje"), new("amount", "Importe en centavos")]), new("value", "Valor", "decimal", "10"), new("reason", "Motivo")], async p => { var d = J.O("kind", p.S("kind"), "value", p["value"]); if (p.S("kind") == "amount") d["value"] = checked((long)p["value"]!.GetValue<decimal>()); await Act("request.create", J.O("visitId", visit, "type", "discount", "reason", p.S("reason"), "discount", d)); })));
        if (state!.Obj("restaurant").A("modules").Strings().Contains("customerRequests")) rows.Children.Add(Button("Simular solicitud de cliente", () => Form("Simulación interna de atención", [new("type", "Solicitud", "select", "bill", [new("bill", "Pedir cuenta"), new("attention", "Pedir atención")])], async p => { p["visitId"] = visit; await Act("customer.simulate", p); })));
        AddPosOptions(rows, v);
        ShowModal("Opciones de cuenta", rows);
        return Task.CompletedTask;

    }
    Control Notices() {

        var body = Stack(SectionHeading("Pendientes de tu servicio", "Preparaciones parciales, comandas completas y atención"));
        body.Spacing = 0;
        foreach (var n in Items("notification").Reverse()) {

            var info = Stack(Text(n.S("message"), 14, Graphite, true), Text(DateLabel(n.S("at")) + " · " + (n.B("attended") ? "Atendido" : "Pendiente"), 11, Muted));
            var actions = Wrap(Button("Ver cuenta", () => OpenVisit(n.S("visitId"))));
            if (!n.B("attended")) actions.Children.Add(Button("Atender", () => Act("notification.attend", J.O("id", n.S("id"))), true));
            var row = TwoColumns(Horizontal(new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = Avalonia.Media.Brush.Parse("#FFF0C4"), Child = Brand.Icon("bell", Avalonia.Media.Brush.Parse("#765000"), 18) }, info), actions, 2, 650);
            body.Children.Add(new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 16), Child = row });

        }
        if (!Items("notification").Any()) body.Children.Add(EmptyState("Bandeja al día", "Los avisos aparecerán cuando cocina prepare productos."));
        return Card(body);

    }
}
