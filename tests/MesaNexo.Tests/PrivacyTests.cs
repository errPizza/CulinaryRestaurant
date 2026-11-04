using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
namespace MesaNexo.Tests;

static class PrivacyTests {
    public static async Task Run(string root, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        async Task Deny(Func<Task> act, string name) { try { await act(); throw new Exception("Rejection expected: " + name); } catch (BusinessException) { Check(true, name); } }
        var site = "privacy-site";
        var folder = Path.Combine(root, ".lab", "tests", site);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        if (Directory.Exists(TestLicense.Folder(site))) Directory.Delete(TestLicense.Folder(site), true);
        using var runtime = LabPostgres.Test(root, folder, site);
        var service = new BranchService(runtime, site, new Passwords());
        var password = "Privacy-" + J.Id();
        var enrollment = J.Id();
        await TestLicense.Initialize(service, J.O("siteId", site, "name", "Privacidad", "mode", "licencia", "managerName", "Gerencia", "managerUsername", "gerencia", "managerPassword", password, "enrollmentCode", enrollment, "modules", Restaurant.Modules));
        var token = (await service.Login(J.O("siteId", site, "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "Terminal", "username", "gerencia", "password", password, "enrollmentCode", enrollment))).S("token");
        async Task<JsonObject> Act(string type, JsonObject p, string? auth = null) => (await service.Action(auth ?? token, J.O("operationId", J.Id(), "type", type, "payload", p))).Obj("result");
        await Act("catalog.category", J.O("name", "Todo"));
        var product = await Act("catalog.product", J.O("name", "Plato", "price", 10000, "taxBps", 1600, "category", "Todo", "station", "Cocina"));
        await Act("cash.open", J.O("initial", 0));
        const string name = "Rosa Sánchez Ejemplo", phone = "5512345678", street = "Calle Inventada 123";
        var customer = await Act("customer.save", J.O("name", name, "phone", phone, "notes", "Cliente frecuente"));
        var address = await Act("address.save", J.O("customerId", customer.S("id"), "label", "Casa", "text", street, "lat", 19.4326m, "lon", -99.1332m));
        var visit = (await Act("visit.quick", J.O("customerName", name))).S("id");
        await Act("visit.customer", J.O("id", visit, "customerId", customer.S("id")));
        var units = (await Act("command.send", J.O("visitId", visit, "items", new[] { J.O("productId", product.S("id"), "quantity", 1) }))).A("unitIds").Strings();
        await Deny(() => Act("customer.erase", J.O("id", customer.S("id"), "reason", "Cuenta abierta")), "a customer with an open account is not erased");
        foreach (var status in new[] { "preparing", "ready", "delivered" }) await Act("unit.state", J.O("id", units[0], "status", status));
        await Act("payment.add", J.O("visitId", visit, "amount", 10000, "method", "cash"));
        await Act("visit.close", J.O("id", visit));
        await Act("wallet.adjust", J.O("customerId", customer.S("id"), "amount", 500, "reason", "Bienvenida"));
        var exported = await Act("customer.export", J.O("id", customer.S("id")));
        var text = exported.ToJsonString();
        Check(text.Contains(phone) && exported.A("addresses").Count == 1 && exported.A("visits").Count == 1 && exported.A("wallet").Count > 0, "an export gathers everything tied to the person");
        await Deny(() => Act("customer.erase", J.O("id", customer.S("id"), "reason", "Tiene saldo")), "a customer with a wallet balance is not erased");
        var balance = (await service.Load()).Related("walletmove", "customerId", customer.S("id")).Sum(x => x.L("amount"));
        await Act("wallet.adjust", J.O("customerId", customer.S("id"), "amount", -balance, "reason", "Entregado"));
        var totalsBefore = (await service.Load()).List("payment").Sum(x => x.L("amount"));
        await Deny(() => Act("customer.erase", J.O("id", customer.S("id"))), "erasing needs a reason");
        var erasure = await Act("customer.erase", J.O("id", customer.S("id"), "reason", "Solicitud del titular"));
        var state = await service.Load();
        var everything = string.Concat(state.Committed.Records.Values.Where(x => x.Data.S("kind") != "audit").Select(x => x.Data.ToJsonString()));
        Check(!everything.Contains(name) && !everything.Contains(phone) && !everything.Contains(street), "the person's name, phone and address are gone from the live records");
        Check(state.Get("customer", customer.S("id")).S("name") == "Cliente eliminado" && state.Related("address", "customerId", customer.S("id")).Single().S("text") == "", "the records stay, anonymous");
        Check(state.List("payment").Sum(x => x.L("amount")) == totalsBefore && (await service.AuditChain()).B("valid") && await Task.FromResult(state.Get("visit", visit).S("status") == "closed"), "sales, cash and the audit chain keep their numbers");
        Check(erasure.S("reason") == "Solicitud del titular" && !erasure.ToJsonString().Contains(name), "the erasure is recorded without personal data");
        await Deny(() => Act("customer.erase", J.O("id", customer.S("id"), "reason", "Otra vez")), "a customer is erased once");
        await Act("user.save", J.O("name", "Mesero", "username", "mesero", "role", "mesero", "password", password));
        var enroll = J.Id() + J.Id();
        await service.RotateEnrollment(enroll);
        var waiter = (await service.Login(J.O("siteId", site, "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "Comandero", "username", "mesero", "password", password, "enrollmentCode", enroll))).S("token");
        var other = await Act("customer.save", J.O("name", "Otra Persona", "phone", "5587654321"));
        await Deny(() => Act("customer.export", J.O("id", other.S("id")), waiter), "a waiter cannot export a customer");
        await Deny(() => Act("customer.erase", J.O("id", other.S("id"), "reason", "x"), waiter), "a waiter cannot erase a customer");

        // Retention purge: off unless the provider's file turns it on, and never below 30 days.
        var config = Path.Combine(folder, "privacy.json");
        Check(PrivacyPurge.From(config) is null, "without privacy.json the purge does not run");
        File.WriteAllText(config, J.O("purge", J.O("enabled", false, "days", 90)).ToJsonString());
        Check(PrivacyPurge.From(config) is null, "a disabled purge does not run");
        File.WriteAllText(config, J.O("purge", J.O("enabled", true)).ToJsonString());
        Check(PrivacyPurge.From(config)?.Days == 90, "enabled without a period it keeps 90 days");
        File.WriteAllText(config, J.O("purge", J.O("enabled", true, "days", 7)).ToJsonString());
        try { PrivacyPurge.From(config); throw new Exception("FAIL: a period under 30 days is refused"); } catch (BusinessException) { Check(true, "a period under 30 days is refused"); }
        var old = await Act("reservation.save", J.O("name", "Lucía Reserva Antigua", "contact", "5500001111", "start", DateTimeOffset.UtcNow.AddDays(-120).ToString("O"), "people", 2, "status", "pending", "notes", "Alergia al cacahuate"));
        var recent = await Act("reservation.save", J.O("name", "Pedro Reserva Reciente", "contact", "5500002222", "start", DateTimeOffset.UtcNow.AddDays(-10).ToString("O"), "people", 2, "status", "pending"));
        await Deny(() => Act("privacy.purge", J.O("days", 90), waiter), "a waiter cannot run the purge");
        var first = await service.PurgePersonalData(90);
        var purged = await service.Load();
        Check(first.L("purged") == 1 && purged.Get("reservation", old.S("id")).S("contact") == "" && purged.Get("reservation", old.S("id")).S("notes") == "" && purged.Get("reservation", old.S("id")).S("purgedAt") != "", "a reservation 120 days old is anonymized in place");
        Check(purged.Get("reservation", recent.S("id")).S("contact") == "5500002222", "a reservation inside the period is kept");
        Check(purged.Get("customer", other.S("id")).S("phone") == "5587654321" && purged.List("walletmove").Any() && purged.List("payment").Sum(x => x.L("amount")) == totalsBefore, "customers, wallet and sales are not touched by the purge");
        var record = purged.List("purge").Single();
        Check(record.Obj("counts").L("reservation") == 1 && !record.ToJsonString().Contains("Lucía") && !record.ToJsonString().Contains("5500001111"), "the purge leaves a record with counts and no personal data");
        Check((await service.PurgePersonalData(90)).L("purged") == 0 && (await service.Load()).List("purge").Count() == 1 && (await service.AuditChain()).B("valid"), "a second run finds nothing, adds nothing and the audit chain stays valid");
        // Calls, messages and platform orders, dated in the past on a working copy of the state.
        var view = await service.Load();
        var ago = DateTimeOffset.UtcNow.AddDays(-200).ToString("O");
        var call = view.Add("call", J.O("number", "5511112222", "name", "Llamada Vieja", "at", ago));
        var message = view.Add("message", J.O("channel", "whatsapp", "to", "5511112222", "body", "Tu pedido va en camino", "values", new JsonArray("Llamada Vieja"), "at", ago));
        var platform = view.Add("aggregatororder", J.O("platform", "uber", "customerName", "Cliente Plataforma", "notes", "Tocar el timbre", "status", "delivered", "receivedAt", ago, "subtotal", 5000));
        var fresh = view.Add("call", J.O("number", "5533334444", "name", "Llamada Nueva", "at", J.Now()));
        var batch = service.Domain.Act(view, J.O("id", "privacy-worker", "role", "manager"), "privacy.purge", J.O("days", 90));
        Check(batch.Obj("counts").L("call") == 1 && batch.Obj("counts").L("message") == 1 && batch.Obj("counts").L("aggregatororder") == 1, "old calls, messages and platform orders are purged in the same run");
        Check(call.S("number") == "" && call.S("name") == "" && message.S("to") == "" && message.S("body") == "" && message.A("values").Count == 0 && platform.S("customerName") == "" && platform.S("notes") == "" && platform.L("subtotal") == 5000 && platform.S("status") == "delivered", "they lose the person and keep the amounts and status");
        Check(fresh.S("number") == "5533334444", "a recent call is kept");

    }
}
