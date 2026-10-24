using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
namespace MesaNexo.Tests;
// Stage 2 of the ecosystem sheet: suppliers, purchases, payables, min/max, physical count, expenses, petty cash, cash limit and PIN.
static class BackofficeTests {
    public static async Task Run(string root, string python, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception(name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        async Task Deny(Func<Task> action, string name) { try { await action(); throw new Exception("Expected rejection: " + name); } catch (BusinessException) { Check(true, name); } }
        var folder = Path.Combine(root, ".lab", "back-" + J.Id());
        var password = "Test-" + J.Id();
        var enrollment = J.Id();
        var runtime = new RuntimeStore(python, Path.Combine(root, "runtime", "lab_host.py"), folder, "back-site");
        var service = new BranchService(runtime, "back-site", new Passwords());
        try {

            await service.Initialize(J.O("siteId", "back-site", "name", "Back office", "mode", "licencia", "managerName", "Manager", "managerUsername", "manager", "managerPassword", password, "enrollmentCode", enrollment, "modules", Restaurant.Modules));
            var token = (await service.Login(J.O("siteId", "back-site", "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "office", "username", "manager", "password", password, "enrollmentCode", enrollment))).S("token");
            async Task<JsonObject> Act(string type, JsonObject p) => (await service.Action(token, J.O("operationId", J.Id(), "type", type, "payload", p))).Obj("result");
            var ana = await Act("user.save", J.O("name", "Ana", "username", "ana", "role", "mesero", "password", password));
            var anaCode = J.Id();
            await service.RotateEnrollment(anaCode);
            var anaToken = (await service.Login(J.O("siteId", "back-site", "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "pos", "username", "ana", "password", password, "enrollmentCode", anaCode))).S("token");
            async Task<JsonObject> Waiter(string type, JsonObject p) => (await service.Action(anaToken, J.O("operationId", J.Id(), "type", type, "payload", p))).Obj("result");
            var manager = (await service.Load()).List("user").First(x => x.S("role") == "manager");
            var store = await Act("inventory.warehouse", J.O("name", "Bodega", "costMethod", "average"));
            var beef = await Act("inventory.ingredient", J.O("name", "Res", "unit", "g"));
            var bun = await Act("inventory.ingredient", J.O("name", "Pan", "unit", "unit"));
            var wid = store.S("id");

            // Suppliers and stock levels.
            var supplier = await Act("supplier.save", J.O("name", "Carnes del Norte", "rfc", "cdn010101ab1", "email", "ventas@carnes.mx", "phone", "55 1234 5678", "creditDays", 15));
            Check(supplier.S("rfc") == "CDN010101AB1" && supplier.L("creditDays") == 15 && supplier.B("active"), "supplier is saved with normalized RFC and credit days");
            await Deny(() => Act("supplier.save", J.O("name", "carnes del norte", "creditDays", 0)), "duplicate supplier name is rejected");
            await Deny(() => Act("supplier.save", J.O("name", "Otro", "rfc", "XYZ", "creditDays", 0)), "invalid supplier RFC is rejected");
            await Deny(() => Act("inventory.levels", J.O("ingredientId", beef.S("id"), "unit", "kg", "min", "10", "max", "2")), "maximum below minimum is rejected");
            await Act("inventory.levels", J.O("ingredientId", beef.S("id"), "unit", "kg", "min", "2", "max", "10", "supplierId", supplier.S("id")));
            var levels = (await service.Load()).Get("ingredient", beef.S("id"));
            Check(levels.L("minStock") == 2_000_000 && levels.L("maxStock") == 10_000_000 && levels.S("supplierId") == supplier.S("id"), "ingredient keeps exact min/max and preferred supplier");

            // Purchase order, receipt with differences and payable.
            var order = await Act("purchase.order", J.O("supplierId", supplier.S("id"), "warehouseId", wid, "lines", new[] { J.O("ingredientId", beef.S("id"), "quantity", "5", "unit", "kg", "cost", 75000), J.O("ingredientId", bun.S("id"), "quantity", "20", "unit", "unit", "cost", 20000) }));
            Check(order.S("status") == "ordered" && order.L("number") == 1 && order.L("total") == 95000, "purchase order is numbered and totaled");
            Check(!(await service.Load()).List("stock").Any(x => x.L("quantity") > 0), "an order alone does not change stock");
            var receipt = await Act("purchase.receive", J.O("id", order.S("id"), "invoice", "F-100", "lines", new[] { J.O("ingredientId", beef.S("id"), "quantity", "4.5", "unit", "kg", "cost", 67500), J.O("ingredientId", bun.S("id"), "quantity", "20", "unit", "unit", "cost", 20000) }));
            var state = await service.Load();
            var beefStock = state.Get("stock", wid + "-" + beef.S("id"));
            var payable = state.Get("payable", receipt.S("payableId"));
            Check(beefStock.L("quantity") == 4_500_000 && beefStock.L("value") == 67500, "receipt adds the quantity actually received at its cost");
            Check(receipt.L("differences") == 1 && state.Get("purchase", order.S("id")).S("status") == "received", "receipt records differences against the order");
            Check(payable.L("amount") == 87500 && payable.S("status") == "open" && Math.Abs((DateTimeOffset.Parse(payable.S("dueAt")) - DateTimeOffset.UtcNow.AddDays(15)).TotalMinutes) < 5, "receipt creates a payable due after the supplier credit days");
            Check(state.Get("ingredient", beef.S("id")).L("lastCost") == 15000, "last purchase cost is kept per kilogram");
            await Deny(() => Act("purchase.receive", J.O("id", order.S("id"), "invoice", "F-100")), "an order cannot be received twice");

            // Min/max suggestion counts what is already on order.
            var none = await Act("purchase.suggest", J.O("warehouseId", wid));
            Check(none.A("lines").Count == 0, "no suggestion while stock is above minimum");
            await Act("stock.waste", J.O("warehouseId", wid, "ingredientId", beef.S("id"), "quantity", "3", "unit", "kg", "reason", "Caducado"));
            var suggestion = await Act("purchase.suggest", J.O("warehouseId", wid, "create", true));
            var line = suggestion.A("lines").Objects().Single();
            Check(line.L("suggest") == 8_500_000 && line.L("estimated") == 127500 && suggestion.A("orders").Count == 1, "suggestion refills to maximum and creates the supplier order");
            Check((await Act("purchase.suggest", J.O("warehouseId", wid))).A("lines").Count == 0, "pending suggested order is counted as incoming");
            await Act("purchase.cancel", J.O("id", suggestion.A("orders")[0]!.GetValue<string>(), "reason", "Proveedor sin existencia"));
            Check((await Act("purchase.suggest", J.O("warehouseId", wid))).A("lines").Count == 1, "cancelled order no longer counts as incoming");

            // Paying suppliers from bank and register.
            await Act("cash.open", J.O("initial", 300000));
            await Deny(() => Act("payable.pay", J.O("id", payable.S("id"), "amount", 1000, "method", "transfer", "source", "register")), "register pays suppliers only in cash");
            await Act("payable.pay", J.O("id", payable.S("id"), "amount", 50000, "method", "transfer", "source", "bank", "reference", "SPEI 991"));
            Check((await service.Load()).Get("payable", payable.S("id")).L("paid") == 50000, "partial payment keeps the payable open");
            await Deny(() => Act("payable.pay", J.O("id", payable.S("id"), "amount", 40000, "method", "transfer", "source", "bank")), "payment above the balance is rejected");
            await Act("payable.pay", J.O("id", payable.S("id"), "amount", 37500, "method", "cash", "source", "register"));
            state = await service.Load();
            Check(state.Get("payable", payable.S("id")).S("status") == "paid" && state.List("movement").Any(x => x.S("payableId") == payable.S("id") && x.S("type") == "expense" && x.L("amount") == 37500), "cash payment from the register settles the payable and records the expense");

            // Physical count: theoretical vs counted at average cost.
            var count = await Act("stock.count", J.O("warehouseId", wid, "note", "Cierre de semana", "lines", new[] { J.O("ingredientId", beef.S("id"), "quantity", "1.2", "unit", "kg"), J.O("ingredientId", bun.S("id"), "quantity", "22", "unit", "unit") }));
            var beefLine = count.A("lines").Objects().First(x => x.S("ingredientId") == beef.S("id"));
            Check(beefLine.L("theoretical") == 1_500_000 && beefLine.L("difference") == -300_000 && count.L("shortageCost") == 4500 && count.L("surplusCost") == 2000, "physical count values shortage and surplus at average cost");
            Check((await service.Load()).Get("stock", wid + "-" + beef.S("id")).L("quantity") == 1_200_000, "count leaves stock at the counted quantity");
            var variance = await Act("inventory.variance", J.O("from", DateTimeOffset.UtcNow.AddDays(-1).ToString("O"), "to", DateTimeOffset.UtcNow.AddMinutes(1).ToString("O")));
            Check(variance.L("waste") == 45000 && variance.L("shortage") == 4500 && variance.L("surplus") == 2000 && variance.L("real") == 47500 && variance.L("theoretical") == 0, "variance separates theoretical consumption, waste and count differences");
            await Deny(() => Act("inventory.variance", J.O("from", "2026-01-01", "to", "2025-01-01")), "variance rejects an inverted period");

            // Expense accounts and petty cash.
            var cleaning = await Act("expense.account", J.O("name", "Limpieza", "budget", 150000));
            await Deny(() => Act("expense.account", J.O("name", "limpieza")), "duplicate expense account is rejected");
            await Act("petty.configure", J.O("fund", 200000));
            await Deny(() => Act("expense.record", J.O("accountId", cleaning.S("id"), "amount", 30000, "source", "petty", "description", "Cloro")), "petty cash cannot go negative");
            await Act("petty.fund", J.O("amount", 100000, "fromRegister", true, "reason", "Fondo inicial"));
            await Act("expense.record", J.O("accountId", cleaning.S("id"), "amount", 30000, "source", "petty", "description", "Cloro y jabón", "receipt", "T-55"));
            await Act("expense.record", J.O("accountId", cleaning.S("id"), "amount", 10000, "source", "register", "description", "Bolsas"));
            state = await service.Load();
            var pettyBalance = state.List("pettymove").Sum(x => x.S("type") == "in" ? x.L("amount") : -x.L("amount"));
            Check(pettyBalance == 70000 && state.List("expense").Count(x => x.S("accountId") == cleaning.S("id")) == 2, "expenses are charged to their account from petty cash or register");
            Check(state.List("movement").Any(x => x.S("type") == "withdrawal" && x.L("amount") == 100000) && state.List("movement").Any(x => x.S("type") == "expense" && x.L("amount") == 10000), "petty replenishment and register expense leave the drawer");

            // Cash limit with blocking and partial withdrawal. Drawer: 300000 - 37500 - 100000 - 10000 = 152500.
            await Act("catalog.category", J.O("name", "Food"));
            var big = await Act("catalog.product", J.O("name", "Banquete", "price", 50000, "taxBps", 1600, "category", "Food", "station", "Cocina"));
            var soda = await Act("catalog.product", J.O("name", "Soda", "price", 3000, "taxBps", 1600, "category", "Food", "station", "Cocina"));
            await Act("cash.limit", J.O("limit", 150000, "block", true));
            await Deny(() => Act("sale.quick", J.O("method", "cash", "items", new[] { J.O("productId", soda.S("id"), "quantity", 1) })), "cash sale is blocked while the drawer is over the limit");
            Check((await Act("sale.quick", J.O("method", "card", "items", new[] { J.O("productId", soda.S("id"), "quantity", 1) }))).L("total") == 3000, "card sale is still allowed over the cash limit");
            await Deny(() => Act("cash.withdraw", J.O("amount", 999999)), "withdrawal cannot exceed the drawer");
            var withdrawal = await Act("cash.withdraw", J.O("amount", 50000, "envelope", "S-01"));
            Check(withdrawal.L("drawer") == 102500, "partial withdrawal reports the remaining drawer cash");
            await Act("sale.quick", J.O("method", "cash", "items", new[] { J.O("productId", big.S("id"), "quantity", 1) }));
            Check((await service.Load()).List("ownernotice").Any(x => x.S("type") == "cash-limit" && x.L("amount") == 152500), "crossing the cash limit queues an owner notice");
            await Deny(() => Act("sale.quick", J.O("method", "cash", "items", new[] { J.O("productId", soda.S("id"), "quantity", 1) })), "next cash sale waits for the withdrawal");

            // PIN per user and authorization on the same device.
            await Deny(() => Act("user.pin", J.O("id", manager.S("id"), "passcode", "1234")), "sequential PIN is rejected");
            await Deny(() => Act("user.pin", J.O("id", manager.S("id"), "passcode", "7777")), "repeated-digit PIN is rejected");
            await Act("user.pin", J.O("id", manager.S("id"), "passcode", "4826"));
            await Deny(() => Waiter("user.pin", J.O("id", manager.S("id"), "passcode", "5931")), "a waiter cannot set another person's PIN");
            Check((await Waiter("user.pin", J.O("passcode", "5931"))).B("hasPin"), "a waiter sets their own PIN");
            var managerView = (await service.State(token)).A("users").Objects().First(x => x.S("id") == manager.S("id"));
            var waiterView = (await service.State(anaToken)).A("users").Objects().First(x => x.S("id") == manager.S("id"));
            Check(!managerView.ContainsKey("pinHash") && managerView.B("hasPin") && waiterView.B("hasPin") && !waiterView.ContainsKey("pinHash"), "PIN hash never leaves the server; waiters only see who has a PIN");
            await Act("cash.withdraw", J.O("amount", 100000));
            var visit = await Waiter("visit.quick", new());
            await Waiter("command.send", J.O("visitId", visit.S("id"), "items", new[] { J.O("productId", soda.S("id"), "quantity", 1) }));
            var request = await Waiter("request.create", J.O("visitId", visit.S("id"), "type", "comp", "reason", "Cliente frecuente"));
            await Deny(() => Waiter("request.decide", J.O("id", request.S("id"), "approve", true)), "a waiter cannot approve without a manager");
            var wrong = await Waiter("authorize.pin", J.O("approverId", manager.S("id"), "passcode", "0000", "action", J.O("type", "request.decide", "payload", J.O("id", request.S("id"), "approve", true))));
            Check(!wrong.B("ok", true) && wrong.L("remaining") == 4 && (await service.Load()).Get("user", manager.S("id")).L("pinFails") == 1, "wrong PIN is refused and the failed attempt is kept");
            var right = await Waiter("authorize.pin", J.O("approverId", manager.S("id"), "passcode", "4826", "action", J.O("type", "request.decide", "payload", J.O("id", request.S("id"), "approve", true))));
            state = await service.Load();
            var decided = state.Get("request", request.S("id"));
            Check(right.B("ok") && decided.S("status") == "approved" && decided.S("decidedBy") == manager.S("id") && state.Get("user", manager.S("id")).L("pinFails") == 0, "manager PIN approves the request on the waiter's device");
            Check(state.List("pinauth").Count(x => x.S("approverId") == manager.S("id")) == 2 && state.List("pinauth").Any(x => x.B("ok") && x.S("actor") == ana.S("id")), "every PIN attempt is logged with the requesting employee");
            await Deny(() => Waiter("authorize.pin", J.O("approverId", manager.S("id"), "passcode", "4826", "action", J.O("type", "user.save", "payload", J.O("name", "X", "username", "x", "role", "manager", "password", password)))), "PIN cannot authorize operations outside the allowed list");
            for (var i = 0; i < 5; i++) await Waiter("authorize.pin", J.O("approverId", manager.S("id"), "passcode", "1357", "action", J.O("type", "drawer.request", "payload", J.O("reason", "Cambio"))));
            await Deny(() => Waiter("authorize.pin", J.O("approverId", manager.S("id"), "passcode", "4826", "action", J.O("type", "drawer.request", "payload", J.O("reason", "Cambio")))), "five wrong PINs lock the manager PIN for five minutes");

            // Fine-grained permissions and back-office visibility.
            await Act("user.save", J.O("id", ana.S("id"), "name", "Ana", "username", "ana", "role", "mesero", "permissions", J.O("payments", true, "shiftReports", true, "quickSale", false, "mergeTables", false, "transfer", true)));
            await Deny(() => Waiter("sale.quick", J.O("method", "card", "items", new[] { J.O("productId", soda.S("id"), "quantity", 1) })), "counter sale permission can be removed from a waiter");
            var other = await Waiter("visit.quick", new());
            await Deny(() => Waiter("visit.mergeMany", J.O("id", visit.S("id"), "otherIds", new[] { other.S("id") })), "merge permission can be removed from a waiter");
            var waiterState = await service.State(anaToken);
            Check(new[] { "suppliers", "purchases", "payables", "expenses", "pettymoves", "pinauths", "stockcounts" }.All(k => waiterState.A(k).Count == 0), "waiters do not see purchases, payables, expenses or PIN logs");

        } finally { runtime.Dispose(); }

    }
}
