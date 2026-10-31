using System.Text.Json.Nodes;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;

// One branch per server. Every entity is an independently encrypted, bounded record.
public sealed partial class Restaurant(IPasswordHasher passwords) {
    public static readonly string[] Modules = ["reservations", "reviews", "customerRequests", "printing", "courses", "promotions", "multiBranch"];
    public static readonly string[] Cooking = ["Medio", "Tres cuartos", "Bien cocido"];
    public static readonly string[] Final = ["delivered", "cancelled", "unavailable"];
    public JsonObject SafeUser(JsonObject u) { var o = (JsonObject)u.DeepClone(); o.Remove("password"); o.Remove("pinHash"); o["hasPin"] = u.S("pinHash") != ""; return o; }
    public JsonObject Totals(BranchState s, JsonObject v) {

        var gross = s.Related("unit", "visitId", v.S("id")).Where(x => !new[] { "cancelled", "unavailable" }.Contains(x.S("status"))).Sum(x => x.L("price")) + v.L("deliveryFee");
        var d = v.Obj("discount");
        var discount = d.S("kind") == "percent" ? Cents.Percent(gross, d["value"]?.GetValue<decimal>() ?? 0) : Math.Min(gross, d.L("value"));
        var paid = s.Related("payment", "visitId", v.S("id")).Sum(x => x.L("amount"));
        return J.O("gross", gross, "discount", discount, "total", gross - discount, "paid", paid, "balance", gross - discount - paid, "tax", IncludedTax(s, v, gross - discount));

    }
    // The verified license of the branch, supplied by its server. Features and limits come only from it, never from meta.
    public Func<BranchState, LicenseView> License { get; set; } = _ => LicenseView.Missing;
    public void Usage(BranchState s, JsonObject u) {

        Role(u, "mesero", "cocina", "manager", "cajero");
        var license = License(s);
        Need(license.Usable, license.Message, 423);

    }
    bool Licensed(BranchState s, string module) => License(s).Modules.Contains(module);
    // Fixed terminals count against the license; mobile order takers need the add-on but are unlimited.
    public void DeviceAllowed(BranchState s, string kind, string deviceId) {

        if (kind == "mobile") { Module(s, "mobileOrdering"); return; }
        var max = License(s).MaxTerminals;
        var used = s.List("device").Count(d => d.B("authorized") && d.S("kind") != "mobile" && d.S("id") != deviceId);
        Need(used < max, $"La licencia permite {max} terminales fijas y ya están dadas de alta {used}; desautoriza una en Equipo y dispositivos", 403);

    }
    static readonly Dictionary<string, string> ModuleNames = new() { ["reservations"] = "Reservaciones (plan Pro)", ["mobileOrdering"] = "Comanderos móviles", ["deliveryHub"] = "Delivery Hub", ["webOrdering"] = "Web propia de pedidos" };
    void Module(BranchState s, string module) => Need(Licensed(s, module), "Módulo no contratado: " + ModuleNames.GetValueOrDefault(module, module), 403);
    JsonObject Own(BranchState s, JsonObject u, string id) { var v = s.Get("visit", id); Need(v.S("status") == "open", "La cuenta está cerrada"); Need(u.S("role") == "manager" || v.S("waiterId") == u.S("id"), "La cuenta pertenece a otro mesero", 403); return v; }
    JsonObject Add(BranchState s, string kind, JsonObject value) { value["siteId"] = s.Meta.S("siteId"); return s.Add(kind, value); }
    JsonObject Notice(BranchState s, JsonObject v, string message, string kind, string? unitId = null) => Add(s, "notification", J.O("visitId", v.S("id"), "waiterId", v.S("waiterId"), "message", message, "type", kind, "unitId", unitId, "at", J.Now(), "attended", false));
    void History(JsonObject unit, JsonObject u, string status, string reason = "") { var a = unit.A("history"); a.Add(J.O("at", J.Now(), "actor", u.S("id"), "status", status, "reason", reason)); unit["history"] = a.Parent is null ? a : a.DeepClone(); }
    void Release(BranchState s, string tableId) { if (tableId == "") return; if (!s.Related("visit", "tableId", tableId).Any(x => x.S("status") == "open")) s.Get("table", tableId)["status"] = "cleaning"; }
    void Complete(BranchState s) {

        foreach (var c in s.Related("command", "completeNotified", "false").Concat(s.Related("command", "completeNotified", "")).ToArray()) {

            var units = c.A("unitIds").Strings().Select(id => s.Get("unit", id)).ToArray();
            if (!c.B("completeNotified") && units.Length > 0 && units.All(x => Final.Append("ready").Contains(x.S("status")))) {

                c["completeNotified"] = true;
                foreach (var vid in units.Select(x => x.S("visitId")).Distinct()) { var v = s.Get("visit", vid); Notice(s, v, "Comanda " + (v.S("tableId") == "" ? "Ticket " + v.S("id")[..8] : s.Get("table", v.S("tableId")).S("name")) + " completa lista", "complete"); }

            }

        }

    }
    // A reservation holds a free table for two hours around its start.
    string TableStatus(BranchState s, JsonObject t) => !new[] { "free", "reserved" }.Contains(t.S("status")) ? t.S("status") : s.Related("reservation", "tableId", t.S("id")).Any(r => r.S("status") == "confirmed" && DateTimeOffset.TryParse(r.S("start"), out var at) && Math.Abs((at - DateTimeOffset.UtcNow).TotalHours) < 2) ? "reserved" : "free";
    void ReconcileReservations(BranchState s) { foreach (var t in s.List("table").Where(t => new[] { "free", "reserved" }.Contains(t.S("status")))) t["status"] = TableStatus(s, t); }
    // Elements already projected for /api/state, reused while what they were built from is unchanged: the record's
    // committed version plus, for some variants, a stamp of what else they read. Entries no snapshot used during the
    // last 500 snapshots are dropped.
    sealed class Projected(string stamp, StateItem item, long used) { public string Stamp => stamp; public StateItem Item => item; public long Used = used; }
    readonly System.Collections.Concurrent.ConcurrentDictionary<(string Variant, string Key), Projected> projected = new(); long snapshots;
    StateItem Project(BranchState s, string variant, JsonObject record, Func<JsonObject, JsonObject> build, Func<string>? stamp = null) {

        // A writable state may hold edits that are not committed: its records are never cached.
        if (!s.ReadOnly) { var fresh = build(record); return new(fresh.S("id"), fresh, StateDelta.Hash(fresh)); }
        var key = BranchState.Key(record.S("kind"), record.S("id"));
        var full = (s.Committed.TryGet(key, out var stored) ? stored.Version : -1) + "|" + stamp?.Invoke();
        var now = Volatile.Read(ref snapshots);
        if (projected.TryGetValue((variant, key), out var hit) && hit.Stamp == full) { Volatile.Write(ref hit.Used, now); return hit.Item; }
        var node = build(record);
        var item = new StateItem(node.S("id"), node, StateDelta.Hash(node));
        projected[(variant, key)] = new(full, item, now);
        return item;

    }
    static JsonObject Without(JsonObject x, string field) { var c = (JsonObject)x.DeepClone(); c.Remove(field); return c; }
    public JsonObject Snapshot(BranchState s, JsonObject u, string session) => Parts(s, u, session).Materialize();
    // Reads the shared committed state, so nothing here may modify s. History is preserved in storage;
    // the API returns open visits, the newest 500 others and only the units of the visits it returns.
    public StateParts Parts(BranchState s, JsonObject u, string session) {

        var generation = Interlocked.Increment(ref snapshots);
        if (generation % 500 == 0)
        foreach (var (k, v) in projected)
        if (v.Used < generation - 500) projected.TryRemove(k, out _);
        Usage(s, u);
        var role = u.S("role");
        var me = u.S("id");
        var meta = (JsonObject)s.Meta.DeepClone();
        meta.Remove("enrollmentHash");
        var license = License(s);
        meta["modules"] = J.N(license.Modules);
        meta["tier"] = license.Tier;
        meta["maxTerminals"] = license.MaxTerminals;
        meta["maxUsers"] = license.MaxUsers;
        meta["maxTables"] = license.MaxTables;
        meta["license"] = license.Summary();
        // Renewing is the manager's job; the rest of the team hears about it only when the branch is days from locking.
        if (role != "manager" && meta.Obj("license").Obj("warning").S("level") != "critical") meta.Obj("license").Remove("warning");
        var outp = new StateParts();
        outp["user"] = SafeUser(u);
        outp["sessionId"] = session;
        outp["restaurant"] = meta;
        outp["siteId"] = s.Meta.S("siteId");
        string Version(JsonObject x) => x.S("id") + ":" + (s.Committed.TryGet(BranchState.Key(x.S("kind"), x.S("id")), out var r) ? r.Version : -1);
        var visible = role == "mesero" ? s.Related("visit", "waiterId", me).Where(v => u.Obj("permissions").B("shiftReports", true) || v.S("status") == "open").Select(x => x.S("id")).ToHashSet() : null;
        bool Visible(string visitId) => visible is null || visible.Contains(visitId);
        var shown = s.Related("visit", "status", "open").Where(v => Visible(v.S("id"))).Concat(s.Latest("visit", 500, v => v.S("status") != "open" && Visible(v.S("id")))).ToArray();
        foreach (var kind in new[] { "table", "product", "category", "visit", "command", "unit", "notification", "request", "payment", "reservation", "review", "cash", "movement", "promotion", "ticket", "grant", "audit", "device", "user", "session" }.Concat(EcosystemKinds)) {

            IEnumerable<JsonObject> values = kind switch {
                "visit" => shown,
                "unit" => shown.SelectMany(v => s.Related("unit", "visitId", v.S("id"))),
                "notification" when role == "mesero" => s.Latest(kind, 500, x => x.S("waiterId") == me),
                "command" or "payment" when role == "mesero" => s.Latest(kind, 500, x => Visible(x.S("visitId"))),
                "audit" or "command" or "notification" or "session" or "payment" or "movement" => s.Latest(kind, 500),
                "request" when role == "mesero" => s.List(kind).Where(x => Visible(x.S("visitId"))),
                _ => s.List(kind)
            };
            if (role != "manager" && new[] { "review", "audit", "grant", "device", "ticket", "session" }.Contains(kind)) values = [];
            if (role == "mesero" && new[] { "cash", "movement" }.Contains(kind)) values = [];
            if (role == "cocina" && new[] { "payment", "cash", "movement", "reservation", "notification", "request" }.Contains(kind)) values = [];
            // How each record becomes an element: the variant names the projection, the stamp what else it reads.
            Func<JsonObject, JsonObject> build = x => (JsonObject)x.DeepClone();
            Func<JsonObject, string>? stamp = null;
            var variant = kind;
            switch (kind) {
                case "table":
                    build = t => { var c = (JsonObject)t.DeepClone(); c["status"] = TableStatus(s, t); return c; };
                    stamp = t => TableStatus(s, t);
                    break;
                case "user" when role == "manager":
                    build = SafeUser;
                    variant = "user:safe";
                    break;
                case "user":
                    build = x => { var safe = SafeUser(x); return J.O("id", safe.S("id"), "name", safe.S("name"), "role", safe.S("role"), "active", safe.B("active"), "hasPin", safe.S("role") == "manager" && safe.B("hasPin")); };
                    variant = "user:public";
                    break;
                case "grant" or "session":
                    build = x => Without(x, "tokenHash");
                    break;
                case "device":
                    build = x => Without(x, "secretHash");
                    break;
                case "visit" when role == "cocina":
                    build = v => J.O("id", v.S("id"), "tableId", v.S("tableId"), "waiterId", v.S("waiterId"), "status", v.S("status"), "openedAt", v.S("openedAt"));
                    variant = "visit:cocina";
                    break;
                case "visit":
                    build = v => { var c = Without(v, "trackingKey"); foreach (var p in Totals(s, v)) c[p.Key] = p.Value?.DeepClone(); return c; };
                    variant = "visit:totals";
                    stamp = v => string.Join(",", s.Related("unit", "visitId", v.S("id")).Select(Version)) + ";" + string.Join(",", s.Related("payment", "visitId", v.S("id")).Select(Version));
                    break;
                case "unit" when role == "cocina":
                    build = x => { var c = (JsonObject)x.DeepClone(); c.Remove("price"); c.Remove("basePrice"); c.Remove("promoId"); c["extras"] = new JsonArray(x.A("extras").Objects().Select(e => (JsonNode)J.O("name", e.S("name"))).ToArray()); c["modifiers"] = new JsonArray(x.A("modifiers").Objects().Select(m => (JsonNode)J.O("group", m.S("group"), "name", m.S("name"))).ToArray()); return c; };
                    variant = "unit:cocina";
                    break;
            }
            outp.Items(kind + "s", values.Select(x => Project(s, variant, x, build, stamp is null ? null : () => stamp(x))));

        }
        FilterEcosystemSnapshot(s, u, outp);
        return outp;

    }
    public JsonObject Act(BranchState s, JsonObject u, string type, JsonObject p) {

        ReconcileReservations(s);
        Usage(s, u);
        Need(!type.StartsWith("owner."), "El panel del proveedor se ejecuta en el servicio central separado", 403);
        GuardEcosystem(s, u, type, p);
        if (TryEcosystem(s, u, type, p, out var extended)) { IntegrationAfterAction(s, u, type, p, extended); return (JsonObject)extended.DeepClone(); }
        JsonObject result = J.O("ok", true);
        string id = p.S("id");
        switch (type) {
            case "visit.open": {

                    Role(u, "mesero", "manager");
                    var t = s.Get("table", p.S("tableId"));
                    Need(t.S("status") == "free", "Mesa no disponible");
                    var people = Amount(p, "people", 1, t.L("capacity"));
                    var w = s.Get("user", p.S("waiterId", u.S("id")));
                    Need(w.B("active") && w.S("role") == "mesero", "Selecciona un mesero activo");
                    Need(u.S("role") == "manager" || w.S("id") == u.S("id"), "Solo puedes abrir tus cuentas", 403);
                    result = Add(s, "visit", J.O("tableId", t.S("id"), "people", people, "waiterId", w.S("id"), "status", "open", "openedAt", J.Now(), "trackingKey", Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
                    t["status"] = "occupied";
                    break;

                }
            case "table.clean":
                Role(u, "mesero", "manager");
                var clean = s.Get("table", id);
                Need(clean.S("status") == "cleaning", "Mesa sin limpieza pendiente");
                clean["status"] = "free";
                break;
            case "command.send": {

                    Role(u, "mesero", "manager");
                    var v = Own(s, u, p.S("visitId"));
                    var items = p.A("items");
                    Need(items.Count is > 0 and <= 50 && items.Objects().Count() == items.Count, "Agrega entre 1 y 50 renglones válidos");
                    var c = Add(s, "command", J.O("visitId", v.S("id"), "at", J.Now(), "actor", u.S("id"), "unitIds", new JsonArray(), "completeNotified", false, "priority", false, "label", CommandLabel(v)));
                    Need(items.Objects().Sum(x => Amount(x, "quantity", 1, 50)) <= 100, "Máximo 100 unidades por envío; divide el borrador");
                    foreach (var item in items.Objects()) {

                        var prod = s.Get("product", item.S("productId"));
                        Need(prod.B("visible") && prod.B("available"), "Producto oculto o agotado: " + prod.S("name"));
                        var quantity = Amount(item, "quantity", 1, 50);
                        var extraNames = item.A("extras").Strings();
                        Need(extraNames.Distinct().Count() == extraNames.Length && extraNames.Length <= 20, "Selecciona cada extra una sola vez");
                        var extras = extraNames.Select(name => prod.A("extras").Objects().FirstOrDefault(e => e.S("name") == name) ?? throw new BusinessException(400, "Extra no válido")).ToArray();
                        var cooking = prod.B("supportsCooking") ? item.S("cooking") : "";
                        Need(cooking == "" || Cooking.Contains(cooking), "Punto de cocción inválido");
                        Need(!prod.B("supportsCooking") || !prod.B("cookingRequired") || cooking != "", "Elige el punto de cocción de " + prod.S("name"));
                        var modifiers = ChosenModifiers(prod, item);
                        var listPrice = ListPrice(s, v, prod, out var priceListId);
                        var price = checked(listPrice + extras.Sum(e => e.L("price")) + modifiers.Sum(m => m.L("price")));
                        string? promoId = null;
                        if (Licensed(s, "promotions")) {

                            var tz = TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City");
                            var hour = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).Hour;
                            var promo = s.List("promotion").FirstOrDefault(x => x.B("active") && x.S("coupon") == "" && x.A("productIds").Strings().Contains(prod.S("id")) && hour >= x.I("startHour") && hour < x.I("endHour") && (!x.B("combo") || x.A("productIds").Strings().All(pid => items.Objects().Any(z => z.S("productId") == pid))));
                            if (promo is not null) { price = Cents.Percent(price, 100m - promo.L("percent")); promoId = promo.S("id"); }

                        }
                        if (item.B("held")) Module(s, "courses");
                        var course = item.S("course", "principal");
                        Need(new[] { "entrada", "principal", "postre" }.Contains(course), "Tiempo inválido");
                        var person = Amount(item, "person", 0, v.L("people"));
                        if (person == 0) person = 1;
                        for (var n = 0; n < quantity; n++) {

                            var unit = Add(s, "unit", J.O("visitId", v.S("id"), "commandId", c.S("id"), "productId", prod.S("id"), "name", prod.S("name"), "price", price, "basePrice", prod.L("price"), "priceListId", priceListId, "extras", extras, "modifiers", modifiers, "cooking", cooking, "notes", Sanitize(item.S("notes"), 500), "person", person, "course", course, "station", prod.S("station"), "status", item.B("held") ? "held" : "received", "receivedAt", J.Now(), "history", new JsonArray(), "promoId", promoId, "taxBps", prod.L("taxBps"), "recipeVersionId", prod.S("recipeVersionId")));
                            History(unit, u, unit.S("status"));
                            c.A("unitIds").Add(unit.S("id"));

                        }

                    }
                    result = J.O("id", c.S("id"), "unitIds", c.A("unitIds"), "visitId", v.S("id"));
                    break;

                }
            case "unit.state": {

                    var unit = s.Get("unit", id);
                    var v = s.Get("visit", unit.S("visitId"));
                    Need(v.S("status") == "open", "Cuenta cerrada");
                    var next = p.S("status");
                    var current = unit.S("status");
                    if (next == "delivered") { Role(u, "mesero", "manager"); Own(s, u, v.S("id")); Need(current == "ready", "Solo se entrega un producto listo"); unit["deliveredAt"] = J.Now(); foreach (var n in s.Related("notification", "unitId", id)) n["attended"] = true; } else {

                        Role(u, "cocina", "manager");
                        Need((current == "held" && next == "received") || (new[] { "received", "preparing" }.Contains(current) && next == "unavailable") || (current == "received" && next == "preparing") || (current == "preparing" && next == "ready"), "Transición de preparación no permitida");
                        if (current == "held") { Module(s, "courses"); unit["receivedAt"] = J.Now(); }
                        if (next == "unavailable") { unit["reason"] = Text(p, "reason"); Notice(s, v, unit.S("name") + " no disponible: " + unit.S("reason"), "unavailable", id); }
                        if (next == "preparing") unit["startedAt"] = J.Now();
                        if (next == "ready") { unit["readyAt"] = J.Now(); Notice(s, v, unit.S("name") + " listo para recoger", "ready", id); }

                    }
                    unit["status"] = next;
                    History(unit, u, next, p.S("reason"));
                    Need(Totals(s, v).L("balance") >= 0, "La operación dejaría un saldo negativo; concilia pagos");
                    Complete(s);
                    break;

                }
            case "visit.deliverAll": {

                    Role(u, "mesero", "manager");
                    var v = Own(s, u, id);
                    foreach (var unit in s.Related("unit", "visitId", id).Where(x => x.S("status") == "ready").ToArray()) Act(s, u, "unit.state", J.O("id", unit.S("id"), "status", "delivered"));
                    break;

                }
            case "notification.attend": { Role(u, "mesero", "manager"); var n = s.Get("notification", id); Need(u.S("role") == "manager" || n.S("waiterId") == u.S("id"), "Aviso de otro mesero", 403); n["attended"] = true; n["attendedAt"] = J.Now(); break; }
            case "visit.transfer": { Role(u, "mesero", "manager"); var v = Own(s, u, id); var w = s.Get("user", p.S("waiterId")); Need(w.B("active") && w.S("role") == "mesero", "Mesero no válido"); v["waiterId"] = w.S("id"); foreach (var n in s.Related("notification", "visitId", id).Where(x => !x.B("attended"))) n["waiterId"] = w.S("id"); break; }
            case "visit.move": { Role(u, "mesero", "manager"); var v = Own(s, u, id); var t = s.Get("table", p.S("tableId")); Need(t.S("status") == "free" && t.L("capacity") >= v.L("people"), "Mesa ocupada o capacidad insuficiente"); var old = v.S("tableId"); v["tableId"] = t.S("id"); t["status"] = "occupied"; Release(s, old); break; }
            case "visit.split": {

                    Role(u, "mesero", "manager");
                    var v = Own(s, u, id);
                    Need(!s.Related("payment", "visitId", id).Any() && v["discount"] is null, "Divide antes de registrar pagos o descuentos");
                    var ids = p.A("unitIds").Strings();
                    var all = s.Related("unit", "visitId", id).ToArray();
                    Need(ids.Length > 0 && ids.Distinct().Count() == ids.Length && ids.Length < all.Length && ids.All(uid => all.Any(x => x.S("id") == uid)), "La división debe dejar unidades en ambas cuentas");
                    var nv = (JsonObject)v.DeepClone();
                    nv["id"] = J.Id();
                    nv["people"] = 1;
                    nv["openedAt"] = J.Now();
                    nv["splitFrom"] = id;
                    nv["trackingKey"] = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                    Add(s, "visit", nv);
                    foreach (var unit in all.Where(x => ids.Contains(x.S("id")))) { unit["visitId"] = nv.S("id"); foreach (var n in s.Related("notification", "unitId", unit.S("id"))) n["visitId"] = nv.S("id"); }
                    result = J.O("id", nv.S("id"));
                    break;

                }
            case "visit.merge": {

                    Role(u, "mesero", "manager");
                    var v = Own(s, u, id);
                    var other = Own(s, u, p.S("otherId"));
                    Need(id != other.S("id"), "Cuentas incompatibles");
                    Need(new[] { v, other }.All(x => x["discount"] is null && !s.Related("payment", "visitId", x.S("id")).Any()), "Une antes de pagos o descuentos");
                    foreach (var unit in s.Related("unit", "visitId", other.S("id"))) unit["visitId"] = id;
                    foreach (var n in s.Related("notification", "visitId", other.S("id"))) { n["visitId"] = id; n["waiterId"] = v.S("waiterId"); }
                    other["status"] = "merged";
                    other["mergedInto"] = id;
                    other["closedAt"] = J.Now();
                    other["trackingExpires"] = J.Now();
                    Release(s, other.S("tableId"));
                    break;

                }
            case "payment.add": {

                    Role(u, "mesero", "manager");
                    Need(u.S("role") == "manager" || u.Obj("permissions").B("payments", true), "Permiso de pagos deshabilitado", 403);
                    var v = Own(s, u, p.S("visitId"));
                    var amount = Amount(p, "amount", 1);
                    Need(amount <= Totals(s, v).L("balance"), "El pago supera el saldo");
                    var cash = s.List("cash").SingleOrDefault(x => x.S("status") == "open") ?? throw new BusinessException(400, "Abre una caja antes de registrar pagos");
                    ValidatePayment(s, v, p, amount);
                    result = Add(s, "payment", J.O("visitId", v.S("id"), "cashId", cash.S("id"), "amount", amount, "tip", Amount(p, "tip"), "method", Choice(p, "method", "cash", "card", "transfer"), "actor", u.S("id"), "at", J.Now(), "externalProcessed", false, "folioId", p.S("folioId"), "tendered", p.L("tendered", amount + Amount(p, "tip")), "change", p.L("tendered", amount + Amount(p, "tip")) - amount - Amount(p, "tip")));
                    break;

                }
            case "visit.close": {

                    Role(u, "mesero", "manager");
                    var v = Own(s, u, id);
                    CloseVisit(s, v);
                    break;

                }
            case "request.create": {

                    Role(u, "mesero", "manager");
                    var v = Own(s, u, p.S("visitId"));
                    var kind = Choice(p, "type", "cancel", "change", "discount", "comp");
                    if (kind is not ("discount" or "comp")) { var unit = s.Get("unit", p.S("unitId")); Need(unit.S("visitId") == v.S("id") && !Final.Contains(unit.S("status")), "Platillo no modificable"); } else if (kind == "discount") ValidateDiscount(p.Obj("discount"));
                    result = Add(s, "request", J.O("visitId", v.S("id"), "actor", u.S("id"), "type", kind, "reason", Text(p, "reason"), "status", "pending", "at", J.Now(), "unitId", p.S("unitId"), "notes", p.S("notes"), "productId", p.S("productId"), "discount", p["discount"]));
                    break;

                }
            case "request.decide": {

                    Role(u, "manager");
                    var r = s.Get("request", id);
                    Need(r.S("status") == "pending", "Solicitud ya resuelta");
                    var v = s.Get("visit", r.S("visitId"));
                    Need(v.S("status") == "open", "Cuenta cerrada");
                    if (p.B("approve")) {

                        var gross = Totals(s, v).L("gross");
                        var at = r.Obj("discount");
                        DualControl(s, u, r.S("type") switch { "cancel" => s.Get("unit", r.S("unitId")).L("price"), "comp" => gross, "discount" => at.S("kind") == "percent" ? Cents.Percent(gross, at["value"]?.GetValue<decimal>() ?? 0) : at.L("value"), _ => 0 }, r.S("actor"));

                        if (r.S("type") is "discount" or "comp") {

                            var d = r.S("type") == "comp" ? J.O("kind", "percent", "value", 100) : r.Obj("discount");
                            ValidateDiscount(d);
                            var value = d["value"]!.GetValue<decimal>();
                            var percentage = d.S("kind") == "percent" ? value : gross == 0 ? (value == 0 ? 0 : decimal.MaxValue) : value * 100 / gross;
                            Need(r.S("type") == "comp" || percentage <= s.Meta.L("discountLimit", 30), "Supera el límite de descuento");
                            v["discount"] = d.DeepClone();
                            v.Obj("discount")["approvedBy"] = u.S("id");
                            v.Obj("discount")["reason"] = r.S("reason");

                        } else {

                            var unit = s.Get("unit", r.S("unitId"));
                            Need(!Final.Contains(unit.S("status")), "El platillo ya no es modificable");
                            if (r.S("type") == "cancel") unit["status"] = "cancelled";
                            else {

                                unit["notes"] = Text(r, "notes");
                                s.Get("command", unit.S("commandId"))["completeNotified"] = false;
                                if (unit.S("status") is "ready" or "preparing") { unit["status"] = "received"; unit["receivedAt"] = J.Now(); unit["startedAt"] = null; unit["readyAt"] = null; }
                                if (r.S("productId") != "") { var prod = s.Get("product", r.S("productId")); Need(prod.B("visible") && prod.B("available"), "Producto no disponible"); foreach (var key in new[] { "name", "station" }) unit[key] = prod[key]?.DeepClone(); unit["productId"] = prod.S("id"); unit["recipeVersionId"] = prod.S("recipeVersionId"); unit["taxBps"] = prod.L("taxBps"); unit["price"] = prod.L("price"); unit["basePrice"] = prod.L("price"); unit["extras"] = new JsonArray(); unit["cooking"] = ""; unit["status"] = "received"; unit["receivedAt"] = J.Now(); unit["startedAt"] = null; unit["readyAt"] = null; }

                            }
                            History(unit, u, unit.S("status"), r.S("reason"));
                            foreach (var n in s.Related("notification", "visitId", v.S("id")).Where(n => (n.S("unitId") == unit.S("id") || n.S("type") == "complete"))) n["attended"] = true;

                        }
                        Need(Totals(s, v).L("balance") >= 0, "El cambio dejaría un saldo negativo");

                    }
                    r["status"] = p.B("approve") ? "approved" : "rejected";
                    r["decidedBy"] = u.S("id");
                    r["decidedAt"] = J.Now();
                    Notice(s, v, "Solicitud " + r.S("status") + ": " + r.S("reason"), "authorization");
                    Complete(s);
                    break;

                }
            case "cash.open":
                Role(u, "manager");
                Need(!s.Related("cash", "status", "open").Any(), "Ya hay una caja abierta");
                result = Add(s, "cash", J.O("initial", Amount(p, "initial"), "status", "open", "openedAt", J.Now(), "openedBy", u.S("id")));
                break;
            case "cash.movement": { Role(u, "manager"); var cash = s.Get("cash", p.S("cashId")); Need(cash.S("status") == "open", "Caja cerrada"); result = Add(s, "movement", J.O("cashId", cash.S("id"), "type", Choice(p, "type", "income", "expense", "withdrawal"), "amount", Amount(p, "amount", 1), "reason", Text(p, "reason"), "actor", u.S("id"), "at", J.Now())); break; }
            case "cash.close": {

                    Role(u, "manager");
                    var cash = s.Get("cash", id);
                    Need(cash.S("status") == "open", "Caja cerrada");
                    var expected = cash.L("initial") + s.Related("payment", "cashId", id).Where(x => x.S("method") == "cash").Sum(x => x.L("amount") + x.L("tip")) + s.Related("movement", "cashId", id).Sum(x => x.S("type") == "income" ? x.L("amount") : -x.L("amount"));
                    var counted = Amount(p, "counted");
                    cash["status"] = "closed";
                    cash["expected"] = expected;
                    cash["counted"] = counted;
                    cash["difference"] = counted - expected;
                    cash["closedAt"] = J.Now();
                    cash["closedBy"] = u.S("id");
                    break;

                }
            default:
                result = Manage(s, u, type, p);
                break;
        }
        AfterEcosystem(s, u, type, p, result);
        return (JsonObject)result.DeepClone();

    }
    void ValidateDiscount(JsonObject d) { var k = Choice(d, "kind", "amount", "percent"); if (k == "amount") Amount(d, "value"); else { var value = d["value"]?.GetValue<decimal>() ?? -1; Need(value is >= 0 and <= 100, "Descuento inválido"); } }
    JsonObject Manage(BranchState s, JsonObject u, string type, JsonObject p) {

        var id = p.S("id");
        JsonObject result = J.O("ok", true);
        if (type == "customer.simulate") { Role(u, "mesero", "manager"); Module(s, "customerRequests"); var v = Own(s, u, p.S("visitId")); Notice(s, v, "Solicitud simulada de " + Choice(p, "type", "attention", "bill"), "customer"); return result; }
        if (type == "command.priority" || type == "command.print") {

            Role(u, "cocina", "manager");
            var c = s.Get("command", id);
            if (type == "command.priority") { c["priority"] = p.B("priority"); c["priorityReason"] = Text(p, "reason"); } else { Module(s, "printing"); result = J.O("id", id, "label", c.S("label"), "units", c.A("unitIds").Strings().Select(uid => { var x = s.Get("unit", uid); return J.O("name", x.S("name"), "station", x.S("station"), "notes", x.S("notes"), "cooking", x.S("cooking"), "extras", x.A("extras").Objects().Select(e => e.S("name")).ToArray()); }).ToArray(), "printMode", "native-preview"); }
            return result;

        }
        Role(u, "manager");
        switch (type) {
            case "catalog.product": {

                    var prod = id != "" ? s.Get("product", id) : Add(s, "product", new());
                    var price = Amount(p, "price");
                    var name = Text(p, "name", 150);
                    Need(s.Related("category", "name", p.S("category")).Any(), "Categoría no registrada");
                    Need(s.Meta.A("stations").Strings().Contains(p.S("station")), "Estación no configurada");
                    var extras = p.A("extras");
                    Need(extras.Count <= 30 && extras.Objects().Select(x => x.S("name")).Distinct().Count() == extras.Count, "Extras inválidos o repetidos");
                    foreach (var e in extras.Objects()) { Text(e, "name", 100); Amount(e, "price"); }
                    var image = p.S("image");
                    Need(image.Length <= 40 * 1024 && (image == "" || (Uri.TryCreate(image, UriKind.Absolute, out var imageUri) && imageUri.Scheme == "https" && imageUri.UserInfo == "") || image.StartsWith("data:image/png;base64,") || image.StartsWith("data:image/jpeg;base64,") || image.StartsWith("data:image/webp;base64,")), "Imagen: HTTPS o PNG/JPEG/WebP incrustado de hasta 40 KiB; para imágenes mayores usa una URL HTTPS");
                    foreach (var key in new[] { "category", "station", "description", "ingredients", "allergens", "tags" }) { Need(p.S(key).Length <= 2000, key + " demasiado largo"); prod[key] = p.S(key); }
                    prod["taxBps"] = Amount(p, "taxBps", 0, 10000);
                    prod["name"] = name;
                    prod["price"] = price;
                    prod["image"] = image;
                    prod["extras"] = extras.DeepClone();
                    prod["supportsCooking"] = p.B("supportsCooking");
                    prod["cookingRequired"] = p.B("supportsCooking") && p.B("cookingRequired");
                    prod["modifiers"] = ValidModifierGroups(p.A("modifiers"));
                    prod["visible"] = p.B("visible", true);
                    prod["available"] = p.B("available", true);
                    result = J.O("id", prod.S("id"));
                    break;

                }
            case "catalog.toggle": { var prod = s.Get("product", id); prod[Choice(p, "field", "visible", "available")] = p.B("value"); break; }
            case "catalog.category": {

                    var name = Text(p, "name", 100);
                    Need(!s.Related("category", "name", name).Any(x => x.S("id") != id), "Categoría repetida");
                    if (id != "") { var c = s.Get("category", id); var old = c.S("name"); c["name"] = name; foreach (var prod in s.Related("product", "category", old)) prod["category"] = name; } else result = Add(s, "category", J.O("name", name));
                    break;

                }
            case "table.save": {

                    if (id == "") Need(s.List("table").Count() < License(s).MaxTables, "Límite de mesas del plan");
                    var t = id != "" ? s.Get("table", id) : Add(s, "table", J.O("status", "free"));
                    Need(id == "" || !s.Related("visit", "tableId", id).Any(x => x.S("status") == "open"), "Mesa con cuentas abiertas");
                    t["name"] = Text(p, "name", 100);
                    t["zone"] = Text(p, "zone", 100);
                    t["capacity"] = Amount(p, "capacity", 1, 100);
                    result = t;
                    break;

                }
            case "branch.save":
                throw new BusinessException(403, "Cada sucursal requiere identidad y servidor propios. El alta corresponde al panel central.");
            case "settings.save": {

                    var m = s.Meta;
                    var stations = p.A("stations").Strings();
                    Need(stations.Length is > 0 and <= 20 && stations.All(x => x.Length is > 0 and <= 100), "Estaciones inválidas");
                    Need(System.Text.RegularExpressions.Regex.IsMatch(p.S("color"), "^#[a-fA-F0-9]{6}$"), "Color inválido");
                    m["name"] = Text(p, "name", 150);
                    m["color"] = p.S("color");
                    m["contact"] = p.S("contact");
                    m["stations"] = J.N(stations);
                    m["delayMinutes"] = Amount(p, "delayMinutes", 1, 120);
                    m["discountLimit"] = Amount(p, "discountLimit", 0, 100);
                    if (p.ContainsKey("drawerOnCash")) m["drawerOnCash"] = p.B("drawerOnCash");
                    if (p.ContainsKey("displayBanners")) { var banners = p.A("displayBanners").Strings().Select(b => Sanitize(b, 140)).Where(b => b != "").ToArray(); Need(banners.Length <= 10, "Máximo 10 mensajes para la pantalla del cliente"); m["displayBanners"] = J.N(banners); }
                    break;

                }
            case "user.save": {

                    var user = id != "" ? s.Get("user", id) : Add(s, "user", new());
                    var role = Choice(p, "role", "mesero", "cocina", "manager", "cajero");
                    Need(user.S("id") != u.S("id") || (p.B("active", true) && role == "manager"), "No puedes quitarte el acceso de gerente");
                    var username = Text(p, "username", 100);
                    Need(!s.Related("user", "username", username).Any(x => x.S("id") != user.S("id")), "Usuario existente");
                    Need(id != "" || s.List("user").Count() <= License(s).MaxUsers, "Límite de empleados del plan");
                    user["name"] = Text(p, "name", 100);
                    user["username"] = username;
                    user["role"] = role;
                    user["active"] = p.B("active", true);
                    if (p["approvalLimit"] is not null && p.L("approvalLimit") != user.L("approvalLimit")) { Need(user.S("id") != u.S("id"), "Tu límite de autorización lo cambia otro gerente", 403); user["approvalLimit"] = Amount(p, "approvalLimit"); }
                    var permissions = new JsonObject();
                    foreach (var key in UserPermissions) permissions[key] = p.Obj("permissions").B(key, true);
                    user["permissions"] = permissions;
                    if (p.S("password") != "") user["password"] = passwords.Hash(p.S("password"));
                    Need(user.S("password") != "", "Contraseña requerida");
                    if (!user.B("active") || p.S("password") != "")
                    foreach (var sess in s.Related("session", "userId", user.S("id"))) sess["revoked"] = true;
                    result = SafeUser(user);
                    break;

                }
            case "device.enrollment":
                Need(p.S("code").Trim().Length >= 20, "Código de alta demasiado corto");
                s.Meta["enrollmentHash"] = J.Hash(p.S("code").Trim());
                break;
            case "session.revoke":
                s.Get("session", id)["revoked"] = true;
                break;
            case "installation.authorize": { var d = s.Get("device", id); if (p.B("authorized") && !d.B("authorized")) DeviceAllowed(s, d.S("kind") == "mobile" ? "mobile" : "terminal", id); d["authorized"] = p.B("authorized"); if (!d.B("authorized")) foreach (var sess in s.Related("session", "deviceId", id)) sess["revoked"] = true; break; }
            case "reservation.save": {

                    Module(s, "reservations");
                    var r = id != "" ? s.Get("reservation", id) : Add(s, "reservation", J.O("at", J.Now()));
                    var status = Choice(p, "status", "pending", "confirmed", "proposed", "rejected", "attended", "cancelled", "no_show");
                    Need(DateTimeOffset.TryParse(p.S("start"), out var start), "Fecha y hora inválidas");
                    var people = Amount(p, "people", 1, 100);
                    JsonObject? t = p.S("tableId") == "" ? null : s.Get("table", p.S("tableId"));
                    if (new[] { "confirmed", "attended" }.Contains(status)) { Need(t is not null && t.L("capacity") >= people, "Capacidad insuficiente o falta mesa"); Need(!s.List("reservation").Any(x => x.S("id") != r.S("id") && x.S("tableId") == t!.S("id") && new[] { "confirmed", "attended" }.Contains(x.S("status")) && Math.Abs((DateTimeOffset.Parse(x.S("start")) - start).TotalHours) < 2), "Conflicto de reservación (bloque de dos horas)"); if (r.S("status") == "proposed") Need(p.B("accepted"), "Registra la aceptación del horario propuesto"); }
                    r["name"] = Text(p, "name", 100);
                    r["contact"] = Text(p, "contact", 150);
                    r["start"] = start.ToString("O");
                    r["people"] = people;
                    r["notes"] = p.S("notes");
                    r["tableId"] = t?.S("id") ?? "";
                    r["status"] = status;
                    r["accepted"] = p.B("accepted");
                    r["simulated"] = p.B("simulated");
                    result = r;
                    break;

                }
            case "review.simulate": {

                    Module(s, "reviews");
                    var v = s.Get("visit", p.S("visitId"));
                    Need(v.S("status") == "closed", "Solo visitas cerradas");
                    Need(!s.Related("review", "visitId", v.S("id")).Any(), "La visita ya tiene evaluación", 409);
                    result = Add(s, "review", J.O("visitId", v.S("id"), "food", Amount(p, "food", 1, 5), "service", Amount(p, "service", 1, 5), "overall", Amount(p, "overall", 1, 5), "comment", p.S("comment"), "at", J.Now(), "simulated", true, "private", true));
                    break;

                }
            case "promotion.save": {

                    Module(s, "promotions");
                    var prodIds = p.A("productIds").Strings();
                    Need(prodIds.Length > 0, "Selecciona productos");
                    foreach (var pid in prodIds) s.Get("product", pid);
                    var promo = id != "" ? s.Get("promotion", id) : Add(s, "promotion", new());
                    promo["name"] = Text(p, "name", 100);
                    promo["startHour"] = Amount(p, "startHour", 0, 23);
                    promo["endHour"] = Amount(p, "endHour", 1, 24);
                    Need(promo.L("startHour") < promo.L("endHour"), "Horario inválido");
                    promo["productIds"] = J.N(prodIds);
                    promo["percent"] = Amount(p, "percent", 1, 100);
                    var coupon = p.S("coupon").Trim().ToUpperInvariant();
                    Need(coupon == "" || System.Text.RegularExpressions.Regex.IsMatch(coupon, "^[A-Z0-9_-]{3,32}$"), "Cupón: 3 a 32 letras, números, guion o subrayado");
                    Need(coupon == "" || !s.List("promotion").Any(x => x.S("id") != promo.S("id") && x.S("coupon") == coupon), "Cupón ya registrado");
                    promo["coupon"] = coupon;
                    promo["combo"] = p.B("combo");
                    promo["active"] = p.B("active");
                    result = promo;
                    break;

                }
            case "ticket.create":
                result = Add(s, "ticket", J.O("subject", Text(p, "subject", 200), "status", "open", "at", J.Now(), "messages", new[] { J.O("id", J.Id(), "actor", u.S("id"), "name", u.S("name"), "message", Text(p, "message"), "at", J.Now()) }));
                break;
            case "ticket.reply": { var ticket = s.Get("ticket", id); Need(ticket.A("messages").Count < 20, "Abre otro ticket para continuar"); ticket.A("messages").Add(J.O("id", J.Id(), "actor", u.S("id"), "name", u.S("name"), "message", Text(p, "message"), "at", J.Now())); break; }
            default:
                throw new BusinessException(404, "Operación no implementada: " + type);
        }
        return (JsonObject)result.DeepClone();

    }
}