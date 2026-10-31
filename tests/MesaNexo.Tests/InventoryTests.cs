using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
namespace MesaNexo.Tests;

static class InventoryTests {
    public static async Task Run(string root, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        async Task Deny(Func<Task> act, string name) { try { await act(); throw new Exception("Rejection expected: " + name); } catch (BusinessException) { Check(true, name); } }
        var site = "inventory-site";
        var folder = Path.Combine(root, ".lab", "tests", site);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        if (Directory.Exists(TestLicense.Folder(site))) Directory.Delete(TestLicense.Folder(site), true);
        using var runtime = LabPostgres.Test(root, folder, site);
        var service = new BranchService(runtime, site, new Passwords());
        var password = "Inventory-" + J.Id();
        var enrollment = J.Id();
        await TestLicense.Initialize(service, J.O("siteId", site, "name", "Inventario", "mode", "licencia", "managerName", "Gerencia", "managerUsername", "gerencia", "managerPassword", password, "enrollmentCode", enrollment, "modules", Restaurant.Modules));
        var manager = J.O("siteId", site, "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "Terminal 1", "username", "gerencia", "password", password, "enrollmentCode", enrollment);
        var token = (await service.Login(manager)).S("token");
        async Task<JsonObject> Act(string type, JsonObject p, string? operation = null, string? auth = null) => (await service.Action(auth ?? token, J.O("operationId", operation ?? J.Id(), "type", type, "payload", p))).Obj("result");
        await Act("user.save", J.O("name", "Segunda terminal", "username", "segunda", "role", "manager", "password", password));
        var enrollSecond = J.Id() + J.Id();
        await service.RotateEnrollment(enrollSecond);
        var second = (await service.Login(J.O("siteId", site, "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "Terminal 2", "username", "segunda", "password", password, "enrollmentCode", enrollSecond))).S("token");
        await Act("catalog.category", J.O("name", "Bebidas"));
        var product = await Act("catalog.product", J.O("name", "Vino", "price", 10000, "taxBps", 1600, "category", "Bebidas", "station", "Cocina"));
        await Act("cash.open", J.O("initial", 0));
        var warehouse = (await Act("inventory.warehouse", J.O("name", "Principal", "costMethod", "average"))).S("id");
        var other = (await Act("inventory.warehouse", J.O("name", "Barra", "costMethod", "fifo"))).S("id");
        var wine = (await Act("inventory.ingredient", J.O("name", "Botella", "unit", "unit"))).S("id");
        await Act("inventory.configure", J.O("warehouseId", warehouse, "policy", "reserve-on-send-consume-on-close"));
        var recipe = await Act("recipe.save", J.O("name", "Copa", "yield", 1, "lines", new[] { J.O("ingredientId", wine, "quantity", 1, "unit", "unit") }));
        await Act("recipe.bind", J.O("recipeId", recipe.S("recipeId"), "productId", product.S("id")));
        await Act("stock.receive", J.O("warehouseId", warehouse, "ingredientId", wine, "quantity", 1, "unit", "unit", "cost", 3000, "reference", "L1"));
        async Task<JsonObject> Stock() => (await service.Load()).List("stock").Single(x => x.S("warehouseId") == warehouse);
        async Task<bool> Balanced() => (await service.CheckInventory()).B("balanced");
        Task<JsonObject> Send(string visit, int quantity, string? operation = null, string? auth = null) => Act("command.send", J.O("visitId", visit, "items", new[] { J.O("productId", product.S("id"), "quantity", quantity) }), operation, auth);
        // The last unit, from two terminals at once.
        var left = (await Act("visit.quick", new())).S("id");
        var right = (await Act("visit.quick", new(), null, second)).S("id");
        var race = await Task.WhenAll(new[] { (left, token), (right, second) }.Select(async x => { try { await Send(x.Item1, 1, null, x.Item2); return true; } catch (BusinessException e) { Check(e.Message.StartsWith("Stock insuficiente"), "the terminal that lost is told why"); return false; } }));
        Check(race.Count(x => x) == 1, "the last unit goes to exactly one of two terminals");
        var stock = await Stock();
        Check(stock.L("quantity") == 1000 && stock.L("reserved") == 1000, "the stock never goes negative");
        Check(await Balanced(), "the balance holds with a reservation open");
        // A retry of the same operation reserves nothing twice.
        await Act("stock.receive", J.O("warehouseId", warehouse, "ingredientId", wine, "quantity", 9, "unit", "unit", "cost", 27000, "reference", "L2"));
        var winner = race[0] ? left : right;
        var loser = race[0] ? right : left;
        var operation = J.Id();
        var sent = await Send(loser, 3, operation);
        await Send(loser, 3, operation);
        Check((await Stock()).L("reserved") == 4000, "a retried send reserves once");
        // Partial cancellation of a prepared command: one portion prepared, one not.
        var units = sent.A("unitIds").Strings();
        await Act("unit.state", J.O("id", units[0], "status", "preparing"));
        await Act("unit.state", J.O("id", units[0], "status", "ready"));
        foreach (var unit in units.Take(2)) {

            var request = await Act("request.create", J.O("visitId", loser, "unitId", unit, "type", "cancel", "reason", "El cliente cambió de idea"));
            await Act("request.decide", J.O("id", request.S("id"), "approve", true));

        }
        await service.ProcessInventory();
        var state = await service.Load();
        Check(state.Get("unitstock", units[0]).S("status") == "waste-queued" && state.Get("unitstock", units[1]).S("status") == "released-unprepared", "a prepared portion is waste, an unprepared one goes back");
        stock = await Stock();
        Check(stock.L("quantity") == 9000 && stock.L("reserved") == 2000, "only the prepared portion left the stock");
        var waste = state.List("stockmovement").Single(x => x.S("type") == "prepared-waste");
        Check(waste.A("recipeVersions").Strings().SequenceEqual([recipe.S("id")]) && waste.L("cost") == -3000, "waste keeps its recipe version and cost");
        Check(await Balanced(), "the balance holds after a partial cancellation");
        // A sale, closed with the recipe it was sent with, and returned later.
        foreach (var unit in units[2..].Concat((await service.Load()).List("unit").Where(x => x.S("visitId") == winner).Select(x => x.S("id")))) foreach (var status in new[] { "preparing", "ready", "delivered" }) await Act("unit.state", J.O("id", unit, "status", status));
        var due = (await service.Load()).List("unit").Where(x => x.S("visitId") == loser && x.S("status") == "delivered").Sum(x => x.L("price"));
        await Act("payment.add", J.O("visitId", loser, "amount", due, "method", "cash"));
        await Act("recipe.save", J.O("id", recipe.S("recipeId"), "name", "Copa", "yield", 1, "lines", new[] { J.O("ingredientId", wine, "quantity", 2, "unit", "unit") }));
        await Act("visit.close", J.O("id", loser));
        await service.ProcessInventory();
        state = await service.Load();
        var sale = state.List("stockmovement").Single(x => x.S("type") == "sale");
        Check(sale.L("quantity") == -1000 && sale.A("recipeVersions").Strings().SequenceEqual([recipe.S("id")]), "a sale keeps the recipe it was sent with, not the newer one");
        await Deny(() => Act("stock.return", J.O("movementId", sale.S("id"), "quantity", 2, "reason", "Más de lo vendido")), "a return cannot exceed the sale");
        var before = await Stock();
        await Act("stock.return", J.O("movementId", sale.S("id"), "quantity", "0.5", "reason", "Media botella sin abrir"));
        await Act("stock.return", J.O("movementId", sale.S("id"), "reason", "El resto"));
        await Deny(() => Act("stock.return", J.O("movementId", sale.S("id"), "reason", "Otra vez")), "a sale cannot be returned twice");
        stock = await Stock();
        Check(stock.L("quantity") == before.L("quantity") + 1000 && stock.L("value") == before.L("value") - sale.L("cost"), "returns add up to the sale, in units and in cost");
        Check((await service.Load()).Get("stockmovement", sale.S("id")).L("quantity") == -1000, "the sale itself is not rewritten");
        // Everything else that moves stock.
        var supplier = await Act("supplier.save", J.O("name", "Vinos del Valle", "creditDays", 0));
        var order = await Act("purchase.order", J.O("supplierId", supplier.S("id"), "warehouseId", warehouse, "lines", new[] { J.O("ingredientId", wine, "quantity", 4, "unit", "unit", "cost", 12000) }));
        await Act("purchase.receive", J.O("id", order.S("id"), "invoice", "F-1", "lines", new[] { J.O("ingredientId", wine, "quantity", 3, "unit", "unit", "cost", 9000) }));
        await Act("stock.waste", J.O("warehouseId", warehouse, "ingredientId", wine, "quantity", 1, "unit", "unit", "reason", "Botella rota"));
        var counted = (await Stock()).L("quantity") / 1000 - 1;
        await Act("stock.count", J.O("warehouseId", warehouse, "lines", new[] { J.O("ingredientId", wine, "quantity", counted, "unit", "unit") }));
        await Act("transfer.create", J.O("fromWarehouseId", warehouse, "toWarehouseId", other, "ingredientId", wine, "quantity", 1, "unit", "unit", "reason", "Barra"));
        Check(await Balanced(), "purchases, sales, waste, counts, returns and an open transfer reconcile");
        // A stock changed outside the movements.
        var row = (await runtime.CallAsync(J.O("op", "get", "record_id", (await service.Load()).Committed.Records.Single(x => x.Value.Data.S("kind") == "stock" && x.Value.Data.S("warehouseId") == warehouse).Key)))!;
        var data = (JsonObject)row.Obj("data").DeepClone();
        data["quantity"] = data.L("quantity") + 1000;
        var meta = (await runtime.CallAsync(J.O("op", "get", "record_id", "mn_meta_site")))!;
        var e = J.Id();
        await runtime.CallAsync(J.O("op", "transaction_v1", "protocol", 1, "event_id", e, "receipt", J.O("actor", "atacante", "fingerprint", e, "operation", "edit", "result", new JsonObject(), "at", J.Now()), "changes", new[] { J.O("record_id", "mn_meta_site", "expected_version", meta.L("version"), "data", meta.Obj("data").DeepClone()), J.O("record_id", row.S("record_id"), "expected_version", row.L("version"), "data", data) }));
        await Task.Delay(1100);
        var broken = await service.CheckInventory();
        Check(!broken.B("balanced") && broken.A("problems").Count == 1, "a stock changed outside its movements is caught");
        Check((await service.State(token)).Obj("connection").A("server").Objects().Any(x => x.S("message").Contains("no cuadran")), "the manager is told");

    }
}
