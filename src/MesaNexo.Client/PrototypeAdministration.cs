using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MesaNexo.Core;
namespace MesaNexo.Client;

public sealed partial class MainView {
    static string DateLabel(string raw) => DateTimeOffset.TryParse(raw, out var d) ? d.ToLocalTime().ToString("d MMM yyyy, h:mm tt", CultureInfo.GetCultureInfo("es-MX")) : "—";
    Control Table(string[] columns, IEnumerable<Control[]> rows) {

        var root = Stack();
        root.Spacing = 0;
        if (ViewWidth > 650) { var head = new UniformGrid { Columns = columns.Length }; foreach (var name in columns) head.Children.Add(new Border { Padding = new Thickness(12, 14), Child = Text(name, 11, Brush.Parse("#536173"), true) }); root.Children.Add(new Border { Background = Brush.Parse("#EEF1F5"), Child = head }); }
        foreach (var cells in rows) { if (ViewWidth <= 650) { var body = Stack(); body.Spacing = 12; for (var i = 0; i < columns.Length; i++) body.Children.Add(Stack(Text(columns[i], 11, Muted), cells[i])); root.Children.Add(new Border { BorderBrush = Brush.Parse("#E0E5EC"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 16), Child = body }); } else { var row = new UniformGrid { Columns = columns.Length }; foreach (var cell in cells) row.Children.Add(new Border { Padding = new Thickness(12, 15), Child = cell }); root.Children.Add(new Border { BorderBrush = Brush.Parse("#E0E5EC"), BorderThickness = new Thickness(0, 0, 0, 1), Child = row }); } }
        return root;

    }
    Control Stats(params (string Label, string Value, string Note, string Icon)[] items) {

        var grid = new UniformGrid { Columns = ViewWidth <= 900 ? 2 : 4 };
        foreach (var item in items) { var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") }; title.Children.Add(Text(item.Label, 12, Muted)); if (ViewWidth > 380) { var icon = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Background = Brush.Parse("#EDF0F4"), Child = Brand.Icon(item.Icon, null, 18) }; Grid.SetColumn(icon, 1); title.Children.Add(icon); } var figure = Text(item.Value, ViewWidth <= 650 ? 28 : 32, Raised, true); PrototypeMotion.CountUp(figure, "stats." + page + "." + item.Label); var card = Card(Stack(title, figure, Text(item.Note, 11, Muted))); card.MinHeight = 142; card.Padding = new Thickness(ViewWidth <= 650 ? 14 : 22, 20); card.Margin = new Thickness(0, 0, 18, 18); grid.Children.Add(card); }
        return grid;

    }
    Control Cash() {

        var cash = Items("cash").ToArray();
        var opened = cash.FirstOrDefault(c => c.S("status") == "open");
        var actions = Wrap(Button("Arqueo ciego", () => { page = "Arqueo ciego"; Render(); return Task.CompletedTask; }));
        var threshold = state!.Obj("restaurant").L("dualControlAmount", 100000);
        actions.Children.Add(Button("Devolver un pago", () => Form("Devolver un pago · desde " + Money(threshold) + " autoriza otro gerente", [new("id", "Pago", "select", "", Items("payment").Where(x => Item("visit", x.S("visitId")).S("status") == "closed").Reverse().Select(x => new Option(x.S("id"), Money(x.L("amount") + x.L("tip")) + " · " + x.S("method") + " · " + DateLabel(x.S("at")))).ToArray()), new("amount", "Importe MXN (0 devuelve todo)", "money", "0"), new("reason", "Motivo"), .. ApprovalFields], p => { if (p.L("amount") == 0) p.Remove("amount"); return Authorized("payment.refund", p); })));
        // Refunds a provider left to a person: money only counts as returned once a manager confirms it from the portal.
        var manualRefunds = Items("refund").Where(r => { var charge = Item("terminaltxn", r.S("providerRecord")); if (charge.Count == 0) charge = Item("paylink", r.S("providerRecord")); return r.S("route") == "provider" && charge.S("status") == "refund-manual" && (charge.S("refundId") is var current && (current == "" || current == r.S("id"))); }).ToArray();
        if (manualRefunds.Length > 0) actions.Children.Add(Button("Confirmar devolución (" + manualRefunds.Length + ")", () => Form("Confirmar devolución hecha en el portal del proveedor", [new("id", "Devolución", "select", "", manualRefunds.Select(x => new Option(x.S("id"), Money(x.L("amount")) + " · " + DateLabel(x.S("at")) + " · " + x.S("reason"))).ToArray()), new("reference", "Referencia del portal")], p => Act("refund.settle", p))));
        actions.Children.Add(Button("Doble aprobación", () => Form("Importe desde el que hacen falta dos gerentes", [new("amount", "Importe MXN", "money", (threshold / 100m).ToString(CultureInfo.InvariantCulture)), .. ApprovalFields], p => Authorized("dual.configure", p))));
        if (opened is null) actions.Children.Add(Button("Abrir caja", () => Form("Abrir caja", [new("initial", "Fondo inicial MXN", "money", "500")], p => Act("cash.open", p)), true));
        else { actions.Children.Add(Button("+ Movimiento", () => Form("Registrar movimiento", [new("type", "Movimiento", "select", "income", [new("income", "Ingreso"), new("expense", "Gasto"), new("withdrawal", "Retiro")]), new("amount", "Importe MXN", "money", "0"), new("reason", "Motivo")], async p => { p["cashId"] = opened.S("id"); await Act("cash.movement", p); }))); actions.Children.Add(Button("Arqueo y cierre", () => Form("Cerrar caja", [new("counted", "Efectivo contado MXN", "money", "0")], async p => { p["id"] = opened.S("id"); await Act("cash.close", p); }), true)); }
        var payments = Items("payment").Where(p => p.S("cashId") == opened?.S("id")).ToArray();
        var stats = Stats(("Estado de caja", opened is null ? "Cerrada" : "Abierta", opened is null ? "Abre antes de registrar pagos" : DateLabel(opened.S("openedAt")), "wallet"), ("Fondo inicial", Money(opened?.L("initial") ?? 0), "Efectivo al abrir", "book"), ("Pagos registrados", Money(payments.Sum(p => p.L("amount"))), "Todas las formas de pago", "chart"));
        var body = Stack(SectionHeading("Control de caja", "Propinas en efectivo incluidas en el arqueo", actions), Table(["Apertura", "Cierre", "Fondo", "Esperado", "Contado", "Diferencia"], cash.Select(c => new Control[] { Text(DateLabel(c.S("openedAt")), 12), Text(DateLabel(c.S("closedAt")), 12), Text(Money(c.L("initial")), 12), Text(c.ContainsKey("expected") ? Money(c.L("expected")) : "—", 12), Text(c.ContainsKey("counted") ? Money(c.L("counted")) : "—", 12), Text(c.ContainsKey("difference") ? Money(c.L("difference")) : "—", 12) })), Text("Movimientos de caja", 17, Graphite, true), Table(["Fecha", "Tipo", "Importe", "Motivo"], Items("movement").Select(m => new Control[] { Text(DateLabel(m.S("at")), 12), Text(m.S("type") switch { "income" => "Ingreso", "expense" => "Gasto", _ => "Retiro" }, 12), Text(Money(m.L("amount")), 12), Text(m.S("reason"), 12) })));
        return Stack(stats, Card(body));

    }
    Control Team() {

        var people = Card(Stack(SectionHeading("Equipo del restaurante", "La desactivación y revocación se comprueban en cada petición.", Button("+ Empleado", () => EditUser(new()), true)), Table(["Empleado", "Usuario", "Rol", "Estado", "Acción"], Items("user").Select(u => new Control[] { Text(u.S("name"), 13, Graphite, true), Text(u.S("username"), 13), Text(Label(u.S("role")), 13), Text(u.B("active") ? "Activo" : "Desactivado", 13), Button("Editar permisos", () => EditUser(u)) }))));
        var devices = Card(Stack(SectionHeading("Dispositivos e instalaciones", action: Button("Generar código de alta", async () => { var code = J.Id(); await Act("device.enrollment", J.O("code", code)); ShowModal("Código de un solo uso", Stack(Text(code, 21, Graphite, true), Text("Entrega este código por un canal administrativo. Se consume al registrar un dispositivo."))); })), Table(["Dispositivo", "Versión real informada", "Estado", "Última conexión", "Acción"], Items("device").Select(d => new Control[] { Text(d.S("name"), 13), Text(d.S("version", "No informada"), 12), Text(d.B("authorized") ? "Autorizado" : "Bloqueado", 13), Text(DateLabel(d.S("lastSeen")), 12), Button(d.B("authorized") ? "Revocar" : "Autorizar", () => Act("installation.authorize", J.O("id", d.S("id"), "authorized", !d.B("authorized")))) }))));
        var sessions = Card(Stack(SectionHeading("Sesiones activas"), Table(["Empleado", "Apertura", "Vencimiento", "Estado", "Acción"], Items("session").Select(s => new Control[] { Text(EntityName("user", s.S("userId")), 13), Text(DateLabel(s.S("at")), 12), Text(DateLabel(s.S("expires")), 12), Text(s.B("revoked") ? "Revocada" : "Activa", 13), s.B("revoked") ? Text("—") : Button("Revocar", () => Act("session.revoke", J.O("id", s.S("id")))) }))));
        return Stack(people, devices, sessions);

    }
    Control Settings() {

        var r = state!.Obj("restaurant");
        var edit = Button("Editar configuración", () => Form("Configurar restaurante", [From(r, "name", "Nombre"), From(r, "color", "Color #RRGGBB"), From(r, "contact", "Contacto"), From(r, "delayMinutes", "Umbral de demora (minutos)", "int"), From(r, "discountLimit", "Límite de descuento %", "int"), new("stations", "Estaciones separadas por línea", "lines", string.Join('\n', r.A("stations").Strings()))], p => Act("settings.save", p)), true);
        var details = new UniformGrid { Columns = ViewWidth <= 650 ? 1 : 3 };
        foreach (var pair in new[] { ("Restaurante", r.S("name")), ("Contacto", r.S("contact")), ("Demora", r.L("delayMinutes") + " min"), ("Descuento máximo", r.L("discountLimit") + "%"), ("Estaciones", string.Join(", ", r.A("stations").Strings())), ("Revisión vigente", r.L("revision").ToString()) }) details.Children.Add(new Border { Padding = new Thickness(0, 12), Child = Stack(Text(pair.Item1, 11, Muted), Text(pair.Item2, 14, Graphite, true)) });
        return Stack(Card(Stack(SectionHeading("Identidad y reglas del restaurante", action: edit), details)), Card(Stack(SectionHeading("Mesas, zonas y capacidad", action: Button("+ Mesa", () => EditTable(new()), true)), Table(["Mesa", "Zona", "Capacidad", "Sucursal", "Acción"], Items("table").Select(t => new Control[] { Text(t.S("name"), 13, Graphite, true), Text(t.S("zone"), 13), Text(t.L("capacity").ToString(), 13), Text(r.S("siteId"), 12), Button("Editar", () => EditTable(t)) })))), Card(Stack(SectionHeading("Integraciones preparadas", "No procesan transacciones externas"), Text("Tarjetas y transferencias: registro manual del pago recibido. Facturación y proveedor bancario: requieren contratar y conectar un servicio externo.", 13, Muted))));

    }
    Control Reservations() => Card(Stack(SectionHeading("Agenda de reservaciones", "Las solicitudes simuladas empiezan pendientes. Bloques de 2 horas.", Button("+ Reservación / solicitud simulada", () => EditReservation(new()), true)), Table(["Cliente", "Fecha y hora", "Mesa / personas", "Estado", "Acción"], Items("reservation").Select(r => new Control[] { Stack(Text(r.S("name"), 13, Graphite, true), Text(r.S("contact") + (r.B("simulated") ? " · Simulada" : ""), 11, Muted)), Text(DateLabel(r.S("start")), 12), Text(EntityName("table", r.S("tableId")) + " · " + r.L("people"), 12), Badge(r.S("status")), Button("Administrar", () => EditReservation(r)) }))));
    Control Promotions() => Card(Stack(SectionHeading("Promociones por horario y combos", "Se aplican al confirmar comandas, según horario de Ciudad de México.", Button("+ Promoción", () => EditPromotion(new()), true)), Table(["Promoción", "Horario", "Descuento", "Productos", "Acción"], Items("promotion").Select(p => new Control[] { Text(p.S("name") + (p.B("combo") ? " · Combo" : ""), 13, Graphite, true), Text(p.L("startHour") + ":00–" + p.L("endHour") + ":00", 13), Text(p.L("percent") + "% " + (p.B("active") ? "activa" : "inactiva"), 13), Text(string.Join(", ", p.A("productIds").Strings().Select(id => EntityName("product", id))), 12), Button("Editar", () => EditPromotion(p)) }))));
    Control Reviews() {

        var rows = Stack(SectionHeading("Evaluaciones privadas", "Una por visita cerrada; ningún comentario se publica.", Button("Simular evaluación", () => Form("Simular evaluación de cliente", [new("visitId", "Visita cerrada", "select", "", Items("visit").Where(v => v.S("status") == "closed").Select(v => new Option(v.S("id"), EntityName("table", v.S("tableId")))).ToArray()), new("food", "Comida", "int", "5"), new("service", "Atención", "int", "5"), new("overall", "Experiencia", "int", "5"), new("comment", "Comentario", "multiline")], p => Act("review.simulate", p)), true)));
        foreach (var r in Items("review")) rows.Children.Add(new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 18), Child = Stack(Text(new string('★', (int)r.L("overall")) + new string('☆', Math.Max(0, 5 - (int)r.L("overall"))), 18, Brush.Parse("#976A0D"), true), Text("Comida " + r.L("food") + "/5 · Atención " + r.L("service") + "/5 · General " + r.L("overall") + "/5", 11, Muted), Text(r.S("comment", "Sin comentario"), 13), Text(DateLabel(r.S("at")) + " · Simulación privada", 11, Muted)) });
        if (!Items("review").Any()) rows.Children.Add(EmptyState("Las experiencias quedan aquí", "Cierra una visita para registrar su evaluación de prueba."));
        return Card(rows);

    }
    Control Reports() {

        var payments = Items("payment").ToArray();
        var units = Items("unit").ToArray();
        var times = units.Where(u => DateTimeOffset.TryParse(u.S("readyAt"), out _) && DateTimeOffset.TryParse(u.S("receivedAt"), out _)).Select(u => (DateTimeOffset.Parse(u.S("readyAt")) - DateTimeOffset.Parse(u.S("receivedAt"))).TotalMinutes).ToArray();
        var stats = Stats(("Ventas registradas", Money(payments.Sum(p => p.L("amount"))), "Pagos recibidos", "chart"), ("Propinas", Money(payments.Sum(p => p.L("tip"))), "Separadas de las ventas", "wallet"), ("Descuentos", Money(Items("visit").Sum(v => v.L("discount"))), "Autorizados", "book"), ("Preparación promedio", (times.Length > 0 ? times.Average() : 0).ToString("F1", CultureInfo.GetCultureInfo("es-MX")) + " min", "Recepción → listo", "food"));
        var products = Card(Stack(SectionHeading("Productos y cancelaciones", action: Button("Historial completo / Exportar CSV", () => { ShowModal("Historial de cuentas", AccountHistory()); return Task.CompletedTask; })), Table(["Producto", "Unidades", "Canceladas", "No disponibles", "Consumo registrado"], Items("product").Select(p => { var rows = units.Where(u => u.S("productId") == p.S("id")).ToArray(); return new Control[] { Text(p.S("name"), 13), Text(rows.Length.ToString(), 13), Text(rows.Count(u => u.S("status") == "cancelled").ToString(), 13), Text(rows.Count(u => u.S("status") == "unavailable").ToString(), 13), Text(Money(rows.Where(u => u.S("status") is not "cancelled" and not "unavailable").Sum(u => u.L("price"))), 13) }; }))));
        return Stack(stats, products, Card(Stack(SectionHeading("Historial administrativo"), Audit())));

    }
}