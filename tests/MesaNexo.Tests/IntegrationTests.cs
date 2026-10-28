using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
namespace MesaNexo.Tests;
// Stage 4 of the ecosystem sheet: simulated third parties (PAC, PIN pad, payment links, messaging, delivery platforms, hotel PMS, maps, caller ID and printer).
static class IntegrationTests {
    public static async Task Run(string root, string python, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception(name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        async Task Deny(Func<Task> action, string name, int status = 0) { try { await action(); throw new Exception("Expected rejection: " + name); } catch (BusinessException e) when (status == 0 || e.Status == status) { Check(true, name); } }
        var folder = Path.Combine(root, ".lab", "integrations-" + J.Id());
        var password = "Test-" + J.Id();
        var enrollment = J.Id();
        var runtime = LabPostgres.Test(root, folder, "sim-site");
        var service = new BranchService(runtime, "sim-site", new Passwords());
        try {

            await TestLicense.Initialize(service, J.O("siteId", "sim-site", "name", "Simulada", "mode", "licencia", "managerName", "Manager", "managerUsername", "manager", "managerPassword", password, "enrollmentCode", enrollment, "modules", Restaurant.Modules));
            var token = (await service.Login(J.O("siteId", "sim-site", "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "office", "username", "manager", "password", password, "enrollmentCode", enrollment))).S("token");
            async Task<JsonObject> Act(string type, JsonObject p) => (await service.Action(token, J.O("operationId", J.Id(), "type", type, "payload", p))).Obj("result");
            var ana = await Act("user.save", J.O("name", "Ana", "username", "ana", "role", "mesero", "password", password));
            var code = J.Id();
            await service.RotateEnrollment(code);
            var anaToken = (await service.Login(J.O("siteId", "sim-site", "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "pos", "username", "ana", "password", password, "enrollmentCode", code))).S("token");
            async Task<JsonObject> Waiter(string type, JsonObject p) => (await service.Action(anaToken, J.O("operationId", J.Id(), "type", type, "payload", p))).Obj("result");
            async Task Serve(string visitId) { foreach (var uid in (await service.Load()).List("unit").Where(x => x.S("visitId") == visitId && x.S("status") == "received").Select(x => x.S("id")).ToArray()) { await Act("unit.state", J.O("id", uid, "status", "preparing")); await Act("unit.state", J.O("id", uid, "status", "ready")); } }
            async Task<JsonObject> Visit(string id) => (await service.State(token)).A("visits").Objects().Single(x => x.S("id") == id);
            await Act("catalog.category", J.O("name", "Food"));
            var taco = await Act("catalog.product", J.O("name", "Taco", "price", 3000, "taxBps", 1600, "category", "Food", "station", "Cocina"));
            var soup = await Act("catalog.product", J.O("name", "Sopa", "price", 5000, "taxBps", 1600, "category", "Food", "station", "Cocina"));
            var t1 = await Act("table.save", J.O("name", "Mesa 1", "zone", "Salón", "capacity", 4));
            var t2 = await Act("table.save", J.O("name", "Mesa 2", "zone", "Salón", "capacity", 4));
            await Act("cash.open", J.O("initial", 50000));
            async Task<JsonObject> Account(JsonObject table, params JsonObject[] products) { var v = await Act("visit.open", J.O("tableId", table.S("id"), "people", 2, "waiterId", ana.S("id"))); await Act("command.send", J.O("visitId", v.S("id"), "items", products.Select(x => J.O("productId", x.S("id"), "quantity", 1)).ToArray())); return v; }

            // Activation: live mode needs the provider's credentials on the server.
            await Deny(() => Act("terminal.charge", J.O("visitId", J.Id(), "amount", 100)), "simulators refuse to run until activated", 400);
            await Deny(() => Act("integration.configure", J.O("provider", "pac", "mode", "live")), "live mode is refused without the provider's credentials on the server", 400);
            await Deny(() => Waiter("integration.configure", J.O("provider", "terminal", "mode", "simulated")), "only a manager turns integrations on", 403);
            foreach (var key in Restaurant.IntegrationKeys.Where(k => k != "printer")) await Act("integration.configure", J.O("provider", key, "mode", "simulated"));
            var state = await service.Load();
            Check(state.List("integration").Count() == 8 && state.Get("integration", "terminal").Obj("settings").S("terminalId") == "SIM-01" && state.Get("integration", "maps").Obj("settings").L("radiusKm") == 5, "activating a simulator stores default settings");
            await Act("integration.configure", J.O("provider", "pms", "mode", "simulated", "settings", J.O("system", "opera", "rooms", new[] { J.O("room", "204", "guest", "Juan Pérez López", "limit", 8000), J.O("room", "305", "guest", "Marta Ruiz", "limit", 0) })));
            await Act("integration.configure", J.O("provider", "messaging", "mode", "simulated", "settings", J.O("whatsapp", true, "email", true, "ownerEmail", "dueno@example.mx")));
            await Deny(() => Act("integration.configure", J.O("provider", "pms", "mode", "simulated", "settings", J.O("rooms", new[] { J.O("room", "1", "guest", "A"), J.O("room", "1", "guest", "B") }))), "PMS rejects duplicated rooms");

            // PIN pad: approved, declined, silent and reconciled exactly once.
            var v1 = await Account(t1, taco, soup);
            var approved = await Waiter("terminal.charge", J.O("visitId", v1.S("id"), "amount", 3000, "tip", 500, "card", "approve"));
            state = await service.Load();
            var cardPay = state.Get("payment", approved.S("paymentId"));
            Check(approved.S("status") == "approved" && approved.S("authCode").Length == 6 && cardPay.S("method") == "card" && cardPay.L("tip") == 500 && cardPay.Obj("terminal").S("last4") == "4242", "approved terminal charge records a card payment with authorization code");
            var declined = await Waiter("terminal.charge", J.O("visitId", v1.S("id"), "amount", 5000, "card", "decline"));
            Check(declined.S("status") == "declined" && (await service.Load()).List("payment").Count(x => x.S("visitId") == v1.S("id")) == 1, "declined card leaves no payment");
            var silent = await Waiter("terminal.charge", J.O("visitId", v1.S("id"), "amount", 5000, "card", "timeout"));
            Check(silent.S("status") == "uncertain", "a terminal without response stays uncertain");
            await Deny(() => Waiter("terminal.charge", J.O("visitId", v1.S("id"), "amount", 5000, "card", "approve")), "a new charge waits until the silent one is reconciled", 400);
            var reconciled = await Waiter("terminal.reconcile", J.O("id", silent.S("id")));
            Check(reconciled.S("status") == "approved" && (await Visit(v1.S("id"))).L("balance") == 0, "reconciling applies the silent charge once");
            await Deny(() => Waiter("terminal.reconcile", J.O("id", silent.S("id"))), "a reconciled charge cannot be applied again", 400);
            var v2 = await Account(t2, taco);
            var lost = await Act("terminal.charge", J.O("visitId", v2.S("id"), "amount", 3000, "card", "timeout"));
            await Act("payment.add", J.O("visitId", v2.S("id"), "amount", 3000, "method", "cash"));
            Check((await Act("terminal.reconcile", J.O("id", lost.S("id")))).S("status") == "reversed" && (await Visit(v2.S("id"))).L("paid") == 3000, "a silent charge on an account paid meanwhile is reversed, not doubled");

            // Counter sale through the terminal is all or nothing.
            var visits = (await service.Load()).List("visit").Count();
            await Deny(() => Act("sale.quick", J.O("method", "card", "terminal", true, "card", "decline", "items", new[] { J.O("productId", taco.S("id"), "quantity", 1) })), "declined card at the counter is rejected", 402);
            Check((await service.Load()).List("visit").Count() == visits, "a declined counter sale leaves no account behind");
            var quick = await Act("sale.quick", J.O("method", "card", "terminal", true, "items", new[] { J.O("productId", taco.S("id"), "quantity", 1) }));
            Check((await service.Load()).Get("payment", quick.S("paymentId")).Obj("terminal").S("brand") == "Visa", "counter card sale goes through the simulated terminal");

            // Payment links: provider webhook simulated, never applied twice.
            var mario = await Act("customer.save", J.O("name", "Mario", "phone", "55 1234 5678"));
            var v3 = await Act("visit.quick", new());
            await Act("visit.customer", J.O("id", v3.S("id"), "customerId", mario.S("id")));
            await Act("command.send", J.O("visitId", v3.S("id"), "items", new[] { J.O("productId", soup.S("id"), "quantity", 1) }));
            var link = await Act("paylink.create", J.O("visitId", v3.S("id")));
            state = await service.Load();
            Check(link.S("payUrl").StartsWith("https://pagos.simulado.invalid/stripe/") && link.L("amount") == 5000 && state.List("message").Any(m => m.S("channel") == "whatsapp" && m.S("to") == "55 1234 5678" && m.S("body").Contains(link.S("payUrl"))), "payment link is created and sent to the customer by simulated WhatsApp");
            await Deny(() => Act("paylink.create", J.O("visitId", v3.S("id"))), "only one pending link per account", 400);
            var paid = await Act("paylink.simulate", J.O("id", link.S("id"), "outcome", "paid"));
            state = await service.Load();
            Check(paid.S("status") == "paid" && state.Get("payment", paid.S("paymentId")).S("method") == "online" && state.Get("payment", paid.S("paymentId")).S("provider") == "stripe", "paid webhook records an online payment");
            await Deny(() => Act("paylink.simulate", J.O("id", link.S("id"), "outcome", "paid")), "a link cannot be paid twice", 400);
            var v4 = await Act("visit.quick", new());
            await Act("command.send", J.O("visitId", v4.S("id"), "items", new[] { J.O("productId", taco.S("id"), "quantity", 1) }));
            var late = await Act("paylink.create", J.O("visitId", v4.S("id")));
            await Act("payment.add", J.O("visitId", v4.S("id"), "amount", 3000, "method", "cash"));
            Check((await Act("paylink.simulate", J.O("id", late.S("id"), "outcome", "paid"))).S("status") == "refunded" && (await Visit(v4.S("id"))).L("paid") == 3000, "payment arriving for a covered account is refunded");
            var failed = await Act("visit.quick", new());
            await Act("command.send", J.O("visitId", failed.S("id"), "items", new[] { J.O("productId", taco.S("id"), "quantity", 1) }));
            Check((await Act("paylink.simulate", J.O("id", (await Act("paylink.create", J.O("visitId", failed.S("id")))).S("id"), "outcome", "failed"))).S("status") == "failed" && (await Visit(failed.S("id"))).L("paid") == 0, "failed online payment records nothing");

            // Hotel PMS: room, surname and credit limit.
            var v5 = await Act("visit.quick", new());
            await Act("command.send", J.O("visitId", v5.S("id"), "items", new[] { J.O("productId", soup.S("id"), "quantity", 2) }));
            await Deny(() => Act("pms.charge", J.O("visitId", v5.S("id"), "room", "999", "guest", "Pérez")), "room without a registered guest is rejected", 404);
            await Deny(() => Act("pms.charge", J.O("visitId", v5.S("id"), "room", "204", "guest", "Juan Gómez")), "guest surname must match the room", 403);
            await Deny(() => Act("pms.charge", J.O("visitId", v5.S("id"), "room", "204", "guest", "perez")), "room credit limit is enforced", 402);
            var post = await Act("pms.charge", J.O("visitId", v5.S("id"), "room", "204", "guest", "Sr. Perez", "amount", 8000));
            state = await service.Load();
            Check(post.S("status") == "posted" && post.S("reference").StartsWith("OPERA-") && state.Get("payment", post.S("paymentId")).S("method") == "room", "room charge posts to the simulated Opera folio as a room payment");
            Check((await Act("pms.charge", J.O("visitId", v5.S("id"), "room", "305", "guest", "Ruiz"))).L("amount") == 2000 && (await Visit(v5.S("id"))).L("balance") == 0, "a room without limit takes the remaining balance");

            // Maps and caller ID.
            var first = await Act("geo.lookup", J.O("text", "Av. Insurgentes Sur 1000, CDMX"));
            var again = await Act("geo.lookup", J.O("text", "av. insurgentes sur 1000, cdmx"));
            Check(J.Canonical(first) == J.Canonical(again) && first["distanceKm"]!.GetValue<decimal>() <= 5 && first.L("etaMinutes") >= 5, "simulated geocoder is deterministic and stays inside the radius");
            var address = await Act("address.save", J.O("customerId", mario.S("id"), "label", "Casa", "text", "Av. Insurgentes Sur 1000, CDMX", "geocode", true));
            Check(address.S("coordinateSource") == "simulated-geocoder" && J.Canonical(address["lat"]) == J.Canonical(first["lat"]), "address can be located by the simulated geocoder");
            await Act("zone.save", J.O("name", "Centro", "fee", 3000, "polygon", new[] { J.O("lat", "19.30", "lon", "-99.25"), J.O("lat", "19.30", "lon", "-99.00"), J.O("lat", "19.55", "lon", "-99.00"), J.O("lat", "19.55", "lon", "-99.25") }));
            var courier = await Act("courier.save", J.O("name", "Pedro"));
            var delivery = await Act("delivery.create", J.O("customerId", mario.S("id"), "mode", "delivery", "addressId", address.S("id")));
            state = await service.Load();
            var d = state.Get("delivery", delivery.S("id"));
            Check(Math.Abs(d.L("etaMinutes") - first.L("etaMinutes")) <= 1 && Math.Abs(d["distanceKm"]!.GetValue<decimal>() - first["distanceKm"]!.GetValue<decimal>()) <= 0.1m, "delivery stores distance and ETA from the simulated map");
            await Act("command.send", J.O("visitId", d.S("visitId"), "items", new[] { J.O("productId", taco.S("id"), "quantity", 1) }));
            await Serve(d.S("visitId"));
            await Act("delivery.assign", J.O("id", d.S("id"), "courierId", courier.S("id")));
            await Act("delivery.dispatch", J.O("id", d.S("id")));
            Check((await service.Load()).List("message").Any(m => m.S("event") == "dispatch" && m.S("refId") == d.S("id") && m.S("body").Contains("Pedro")), "dispatch notifies the customer by simulated WhatsApp");
            var pickup = await Act("delivery.create", J.O("customerId", mario.S("id"), "mode", "pickup"));
            await Act("command.send", J.O("visitId", pickup.S("visitId"), "items", new[] { J.O("productId", taco.S("id"), "quantity", 1), J.O("productId", soup.S("id"), "quantity", 1) }));
            var pickupUnits = (await service.Load()).List("unit").Where(x => x.S("visitId") == pickup.S("visitId")).ToArray();
            await Act("unit.state", J.O("id", pickupUnits[0].S("id"), "status", "preparing"));
            await Act("unit.state", J.O("id", pickupUnits[0].S("id"), "status", "ready"));
            Check(!(await service.Load()).List("message").Any(m => m.S("event") == "pickup-ready"), "pickup message waits until the whole order is ready");
            await Serve(pickup.S("visitId"));
            Check((await service.Load()).List("message").Count(m => m.S("event") == "pickup-ready" && m.S("refId") == pickup.S("id")) == 1, "customer is told once that the pickup order is ready");
            var call = await Waiter("callerid.simulate", J.O("number", "+52 55 1234 5678", "name", "MARIO"));
            Check(call.S("customerId") == mario.S("id") && call.S("source") == "simulated-callerid", "simulated incoming call is matched to the customer by phone");
            Check((await Waiter("callerid.simulate", J.O("number", "5599990000"))).S("customerId") == "", "unknown caller is shown as a new number");

            // Delivery platforms: incoming order, kitchen label, platform payment and handoff.
            var uber = await Act("aggregator.simulate", J.O("platform", "uber", "customerName", "Laura M.", "items", new[] { J.O("productId", taco.S("id"), "quantity", 2) }));
            Check(uber.S("status") == "pending" && uber.S("externalId").StartsWith("UE-") && uber.L("subtotal") == 6000, "simulated Uber Eats order arrives pending");
            Check((await service.State(anaToken)).A("aggregatororders").Objects().Any(x => x.S("id") == uber.S("id")), "waiters see platform orders to hand them off");
            await Deny(() => Waiter("aggregator.decide", J.O("id", uber.S("id"), "accept", true)), "only a manager accepts platform orders", 403);
            var accepted = await Act("aggregator.decide", J.O("id", uber.S("id"), "accept", true));
            state = await service.Load();
            var uv = state.Get("visit", accepted.S("visitId"));
            Check(uv.S("channel") == "aggregator" && state.List("command").Single(x => x.S("visitId") == uv.S("id")).S("label") == "UBER EATS", "accepted platform order reaches the kitchen labelled UBER EATS");
            Check(state.List("payment").Single(x => x.S("visitId") == uv.S("id")).S("method") == "platform" && accepted.L("commission") == 1500 && accepted.L("net") == 4500, "platform pays the order minus the 25 % commission");
            await Deny(() => Act("aggregator.handoff", J.O("id", uber.S("id"))), "handoff waits for the kitchen");
            await Serve(uv.S("id"));
            var handed = await Act("aggregator.handoff", J.O("id", uber.S("id")));
            Check(handed.S("status") == "handed-off" && (await service.Load()).Get("visit", uv.S("id")).S("status") == "closed", "handing off to the platform courier closes the prepaid order");
            var rappi = await Act("aggregator.simulate", J.O("platform", "rappi"));
            var rejected = await Act("aggregator.decide", J.O("id", rappi.S("id"), "accept", false, "reason", "Cocina saturada"));
            Check(rejected.S("status") == "rejected" && rejected.S("visitId") == "", "rejected platform order creates no account");
            await Act("integration.configure", J.O("provider", "aggregators", "mode", "simulated", "settings", J.O("platforms", new[] { "didi" }, "commissionBps", 2000, "autoAccept", true)));
            await Deny(() => Act("aggregator.simulate", J.O("platform", "uber")), "disabled platforms receive no orders");
            var didi = await Act("aggregator.simulate", J.O("platform", "didi", "items", new[] { J.O("productId", soup.S("id"), "quantity", 1) }));
            Check(didi.S("status") == "accepted" && didi.L("commission") == 1000, "auto-accept sends platform orders straight to the kitchen");
            var sync = await Act("aggregator.sync", new());
            Check(sync.L("published") == 2, "menu sync publishes the available products");

            // Simulated PAC: SAT receiver rule, stamping, e-mail and cancellation.
            var ticket = (await service.Load()).List("visit").First(x => x.S("status") == "closed" && x.S("id") == uv.S("id"));
            var bad = await Act("fiscal.request", J.O("visitId", ticket.S("id"), "rfc", "XAXX010101000", "name", "PUBLICO EN GENERAL", "postalCode", "06000", "regime", "601", "use", "G03"));
            Check((await Act("pac.stamp", J.O("id", bad.S("id")))).S("status") == "simulated-rejected", "simulated PAC applies the SAT generic RFC rule");
            var good = await Act("fiscal.request", J.O("visitId", ticket.S("id"), "rfc", "EKU9003173C9", "name", "ESCUELA KEMPER URGATE", "postalCode", "42501", "regime", "601", "use", "G03", "email", "compras@example.mx"));
            var stamped = await Act("pac.stamp", J.O("id", good.S("id")));
            state = await service.Load();
            Check(stamped.S("status") == "simulated-stamped" && Guid.TryParse(stamped.S("fiscalUuid"), out _) && stamped.S("message").Contains("sin validez fiscal") && stamped.Obj("cfdi").L("folio") == 1, "simulated stamp returns a UUID labelled without fiscal validity");
            Check(state.List("message").Any(m => m.S("channel") == "email" && m.S("to") == "compras@example.mx" && m.S("body").Contains(stamped.S("fiscalUuid"))), "stamped invoice is e-mailed (simulated) to the receiver");
            await Deny(() => Act("visit.reopen", J.O("id", ticket.S("id"), "reason", "x")), "a stamped invoice blocks reopening the ticket");
            Check((await Act("fiscal.cancel.request", J.O("id", good.S("id"), "motive", "02", "reason", "Datos erróneos"))).S("status") == "simulated-cancelled", "simulated PAC cancels a simulated CFDI");
            await Act("integration.configure", J.O("provider", "pac", "mode", "simulated", "settings", J.O("autoStamp", true, "series", "B")));
            var auto = await Act("fiscal.request", J.O("visitId", ticket.S("id"), "rfc", "EKU9003173C9", "name", "ESCUELA KEMPER URGATE", "postalCode", "42501", "regime", "601", "use", "G03"));
            Check((await service.Load()).Get("fiscal", auto.S("id")).S("status") == "simulated-stamped", "auto-stamp times new fiscal requests immediately");

            // Owner notices go out by simulated e-mail; the simulated printer resolves jobs.
            await Act("cash.limit", J.O("limit", 50000, "block", false));
            var v6 = await Act("visit.quick", new());
            await Act("command.send", J.O("visitId", v6.S("id"), "items", new[] { J.O("productId", soup.S("id"), "quantity", 1) }));
            await Act("payment.add", J.O("visitId", v6.S("id"), "amount", 5000, "method", "cash"));
            state = await service.Load();
            var notice = state.List("ownernotice").Single(x => x.S("type") == "cash-limit");
            Check(notice.S("messageId") != "" && state.Get("message", notice.S("messageId")).S("to") == "dueno@example.mx", "owner notice is sent by simulated e-mail");
            await Act("drawer.request", J.O("reason", "Arqueo"));
            Check((await service.Load()).List("printjob").Any(x => x.S("status") == "pending"), "without the printer simulator jobs stay pending");
            await Act("integration.configure", J.O("provider", "printer", "mode", "simulated"));
            state = await service.Load();
            Check(state.List("printjob").All(x => x.S("status") != "pending") && state.List("printjob").Any(x => x.S("status") == "simulated" && x.S("evidence").Contains("simulad")), "printer simulator resolves pending jobs as simulated");
            var drawer = await Act("drawer.request", J.O("reason", "Cambio"));
            Check((await service.Load()).Get("printjob", drawer.S("id")).S("status") == "simulated", "drawer pulse is simulated, not physical");
            await Deny(() => Act("message.test", J.O("channel", "email", "to", "no-es-correo")), "test message validates the recipient");
            Check((await Act("message.test", J.O("channel", "whatsapp", "to", "5512345678"))).S("status") == "simulated-sent", "test WhatsApp is recorded as simulated");

            // Visibility and reports.
            var waiterView = await service.State(anaToken);
            Check(waiterView.A("messages").Count == 0 && waiterView.A("integrationlogs").Count == 0 && waiterView.A("pmsposts").Count == 0 && waiterView.A("integrations").Objects().All(x => x.Count == 2), "waiters only see which simulators are on, not logs, messages or guests");
            Check(waiterView.A("terminaltxns").Count == 4 && !waiterView.A("terminaltxns").Objects().Any(x => x.S("visitId") == quick.S("visitId")), "waiters only see terminal charges of their own accounts");
            var log = (await service.Load()).List("integrationlog").ToArray();
            Check(log.All(x => x.B("simulated")) && new[] { "pac", "terminal", "onlinepay", "pms", "maps", "callerid", "aggregators" }.All(k => log.Any(x => x.S("provider") == k)), "every simulated operation is written to the integration log");
            var x = await Act("report.x", new());
            Check(new[] { "online", "room", "platform", "card" }.All(m => x.Obj("methods").ContainsKey(m)), "X reading separates online, room and platform payments");

        } finally { runtime.Dispose(); }

    }
}
