using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using MesaNexo.Core;
namespace MesaNexo.Client;

public sealed partial class MainView {
    void AddPosOptions(StackPanel rows, JsonObject v) {

        if (v.S("status") == "closed" && user!.S("role") == "manager") rows.Children.Add(Button("Reabrir cuenta cerrada", () => Form("Reapertura sin reversión automática de stock ni pagos", [new("reason", "Motivo obligatorio")], p => Act("visit.reopen", WithId(p, visit)))));
        rows.Children.Add(Button("Solicitar cortesía", () => Form("Cortesía con autorización de gerente", [new("reason", "Motivo obligatorio")], async p => { p["visitId"] = visit; p["type"] = "comp"; await Act("request.create", p); })));
        rows.Children.Add(Button("Dividir por asiento", () => Form("Separar una persona", [new("person", "Persona/asiento", "int", "1")], p => Act("visit.split.person", WithId(p, visit)))));
        rows.Children.Add(Button("Repartir en N partes iguales", () => Form("Subfolios financieros", [new("parts", "Número de partes", "int", "2")], p => Act("visit.divide", WithId(p, visit)))));
        rows.Children.Add(Button("Retirar subfolios sin pagos", () => Act("visit.undivide", J.O("id", visit))));
        rows.Children.Add(Button(v.B("printed") ? "Cuenta emitida · bloqueada" : "Emitir cuenta y preparar impresión", () => Act("visit.issueBill", J.O("id", visit))));
        if (user!.S("role") == "manager" && v.B("printed")) rows.Children.Add(Button("Autorizar reapertura de cuenta impresa", () => Form("Reapertura autorizada", [new("reason", "Motivo obligatorio")], p => Act("visit.unlock", WithId(p, visit)))));

    }
    static JsonObject WithId(JsonObject p, string id) { p["id"] = id; return p; }
    Control BlindCash() {

        var list = Stack(Text("Captura tu conteo. Los totales esperados sólo están disponibles para revisión autorizada.", 13, Muted));
        var open = Items("cash").FirstOrDefault(c => c.S("status") == "open");
        if (open is null) list.Children.Add(Button("Abrir turno ciego", () => Form("Turno de caja", user!.S("role") == "manager" ? [new("initial", "Fondo inicial MXN", "money", "0"), new("operatorId", "Cajero responsable", "select", "", Options("user", x => x.S("role") == "cajero" && x.B("active")))] : [new("initial", "Fondo inicial MXN", "money", "0")], p => Act("cash.blind.open", p)), true));
        else if (user!.S("role") == "cajero")
            list.Children.Add(Button("Contar y confirmar cierre", () => {

                var fields = Restaurant.Denominations.Select(d => new Field("d" + d, "Cantidad de " + Money(d), "int", "0")).ToList();
                fields.Add(new("card", "Total contado tarjeta MXN", "money", "0"));
                fields.Add(new("transfer", "Total contado transferencia MXN", "money", "0"));
                return Form("Arqueo ciego · cierre definitivo", fields.ToArray(), async p => { var counts = new JsonArray(); foreach (var d in Restaurant.Denominations) counts.Add(J.O("value", d, "count", p.L("d" + d))); await Act("cash.blind.close", J.O("id", open.S("id"), "denominations", counts, "card", p.L("card"), "transfer", p.L("transfer"))); });

            }, true));
        else list.Children.Add(Text("El cajero asignado debe confirmar su conteo desde su propia sesión.", 14));
        foreach (var c in Items("cash").Where(c => c.B("blind"))) list.Children.Add(Card(Stack(Text("Turno " + c.S("id")[..8], 17, Graphite, true), Badge(c.S("status")), Text("Apertura " + DateLabel(c.S("openedAt")), 12, Muted))));
        if (user!.S("role") == "manager") list.Children.Add(Button("Registrar ajuste sobre cierre", () => Form("Ajuste autorizado; conserva el cierre original", [new("id", "Cierre", "select", "", Options("cash", x => x.S("status") == "closed")), new("method", "Medio", "select", "cash", [new("cash", "Efectivo"), new("card", "Tarjeta"), new("transfer", "Transferencia")]), new("direction", "Ajuste", "select", "increase", [new("increase", "Aumentar"), new("decrease", "Disminuir")]), new("amount", "Importe MXN", "money", "0"), new("reason", "Motivo")], p => Act("cash.adjust", p))));
        return Card(list);

    }
    Control InventoryPage() {

        var actions = Wrap(
            Button("+ Almacén", () => Form("Almacén", [new("name", "Nombre"), new("costMethod", "Método de costo", "select", "average", [new("average", "Promedio ponderado"), new("fifo", "PEPS")])], p => Act("inventory.warehouse", p))),
            Button("+ Insumo", () => Form("Insumo", [new("name", "Nombre"), new("unit", "Unidad base", "select", "g", [new("g", "Gramos"), new("ml", "Mililitros"), new("unit", "Unidades")])], p => Act("inventory.ingredient", p))),
            Button("Política y almacén de venta", () => Form("Reserva al enviar · consumo al liquidar", [new("warehouseId", "Almacén de venta", "select", "", Options("warehouse"))], async p => { p["policy"] = "reserve-on-send-consume-on-close"; await Act("inventory.configure", p); })),
            Button("+ Receta / versión", () => RecipeForm(new())),
            Button("Vincular receta a producto", () => Form("Próximas unidades usarán esta versión", [new("productId", "Producto", "select", "", Options("product")), new("recipeId", "Receta", "select", "", Options("recipe"))], p => Act("recipe.bind", p))),
            Button("Entrada", () => StockForm("stock.receive")), Button("Merma", () => StockForm("stock.waste")),
            Button("Traspasar", () => Form("Entrega de almacén; otra persona deberá recibir", [new("fromWarehouseId", "Origen", "select", "", Options("warehouse")), new("toWarehouseId", "Destino", "select", "", Options("warehouse")), new("ingredientId", "Insumo", "select", "", Options("ingredient")), new("quantity", "Cantidad", "decimal", "1"), new("unit", "Unidad", "select", "base", UnitOptions()), new("reason", "Motivo")], p => Act("transfer.create", p))),
            Button("Semáforos KDS", () => Form("Tiempos de preparación", [new("name", "Estación", "select", "", state!.Obj("restaurant").A("stations").Strings().Select(x => new Option(x, x)).ToArray()), new("yellowMinutes", "Amarillo desde minutos", "int", "7"), new("redMinutes", "Rojo desde minutos", "int", "15")], p => Act("station.configure", p))));
        var list = Stack(SectionHeading("Existencias y recetas", "Cantidades en unidad base. Reserva al enviar; cancelar libera sólo lo no preparado. Consumo al liquidar.", actions), Table(["Almacén", "Insumo", "Existencia", "Reservado", "Valor"], Items("stock").Select(x => new Control[] { Text(EntityName("warehouse", x.S("warehouseId"))), Text(EntityName("ingredient", x.S("ingredientId"))), Text((x.L("quantity") / 1000m).ToString(CultureInfo.InvariantCulture)), Text((x.L("reserved") / 1000m).ToString(CultureInfo.InvariantCulture)), Text(Money(x.L("value"))) })));
        foreach (var recipe in Items("recipe")) list.Children.Add(Card(Stack(Text(recipe.S("name") + " · versión " + recipe.L("version"), 17, Graphite, true), Button("Nueva versión", () => RecipeForm(recipe)))));
        foreach (var t in Items("transfer").Where(x => x.S("status") == "sender-approved")) list.Children.Add(Card(Stack(Text(EntityName("ingredient", t.S("ingredientId")) + " · " + (t.L("quantity") / 1000m) + " · " + EntityName("warehouse", t.S("fromWarehouseId")) + " → " + EntityName("warehouse", t.S("toWarehouseId"))), Wrap(Button("Confirmar recepción identificada", () => Act("transfer.receive", J.O("id", t.S("id")))), Button("Cancelar con motivo", () => Form("Cancelar traspaso", [new("reason", "Motivo")], p => Act("transfer.cancel", WithId(p, t.S("id")))))))));
        list.Children.Add(Text("Trabajos de consumo: " + Items("stockjob").Count(x => x.S("status") == "pending") + " pendientes · " + Items("stockjob").Count(x => x.S("status") == "done") + " completos", 13, Muted));
        list.Children.Add(Button("Devolución física de un consumo", () => Form("Devolver consumo comprobado", [new("movementId", "Consumo", "select", "", Items("stockmovement").Where(x => x.S("type") == "sale").Select(x => new Option(x.S("id"), EntityName("ingredient", x.S("ingredientId")) + " · " + (-x.L("quantity") / 1000m) + " · " + DateLabel(x.S("at")))).ToArray()), new("quantity", "Cantidad (vacío para devolver todo)"), new("reason", "Motivo y comprobación física")], p => Act("stock.return", p))));
        return list;

    }
    Option[] UnitOptions() => [new("base", "Unidad base del insumo"), new("kg", "Kilogramos"), new("g", "Gramos"), new("mg", "Miligramos"), new("l", "Litros"), new("ml", "Mililitros"), new("unit", "Unidades"), new("dozen", "Docenas")];
    Task StockForm(string type) => Form(type == "stock.receive" ? "Entrada con costo total" : "Registrar desperdicio", [new("warehouseId", "Almacén", "select", "", Options("warehouse")), new("ingredientId", "Insumo", "select", "", Options("ingredient")), new("quantity", "Cantidad", "decimal", "1"), new("unit", "Unidad", "select", "base", UnitOptions()), type == "stock.receive" ? new("cost", "Costo total MXN", "money", "0") : new("reason", "Motivo de merma"), new("reference", "Referencia de entrada")], p => Act(type, p));
    Task RecipeForm(JsonObject recipe) => Form("Receta · nueva versión histórica", [From(recipe, "name", "Nombre"), new("yield", "Rendimiento en porciones", "decimal", "1"), new("linesText", "Un renglón por insumo: Nombre | cantidad | unidad. Subreceta: @Nombre | porciones | unit", "multiline")], async p => {

        var lines = new JsonArray();
        foreach (var line in p.S("linesText").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { var cells = line.Split('|', StringSplitOptions.TrimEntries); Rules.Need(cells.Length == 3, "Cada renglón requiere Nombre | cantidad | unidad"); var sub = cells[0].StartsWith('@'); var name = sub ? cells[0][1..] : cells[0]; var matches = Items(sub ? "recipe" : "ingredient").Where(x => x.S("name") == name).ToArray(); Rules.Need(matches.Length == 1, "Nombre inexistente o ambiguo: " + name); lines.Add(J.O(sub ? "recipeId" : "ingredientId", matches[0].S("id"), "quantity", cells[1], "unit", cells[2])); }
        p.Remove("linesText");
        p["lines"] = lines;
        if (recipe.S("id") != "") p["id"] = recipe.S("id");
        await Act("recipe.save", p);

    });
    Control DeliveryPage() {

        var list = Stack(SectionHeading("Pedidos y clientes", "La asignación al repartidor no registra un pago."), Wrap(
            Button("+ Cliente", () => Form("Cliente", [new("name", "Nombre"), new("phone", "Teléfono"), new("notes", "Notas", "multiline")], p => Act("customer.save", p))),
            Button("+ Dirección", () => Form("Dirección y coordenadas manuales", [new("customerId", "Cliente", "select", "", Options("customer")), new("label", "Etiqueta"), new("text", "Dirección completa", "multiline"), new("lat", "Latitud", "decimal", "0"), new("lon", "Longitud", "decimal", "0")], p => Act("address.save", p))),
            Button("Nuevo delivery / pick-up", () => Form("Pedido local", [new("customerId", "Cliente", "select", "", Options("customer")), new("mode", "Servicio", "select", "pickup", [new("pickup", "Recoger"), new("delivery", "Entrega")]), new("addressId", "Dirección de entrega", "select", "", Items("address").Select(x => new Option(x.S("id"), EntityName("customer", x.S("customerId")) + " · " + x.S("label"))).ToArray())], async p => { var d = await Act("delivery.create", p); await OpenVisit(d.S("visitId")); }), true)));
        if (user!.S("role") == "manager") list.Children.Add(Wrap(Button("+ Repartidor", () => Form("Repartidor", [new("name", "Nombre")], p => Act("courier.save", p))), Button("+ Zona y tarifa", () => Form("Polígono local", [new("name", "Nombre"), new("fee", "Tarifa MXN", "money", "0"), new("priority", "Prioridad (menor primero)", "int", "0"), new("vertices", "Un vértice por renglón: latitud,longitud", "multiline")], async p => { var vertices = new JsonArray(); foreach (var line in p.S("vertices").Split('\n', StringSplitOptions.RemoveEmptyEntries)) { var c = line.Split(',', StringSplitOptions.TrimEntries); Rules.Need(c.Length == 2, "Vértice: latitud,longitud"); vertices.Add(J.O("lat", c[0], "lon", c[1])); } p.Remove("vertices"); p["polygon"] = vertices; await Act("zone.save", p); }))));
        if (user!.S("role") == "manager") {

            list.Children.Add(Button("Configurar recepción de pedidos web", () => Form("Identidad central de esta sucursal", [new("tenantId", "Empresa", "text", state!.Obj("restaurant").S("tenantId")), new("branchId", "Sucursal central", "text", state.Obj("restaurant").S("branchId")), new("waiterId", "Responsable local", "select", "", Options("user", x => x.S("role") == "mesero" && x.B("active"))), new("enabled", "Habilitar propuestas firmadas", "bool", "true")], p => Act("commerce.configure", p))));
            foreach (var proposal in Items("cloudproposal").Where(x => x.S("status") == "pending-review")) { var payload = proposal.Obj("command").Obj("payload"); list.Children.Add(Card(Stack(Text("Pedido web · pendiente de tu aceptación", 18, Graphite, true), Text(payload.S("name") + " · " + Money(payload.L("quotedTotal")), 14), Text(string.Join(" · ", payload.A("items").Objects().Select(x => x.L("quantity") + " × " + EntityName("product", x.S("productId")))), 13), Button("Aceptar o rechazar propuesta", () => Form("Decisión local autorizada", [new("accept", "Aceptar después de revisar disponibilidad", "bool", "false"), new("reason", "Motivo de la decisión")], p => Act("commerce.decide", WithId(p, proposal.S("id")))))))); }

        }
        foreach (var d in Items("delivery").Reverse()) {

            var v = Item("visit", d.S("visitId"));
            var actions = Wrap(Button("Cuenta", () => OpenVisit(d.S("visitId"))));
            if (d.S("mode") == "pickup" && d.S("status") == "accepted-local") actions.Children.Add(Button("Confirmar recogida", () => Act("delivery.pickup", J.O("id", d.S("id")))));
            if (user.S("role") == "manager" && d.S("status") == "accepted-local" && d.S("mode") == "delivery") actions.Children.Add(Button("Asignar repartidor", () => Form("Responsabilidad pendiente", [new("courierId", "Repartidor", "select", "", Options("courier", x => x.B("active")))], p => Act("delivery.assign", WithId(p, d.S("id"))))));
            if (d.S("status") == "assigned") actions.Children.Add(Button("Confirmar salida", () => Act("delivery.dispatch", J.O("id", d.S("id")))));
            if (user.S("role") == "manager" && d.S("status") == "out-for-delivery") actions.Children.Add(Button("Conciliar cobro comprobado", () => Form("Efectivo o comprobante revisado", [new("amount", "Importe MXN", "money", ((d.L("responsibility") - d.L("reconciled")) / 100m).ToString(CultureInfo.InvariantCulture)), new("method", "Medio", "select", "cash", [new("cash", "Efectivo contado"), new("card", "Voucher revisado manualmente"), new("transfer", "Transferencia revisada manualmente")]), new("reference", "Referencia y comprobación")], p => Act("delivery.reconcile", WithId(p, d.S("id"))))));
            list.Children.Add(Card(Stack(Text(EntityName("customer", d.S("customerId")) + " · " + (d.S("mode") == "pickup" ? "Recoger" : "Entrega"), 18, Graphite, true), Badge(d.S("status")), Text(d.Obj("addressSnapshot").S("text"), 12, Muted), Text("Tarifa " + Money(d.L("fee")) + " · Pendiente repartidor " + Money(d.L("responsibility") - d.L("reconciled")) + " · Saldo " + Money(v.L("balance")), 13), actions)));

        }
        foreach (var c in Items("customer")) list.Children.Add(Card(Stack(Text(c.S("name") + " · " + c.S("phone"), 15, Graphite, true), Text("Historial local: " + Items("visit").Count(v => v.S("customerId") == c.S("id")) + " pedidos", 12, Muted), Text(string.Join(" · ", Items("address").Where(a => a.S("customerId") == c.S("id")).Select(a => a.S("label") + ": " + a.S("text"))), 12, Muted))));
        return list;

    }
    Control PrintJobsPage() {

        var list = Stack(Text("Los trabajos físicos se concilian aparte de los cobros. Un intento reclamado queda incierto hasta recibir evidencia del driver.", 13, Muted), Button("Solicitar apertura de cajonera", () => Form("Cajonera del driver configurado", [new("reason", "Motivo")], p => Act("drawer.request", p))));
        foreach (var j in Items("printjob").Reverse()) {

            var actions = Wrap();
            if (new[] { "uncertain", "submitted" }.Contains(j.S("status"))) actions.Children.Add(Button("Conciliar resultado físico", () => Form("Revisión del dispositivo", [new("status", "Resultado", "select", "uncertain", [new("uncertain", "No comprobado"), new("confirmed", "Comprobado"), new("failed", "Fallo comprobado")]), new("evidence", "Evidencia del driver o revisión física")], async p => { p["attemptId"] = j.S("attemptId"); await Act("print.resolve", WithId(p, j.S("id"))); })));
            if (new[] { "failed", "uncertain" }.Contains(j.S("status"))) actions.Children.Add(Button("Crear reimpresión autorizada", () => Form("Puede producir una segunda copia física", [new("reason", "Motivo de reimpresión")], p => Act("print.retry", WithId(p, j.S("id"))))));
            list.Children.Add(Card(Stack(Text((j.S("type") == "drawer" ? "Cajonera" : "Cuenta") + " · " + j.S("id")[..8], 17, Graphite, true), Badge(j.S("status")), Text(DateLabel(j.S("at")) + " · " + j.S("evidence"), 12, Muted), actions)));

        }
        return list;

    }
    Control OwnerAnalytics() {

        var a = state!.Obj("analytics");
        var list = Stack(Stats(("Venta liquidada", Money(a.L("sales")), "Espejo de eventos", "chart"), ("Mesas en servicio", a.L("openTables").ToString(), "Cuentas abiertas: " + a.L("openAccounts"), "food")), Card(Stack(Text("Fuente: " + a.S("source"), 15, Graphite, true), Text("Origen: " + DateLabel(a.S("sourceAt")) + " · Recibido: " + DateLabel(a.S("receivedAt")), 12, Muted), Text(a["lagSeconds"] is null ? "Sin sincronización inicial" : "Retraso desde origen: " + a.L("lagSeconds") + " segundos", 13, Muted))));
        foreach (var cash in a.A("cashCloses").Objects()) list.Children.Add(Card(Stack(Text("Corte · " + DateLabel(cash.S("closedAt")), 17, Graphite, true), Text("Contado " + Money(cash.L("counted")) + " · Esperado " + Money(cash.L("expected")) + " · Diferencia " + Money(cash.L("difference")), 13))));
        foreach (var incident in a.A("incidents").Objects()) list.Children.Add(Card(Stack(Text("Autorización pendiente", 17, Graphite, true), Text(incident.S("reason")))));
        return list;

    }
    Control FiscalPage() {

        var list = Stack(Button("Configurar URL del portal", () => Form("Portal público separado", [new("url", "URL HTTPS")], p => Act("fiscal.portal.configure", p))), Text("CFDI 4.0 · Las solicitudes pendientes no son facturas timbradas. El PAC y los datos fiscales del emisor se configuran en el servicio autorizado.", 13, Muted), Button("Solicitud fiscal", () => Form("Datos de receptor", [new("visitId", "Ticket cerrado", "select", "", Options("visit", v => v.S("status") == "closed")), new("rfc", "RFC"), new("name", "Razón social"), new("postalCode", "Código postal fiscal"), new("regime", "Régimen fiscal"), new("use", "Uso CFDI")], p => Act("fiscal.request", p))), Button("Enlace de autofacturación", () => Form("Token con vigencia de 24 horas", [new("visitId", "Ticket cerrado", "select", "", Options("visit", v => v.S("status") == "closed"))], async p => { var token = await Act("fiscal.token", p); using var data = QRCoder.QRCodeGenerator.GenerateQrCode(token.S("url"), QRCoder.QRCodeGenerator.ECCLevel.Q); using var qr = new QRCoder.PngByteQRCode(data); var bitmap = new Avalonia.Media.Imaging.Bitmap(new System.IO.MemoryStream(qr.GetGraphic(5))); ShowModal("Autofacturación", Stack(new Image { Source = bitmap, Width = 240, Height = 240 }, Text(token.S("url"), 14, Graphite, true), Text("Vence: " + DateLabel(token.S("expires")), 12, Muted))); })));
        foreach (var f in Items("fiscal").Reverse()) {

            // A submission the PAC never answered waits for a manager, who first checks in Facturama that the folio does not exist.
            var actions = Wrap();
            if (f.S("status") == "pending-reconciliation" && f.S("providerId") == "") actions.Children.Add(Button("Reintentar", () => Form("Reintentar con el folio " + f.S("folio"), [new("reason", "Revisé Facturama y el folio no existe")], p => Act("fiscal.retry", WithId(p, f.S("id"))))));
            list.Children.Add(Card(Stack(Text("Ticket " + f.S("visitId")[..Math.Min(8, f.S("visitId").Length)], 17, Graphite, true), Badge(f.S("status")), Text(f.S("message"), 13, Muted), Text(f.S("fiscalUuid"), 12, Muted), actions)));

        }
        return list;

    }
}
