using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
namespace MesaNexo.Tests;
// Stage 1 of the ecosystem sheet: counter sale, modifier groups, take-out labels, pickup time, linked tables and zones.
static class FrontofficeTests {
    public static async Task Run(string root, string python, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception(name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        async Task Deny(Func<Task> action, string name) { try { await action(); throw new Exception("Expected rejection: " + name); } catch (BusinessException) { Check(true, name); } }
        var folder = Path.Combine(root, ".lab", "front-" + J.Id());
        var password = "Test-" + J.Id();
        var enrollment = J.Id();
        var runtime = new RuntimeStore(python, Path.Combine(root, "runtime", "lab_host.py"), folder, "front-site");
        var service = new BranchService(runtime, "front-site", new Passwords());
        try {

            await service.Initialize(J.O("siteId", "front-site", "name", "Front office", "mode", "licencia", "managerName", "Manager", "managerUsername", "manager", "managerPassword", password, "enrollmentCode", enrollment, "modules", Restaurant.Modules));
            var token = (await service.Login(J.O("siteId", "front-site", "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "front", "username", "manager", "password", password, "enrollmentCode", enrollment))).S("token");
            async Task<JsonObject> Act(string type, JsonObject p) => (await service.Action(token, J.O("operationId", J.Id(), "type", type, "payload", p))).Obj("result");
            var ana = await Act("user.save", J.O("name", "Ana", "username", "ana", "role", "mesero", "password", password));
            var luis = await Act("user.save", J.O("name", "Luis", "username", "luis", "role", "mesero", "password", password));
            var cook = await Act("user.save", J.O("name", "Cocina", "username", "cook", "role", "cocina", "password", password));
            var cookCode = J.Id();
            await service.RotateEnrollment(cookCode);
            var cookToken = (await service.Login(J.O("siteId", "front-site", "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "kds", "username", "cook", "password", password, "enrollmentCode", cookCode))).S("token");
            async Task<JsonObject> Kitchen(string type, JsonObject p) => (await service.Action(cookToken, J.O("operationId", J.Id(), "type", type, "payload", p))).Obj("result");
            await Act("catalog.category", J.O("name", "Food"));
            var groups = new JsonArray(J.O("name", "Término", "required", true, "max", 1, "options", new[] { J.O("name", "Medio", "price", 0), J.O("name", "Bien cocido", "price", 0) }), J.O("name", "Extras", "required", false, "max", 2, "options", new[] { J.O("name", "Queso", "price", 2000), J.O("name", "Tocino", "price", 2500), J.O("name", "Aguacate", "price", 1500) }));
            var burger = await Act("catalog.product", J.O("name", "Burger", "price", 10000, "taxBps", 1600, "category", "Food", "station", "Cocina", "modifiers", groups));
            var steak = await Act("catalog.product", J.O("name", "Steak", "price", 30000, "taxBps", 1600, "category", "Food", "station", "Cocina", "supportsCooking", true, "cookingRequired", true));
            var soda = await Act("catalog.product", J.O("name", "Soda", "price", 3550, "taxBps", 1600, "category", "Food", "station", "Cocina"));
            await Deny(() => Act("catalog.product", J.O("name", "Bad", "price", 100, "category", "Food", "station", "Cocina", "modifiers", new JsonArray(J.O("name", "X", "max", 1, "options", new JsonArray())))), "modifier group without options is rejected");
            Check((await service.Load()).Get("product", burger.S("id")).A("modifiers").Count == 2, "product keeps validated modifier groups");
            await Act("cash.open", J.O("initial", 50000));

            // Modifier groups and cooking.
            var visit = await Act("visit.quick", new());
            var vid = visit.S("id");
            await Deny(() => Act("command.send", J.O("visitId", vid, "items", new[] { J.O("productId", burger.S("id"), "quantity", 1) })), "required modifier blocks the command");
            await Deny(() => Act("command.send", J.O("visitId", vid, "items", new[] { J.O("productId", burger.S("id"), "quantity", 1, "modifiers", new[] { J.O("group", "Término", "options", new[] { "Crudo" }) }) })), "unknown modifier option is rejected");
            await Deny(() => Act("command.send", J.O("visitId", vid, "items", new[] { J.O("productId", burger.S("id"), "quantity", 1, "modifiers", new[] { J.O("group", "Término", "options", new[] { "Medio" }), J.O("group", "Extras", "options", new[] { "Queso", "Tocino", "Aguacate" }) }) })), "modifier group maximum is enforced");
            await Deny(() => Act("command.send", J.O("visitId", vid, "items", new[] { J.O("productId", steak.S("id"), "quantity", 1) })), "required cooking point blocks the command");
            var cmd = await Act("command.send", J.O("visitId", vid, "items", new[] { J.O("productId", burger.S("id"), "quantity", 1, "notes", "Sin\u0007 cebolla\n" + new string('x', 600), "modifiers", new[] { J.O("group", "Término", "options", new[] { "Medio" }), J.O("group", "Extras", "options", new[] { "Queso", "Tocino" }) }), J.O("productId", steak.S("id"), "quantity", 1, "cooking", "Medio") }));
            var state = await service.Load();
            var unit = state.Get("unit", cmd.A("unitIds").Strings()[0]);
            Check(unit.L("price") == 14500 && unit.A("modifiers").Count == 3, "chosen modifiers add their price to the unit");
            Check(unit.S("notes").Length == 500 && !unit.S("notes").Any(char.IsControl), "kitchen comments are sanitized and bounded");
            Check(state.Get("command", cmd.S("id")).S("label") == "", "dine-in command carries no take-out label");
            var kds = (await service.State(cookToken)).A("units").Objects().First(x => x.S("id") == unit.S("id"));
            Check(kds.A("modifiers").Objects().All(m => !m.ContainsKey("price")) && !kds.ContainsKey("price"), "kitchen sees modifiers without prices");

            // Counter sale: one call, change and drawer pulse only for cash.
            await Act("settings.save", J.O("name", "Front office", "color", "#f2b93b", "contact", "", "delayMinutes", 15, "discountLimit", 20, "stations", new[] { "Cocina" }, "drawerOnCash", true, "displayBanners", new[] { "2x1 en refrescos", "Pregunta por el postre" }));
            Check((await service.Load()).Meta.A("displayBanners").Count == 2, "customer display banners are saved");
            await Deny(() => Act("settings.save", J.O("name", "Front office", "color", "#f2b93b", "contact", "", "delayMinutes", 15, "discountLimit", 20, "stations", new[] { "Cocina" }, "displayBanners", Enumerable.Range(0, 11).Select(i => "Mensaje " + i).ToArray())), "customer display accepts at most ten banners");
            await Deny(() => Act("sale.quick", J.O("method", "cash", "tendered", 5000, "items", new[] { J.O("productId", soda.S("id"), "quantity", 2) })), "counter sale rejects insufficient cash");
            var sale = await Act("sale.quick", J.O("method", "cash", "tendered", 10000, "takeout", true, "customerName", "Mario", "items", new[] { J.O("productId", soda.S("id"), "quantity", 2) }));
            state = await service.Load();
            var sv = state.Get("visit", sale.S("visitId"));
            Check(sale.L("total") == 7100 && sale.L("change") == 2900, "counter sale charges the total and returns exact change");
            Check(sv.B("prepaid") && sv.B("takeout") && sv.S("customerName") == "Mario" && sale.S("label") == "PARA LLEVAR" && state.Get("command", sale.S("commandId")).S("label") == "PARA LLEVAR", "take-out sale labels its command PARA LLEVAR");
            Check(state.List("printjob").Count(j => j.S("type") == "drawer" && j.S("paymentId") == sale.S("paymentId")) == 1, "cash sale pulses the cash drawer once");
            var card = await Act("sale.quick", J.O("method", "card", "items", new[] { J.O("productId", soda.S("id"), "quantity", 1) }));
            Check(!(await service.Load()).List("printjob").Any(j => j.S("paymentId") == card.S("paymentId")), "card sale does not open the drawer");
            foreach (var uid in (await service.Load()).List("unit").Where(x => x.S("visitId") == sale.S("visitId")).Select(x => x.S("id")).ToArray()) { await Kitchen("unit.state", J.O("id", uid, "status", "preparing")); await Kitchen("unit.state", J.O("id", uid, "status", "ready")); }
            Check((await service.Load()).Get("visit", sale.S("visitId")).S("status") == "open", "prepaid sale stays open while food is pending");
            await Act("visit.deliverAll", J.O("id", sale.S("visitId")));
            Check((await service.Load()).Get("visit", sale.S("visitId")).S("status") == "closed", "prepaid counter sale closes itself after hand-over");

            // Pickup time and label.
            var customer = await Act("customer.save", J.O("name", "Lucía", "phone", "5550001111"));
            await Deny(() => Act("delivery.create", J.O("customerId", customer.S("id"), "mode", "pickup", "pickupAt", DateTimeOffset.UtcNow.AddHours(-2).ToString("O"))), "pickup time in the past is rejected");
            var pickup = await Act("delivery.create", J.O("customerId", customer.S("id"), "mode", "pickup", "pickupAt", DateTimeOffset.UtcNow.AddMinutes(40).ToString("O")));
            var pc = await Act("command.send", J.O("visitId", pickup.S("visitId"), "items", new[] { J.O("productId", soda.S("id"), "quantity", 1) }));
            state = await service.Load();
            Check(state.Get("delivery", pickup.S("id")).S("pickupAt") != "" && state.Get("delivery", pickup.S("id")).S("pickupName") == "Lucía", "pickup keeps scheduled time and name");
            Check(state.Get("command", pc.S("id")).S("label") == "PARA LLEVAR", "pickup command is labelled PARA LLEVAR for packing");

            // Linked tables and zone reassignment.
            var t1 = await Act("table.save", J.O("name", "T1", "zone", "Terraza", "capacity", 4));
            var t2 = await Act("table.save", J.O("name", "T2", "zone", "Terraza", "capacity", 4));
            var t3 = await Act("table.save", J.O("name", "T3", "zone", "Salón", "capacity", 4));
            var v1 = await Act("visit.open", J.O("tableId", t1.S("id"), "people", 2, "waiterId", ana.S("id")));
            var v2 = await Act("visit.open", J.O("tableId", t2.S("id"), "people", 3, "waiterId", ana.S("id")));
            var v3 = await Act("visit.open", J.O("tableId", t3.S("id"), "people", 2, "waiterId", ana.S("id")));
            await Act("command.send", J.O("visitId", v2.S("id"), "items", new[] { J.O("productId", soda.S("id"), "quantity", 1) }));
            var moved = await Act("visit.reassignZone", J.O("zone", "Terraza", "waiterId", luis.S("id")));
            state = await service.Load();
            Check(moved.L("moved") == 2 && state.Get("visit", v1.S("id")).S("waiterId") == luis.S("id") && state.Get("visit", v3.S("id")).S("waiterId") == ana.S("id"), "waiters are reassigned by zone only");
            var merged = await Act("visit.mergeMany", J.O("id", v1.S("id"), "otherIds", new[] { v2.S("id"), v3.S("id") }));
            state = await service.Load();
            Check(state.Get("visit", v2.S("id")).S("status") == "merged" && state.Get("visit", v3.S("id")).S("status") == "merged" && state.Get("visit", v1.S("id")).L("people") == 7, "several tables merge into one master account");
            Check(state.Get("table", t2.S("id")).S("status") == "occupied" && state.Get("table", t3.S("id")).S("status") == "occupied" && merged.A("linkedTableIds").Count == 2, "linked tables stay occupied while the master account is open");
            var mv = state.Get("visit", v1.S("id"));
            var bal = state.List("unit").Where(x => x.S("visitId") == v1.S("id")).Sum(x => x.L("price"));
            foreach (var uid in state.List("unit").Where(x => x.S("visitId") == v1.S("id")).Select(x => x.S("id")).ToArray()) { await Kitchen("unit.state", J.O("id", uid, "status", "preparing")); await Kitchen("unit.state", J.O("id", uid, "status", "ready")); await Act("unit.state", J.O("id", uid, "status", "delivered")); }
            await Act("payment.add", J.O("visitId", v1.S("id"), "amount", bal, "method", "card"));
            await Act("visit.close", J.O("id", v1.S("id")));
            state = await service.Load();
            Check(new[] { t1, t2, t3 }.All(t => state.Get("table", t.S("id")).S("status") == "cleaning"), "closing the master account releases every linked table");
            await Deny(() => Act("visit.reassignZone", J.O("zone", "Inexistente", "waiterId", luis.S("id"))), "zone reassignment requires an existing zone");

        } finally { runtime.Dispose(); }

    }
}
