using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
using MesaNexo.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
namespace MesaNexo.Tests;
// Facturama's API Web as FacturamaAdapter calls it: POST 3/cfdis stamps, GET cfdi/{id} consults, DELETE cfdi/{id} cancels.
// Mode "reject" answers 400; "lost" stamps and then loses the answer, as a dropped connection; "no-uuid" stamps but
// answers only the provider's Id, so the stamp is learned by consulting.
sealed class SimulatedFacturama(string user, string password): HttpMessageHandler {
    readonly Dictionary<string, (string Uuid, string Status)> cfdis = [];
    public readonly List<JsonObject> Stamped = []; public int StampCalls, ConsultCalls, CancelCalls; public string Mode = "ok";
    static HttpResponseMessage Json(HttpStatusCode code, JsonObject body) => new(code) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    static JsonObject Cfdi(string id, string uuid, string status) => J.O("Id", id, "Status", status, "Complement", J.O("TaxStamp", J.O("Uuid", uuid)));
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {

        if (request.Headers.Authorization?.Parameter != Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password))) return Json(HttpStatusCode.Unauthorized, J.O("Message", "Credenciales inválidas"));
        var path = request.RequestUri!.AbsolutePath;
        var id = path.Split('/').Last();
        if (request.Method == HttpMethod.Post && path == "/3/cfdis") {

            StampCalls++;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            if (Mode == "reject") return Json(HttpStatusCode.BadRequest, J.O("Message", "CFDI40147: régimen del receptor no válido"));
            var created = Guid.NewGuid().ToString("N");
            var uuid = Guid.NewGuid().ToString();
            cfdis[created] = (uuid, "active");
            Stamped.Add(body);
            if (Mode == "lost") throw new HttpRequestException("Connection reset after the PAC stamped");
            return Mode == "no-uuid" ? Json(HttpStatusCode.OK, J.O("Id", created)) : Json(HttpStatusCode.OK, Cfdi(created, uuid, "active"));

        }
        if (!cfdis.TryGetValue(id, out var cfdi)) return Json(HttpStatusCode.NotFound, J.O("Message", "No existe"));
        if (request.Method == HttpMethod.Get) { ConsultCalls++; return Json(HttpStatusCode.OK, Cfdi(id, cfdi.Uuid, cfdi.Status)); }
        if (request.Method == HttpMethod.Delete) { CancelCalls++; cfdis[id] = (cfdi.Uuid, "canceled"); return Json(HttpStatusCode.OK, J.O("Uuid", cfdi.Uuid, "Status", "canceled")); }
        return Json(HttpStatusCode.MethodNotAllowed, new());

    }
}
static class FiscalTests {
    public static async Task Run(string root, string python, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); evidence.Add(J.O("test", name, "result", "passed", "pac", "simulated Facturama API Web; no network")); Console.WriteLine("PASS " + name); }
        async Task Deny(Func<Task> action, string contains, string name) { try { await action(); throw new Exception("Expected rejection: " + name); } catch (BusinessException e) { Check(e.Message.Contains(contains), name); } }
        var folder = Path.Combine(root, ".lab", "fiscal-" + J.Id());
        Directory.CreateDirectory(folder);
        var password = "Test-" + J.Id();
        var enrollment = J.Id();
        using var runtime = LabPostgres.Test(root, folder, "fiscal-site");
        var service = new BranchService(runtime, "fiscal-site", new Passwords());
        var credentials = new IntegrationConfig();
        credentials.Ready("pac");
        service.UseIntegrations(credentials);
        await TestLicense.Initialize(service, J.O("siteId", "fiscal-site", "name", "Taquería Fiscal", "mode", "licencia", "managerName", "Gerencia", "managerUsername", "manager", "managerPassword", password, "enrollmentCode", enrollment, "modules", Restaurant.Modules));
        var token = (await service.Login(J.O("siteId", "fiscal-site", "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "fiscal test", "username", "manager", "password", password, "enrollmentCode", enrollment))).S("token");
        async Task<JsonObject> Act(string type, JsonObject p) => (await service.Action(token, J.O("operationId", J.Id(), "type", type, "payload", p))).Obj("result");
        await Act("catalog.category", J.O("name", "Comida"));
        var tacos = await Act("catalog.product", J.O("name", "Orden de tacos", "price", 11600, "taxBps", 1600, "category", "Comida", "station", "Cocina"));
        var agua = await Act("catalog.product", J.O("name", "Agua fresca", "price", 3480, "taxBps", 1600, "category", "Comida", "station", "Barra"));
        await Act("cash.open", J.O("initial", 0));
        // A settled ticket: two orders of tacos and one water, 26,680 cents with IVA included, paid as given.
        async Task<string> Ticket(params (string Method, long Amount)[] payments) {

            var visit = (await Act("visit.quick", new())).S("id");
            var units = (await Act("command.send", J.O("visitId", visit, "items", new[] { J.O("productId", tacos.S("id"), "quantity", 2), J.O("productId", agua.S("id"), "quantity", 1) }))).A("unitIds").Strings();
            foreach (var unit in units)
            foreach (var status in new[] { "preparing", "ready", "delivered" }) await Act("unit.state", J.O("id", unit, "status", status));
            foreach (var (method, amount) in payments.Length > 0 ? payments : [("cash", 26680L)])
                await Act("payment.add", J.O("visitId", visit, "amount", amount, "method", method));
            await Act("visit.close", J.O("id", visit));
            return visit;

        }
        async Task<string> Request(string visit) => (await Act("fiscal.request", J.O("visitId", visit, "rfc", "EKU9003173C9", "name", "ESCUELA KEMPER URGATE", "postalCode", "42501", "regime", "601", "use", "G03"))).S("id");
        async Task<JsonObject> Fiscal(string id) => (await service.Load()).Get("fiscal", id);
        // Changes a record as another process would, to place a request in states that otherwise take minutes to reach.
        async Task Patch(string id, Action<JsonObject> change) { var state = await service.Load(); change(state.Get("fiscal", id)); state.Meta["revision"] = state.Meta.L("revision") + 1; await runtime.CallAsync(J.O("op", "transaction_v1", "protocol", 1, "event_id", J.Id(), "receipt", J.O("actor", "fiscal-fixture", "fingerprint", J.Id(), "result", new JsonObject()), "changes", new JsonArray(state.Changes().Select(x => (JsonNode)x).ToArray()))); }
        var issuer = J.O("expeditionPlace", "06600", "product", J.O("productCode", "90101501", "unitCode", "E48", "taxObject", "02"));
        var facturama = new SimulatedFacturama("sandbox-user", "sandbox-pass");
        using var pac = new FacturamaAdapter("sandbox-user", "sandbox-pass", true, facturama);
        Task<bool> Process(JsonObject? config = null) => service.ProcessFiscal(pac, config ?? issuer, allRequests: true);

        // Configuration of the branch.
        var config = Path.Combine(folder, "fiscal.json");
        File.WriteAllText(config, J.O("provider", "facturama", "environment", "production", "username", "u", "password", "p", "issuer", issuer).ToJsonString());
        Check(!FiscalOptions.Load(config).Adapter().IsSandbox, "a production configuration stamps on Facturama's production API");
        File.WriteAllText(config, J.O("provider", "facturama", "environment", "sandbox", "username", "u", "password", "p", "issuer", J.O("expeditionPlace", "06600")).ToJsonString());
        Check(Throws(() => FiscalOptions.Load(config), "clave SAT por defecto"), "sandbox configuration needs a default SAT key for products");
        File.WriteAllText(config, J.O("provider", "facturama", "environment", "sandbox", "username", "u", "password", "p", "issuer", issuer).ToJsonString());
        var options = FiscalOptions.Load(config);
        Check(options.Adapter().IsSandbox && options.Issuer.S("expeditionPlace") == "06600", "a valid sandbox configuration builds a sandbox adapter");

        // A request becomes a sandbox CFDI built from the sale itself.
        var cashTicket = await Ticket();
        var first = await Request(cashTicket);
        Check(await Process(), "pending fiscal request is processed without a per-request flag");
        var stamped = await Fiscal(first);
        var sent = facturama.Stamped.Single();
        Check(stamped.S("status") == "sandbox-stamped" && Guid.TryParse(stamped.S("sandboxUuid"), out _) && stamped.S("message").StartsWith("SANDBOX"), "the CFDI is stamped in the sandbox and labelled without fiscal validity");
        Check(sent.S("Folio") == stamped.S("folio") && sent.S("Date") == stamped.S("issuedLocal") && sent.S("ExpeditionPlace") == "06600" && sent.S("PaymentMethod") == "PUE" && sent.Obj("Receiver").S("Rfc") == "EKU9003173C9", "CFDI carries the frozen folio and date, the expedition place and the receiver");
        var items = sent.A("Items").Objects().ToArray();
        Check(items.Length == 3 && items.All(x => x.S("ProductCode") == "90101501" && x.S("UnitCode") == "E48" && x.S("TaxObject") == "02") && items.Sum(x => x["Total"]!.GetValue<decimal>()) == 266.80m, "every sold unit uses the default SAT key and the items add up to the ticket");
        Check(items.All(x => x.A("Taxes").Objects().Single()["Rate"]!.GetValue<decimal>() == 0.16m) && items.Sum(x => x.A("Taxes").Objects().Single()["Total"]!.GetValue<decimal>()) == 36.80m, "IVA follows each unit's frozen 16% rate");
        Check(sent.S("PaymentForm") == "01", "cash ticket is invoiced with form of payment 01");
        Check(!await Process() && facturama.StampCalls == 1, "a stamped request is not sent again");

        await Request(await Ticket(("card", 26680L)));
        await Process();
        Check(facturama.Stamped.Last().S("PaymentForm") == "04", "card ticket is invoiced with form of payment 04");
        await Request(await Ticket(("cash", 6680L), ("card", 20000L)));
        await Process();
        Check(facturama.Stamped.Last().S("PaymentForm") == "04", "several forms of payment use the one that covered most of the ticket");
        var transfer = await Request(await Ticket(("transfer", 26680L)));
        await Process(J.O("expeditionPlace", "06600", "product", issuer.Obj("product"), "paymentForms", J.O("transfer", "02")));
        Check(facturama.Stamped.Last().S("PaymentForm") == "02" && (await Fiscal(transfer)).S("status") == "sandbox-stamped", "a branch can override the SAT form of a payment method");

        // A blocked request does not hold back the ones behind it.
        var blocked = await Request(await Ticket());
        var behind = await Request(await Ticket());
        var strict = J.O("expeditionPlace", "06600", "product", issuer.Obj("product"), "products", J.O(tacos.S("id"), J.O("productCode", "90101501", "unitCode", "E48", "taxObject", "02", "rateBps", 800)));
        Check(await Process(strict) && (await Fiscal(blocked)).S("status") == "validation-blocked" && (await Fiscal(blocked)).S("message").Contains("Tasa fiscal"), "a SAT key whose rate differs from the sale blocks validation");
        Check(await Process(strict) && (await Fiscal(behind)).S("status") == "validation-blocked", "the request behind a blocked one is still evaluated");
        Check(!await Process(strict), "blocked requests are not retried while the configuration that blocked them stays the same");
        Check(await Process() && await Process() && (await Fiscal(blocked)).S("status") == "sandbox-stamped" && (await Fiscal(behind)).S("status") == "sandbox-stamped", "fixing the configuration releases every blocked request");

        // The PAC stamped but the answer was lost: never stamp twice.
        facturama.Mode = "lost";
        var lost = await Request(await Ticket());
        var calls = facturama.StampCalls;
        Check(await Process() && (await Fiscal(lost)).S("status") == "pending-reconciliation" && (await Fiscal(lost)).S("providerId") == "", "a lost answer leaves the request pending reconciliation without the provider's id");
        Check((await Fiscal(lost)).S("message").Contains((await Fiscal(lost)).S("folio")), "the message names the folio to look up in the PAC");
        await Patch(lost, f => f["leaseUntil"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
        facturama.Mode = "ok";
        Check(!await Process() && facturama.StampCalls == calls + 1, "an unanswered submission is never sent again by the service");
        await Deny(() => Act("fiscal.retry", J.O("id", first, "reason", "x")), "sin respuesta", "only an unanswered submission can be retried");
        var lostFolio = facturama.Stamped.Last().S("Folio");
        var lostDate = facturama.Stamped.Last().S("Date");
        await Act("fiscal.retry", J.O("id", lost, "reason", "No aparece en Facturama"));
        Check(await Process() && (await Fiscal(lost)).S("status") == "sandbox-stamped" && facturama.Stamped.Last().S("Folio") == lostFolio && facturama.Stamped.Last().S("Date") == lostDate, "a manager's retry stamps with the same folio and date");

        // A claim that expired without the provider's id may have reached the PAC.
        var crashed = await Request(await Ticket());
        await Patch(crashed, f => { f["status"] = "submitting"; f["leaseUntil"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"); });
        calls = facturama.StampCalls;
        Check(await Process() && (await Fiscal(crashed)).S("status") == "pending-reconciliation" && facturama.StampCalls == calls, "an expired submission without the provider's id becomes a reconciliation, not a second stamp");

        // The answer came without the stamp: the provider's id is consulted, not stamped again.
        facturama.Mode = "no-uuid";
        var partial = await Request(await Ticket());
        calls = facturama.StampCalls;
        Check(await Process() && (await Fiscal(partial)).S("status") == "pending-reconciliation" && (await Fiscal(partial)).S("providerId") != "", "an answer without stamp keeps the provider's id");
        facturama.Mode = "ok";
        await Patch(partial, f => f["leaseUntil"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
        Check(await Process() && (await Fiscal(partial)).S("status") == "sandbox-stamped" && facturama.ConsultCalls == 1 && facturama.StampCalls == calls + 1, "the stamp is learned by consulting the provider's id");

        // The PAC rejects: the request is closed and the ticket can be requested again.
        facturama.Mode = "reject";
        var rejectedTicket = await Ticket();
        var rejected = await Request(rejectedTicket);
        Check(await Process() && (await Fiscal(rejected)).S("status") == "rejected", "a PAC rejection closes the request");
        facturama.Mode = "ok";
        var again = await Request(rejectedTicket);
        Check(await Process() && (await Fiscal(again)).S("status") == "sandbox-stamped", "a rejected ticket can be requested and stamped again");

        // Cancellation.
        await Act("fiscal.cancel.request", J.O("id", first, "reason", "Datos del receptor equivocados", "motive", "02"));
        Check(await Process() && (await Fiscal(first)).S("status") == "sandbox-cancelled" && facturama.CancelCalls == 1, "a cancellation request reaches the PAC and is recorded");

        // With the PAC simulator on, the sandbox stays out of it.
        await Act("integration.configure", J.O("provider", "pac", "mode", "simulated"));
        var simulatedRequest = await Request(await Ticket());
        calls = facturama.StampCalls;
        Check(!await Process() && facturama.StampCalls == calls && (await Fiscal(simulatedRequest)).S("status") == "pending", "the sandbox does not touch requests while the PAC simulator is on");
        await Act("integration.configure", J.O("provider", "pac", "mode", "live"));

        // The service a branch runs.
        var worker = new FiscalWorker(service, pac, issuer, NullLogger<FiscalWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try {

            var live = await Request(await Ticket());
            var state = "";
            for (var i = 0; i < 100 && state != "sandbox-stamped"; i++) { state = (await Fiscal(live)).S("status"); if (state != "sandbox-stamped") await Task.Delay(100); }
            Check(state == "sandbox-stamped" && (await Fiscal(simulatedRequest)).S("status") == "sandbox-stamped", "fiscal service stamps new requests without being asked");
            Check((await service.State(token)).Obj("connection").S("fiscal") == "sandbox", "fiscal service status reaches /api/state");

        } finally { await worker.StopAsync(CancellationToken.None); }

    }
    static bool Throws(Action action, string contains) { try { action(); return false; } catch (BusinessException e) { return e.Message.Contains(contains); } }
}
