using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
namespace MesaNexo.Tests;

// Properties rather than examples: random accounts on the real domain, from a fixed seed so a failure can be repeated.
static class MoneyTests {
    const int Seed = 20261030, Pure = 50000, Accounts = 40;
    public static async Task Run(string root, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        async Task Deny(Func<Task> act, string name) { try { await act(); throw new Exception("Rejection expected: " + name); } catch (BusinessException) { Check(true, name); } }
        var random = new Random(Seed);
        for (var i = 0; i < Pure; i++) {

            var amount = random.NextInt64(0, 10_000_000);
            var weights = Enumerable.Range(0, random.Next(1, 12)).Select(_ => random.NextInt64(0, 100_000)).ToArray();
            if (weights.Sum() == 0) weights[0] = 1;
            var parts = Cents.Allocate(amount, weights);
            if (parts.Sum() != amount || parts.Any(x => x < 0)) throw new Exception($"Allocate({amount}, [{string.Join(",", weights)}])");
            for (var j = 0; j < parts.Length; j++) if (Math.Abs(parts[j] - amount * (decimal)weights[j] / weights.Sum()) >= 1) throw new Exception($"Allocate strayed more than a centavo: {amount}, [{string.Join(",", weights)}]");
            var tax = Cents.IncludedTax(amount, new long[] { 0, 800, 1600 }[i % 3]);
            if (tax < 0 || tax > amount) throw new Exception("Included tax out of range: " + amount);

        }
        Check(true, $"{Pure} random shares add up to the whole, each within a centavo of its proportion");
        Check(Cents.Round(0.5m) == 1 && Cents.Round(1.5m) == 2 && Cents.Round(2.5m) == 3 && Cents.Bps(5, 5000) == 3 && Cents.BpsFloor(5, 5000) == 2, "half a centavo rounds away from zero, and rewards round down");
        var site = "money-site";
        var folder = Path.Combine(root, ".lab", "tests", site);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        if (Directory.Exists(TestLicense.Folder(site))) Directory.Delete(TestLicense.Folder(site), true);
        using var runtime = LabPostgres.Test(root, folder, site);
        var service = new BranchService(runtime, site, new Passwords());
        var password = "Money-" + J.Id();
        var enrollment = J.Id();
        await TestLicense.Initialize(service, J.O("siteId", site, "name", "Dinero", "mode", "licencia", "managerName", "Gerencia", "managerUsername", "gerencia", "managerPassword", password, "enrollmentCode", enrollment, "modules", Restaurant.Modules));
        var token = (await service.Login(J.O("siteId", site, "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "Terminal", "username", "gerencia", "password", password, "enrollmentCode", enrollment))).S("token");
        async Task<JsonObject> Act(string type, JsonObject p) => (await service.Action(token, J.O("operationId", J.Id(), "type", type, "payload", p))).Obj("result");
        await Act("catalog.category", J.O("name", "Todo"));
        var cash = await Act("cash.open", J.O("initial", 0));
        var issuer = J.O("expeditionPlace", "06600", "product", J.O("productCode", "90101501", "unitCode", "E48", "taxObject", "02"));
        var receiver = J.O("rfc", "XAXX010101000", "name", "PUBLICO", "postalCode", "06600", "regime", "616", "use", "S01");
        long cashExpected = 0;
        string? lastCashPayment = null, lastCardPayment = null;
        for (var i = 0; i < Accounts; i++) {

            var bps = new long[] { 0, 800, 1600 };
            var count = random.Next(1, 7);
            var items = new List<JsonObject>();
            for (var j = 0; j < count; j++) items.Add(J.O("productId", (await Act("catalog.product", J.O("name", "P" + i + "-" + j, "price", random.NextInt64(1, 50000), "taxBps", bps[random.Next(3)], "category", "Todo", "station", "Cocina"))).S("id"), "quantity", 1));
            var visit = (await Act("visit.quick", new())).S("id");
            var units = (await Act("command.send", J.O("visitId", visit, "items", new JsonArray(items.Select(x => (JsonNode)x).ToArray())))).A("unitIds").Strings();
            foreach (var unit in units) foreach (var status in new[] { "preparing", "ready", "delivered" }) await Act("unit.state", J.O("id", unit, "status", status));
            var gross = (await service.Load()).Related("unit", "visitId", visit).Sum(x => x.L("price"));
            var discountKind = random.Next(3);
            var discount = discountKind == 1 ? J.O("kind", "percent", "value", random.Next(1, 61) / 2m) : discountKind == 2 ? J.O("kind", "amount", "value", random.NextInt64(0, gross / 4 + 1)) : null;
            if (discount is not null) { var request = await Act("request.create", J.O("visitId", visit, "type", "discount", "discount", discount, "reason", "Prueba")); await Act("request.decide", J.O("id", request.S("id"), "approve", true)); }
            var state = await service.Load();
            var totals = service.Domain.Totals(state, state.Get("visit", visit));
            Check(totals.L("gross") == gross && totals.L("discount") + totals.L("total") == gross && totals.L("tax") <= totals.L("total") && totals.L("balance") == totals.L("total"), $"account {i}: gross, discount, total and tax agree");
            var payments = new List<(string Id, string Method, long Charged)>();
            async Task Pay(long amount, string? folio) {

                var method = random.Next(2) == 0 ? "cash" : "card";
                var tip = random.Next(4) == 0 ? random.NextInt64(1, 3000) : 0;
                var pay = await Act("payment.add", folio is null ? J.O("visitId", visit, "amount", amount, "tip", tip, "method", method) : J.O("visitId", visit, "amount", amount, "tip", tip, "method", method, "folioId", folio));
                payments.Add((pay.S("id"), method, amount + tip));
                if (method == "cash") cashExpected += amount + tip;

            }
            var total = totals.L("total");
            if (total > 0 && random.Next(2) == 0) {

                var parts = random.Next(2, 7);
                await Act("visit.divide", J.O("id", visit, "parts", parts));
                var folios = (await service.Load()).Related("folio", "visitId", visit).Where(x => !x.B("void")).OrderBy(x => x.L("number")).ToArray();
                Check(folios.Sum(x => x.L("total")) == total && folios.Sum(x => x.L("tax")) == totals.L("tax") && folios.Sum(x => x.L("discount")) == totals.L("discount") && folios.Sum(x => x.L("gross")) == gross, $"account {i}: {parts} parts add up to total, tax, discount and gross");
                foreach (var folio in folios.Where(x => x.L("total") > 0)) await Pay(folio.L("total"), folio.S("id"));

            } else if (total > 0) {

                var first = random.NextInt64(1, total + 1);
                await Pay(first, null);
                if (first < total) await Pay(total - first, null);

            }
            await Act("visit.close", J.O("id", visit));
            state = await service.Load();
            var closed = service.Domain.Totals(state, state.Get("visit", visit));
            Check(closed.L("balance") == 0 && closed.L("paid") == total, $"account {i}: mixed payments cover the total exactly");
            if (total > 0) {

                var cfdi = service.BuildCfdi(state, J.O("visitId", visit, "totals", closed, "receiver", receiver, "folio", J.Id(), "issuedLocal", "2026-10-30T12:00:00"), (JsonObject)issuer.DeepClone());
                var lines = cfdi.A("Items").Objects().ToArray();
                var taxes = lines.Sum(x => x["Taxes"] is JsonArray t && t.Count > 0 ? t[0]!["Total"]!.GetValue<decimal>() : 0m);
                Check(lines.Sum(x => x["Total"]!.GetValue<decimal>()) == total / 100m && lines.Sum(x => x["Subtotal"]!.GetValue<decimal>()) + taxes == total / 100m && taxes == closed.L("tax") / 100m, $"account {i}: the CFDI matches the ticket in total and tax");

            }
            foreach (var pay in payments) { if (pay.Method == "cash" && pay.Charged > 200) lastCashPayment = pay.Id; if (pay.Method == "card") lastCardPayment = pay.Id; }

        }
        // Refunds, against the same cash register.
        await Deny(() => Act("payment.refund", J.O("id", lastCashPayment, "reason", "x", "amount", 100_000_000)), "a refund cannot exceed the charge");
        var charged = await service.Load().ContinueWith(x => { var p = x.Result.Get("payment", lastCashPayment!); return p.L("amount") + p.L("tip"); });
        var refund = await Act("payment.refund", J.O("id", lastCashPayment, "amount", 100, "reason", "Platillo equivocado"));
        Check(refund.S("route") == "cash" && refund.L("amount") == 100 && refund.S("creditNote") == "none", "a cash refund can be partial");
        await Act("payment.refund", J.O("id", lastCashPayment, "reason", "El resto"));
        await Deny(() => Act("payment.refund", J.O("id", lastCashPayment, "reason", "Otra vez")), "a payment cannot be refunded twice");
        var state2 = await service.Load();
        Check(state2.Related("movement", "cashId", cash.S("id")).Where(x => x.S("refundId") != "").Sum(x => x.L("amount")) == charged, "cash refunds leave the drawer as expenses tied to the refund");
        Check(state2.Get("payment", lastCashPayment!).L("amount") + state2.Get("payment", lastCashPayment!).L("tip") == charged, "the payment itself is not edited");
        var card = await Act("payment.refund", J.O("id", lastCardPayment, "reason", "Devuelto en el portal"));
        Check(card.S("route") == "manual", "a card without a live provider is recorded for a person to return");
        var close = await Act("cash.close", J.O("id", cash.S("id"), "counted", cashExpected - charged, "notes", "Cierre"));
        Check(close.L("difference") == 0, "the drawer closes to the centavo after random sales, tips and refunds");
        var trail = await Act("visit.trail", J.O("id", state2.Get("payment", lastCashPayment!).S("visitId")));
        Check(trail.A("units").Count > 0 && trail.A("payments").Count > 0 && trail.A("refunds").Objects().Where(x => x.S("paymentId") == lastCashPayment).Sum(x => x.L("amount")) == charged, "a sale can be followed from its units to its refunds");
        var open = (await Act("visit.quick", new())).S("id");
        await Deny(() => Act("payment.refund", J.O("id", lastCashPayment, "visitId", open, "reason", "x", "amount", 1)), "a refund never goes past the charge, whichever account asks");

    }
}
