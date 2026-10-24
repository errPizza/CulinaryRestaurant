using System.Text.Json.Nodes;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;
// Front office: counter sales, modifier groups, take-out labels, linked tables and zone reassignment.
public sealed partial class Restaurant {
    // Free text for the kitchen: control characters removed, bounded length.
    public static string Sanitize(string value, int max) {

        var clean = new string(value.Where(c => !char.IsControl(c) || c == ' ').ToArray()).Trim();
        return clean.Length <= max ? clean : clean[..max];

    }
    static JsonArray ValidModifierGroups(JsonArray groups) {

        Need(groups.Count <= 10 && groups.Objects().Count() == groups.Count, "Máximo 10 grupos de modificadores");
        Need(groups.Objects().Select(g => g.S("name").Trim()).Distinct().Count() == groups.Count, "Grupos de modificadores repetidos");
        var result = new JsonArray();
        foreach (var g in groups.Objects()) {

            var name = Text(g, "name", 60);
            var options = g.A("options");
            Need(options.Count is >= 1 and <= 20 && options.Objects().Count() == options.Count, $"{name}: de 1 a 20 opciones");
            Need(options.Objects().Select(o => o.S("name").Trim()).Distinct().Count() == options.Count, $"{name}: opciones repetidas");
            var max = Amount(g, "max", 1, options.Count);
            result.Add(J.O("name", name, "required", g.B("required"), "max", max, "options", options.Objects().Select(o => J.O("name", Text(o, "name", 60), "price", Amount(o, "price"))).ToArray()));

        }
        return result;

    }
    // Resolves the chosen options of one order line against the product's groups.
    static JsonObject[] ChosenModifiers(JsonObject product, JsonObject item) {

        var groups = product.A("modifiers").Objects().ToArray();
        var chosen = item.A("modifiers").Objects().ToArray();
        Need(chosen.All(c => groups.Any(g => g.S("name") == c.S("group"))), "Modificador no válido para " + product.S("name"));
        Need(chosen.Select(c => c.S("group")).Distinct().Count() == chosen.Length, "Cada grupo de modificadores se envía una vez");
        var result = new List<JsonObject>();
        foreach (var g in groups) {

            var names = chosen.FirstOrDefault(c => c.S("group") == g.S("name"))?.A("options").Strings() ?? [];
            Need(names.Distinct().Count() == names.Length, g.S("name") + ": opción repetida");
            Need(!g.B("required") || names.Length >= 1, "Elige " + g.S("name") + " para " + product.S("name"));
            Need(names.Length <= g.L("max", 1), g.S("name") + ": máximo " + g.L("max", 1) + " opciones");
            foreach (var n in names) { var o = g.A("options").Objects().FirstOrDefault(x => x.S("name") == n) ?? throw new BusinessException(400, g.S("name") + ": opción no válida"); result.Add(J.O("group", g.S("name"), "name", n, "price", o.L("price"))); }

        }
        return result.ToArray();

    }
    static string CommandLabel(JsonObject visit) => visit.B("takeout") || visit.S("channel") == "pickup" ? "PARA LLEVAR" : visit.S("channel") == "delivery" ? "A DOMICILIO" : visit.S("channel") == "staff" ? "EMPLEADO" : visit.S("channel") == "aggregator" ? Platforms.GetValueOrDefault(visit.S("platform"), "PLATAFORMA") : "";
    void CloseVisit(BranchState s, JsonObject v) {

        var id = v.S("id");
        Need(Totals(s, v).L("balance") == 0, "Debe liquidarse el saldo");
        Need(!s.Related("unit", "visitId", id).Any(x => !Final.Contains(x.S("status"))), "Hay productos pendientes de preparación o entrega");
        Need(!s.Related("request", "visitId", id).Any(x => x.S("status") == "pending"), "Resuelve autorizaciones pendientes");
        v["status"] = "closed";
        v["closeCycle"] = v.L("closeCycle") + 1;
        v["closedAt"] = J.Now();
        v["trackingExpires"] = DateTimeOffset.UtcNow.AddHours(24).ToString("O");
        Release(s, v.S("tableId"));
        foreach (var linked in v.A("linkedTableIds").Strings()) Release(s, linked);
        QueueConsumption(s, v);
        LoyaltyEarn(s, v);

    }
    // A counter sale is all or nothing: a declined or silent terminal leaves no sale behind.
    JsonObject TerminalSale(BranchState s, JsonObject u, string visitId, long amount, long tip, string card) {

        var txn = Act(s, u, "terminal.charge", J.O("visitId", visitId, "amount", amount, "tip", tip, "card", card));
        Need(txn.S("status") == "approved", txn.S("status") == "declined" ? "Tarjeta rechazada: " + txn.S("message") + ". La venta no se registró" : "La terminal no respondió y la venta no se registró. Revisa la terminal antes de cobrar de nuevo", 402);
        return s.Get("payment", txn.S("paymentId"));

    }
    bool TryFront(BranchState s, JsonObject u, string type, JsonObject p, out JsonObject result) {

        result = J.O("ok", true);
        var id = p.S("id");
        switch (type) {
            case "sale.quick": {

                    Role(u, "manager", "mesero");
                    Need(s.Related("cash", "status", "open").Any(), "Abre una caja antes de vender en mostrador");
                    var method = Choice(p, "method", "cash", "card", "transfer", "wallet");
                    var visit = Act(s, u, "visit.quick", J.O("takeout", p.B("takeout"), "customerName", p.S("customerName")));
                    var vid = visit.S("id");
                    if (p.S("customerId") != "") Act(s, u, "visit.customer", J.O("id", vid, "customerId", p.S("customerId")));
                    var command = Act(s, u, "command.send", J.O("visitId", vid, "items", p.A("items").DeepClone()));
                    var v = s.Get("visit", vid);
                    var balance = Totals(s, v).L("balance");
                    var tip = Amount(p, "tip");
                    var pay = J.O("visitId", vid, "amount", balance, "tip", tip, "method", method);
                    if (method == "cash") { var tendered = p.ContainsKey("tendered") ? Amount(p, "tendered") : balance + tip; Need(tendered >= balance + tip, "Efectivo recibido insuficiente: faltan " + (balance + tip - tendered) / 100m + " MXN"); pay["tendered"] = tendered; }
                    Need(balance > 0, "La venta no tiene importe");
                    Need(method != "wallet" || tip == 0, "La propina con monedero no está permitida");
                    // A real terminal answers later: the sale stays open with the charge queued, and is prepaid once approved. A declined
                    // card leaves the account open on the counter to pay another way or cancel.
                    if (method == "card" && p.B("terminal") && IsLive(s, "terminal")) { var txn = Act(s, u, "terminal.charge", J.O("visitId", vid, "amount", balance, "tip", tip, "terminalId", p.S("terminalId"))); s.Get("terminaltxn", txn.S("id"))["counter"] = true; result = J.O("visitId", vid, "commandId", command.S("id"), "terminalTxnId", txn.S("id"), "total", balance, "label", CommandLabel(v), "pending", true); break; }
                    var payment = method == "wallet" ? Act(s, u, "wallet.pay", J.O("visitId", vid, "amount", balance)) : method == "card" && p.B("terminal") ? TerminalSale(s, u, vid, balance, tip, p.S("card", "approve")) : Act(s, u, "payment.add", pay);
                    v["prepaid"] = true;
                    result = J.O("visitId", vid, "commandId", command.S("id"), "paymentId", payment.S("id"), "total", balance, "change", payment.L("change"), "label", CommandLabel(v));
                    break;

                }
            case "visit.mergeMany": {

                    Role(u, "mesero", "manager");
                    var master = Own(s, u, id);
                    var others = p.A("otherIds").Strings();
                    Need(others.Length is >= 1 and <= 10 && others.Distinct().Count() == others.Length && !others.Contains(id), "Selecciona de 1 a 10 cuentas distintas");
                    var linked = master.A("linkedTableIds").Strings().ToList();
                    foreach (var other in others) {

                        var o = s.Get("visit", other);
                        var table = o.S("tableId");
                        Act(s, u, "visit.merge", J.O("id", id, "otherId", other));
                        foreach (var t in o.A("linkedTableIds").Strings().Append(table).Where(t => t != "" && t != master.S("tableId") && !linked.Contains(t))) { s.Get("table", t)["status"] = "occupied"; linked.Add(t); }

                    }
                    master = s.Get("visit", id);
                    master["linkedTableIds"] = J.N(linked);
                    master["people"] = Math.Min(500, master.L("people") + others.Sum(x => s.Get("visit", x).L("people")));
                    result = J.O("id", id, "linkedTableIds", linked);
                    break;

                }
            case "visit.reassignZone": {

                    Role(u, "manager");
                    var zone = Text(p, "zone", 100);
                    var w = s.Get("user", p.S("waiterId"));
                    Need(w.B("active") && w.S("role") == "mesero", "Selecciona un mesero activo");
                    var tables = s.Related("table", "zone", zone).Select(t => t.S("id")).ToHashSet();
                    Need(tables.Count > 0, "Zona sin mesas");
                    int moved = 0, skipped = 0;
                    foreach (var v in s.Related("visit", "status", "open").Where(v => tables.Contains(v.S("tableId"))).ToArray()) {

                        if (v.B("printed") || s.Related("folio", "visitId", v.S("id")).Any(f => !f.B("void"))) { skipped++; continue; }
                        v["waiterId"] = w.S("id");
                        foreach (var n in s.Related("notification", "visitId", v.S("id")).Where(x => !x.B("attended"))) n["waiterId"] = w.S("id");
                        moved++;

                    }
                    result = J.O("moved", moved, "skipped", skipped, "zone", zone, "waiterId", w.S("id"));
                    break;

                }
            default:
                return false;
        }
        return true;

    }
    void FrontAfterAction(BranchState s, JsonObject u, string type, JsonObject p, JsonObject result) {

        // A cash payment pulses the drawer only when the restaurant enables it.
        if (type == "payment.add" && result.S("method") == "cash" && s.Meta.B("drawerOnCash")) Add(s, "printjob", J.O("type", "drawer", "status", "pending", "reason", "Cobro en efectivo", "paymentId", result.S("id"), "visitId", result.S("visitId"), "actor", u.S("id"), "at", J.Now()));
        // A prepaid counter sale closes itself once everything was handed over.
        if (type is "unit.state" or "visit.deliverAll") {

            var vid = type == "visit.deliverAll" ? p.S("id") : s.Get("unit", p.S("id")).S("visitId");
            var v = s.Get("visit", vid);
            if (v.B("prepaid") && v.S("status") == "open" && Totals(s, v).L("balance") == 0 && !s.Related("unit", "visitId", vid).Any(x => !Final.Contains(x.S("status"))) && !s.Related("request", "visitId", vid).Any(x => x.S("status") == "pending")) CloseVisit(s, v);

        }

    }
}
