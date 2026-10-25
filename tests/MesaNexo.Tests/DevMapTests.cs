using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
namespace MesaNexo.Tests;

static class DevMapTests {
    public static async Task Run(string root, string python, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception(name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        async Task Deny(Func<Task> action, string name) { try { await action(); throw new Exception("Expected rejection: " + name); } catch (BusinessException) { Check(true, name); } }
        var folder = Path.Combine(root, ".lab", "devmap-" + J.Id());
        var password = "Test-" + J.Id();
        var enrollment = J.Id();
        string token = "";
        RuntimeStore? runtime = null;
        BranchService service = null!;
        void Start() { runtime = new RuntimeStore(python, Path.Combine(root, "runtime", "lab_host.py"), folder, "devmap-site"); service = new(runtime, "devmap-site", new Passwords()); var live = new IntegrationConfig(); live.Ready("printer"); live.Ready("pac"); service.UseIntegrations(live); TestLicense.Attach(service); }
        Start();
        try {

            await TestLicense.Initialize(service, J.O("siteId", "devmap-site", "name", "DEVMap isolated", "mode", "licencia", "managerName", "Manager", "managerUsername", "manager", "managerPassword", password, "enrollmentCode", enrollment, "modules", Restaurant.Modules));
            var device = J.O("siteId", "devmap-site", "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "isolated", "username", "manager", "password", password, "enrollmentCode", enrollment);
            token = (await service.Login(device)).S("token");
            async Task<JsonObject> Act(string type, JsonObject p, string? operation = null, string? auth = null) => (await service.Action(auth ?? token, J.O("operationId", operation ?? J.Id(), "type", type, "payload", p))).Obj("result");
            var waiter = await Act("user.save", J.O("name", "Service", "username", "waiter", "role", "mesero", "password", password));
            await Act("catalog.category", J.O("name", "Food"));
            var product = await Act("catalog.product", J.O("name", "Exact", "price", 101, "taxBps", 1600, "category", "Food", "station", "Cocina"));
            var cash = await Act("cash.open", J.O("initial", 0));
            var warehouse = await Act("inventory.warehouse", J.O("name", "Main", "costMethod", "average"));
            var secondary = await Act("inventory.warehouse", J.O("name", "Second", "costMethod", "fifo"));
            var flour = await Act("inventory.ingredient", J.O("name", "Flour", "unit", "g"));
            await Act("inventory.configure", J.O("warehouseId", warehouse.S("id"), "policy", "reserve-on-send-consume-on-close"));
            await Act("stock.receive", J.O("warehouseId", warehouse.S("id"), "ingredientId", flour.S("id"), "quantity", "1", "unit", "kg", "cost", 10000, "reference", "lot-1"));
            var recipe = await Act("recipe.save", J.O("name", "Portion", "yield", 1, "lines", new[] { J.O("ingredientId", flour.S("id"), "quantity", "100", "unit", "g") }));
            await Act("recipe.bind", J.O("recipeId", recipe.S("recipeId"), "productId", product.S("id")));
            var visit = await Act("visit.quick", new());
            var vid = visit.S("id");
            var command = await Act("command.send", J.O("visitId", vid, "items", new[] { J.O("productId", product.S("id"), "quantity", 3) }));
            var units = command.A("unitIds").Strings();
            Check((await service.Load()).Get("visit", vid).S("tableId") == "", "quick ticket opens without table");
            Check((await service.Load()).List("stock").Single().L("reserved") == 300000, "command atomically reserves exact ingredient quantities before cooking");
            await Act("visit.issueBill", J.O("id", vid));
            await Deny(() => Act("command.send", J.O("visitId", vid, "items", new[] { J.O("productId", product.S("id"), "quantity", 1) })), "printed bill locks consumption at backend");
            await Deny(() => Act("visit.unlock", J.O("id", vid)), "printed bill reopening needs reason");
            await Act("visit.unlock", J.O("id", vid, "reason", "Correct before settlement"));
            var split = await Act("visit.divide", J.O("id", vid, "parts", 2));
            var state = await service.Load();
            var folios = state.List("folio").Where(x => x.S("visitId") == vid).OrderBy(x => x.L("number")).ToArray();
            Check(folios.Sum(x => x.L("total")) == 303 && folios[0].L("total") == 152 && folios.Sum(x => x.L("tax")) == 42, "equal split conserves total tax and deterministic residual cent");
            Check(state.List("unit").Count() == 3 && state.List("command").Count() == 1, "financial splitting never duplicates cooking units");
            await Deny(() => Act("payment.add", J.O("visitId", vid, "amount", 153, "folioId", folios[0].S("id"), "method", "cash")), "subfolio prevents overpayment");
            var payment = await Act("payment.add", J.O("visitId", vid, "amount", 152, "folioId", folios[0].S("id"), "method", "cash", "tendered", 200));
            Check(payment.L("change") == 48, "cash payment records exact change");
            await Act("payment.add", J.O("visitId", vid, "amount", 151, "folioId", folios[1].S("id"), "method", "cash"));
            foreach (var uid in units) { await Act("unit.state", J.O("id", uid, "status", "preparing")); await Act("unit.state", J.O("id", uid, "status", "ready")); await Act("unit.state", J.O("id", uid, "status", "delivered")); }
            await Act("recipe.save", J.O("id", recipe.S("recipeId"), "name", "Portion", "yield", 1, "lines", new[] { J.O("ingredientId", flour.S("id"), "quantity", "200", "unit", "g") }));
            var closeId = J.Id();
            await Act("visit.close", J.O("id", vid), closeId);
            await Act("visit.close", J.O("id", vid), closeId);
            state = await service.Load();
            Check(state.List("stockjob").Count() == 1 && state.List("stock").Single(x => x.S("warehouseId") == warehouse.S("id")).L("reserved") == 300000, "sale close and frozen recipe consumption job commit atomically once");
            runtime!.Dispose();
            Start();
            await Task.WhenAll(service.ProcessInventory(), service.ProcessInventory());
            state = await service.Load();
            var stock = state.List("stock").Single(x => x.S("warehouseId") == warehouse.S("id"));
            Check(stock.L("quantity") == 700000 && stock.L("reserved") == 0 && stock.L("value") == 7000 && state.List("stockmovement").Count(x => x.S("type") == "sale") == 1, "restart and competing workers consume stock exactly once");
            Check(!await service.ProcessInventory(), "completed stock job is not consumed again");
            var sub = await Act("recipe.save", J.O("name", "Sub", "yield", 1, "lines", new[] { J.O("recipeId", recipe.S("recipeId"), "quantity", 1, "unit", "unit") }));
            await Deny(() => Act("recipe.save", J.O("id", recipe.S("recipeId"), "name", "Portion", "yield", 1, "lines", new[] { J.O("recipeId", sub.S("recipeId"), "quantity", 1, "unit", "unit") })), "recipe graph rejects indirect cycles");
            await Deny(() => Act("stock.waste", J.O("warehouseId", warehouse.S("id"), "ingredientId", flour.S("id"), "quantity", 1, "unit", "l", "reason", "invalid dimension")), "inventory rejects cross-dimension unit conversion");
            await Deny(() => Act("stock.waste", J.O("warehouseId", warehouse.S("id"), "ingredientId", flour.S("id"), "quantity", 1, "unit", "kg", "reason", "too much")), "inventory disallows negative available stock");
            var transfer = await Act("transfer.create", J.O("fromWarehouseId", warehouse.S("id"), "toWarehouseId", secondary.S("id"), "ingredientId", flour.S("id"), "quantity", 100, "unit", "g", "reason", "kitchen"));
            await Deny(() => Act("transfer.receive", J.O("id", transfer.S("id"))), "warehouse transfer requires distinct identified receiver");
            var manager2 = await Act("user.save", J.O("name", "Receiver", "username", "receiver", "role", "manager", "password", password));
            var code2 = J.Id();
            await service.RotateEnrollment(code2);
            var second = (await service.Login(J.O("siteId", "devmap-site", "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "receiver", "username", "receiver", "password", password, "enrollmentCode", code2))).S("token");
            await Act("transfer.receive", J.O("id", transfer.S("id")), auth: second);
            state = await service.Load();
            Check(state.Get("transfer", transfer.S("id")).S("receiverId") == manager2.S("id") && state.List("stock").Sum(x => x.L("quantity")) == 700000, "dual approval transfers preserve aggregate stock");
            var job = state.List("printjob").First();
            var claimed = await Act("print.claim", J.O("id", job.S("id"), "driver", "test-double"));
            await Deny(() => Act("print.claim", J.O("id", job.S("id"), "driver", "test-double")), "uncertain physical print is never automatically retried");
            await Act("print.resolve", J.O("id", job.S("id"), "attemptId", claimed.S("attemptId"), "status", "uncertain", "evidence", "Injected lost driver response"));
            Check((await service.Load()).List("payment").Count() == 2, "physical job handling never repeats commercial payment");
            await Act("cash.close", J.O("id", cash.S("id"), "counted", 303));
            var cashier = await Act("user.save", J.O("name", "Cashier", "username", "cashier", "role", "cajero", "password", password));
            var cc = J.Id();
            await service.RotateEnrollment(cc);
            var cashierToken = (await service.Login(J.O("siteId", "devmap-site", "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "cashier", "username", "cashier", "password", password, "enrollmentCode", cc))).S("token");
            var blind = await Act("cash.blind.open", J.O("initial", 10000), auth: cashierToken);
            var blindState = await service.State(cashierToken);
            Check(!blindState.ContainsKey("payments") && !blindState.ContainsKey("visits") && !blindState.ToJsonString().Contains("expected") && !blindState.ToJsonString().Contains("initial"), "blind cashier API hides expected totals and reconstructable financial sources");
            await Deny(() => service.Report(cashierToken, "", "", null), "blind operator cannot bypass restriction through report/export endpoint");
            await Deny(() => Act("cash.close", J.O("id", blind.S("id"), "counted", 10000)), "blind session rejects legacy nonblind close endpoint");
            async Task<bool> Count() { try { await Act("cash.blind.close", J.O("id", blind.S("id"), "denominations", new[] { J.O("value", 10000, "count", 1) }, "card", 0, "transfer", 0), auth: cashierToken); return true; } catch (BusinessException) { return false; } }
            var racing = await Task.WhenAll(Count(), Count());
            Check(racing.Count(x => x) == 1, "simultaneous blind closes produce one immutable close");
            Check(!(await service.State(cashierToken)).ToJsonString().Contains("difference"), "closed blind report remains hidden from operator");
            await Act("cash.adjust", J.O("id", blind.S("id"), "method", "cash", "direction", "increase", "amount", 100, "reason", "Authorized correction"));
            state = await service.Load();
            Check(state.Get("cash", blind.S("id")).L("counted") == 10000 && state.List("cashadjustment").Count() == 1 && state.Get("user", cashier.S("id")).B("active"), "cash adjustment preserves close and personal user access");
            Check(state.List("ownernotice").Any(x => x.S("cashId") == blind.S("id") && x.S("status") == "pending"), "owner close notification persists locally without WAN");
            await Act("cash.open", J.O("initial", 0));
            var customer = await Act("customer.save", J.O("name", "Local Customer", "phone", "000", "notes", "test"));
            var address = await Act("address.save", J.O("customerId", customer.S("id"), "label", "Home", "text", "Synthetic address", "lat", 1, "lon", 1));
            var polygon = new[] { J.O("lat", 0, "lon", 0), J.O("lat", 0, "lon", 2), J.O("lat", 2, "lon", 2), J.O("lat", 2, "lon", 0) };
            await Act("zone.save", J.O("name", "Local", "polygon", polygon, "fee", 3000, "priority", 1));
            var delivery = await Act("delivery.create", J.O("customerId", customer.S("id"), "addressId", address.S("id"), "mode", "delivery"));
            var courier = await Act("courier.save", J.O("name", "Courier"));
            await Act("delivery.assign", J.O("id", delivery.S("id"), "courierId", courier.S("id")));
            state = await service.Load();
            Check(service.Domain.Totals(state, state.Get("visit", delivery.S("visitId"))).L("paid") == 0 && state.Get("delivery", delivery.S("id")).L("responsibility") == 3000, "courier assignment records responsibility without inventing payment");
            await Act("delivery.dispatch", J.O("id", delivery.S("id")));
            await Act("delivery.reconcile", J.O("id", delivery.S("id"), "amount", 3000, "method", "cash", "reference", "cash counted"));
            Check((await service.Load()).Get("delivery", delivery.S("id")).S("status") == "reconciled", "courier liability clears only after authorized reconciliation");
            Check(Restaurant.InPolygon(new JsonArray(polygon.Select(x => (JsonNode)x.DeepClone()).ToArray()), 0, 1), "local delivery tariff includes polygon boundary");
            await Act("fiscal.portal.configure", J.O("url", "https://invoice.example.invalid"));
            var qr = await Act("fiscal.token", J.O("visitId", vid));
            Check(qr.S("url").Split('#')[1].Length == 64 && !qr.S("url").Contains(vid), "invoice URL uses opaque expiring token without ticket identifiers");
            var fiscal = await Act("fiscal.request", J.O("visitId", vid, "rfc", "XAXX010101000", "name", "PUBLICO EN GENERAL", "postalCode", "01000", "regime", "616", "use", "S01"));
            Check(fiscal.S("status") == "pending" && !fiscal.ContainsKey("fiscalUuid"), "offline fiscal request never invents stamp or fiscal UUID");

            // User-selected reservation policy: unprepared cancellation releases; prepared cancellation becomes waste.
            var policyVisit = await Act("visit.quick", new());
            var policyCommand = await Act("command.send", J.O("visitId", policyVisit.S("id"), "items", new[] { J.O("productId", product.S("id"), "quantity", 2) }));
            var policyUnits = policyCommand.A("unitIds").Strings();
            var stockBefore = (await service.Load()).List("stock").Single(x => x.S("warehouseId") == warehouse.S("id")).L("quantity");
            var cancelBefore = await Act("request.create", J.O("visitId", policyVisit.S("id"), "type", "cancel", "unitId", policyUnits[0], "reason", "Not prepared"));
            await Act("request.decide", J.O("id", cancelBefore.S("id"), "approve", true));
            await Act("unit.state", J.O("id", policyUnits[1], "status", "preparing"));
            var cancelAfter = await Act("request.create", J.O("visitId", policyVisit.S("id"), "type", "cancel", "unitId", policyUnits[1], "reason", "Already prepared waste"));
            await Act("request.decide", J.O("id", cancelAfter.S("id"), "approve", true));
            state = await service.Load();
            Check(state.Get("unitstock", policyUnits[0]).S("status") == "released-unprepared" && state.Get("unitstock", policyUnits[1]).S("status") == "waste-queued", "cancellation releases only unprepared stock and durably queues prepared waste");
            while (await service.ProcessInventory()) { }
            state = await service.Load();
            Check(state.List("stock").Single(x => x.S("warehouseId") == warehouse.S("id")).L("quantity") == stockBefore - 100000 && state.List("stockmovement").Any(x => x.S("type") == "prepared-waste"), "prepared cancellation never silently restores stock");
            var costIngredient = await Act("inventory.ingredient", J.O("name", "Cost layers", "unit", "unit"));
            foreach (var wh in new[] { warehouse, secondary }) { await Act("stock.receive", J.O("warehouseId", wh.S("id"), "ingredientId", costIngredient.S("id"), "quantity", 2, "unit", "unit", "cost", 200, "reference", "lot-cheap")); await Act("stock.receive", J.O("warehouseId", wh.S("id"), "ingredientId", costIngredient.S("id"), "quantity", 2, "unit", "unit", "cost", 400, "reference", "lot-expensive")); await Act("stock.waste", J.O("warehouseId", wh.S("id"), "ingredientId", costIngredient.S("id"), "quantity", 1, "unit", "unit", "reason", "cost test")); }
            state = await service.Load();
            Check(state.List("stockmovement").Single(x => x.S("ingredientId") == costIngredient.S("id") && x.S("warehouseId") == warehouse.S("id") && x.S("type") == "waste").L("cost") == -150 && state.List("stockmovement").Single(x => x.S("ingredientId") == costIngredient.S("id") && x.S("warehouseId") == secondary.S("id") && x.S("type") == "waste").L("cost") == -100, "weighted average and FIFO retain different exact historical consumption costs");
            await Act("promotion.save", J.O("name", "Web only coupon", "coupon", "WEB10", "startHour", 0, "endHour", 24, "percent", 10, "productIds", new[] { product.S("id") }, "active", true));
            var couponVisit = await Act("visit.quick", new());
            var couponCommand = await Act("command.send", J.O("visitId", couponVisit.S("id"), "items", new[] { J.O("productId", product.S("id"), "quantity", 1) }));
            Check((await service.Load()).Get("unit", couponCommand.A("unitIds").Strings().Single()).L("price") == 101, "web coupon is never silently applied as automatic promotion");
            var printVisit = await Act("visit.quick", new());
            await Act("visit.issueBill", J.O("id", printVisit.S("id")));
            var fixturePrinter = new FixturePrinter();
            await service.ProcessPrint(fixturePrinter);
            Check(!await service.ProcessPrint(fixturePrinter) && fixturePrinter.Calls == 1, "claimed uncertain physical effect is not executed twice by print service double");
            // Test-only sandbox authorization fixture: no API/CLI route activates external adapters.
            state = await service.Load();
            state.Get("fiscal", fiscal.S("id"))["sandboxAuthorized"] = true;
            state.Meta["revision"] = state.Meta.L("revision") + 1;
            await runtime!.CallAsync(J.O("op", "transaction_v1", "protocol", 1, "event_id", J.Id(), "receipt", J.O("actor", "sandbox-fixture", "fingerprint", J.Id(), "result", new JsonObject()), "changes", new JsonArray(state.Changes().Select(x => (JsonNode)x).ToArray())));
            var fixturePac = new FixturePac();
            var issuer = J.O("expeditionPlace", "01000", "paymentForm", "01", "products", J.O(product.S("id"), J.O("productCode", "90101501", "unitCode", "E48", "taxObject", "02", "rateBps", 1600)));
            await service.ProcessFiscal(fixturePac, issuer);
            var stamped = (await service.Load()).Get("fiscal", fiscal.S("id"));
            Check(stamped.S("status") == "sandbox-stamped" && !stamped.ContainsKey("fiscalUuid") && fixturePac.Last?.A("Items").Objects().Sum(x => x["Total"]!.GetValue<decimal>()) == 3.03m, "synthetic PAC transport keeps sandbox label and exact historical CFDI total");
            state = await service.Load();
            Check(state.List("audit").SelectMany(a => a.A("changes").Objects()).Any(c => c.Obj("before").S("name") == "Exact" && c.Obj("after").S("status") == "cancelled"), "financial audit identifies changed product with before and after values");
            Check((await service.VerifyAudit(token)).B("valid"), "append-only audit hash chain verifies across restarts");
            state = await service.Load();
            var audit = state.List("audit").First();
            audit["actor"] = "tampered";
            state.Meta["revision"] = state.Meta.L("revision") + 1;
            await runtime!.CallAsync(J.O("op", "transaction_v1", "protocol", 1, "event_id", J.Id(), "receipt", J.O("actor", "laboratory-tamper", "fingerprint", J.Id(), "result", new JsonObject()), "changes", new JsonArray(state.Changes().Select(x => (JsonNode)x).ToArray())));
            Check(!(await service.VerifyAudit(token)).B("valid"), "audit verification detects altered encrypted historical record with unchanged chain anchor");
            await Deny(() => service.Action(token, J.O("operationId", J.Id(), "type", "unknown.operation", "payload", new JsonObject())), "unknown domain operations do not commit");

        } finally { runtime?.Dispose(); }

    }
}
