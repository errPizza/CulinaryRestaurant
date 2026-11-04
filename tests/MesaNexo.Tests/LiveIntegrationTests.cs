using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
using MesaNexo.Integrations;
namespace MesaNexo.Tests;
// A provider's API without network: each request is recorded and answered by `answer` (path and query, method, body).
sealed class FakeApi(Func<HttpRequestMessage, string, HttpResponseMessage> answer): HttpMessageHandler {
    public readonly List<(HttpRequestMessage Request, string Body)> Calls = [];
    public static HttpResponseMessage Json(JsonNode body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {

        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Calls.Add((request, body));
        return answer(request, body);

    }
    public HttpClient Client() => new(this) { Timeout = TimeSpan.FromSeconds(5) };
}
// Live integrations against simulated provider APIs: the queued work, its claim and answer, a lost answer, the money rules and
// what each adapter sends. Nothing leaves the machine.
static class LiveIntegrationTests {
    public static async Task Run(string root, string python, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); evidence.Add(J.O("test", name, "result", "passed", "providers", "simulated HTTP APIs; no network")); Console.WriteLine("PASS " + name); }
        async Task Deny(Func<Task> action, string contains, string name) { try { await action(); throw new Exception("Expected rejection: " + name); } catch (BusinessException e) { Check(e.Message.Contains(contains), name + " (" + e.Message + ")"); } }
        var folder = Path.Combine(root, ".lab", "live-" + J.Id());
        Directory.CreateDirectory(folder);
        var password = "Test-" + J.Id();
        var enrollment = J.Id();
        using var runtime = LabPostgres.Test(root, folder, "live-site");
        var service = new BranchService(runtime, "live-site", new Passwords());
        var live = new IntegrationConfig();
        foreach (var key in new[] { "terminal", "onlinepay", "messaging", "maps", "pms", "aggregators" }) live.Ready(key);
        service.UseIntegrations(live, new JsonArray(J.O("id", "caja-1", "name", "Caja 1", "provider", "mercadopago", "manual", false), J.O("id", "barra", "name", "Barra", "provider", "clip", "manual", false), J.O("id", "bbva", "name", "Salón BBVA", "provider", "bbva", "manual", true)));
        await TestLicense.Initialize(service, J.O("siteId", "live-site", "name", "Taquería Real", "mode", "licencia", "managerName", "Gerencia", "managerUsername", "manager", "managerPassword", password, "enrollmentCode", enrollment, "modules", Restaurant.Modules));
        var token = (await service.Login(J.O("siteId", "live-site", "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "office", "username", "manager", "password", password, "enrollmentCode", enrollment))).S("token");
        async Task<JsonObject> Act(string type, JsonObject p) => (await service.Action(token, J.O("operationId", J.Id(), "type", type, "payload", p))).Obj("result");
        async Task<JsonObject> Get(string kind, string id) => (await service.Load()).Get(kind, id);
        var ana = await Act("user.save", J.O("name", "Ana", "username", "ana", "role", "mesero", "password", password));
        await Act("catalog.category", J.O("name", "Comida"));
        var taco = await Act("catalog.product", J.O("name", "Taco", "price", 3000, "taxBps", 1600, "category", "Comida", "station", "Cocina"));
        var table = await Act("table.save", J.O("name", "Mesa 1", "zone", "Salón", "capacity", 4));
        await Act("cash.open", J.O("initial", 50000));
        async Task<string> Account() { var v = await Act("visit.quick", J.O("waiterId", ana.S("id"))); await Act("command.send", J.O("visitId", v.S("id"), "items", new[] { J.O("productId", taco.S("id"), "quantity", 1) })); return v.S("id"); }
        async Task Close(string visit) { var units = (await service.Load()).List("unit").Where(x => x.S("visitId") == visit).Select(x => x.S("id")).ToArray(); foreach (var unit in units) foreach (var status in new[] { "preparing", "ready", "delivered" }) try { await Act("unit.state", J.O("id", unit, "status", status)); } catch (BusinessException) { } }

        // ---------- activation ----------
        await Deny(() => Act("integration.configure", J.O("provider", "callerid", "mode", "live")), "credenciales válidas", "live mode is refused for a provider without credentials on the server");
        foreach (var key in new[] { "terminal", "onlinepay", "messaging", "pms", "aggregators" }) await Act("integration.configure", J.O("provider", key, "mode", "live"));
        await Act("integration.configure", J.O("provider", "maps", "mode", "live", "settings", J.O("centerLat", "19.4326", "centerLon", "-99.1332", "radiusKm", 8)));
        var connection = (await service.State(token)).Obj("connection");
        Check(connection.Obj("integrations").S("terminal") == "lista" && connection.Obj("integrations").S("callerid") == "sin credenciales" && connection.A("terminals").Count == 3, "/api/state reports which providers have credentials and the branch's terminals");

        // ---------- Mercado Pago Point ----------
        var mpStatus = "at_terminal";
        var mpOrders = new Dictionary<string, string>();
        var dropNextCreate = false;
        var refuseRefund = false;
        var mp = new FakeApi((r, body) => {

            var path = r.RequestUri!.AbsolutePath;
            if (r.Method == HttpMethod.Post && path == "/v1/orders") { var key = r.Headers.GetValues("X-Idempotency-Key").Single(); if (!mpOrders.TryGetValue(key, out var id)) { id = "ORD-" + (mpOrders.Count + 1); mpOrders[key] = id; } if (dropNextCreate) { dropNextCreate = false; throw new HttpRequestException("connection reset after the order was created"); } return FakeApi.Json(J.O("id", id, "status", "created"), HttpStatusCode.Created); }
            if (path.EndsWith("/cancel")) return FakeApi.Json(J.O("id", path.Split('/')[3], "status", "canceled"));
            if (path.EndsWith("/refund")) return refuseRefund ? FakeApi.Json(J.O("message", "refund window closed"), HttpStatusCode.BadRequest) : FakeApi.Json(J.O("id", path.Split('/')[3], "status", "refunded"));
            return FakeApi.Json(J.O("id", path.Split('/').Last(), "status", mpStatus, "transactions", J.O("payments", new JsonArray(J.O("id", "PAY-1", "status", mpStatus == "processed" ? "processed" : "created", "payment_method", J.O("id", "visa", "type", "credit_card"))))));

        });
        var clipStatus = "PENDING";
        var failClip = false;
        var clip = new FakeApi((r, body) => {

            if (failClip) { failClip = false; throw new HttpRequestException("timeout"); }
            if (r.Method == HttpMethod.Post) return FakeApi.Json(J.O("pinpad_request_id", "pinpad-1", "reference", "x", "amount", "30.00"));
            if (r.Method == HttpMethod.Delete) return FakeApi.Json(J.O("code", "PAYMENT_REQUEST_ISSUE", "message", "ya tomado"), HttpStatusCode.BadRequest);
            return FakeApi.Json(J.O("pinpad_request_id", "pinpad-1", "status", clipStatus, "detail", J.O("results", new JsonArray(J.O("status", "approved", "payment_method", J.O("id", "master", "card", J.O("last_digits", "8356")))))));

        });
        var terminals = new TerminalService(new MercadoPagoPoint(mp.Client(), "APP_USR-test"), new ClipPinpad(clip.Client(), "clip-key", "clip-secret"), new() { ["caja-1"] = J.O("id", "caja-1", "name", "Caja 1", "provider", "mercadopago", "deviceId", "PAX__SMARTPOS1"), ["barra"] = J.O("id", "barra", "name", "Barra", "provider", "clip", "deviceId", "P8220724000042"), ["bbva"] = J.O("id", "bbva", "name", "Salón BBVA", "provider", "bbva") });
        Task<bool> SendTerminal() => service.SendNext("terminaltxn", "terminal-service", x => x.S("status") == "queued", terminals.Send, terminals.Recover);
        Task<bool> PollTerminal() => service.PollNext("terminaltxn", "terminal-service", x => x.S("status") is "waiting" or "cancel-requested", terminals.Consult, TimeSpan.Zero);
        Task<bool> RefundTerminal() => service.SendNext("terminaltxn", "terminal-service", x => x.S("status") == "refund-queued", terminals.Refund, null, default, "refunding");

        var visit = await Account();
        var charge = await Act("terminal.charge", J.O("visitId", visit, "amount", 3000, "terminalId", "caja-1"));
        Check(charge.S("status") == "queued" && charge.B("live") && mp.Calls.Count == 0, "a live terminal charge is queued; the action itself contacts nobody");
        await Deny(() => Act("terminal.charge", J.O("visitId", visit, "amount", 100, "terminalId", "caja-1")), "en curso", "a second charge waits while one is on its way");
        Check(await SendTerminal() && (await Get("terminaltxn", charge.S("id"))).S("status") == "waiting" && (await Get("terminaltxn", charge.S("id"))).S("providerRef") == "ORD-1", "the terminal service sends the charge and waits for the customer");
        var create = JsonNode.Parse(mp.Calls[0].Body)!;
        Check(mp.Calls[0].Request.Headers.GetValues("X-Idempotency-Key").Single() == charge.S("id") && create["transactions"]!["payments"]![0]!.S("amount") == "30.00" && create["config"]!["point"]!.S("terminal_id") == "PAX__SMARTPOS1", "Mercado Pago receives the amount in pesos, the terminal and the charge id as idempotency key");
        var commitsBefore = (await service.Load()).Meta.L("revision");
        Check(!await PollTerminal() && (await service.Load()).Meta.L("revision") == commitsBefore, "waiting at the terminal adds nothing to the store");
        mpStatus = "processed";
        Check(await PollTerminal(), "an approved order is recorded");
        var approved = await Get("terminaltxn", charge.S("id"));
        var payment = await Get("payment", approved.S("paymentId"));
        Check(approved.S("status") == "approved" && payment.S("method") == "card" && payment.Obj("terminal").S("brand") == "Visa" && !payment.Obj("terminal").B("simulated") && payment.L("amount") == 3000, "the approved charge becomes a card payment with the brand the terminal reported");

        // A lost answer: the order exists at Mercado Pago, asking again with the same key returns it instead of a second one.
        mpStatus = "at_terminal";
        var lostVisit = await Account();
        var lost = await Act("terminal.charge", J.O("visitId", lostVisit, "amount", 3000, "terminalId", "caja-1"));
        dropNextCreate = true;
        await SendTerminal();
        Check((await Get("terminaltxn", lost.S("id"))).S("status") == "waiting" && mpOrders.Count == 2 && mp.Calls.Count(c => c.Request.Method == HttpMethod.Post && c.Request.RequestUri!.AbsolutePath == "/v1/orders" && c.Request.Headers.GetValues("X-Idempotency-Key").Single() == lost.S("id")) == 2, "a lost creation is recovered by asking again with the same key; no second order");
        await Act("terminal.cancel", J.O("id", lost.S("id")));
        Check((await Get("terminaltxn", lost.S("id"))).S("status") == "cancel-requested" && await PollTerminal() && (await Get("terminaltxn", lost.S("id"))).S("status") == "cancelled", "cancelling from the POS cancels the order on the terminal");
        Check(mp.Calls.Any(c => c.Request.RequestUri!.AbsolutePath.EndsWith("/cancel") && c.Request.Headers.GetValues("x-allow-cancelable-status").Single() == "at_terminal") && (await Get("terminaltxn", lost.S("id"))).S("paymentId") == "", "the cancellation is allowed while at the terminal and records no payment");

        // A charge that arrives for an account already covered is refunded, never applied twice.
        var covered = await Account();
        var late = await Act("terminal.charge", J.O("visitId", covered, "amount", 3000, "terminalId", "caja-1"));
        await SendTerminal();
        await Act("payment.add", J.O("visitId", covered, "amount", 3000, "method", "cash", "tendered", 3000));
        mpStatus = "processed";
        await PollTerminal();
        Check((await Get("terminaltxn", late.S("id"))).S("status") == "refund-queued" && await RefundTerminal() && (await Get("terminaltxn", late.S("id"))).S("status") == "refunded", "a charge over a covered account is refunded through Mercado Pago");
        Check((await service.Load()).List("payment").Count(x => x.S("visitId") == covered) == 1, "the covered account keeps a single payment");

        // ---------- Clip ----------
        clipStatus = "PENDING";
        var clipVisit = await Account();
        var clipCharge = await Act("terminal.charge", J.O("visitId", clipVisit, "amount", 3000, "tip", 500, "terminalId", "barra"));
        await SendTerminal();
        var clipBody = JsonNode.Parse(clip.Calls.Last().Body)!;
        Check(clip.Calls.Last().Request.Headers.Authorization!.Parameter == Convert.ToBase64String(Encoding.UTF8.GetBytes("clip-key:clip-secret")) && clipBody.S("serial_number_pos") == "P8220724000042" && clipBody.S("amount") == "30.00" && clipBody.S("tip_amount") == "5.00", "Clip receives the reader's serial, amount and tip with Basic api key and secret");
        clipStatus = "COMPLETED";
        await PollTerminal();
        var clipTxn = await Get("terminaltxn", clipCharge.S("id"));
        Check(clipTxn.S("status") == "approved" && (await Get("payment", clipTxn.S("paymentId"))).Obj("terminal").S("last4") == "8356", "an approved Clip payment records the brand and last four digits");
        failClip = true;
        var silentVisit = await Account();
        var silent = await Act("terminal.charge", J.O("visitId", silentVisit, "amount", 3000, "terminalId", "barra"));
        await SendTerminal();
        Check((await Get("terminaltxn", silent.S("id"))).S("status") == "uncertain", "a Clip charge without an answer and without its id stays uncertain");
        await Deny(() => Act("terminal.reconcile", J.O("id", silent.S("id"))), "outcome", "an uncertain charge without the provider's id needs the terminal's report");
        await Act("terminal.reconcile", J.O("id", silent.S("id"), "outcome", "not-charged"));
        Check((await Get("terminaltxn", silent.S("id"))).S("status") == "declined", "the terminal's report settles an uncertain charge");

        // ---------- BBVA (semi-integrated) ----------
        await Deny(() => Act("terminal.manual", J.O("visitId", silentVisit, "amount", 3000, "terminalId", "bbva", "authCode", "12", "last4", "1234", "brand", "Visa")), "Autorización", "a voucher needs a real authorization code");
        var bbva = await Act("terminal.manual", J.O("visitId", silentVisit, "amount", 3000, "terminalId", "bbva", "authCode", "a1b2c3", "last4", "4321", "brand", "Mastercard"));
        Check(bbva.S("status") == "approved" && (await Get("payment", bbva.S("paymentId"))).Obj("terminal").S("authCode") == "A1B2C3", "a BBVA charge is recorded from its voucher");

        // ---------- A charge across a crash ----------
        // The provider takes the charge, its answer is lost and the process dies with the claim stored. Each adapter's next
        // run is one charge and one payment, or a person's capture. The claim lives in the store, so this is what a restart finds.
        async Task Crash() {

            using var crash = new CancellationTokenSource();
            try { await service.SendNext("terminaltxn", "terminal-service", x => x.S("status") == "queued", async (s, x, c) => { await terminals.Send(s, x, default); crash.Cancel(); c.ThrowIfCancellationRequested(); throw new InvalidOperationException(); }, terminals.Recover, crash.Token, "sending", TimeSpan.Zero); } catch (OperationCanceledException) { }

        }
        async Task<long> Paid(string v) => (await service.Load()).Related("payment", "visitId", v).Sum(x => x.L("amount"));
        mpStatus = "at_terminal";
        var crashVisit = await Account();
        var crashed = await Act("terminal.charge", J.O("visitId", crashVisit, "amount", 3000, "terminalId", "caja-1"));
        await Crash();
        Check((await Get("terminaltxn", crashed.S("id"))).S("status") == "sending", "a process that dies after calling the provider leaves the claim stored");
        await SendTerminal();
        Check((await Get("terminaltxn", crashed.S("id"))).S("status") == "waiting", "the next run asks again with the same key and finds the order");
        mpStatus = "processed";
        await PollTerminal();
        var recovered = await Get("terminaltxn", crashed.S("id"));
        Check(recovered.S("status") == "approved" && await Paid(crashVisit) == 3000 && mpOrders.Keys.Count(k => k == crashed.S("id")) == 1, "Mercado Pago after a crash: one order, one payment");
        Check(recovered.A("trail").Objects().Select(x => x.S("to")).SequenceEqual(["queued", "sending", "waiting", "approved"]) && recovered.A("trail").Objects().Last().S("providerRef") != "" && recovered.A("trail").Objects().Last().S("currency") == "MXN" && recovered.A("trail").Objects().Last().S("raw").Contains("processed") && !(await Get("terminaltxn", clipCharge.S("id"))).A("trail").Objects().Last().S("raw").Contains("8356"), "every change of the charge stays in its trail, with the provider's answer and no card data");
        var refused = await service.Load();
        refused.Get("terminaltxn", crashed.S("id"))["status"] = "waiting";
        await Deny(() => { service.Domain.ChargeTransitions(refused, "test"); return Task.CompletedTask; }, "no pasa de", "a final state does not change, whoever writes it");
        // A refund of that charge, from a closed account: the provider returns it, and the payment stays as it was.
        foreach (var unit in (await service.Load()).Related("unit", "visitId", crashVisit).Select(x => x.S("id")).ToArray()) foreach (var status in new[] { "preparing", "ready", "delivered" }) await Act("unit.state", J.O("id", unit, "status", status));
        await Act("visit.close", J.O("id", crashVisit));
        await Deny(() => Act("payment.refund", J.O("id", recovered.S("paymentId"), "amount", 1, "reason", "Parcial")), "completo", "a provider's charge goes back whole");
        async Task<JsonObject> MoneyTrail() => await Act("visit.trail", J.O("id", crashVisit));
        var returned = await Act("payment.refund", J.O("id", recovered.S("paymentId"), "reason", "Cobrado por error"));
        Check(returned.S("route") == "provider" && (await Get("terminaltxn", crashed.S("id"))).S("status") == "refund-queued", "a refund queues the provider's charge");
        var asked = await MoneyTrail();
        Check(asked.A("refunds").Objects().Single().S("outcome") == "pending" && asked.L("refunded") == 0 && asked.L("refundsPending") == 3000, "until the provider confirms, the refund is pending and is not counted as money returned");
        await Deny(() => Act("payment.refund", J.O("id", recovered.S("paymentId"), "reason", "Otra vez")), "devolver", "a pending refund cannot be asked twice");
        // The provider refuses: the refund is failed, nothing counts as returned, and it can be asked again.
        refuseRefund = true;
        Check(await RefundTerminal() && (await Get("terminaltxn", crashed.S("id"))).S("status") == "refund-failed", "a refund the provider refuses is recorded as failed");
        var failed = await MoneyTrail();
        Check(failed.A("refunds").Objects().Single().S("outcome") == "failed" && failed.L("refunded") == 0 && failed.L("refundsPending") == 0, "a failed refund is neither returned nor pending");
        refuseRefund = false;
        await Act("payment.refund", J.O("id", recovered.S("paymentId"), "reason", "Cobrado por error, segundo intento"));
        Check(await RefundTerminal() && (await Get("terminaltxn", crashed.S("id"))).S("status") == "refunded" && await Paid(crashVisit) == 3000, "the provider returns the charge, and the payment keeps its history");
        var done = await MoneyTrail();
        Check(done.L("refunded") == 3000 && done.L("refundsPending") == 0 && done.A("refunds").Objects().Select(x => x.S("outcome")).SequenceEqual(["failed", "done"]), "once confirmed the refund counts once, and the failed attempt stays in the trail");
        Check((await Get("terminaltxn", crashed.S("id"))).A("trail").Objects().Select(x => x.S("to")).TakeLast(6).SequenceEqual(["refund-queued", "refunding", "refund-failed", "refund-queued", "refunding", "refunded"]), "every step of both refund attempts stays in the charge's trail");
        await Deny(() => Act("refund.settle", J.O("id", done.A("refunds").Objects().Last().S("id"), "reference", "x")), "a mano", "only a refund left to a person can be confirmed by hand");
        var posts = clip.Calls.Count(c => c.Request.Method == HttpMethod.Post);
        var clipCrashVisit = await Account();
        var clipCrash = await Act("terminal.charge", J.O("visitId", clipCrashVisit, "amount", 3000, "terminalId", "barra"));
        await Crash();
        await SendTerminal();
        Check((await Get("terminaltxn", clipCrash.S("id"))).S("status") == "uncertain" && clip.Calls.Count(c => c.Request.Method == HttpMethod.Post) == posts + 1 && await Paid(clipCrashVisit) == 0, "Clip after a crash: nothing is sent again and the charge waits for a person");
        await Deny(() => Act("terminal.charge", J.O("visitId", clipCrashVisit, "amount", 3000, "terminalId", "barra")), "en curso", "an uncertain charge blocks a new one on the account");
        await Act("terminal.reconcile", J.O("id", clipCrash.S("id"), "outcome", "charged", "authCode", "ab12", "last4", "8356", "brand", "Visa"));
        Check((await Get("terminaltxn", clipCrash.S("id"))).S("status") == "approved" && await Paid(clipCrashVisit) == 3000, "the manager's capture applies the charge once");
        // Clip cannot refund through its API: the refund waits for a person, and only a manager's confirmation closes it.
        foreach (var unit in (await service.Load()).Related("unit", "visitId", clipCrashVisit).Select(x => x.S("id")).ToArray()) foreach (var status in new[] { "preparing", "ready", "delivered" }) await Act("unit.state", J.O("id", unit, "status", status));
        await Act("visit.close", J.O("id", clipCrashVisit));
        var clipRefund = await Act("payment.refund", J.O("id", (await Get("terminaltxn", clipCrash.S("id"))).S("paymentId"), "reason", "Cliente inconforme"));
        Check(await RefundTerminal() && (await Get("terminaltxn", clipCrash.S("id"))).S("status") == "refund-manual", "a Clip refund is left to a person");
        var manual = await Act("visit.trail", J.O("id", clipCrashVisit));
        Check(manual.A("refunds").Objects().Single().S("outcome") == "manual" && manual.L("refunded") == 0 && manual.L("refundsPending") == 3000, "a refund left to a person is pending, not returned");
        await Deny(() => Act("refund.settle", J.O("id", clipRefund.S("id"))), "reference", "confirming it by hand needs the provider's reference");
        await Act("refund.settle", J.O("id", clipRefund.S("id"), "reference", "Portal Clip R-778"));
        var settled = await Act("visit.trail", J.O("id", clipCrashVisit));
        Check(settled.L("refunded") == 3000 && settled.L("refundsPending") == 0 && (await Get("terminaltxn", clipCrash.S("id"))).A("trail").Objects().Last().S("to") == "refunded" && (await Get("terminaltxn", clipCrash.S("id"))).S("message").Contains("R-778"), "the manager's confirmation closes it once, with the reference in the charge's trail");
        await Deny(() => Act("refund.settle", J.O("id", clipRefund.S("id"), "reference", "otra vez")), "a mano", "a confirmed refund is not confirmed twice");
        Check(TerminalService.Capabilities("mercadopago") is { Idempotent: true, Lookup: true, Reverse: true } && TerminalService.Capabilities("clip") is { Idempotent: false, Lookup: true, Reverse: false } && TerminalService.Capabilities("bbva") is { Idempotent: false, Lookup: false, Reverse: false } && StripeCheckout.Capabilities.Idempotent, "each adapter declares what it can prove after a lost answer");

        // ---------- Stripe Checkout + WhatsApp ----------
        var customer = await Act("customer.save", J.O("name", "Mario", "phone", "55 1234 5678"));
        var linkVisit = await Account();
        await Act("visit.customer", J.O("id", linkVisit, "customerId", customer.S("id")));
        var session = "open";
        var stripe = new FakeApi((r, body) => {

            var path = r.RequestUri!.AbsolutePath;
            if (path == "/v1/refunds") return FakeApi.Json(J.O("id", "re_1"));
            if (path.EndsWith("/expire")) return FakeApi.Json(J.O("id", "cs_2", "status", "expired", "payment_status", "unpaid"));
            var id = path == "/v1/checkout/sessions" ? (body.Contains("unit_amount%5D=3000") ? "cs_1" : "cs_2") : path.Split('/').Last();
            return FakeApi.Json(J.O("id", id, "url", "https://checkout.stripe.com/c/pay/" + id, "status", id == "cs_1" ? session : "open", "payment_status", session == "complete" && id == "cs_1" ? "paid" : "unpaid", "payment_intent", "pi_1"));

        });
        var pay = new OnlinePayService(new StripeCheckout(stripe.Client(), "sk_test_123", "https://example.mx/gracias"));
        var link = await Act("paylink.create", J.O("visitId", linkVisit));
        Check(link.S("status") == "queued" && link.S("payUrl") == "", "a live payment link is queued until Stripe creates it");
        await service.SendNext("paylink", "payments-service", x => x.S("status") == "queued", pay.Create, pay.Recover);
        var created = await Get("paylink", link.S("id"));
        Check(created.S("status") == "pending" && created.S("payUrl") == "https://checkout.stripe.com/c/pay/cs_1" && stripe.Calls[0].Request.Headers.GetValues("Idempotency-Key").Single() == link.S("id") && stripe.Calls[0].Body.Contains("currency%5D=mxn"), "Stripe creates the Checkout session in MXN keyed by the link id");
        var notice = (await service.Load()).List("message").Single(x => x.S("refId") == link.S("id"));
        Check(notice.S("status") == "queued" && notice.A("values").Count == 4 && notice.A("values").Strings()[3] == created.S("payUrl"), "the link goes to the customer's WhatsApp once it exists, with the template values in order");
        session = "complete";
        await service.PollNext("paylink", "payments-service", x => x.S("status") is "pending" or "cancel-requested", pay.Consult, TimeSpan.Zero);
        var paid = await Get("paylink", link.S("id"));
        Check(paid.S("status") == "paid" && (await Get("payment", paid.S("paymentId"))).S("method") == "online", "a paid Checkout session becomes an online payment");
        var other = await Account();
        var link2 = await Act("paylink.create", J.O("visitId", other, "amount", 1500));
        await service.SendNext("paylink", "payments-service", x => x.S("status") == "queued", pay.Create, pay.Recover);
        await Act("paylink.cancel", J.O("id", link2.S("id")));
        await service.PollNext("paylink", "payments-service", x => x.S("status") is "pending" or "cancel-requested", pay.Consult, TimeSpan.Zero);
        Check((await Get("paylink", link2.S("id"))).S("status") == "cancelled" && stripe.Calls.Any(c => c.Request.RequestUri!.AbsolutePath == "/v1/checkout/sessions/cs_2/expire"), "cancelling a link expires its Stripe session");

        var whatsappCode = HttpStatusCode.OK;
        var meta = new FakeApi((r, body) => whatsappCode == HttpStatusCode.OK ? FakeApi.Json(J.O("messages", new JsonArray(J.O("id", "wamid.1")))) : FakeApi.Json(J.O("error", J.O("message", "Service unavailable")), whatsappCode));
        var messaging = new MessagingService(new WhatsAppCloud(meta.Client(), "PHONE-1", "token"), J.O("paylink", J.O("name", "liga_de_pago", "language", "es_MX")), null);
        Task<bool> SendMessage() => service.SendNext("message", "messaging-service", m => m.S("status") == "queued" && (!DateTimeOffset.TryParse(m.S("retryAt"), out var at) || at <= DateTimeOffset.UtcNow), messaging.Send);
        whatsappCode = HttpStatusCode.ServiceUnavailable;
        await SendMessage();
        var retried = await Get("message", notice.S("id"));
        Check(retried.S("status") == "queued" && retried.S("retryAt") != "", "a message that did not go out is tried again later");
        // Its retry time is skipped here instead of waiting a minute.
        whatsappCode = HttpStatusCode.OK;
        await service.SendNext("message", "messaging-service", m => m.S("id") == notice.S("id") && m.S("status") == "queued", messaging.Send);
        var sent = await Get("message", notice.S("id"));
        var waBody = JsonNode.Parse(meta.Calls.Last().Body)!;
        Check(sent.S("status") == "sent" && sent.S("providerId") == "wamid.1" && waBody.S("to") == "525512345678" && waBody["template"]!.S("name") == "liga_de_pago" && waBody["template"]!["components"]![0]!["parameters"]!.AsArray().Count == 4, "WhatsApp receives the approved template with its four values and the number with country code");
        await Act("message.test", J.O("channel", "whatsapp", "to", "5511112222"));
        await Act("message.test", J.O("channel", "email", "to", "dueno@example.mx"));
        while (await SendMessage()) { }
        var tests = (await service.Load()).List("message").Where(x => x.S("event") == "test").ToArray();
        Check(tests.All(x => x.S("status") == "failed") && tests.Any(x => x.S("message").Contains("whatsapp.templates.test")) && tests.Any(x => x.S("message") == "Correo sin configurar"), "a message without its template or channel is refused with the reason, never sent");

        // ---------- Google Maps ----------
        var zeroResults = false;
        var google = new FakeApi((r, body) => r.RequestUri!.Host == "routes.googleapis.com" ? FakeApi.Json(J.O("routes", new JsonArray(J.O("distanceMeters", 4200, "duration", "900s")))) : FakeApi.Json(zeroResults ? J.O("status", "ZERO_RESULTS", "results", new JsonArray()) : J.O("status", "OK", "results", new JsonArray(J.O("formatted_address", "Av. Reforma 222, CDMX", "geometry", J.O("location", J.O("lat", 19.4300, "lng", -99.1600)))))));
        var maps = new MapsService(new GoogleMaps(google.Client(), "maps-key"));
        await Act("zone.save", J.O("name", "Centro", "polygon", new[] { J.O("lat", 19.3, "lon", -99.3), J.O("lat", 19.6, "lon", -99.3), J.O("lat", 19.6, "lon", -99.0), J.O("lat", 19.3, "lon", -99.0) }, "fee", 3500));
        var address = await Act("address.save", J.O("customerId", customer.S("id"), "label", "Casa", "text", "Av. Reforma 222", "geocode", true));
        Check(address.S("status") == "queued" && !address.ContainsKey("lat"), "with live maps an address is queued to be located");
        await Deny(() => Act("delivery.create", J.O("customerId", customer.S("id"), "mode", "delivery", "addressId", address.S("id"))), "ubicando", "a delivery waits until its address is located");
        await Deny(() => Act("geo.lookup", J.O("text", "Reforma 222")), "guardar la dirección", "the simulated lookup is not offered with live maps");
        await service.SendNext("address", "maps-service", a => a.S("status") == "queued", maps.Locate);
        var located = await Get("address", address.S("id"));
        Check(located.S("status") == "located" && located["lat"]!.GetValue<decimal>() == 19.43m && located["routeKm"]!.GetValue<decimal>() == 4.2m && located.L("routeMinutes") == 20, "Google locates the address and Routes measures 4.2 km and 15 + 5 minutes from the restaurant");
        var delivery = await Act("delivery.create", J.O("customerId", customer.S("id"), "mode", "delivery", "addressId", address.S("id")));
        var stored = await Get("delivery", delivery.S("id"));
        Check(stored["distanceKm"]!.GetValue<decimal>() == 4.2m && stored.L("etaMinutes") == 20, "the delivery takes distance and time from the located address");
        zeroResults = true;
        var nowhere = await Act("address.save", J.O("customerId", customer.S("id"), "label", "Otra", "text", "Calle inexistente 0", "geocode", true));
        await service.SendNext("address", "maps-service", a => a.S("status") == "queued", maps.Locate);
        Check((await Get("address", nowhere.S("id"))).S("status") == "failed", "an address Google cannot find is marked so");

        // ---------- hotel (Cloudbeds) ----------
        var cloudbeds = new FakeApi((r, body) => r.Method == HttpMethod.Get ? FakeApi.Json(J.O("success", true, "data", new JsonArray(J.O("reservationID", "R-1", "guestName", "Juan Perez", "guestList", J.O("g1", J.O("guestFirstName", "Juan", "guestLastName", "Pérez")))))) : FakeApi.Json(J.O("success", true, "data", J.O("soldProductID", "S-1"))));
        var pms = new PmsService(new CloudbedsPms(cloudbeds.Client(), "cbat_test", "123"), 5000 * 100, "Consumo en restaurante");
        Task<bool> SendPms() => service.SendNext("pmspost", "pms-service", x => x.S("status") == "queued", pms.Send, pms.Recover);
        var roomVisit = await Account();
        var wrong = await Act("pms.charge", J.O("visitId", roomVisit, "room", "204", "guest", "Gómez"));
        await SendPms();
        Check((await Get("pmspost", wrong.S("id"))).S("status") == "rejected" && cloudbeds.Calls.All(c => c.Request.Method == HttpMethod.Get), "a surname that does not match the guest is refused before posting");
        var post = await Act("pms.charge", J.O("visitId", roomVisit, "room", "204", "guest", "Perez"));
        await SendPms();
        var posted = await Get("pmspost", post.S("id"));
        Check(posted.S("status") == "posted" && (await Get("payment", posted.S("paymentId"))).S("method") == "room" && cloudbeds.Calls.Last().Body.Contains("referenceID=" + post.S("id")) && cloudbeds.Calls.Last().Body.Contains("30.00"), "Cloudbeds posts the charge with the posting id as referenceID and the room payment is recorded");
        Check(cloudbeds.Calls.First().Request.RequestUri!.Query.Contains("status=checked_in") && cloudbeds.Calls.First().Request.Headers.GetValues("x-api-key").Single() == "cbat_test", "the guest is looked up among checked-in reservations with the API key");

        // ---------- hotel (OPERA through OHIP), adapter only ----------
        var ohip = new FakeApi((r, body) => {

            var path = r.RequestUri!.AbsolutePath;
            if (path.EndsWith("/tokens")) return FakeApi.Json(J.O("access_token", "ohip-token", "expires_in", 3600));
            if (path.Contains("/rsv/v1/")) return FakeApi.Json(J.O("reservations", J.O("reservationInfo", new JsonArray(J.O("reservationIdList", new JsonArray(J.O("type", "Reservation", "id", "9001")), "reservationGuest", J.O("givenName", "Ana", "surname", "López"))))));
            if (r.Method == HttpMethod.Get) return FakeApi.Json(J.O("postings", new JsonArray(J.O("postingReference", "post-1"))));
            return FakeApi.Json(new JsonObject(), HttpStatusCode.Created);

        });
        var opera = new OperaPms(ohip.Client(), new OAuthToken(ohip.Client(), "https://ohip.example/oauth/v1/tokens", "client", "secret", "client_credentials", appKey: "app"), "https://ohip.example", "app", "HOTEL1", "2000", 7);
        var guest = await opera.InHouse("305", default);
        var operaPost = await opera.Post(guest!, "305", 12345, "post-1", "Consumo", default);
        var charges = JsonNode.Parse(ohip.Calls.Single(c => c.Request.Method == HttpMethod.Post && c.Request.RequestUri!.AbsolutePath.EndsWith("/charges")).Body)!;
        Check(guest!.ReservationId == "9001" && guest.Name == "Ana López" && operaPost.Status == "posted" && charges["criteria"]!["charges"]![0]!.S("transactionCode") == "2000" && charges["criteria"]!["charges"]![0]!["price"]!["amount"]!.GetValue<decimal>() == 123.45m, "OPERA finds the in-house guest and posts the charge with the restaurant's transaction code");
        Check(ohip.Calls.Count(c => c.Request.RequestUri!.AbsolutePath.EndsWith("/tokens")) == 1 && ohip.Calls.Where(c => !c.Request.RequestUri!.AbsolutePath.EndsWith("/tokens")).All(c => c.Request.Headers.GetValues("x-hotelid").Single() == "HOTEL1" && c.Request.Headers.Authorization!.Parameter == "ohip-token") && (await opera.Find(guest, "post-1", default))?.Status == "posted", "OHIP reuses its token, sends the hotel id and finds a posting by its reference");

        // ---------- Deliverect ----------
        var secret = "deliverect-secret";
        var raw = Encoding.UTF8.GetBytes(J.O("_id", "x").ToJsonString());
        Check(DeliverectSignature.Verify(raw, Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), raw)), secret).S("_id") == "x", "a Deliverect webhook with the right HMAC is accepted");
        try { DeliverectSignature.Verify(raw, new string('0', 64), secret); throw new Exception("FAIL: a forged webhook is refused"); } catch (BusinessException) { Check(true, "a forged Deliverect webhook is refused"); }
        using var rsa = RSA.Create(2048);
        var keyFile = Path.Combine(folder, "provider.pem");
        File.WriteAllText(keyFile, rsa.ExportSubjectPublicKeyInfoPem());
        await Act("commerce.configure", J.O("tenantId", "tenant-1", "branchId", "branch-1", "waiterId", ana.S("id"), "enabled", true));
        async Task Deliver(JsonObject payload) {

            var data = J.O("format", "culinary-cloud-v1", "siteId", "live-site", "tenantId", "tenant-1", "branchId", "branch-1", "expires", DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"), "commands", new JsonArray(J.O("tenantId", "tenant-1", "branchId", "branch-1", "id", Guid.NewGuid().ToString(), "type", "aggregator", "payload", payload, "at", J.Now())));
            await service.ReceiveCloudProposals(J.O("data", data, "signature", Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(J.Canonical(data)), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))), keyFile);

        }
        await Deliver(J.O("deliverectId", "D-1", "platform", "uber", "displayId", "A12", "customerName", "Laura", "items", new JsonArray(J.O("plu", taco.S("id"), "name", "Taco", "price", 3000, "quantity", 2, "subItems", new JsonArray(J.O("name", "Salsa verde", "quantity", 1))), J.O("plu", "ZZZ", "name", "Gringa")), "total", 9000));
        var unknown = (await service.Load()).List("aggregatororder").Single(x => x.S("deliverectId") == "D-1");
        Check(unknown.S("status") == "pending" && unknown.S("platformName") == "UBER EATS" && unknown.A("unmapped").Strings().SequenceEqual(["Gringa"]) && unknown.A("items").Objects().Single().S("notes") == "Salsa verde", "a Deliverect order arrives through the cloud inbox, matched by PLU, with modifiers as notes");
        await Deny(() => Act("aggregator.decide", J.O("id", unknown.S("id"), "accept", true)), "PLU", "an order with products missing from the catalog cannot be accepted");
        await Deliver(J.O("deliverectId", "D-1", "cancel", true));
        Check((await Get("aggregatororder", unknown.S("id"))).S("status") == "rejected", "a cancellation from the platform voids a pending order");
        await Deliver(J.O("deliverectId", "D-2", "platform", "rappi", "displayId", "B7", "customerName", "Pedro", "items", new JsonArray(J.O("plu", taco.S("id"), "name", "Taco", "price", 3000, "quantity", 1)), "total", 3000));
        var order = (await service.Load()).List("aggregatororder").Single(x => x.S("deliverectId") == "D-2");
        await Act("aggregator.decide", J.O("id", order.S("id"), "accept", true));
        await Close((await Get("aggregatororder", order.S("id"))).S("visitId"));
        var syncs = (await service.Load()).List("platformsync").Where(x => x.S("orderId") == order.S("id")).Select(x => x.L("code")).Order().ToArray();
        Check(syncs.SequenceEqual([20L, 70L]), "accepting queues status 20 for Deliverect and a finished kitchen queues 70");
        var deliverect = new FakeApi((r, body) => r.RequestUri!.AbsolutePath == "/oauth/token" ? FakeApi.Json(J.O("access_token", "dl-token", "expires_in", 3600)) : FakeApi.Json(new JsonObject()));
        var platforms = new AggregatorService(new DeliverectClient(deliverect.Client(), "https://api.staging.deliverect.com", "client", "secret"), "ACC", "LOC");
        while (await service.SendNext("platformsync", "aggregators-service", x => x.S("status") == "queued", platforms.Send)) { }
        var statusCalls = deliverect.Calls.Where(c => c.Request.RequestUri!.AbsolutePath == "/orderStatus/D-2").Select(c => JsonNode.Parse(c.Body)!.L("status")).ToArray();
        Check(statusCalls.Order().SequenceEqual([20L, 70L]) && deliverect.Calls.Count(c => c.Request.RequestUri!.AbsolutePath == "/oauth/token") == 1, "the statuses reach Deliverect's orderStatus with one client-credentials token");
        await Act("aggregator.sync", new JsonObject());
        while (await service.SendNext("platformsync", "aggregators-service", x => x.S("status") == "queued", platforms.Send)) { }
        var menu = JsonNode.Parse(deliverect.Calls.Single(c => c.Request.RequestUri!.AbsolutePath == "/productAndCategories").Body)!;
        var product = menu["products"]!.AsArray().Single()!;
        Check(menu.S("accountId") == "ACC" && product.S("plu") == taco.S("id") && product.L("price") == 3000 && product.L("deliveryTax") == 16000 && menu["categories"]!.AsArray().Count == 1, "the menu is published with the product id as PLU and 16 % as 16000");
        Check((await Get("integration", "aggregators")).Obj("lastSync").L("published") == 1, "the integration card shows the last menu publication");

        // ---------- Facturama production adapter ----------
        var facturamaApi = new FakeApi((r, body) => FakeApi.Json(J.O("success", true)));
        using var production = new FacturamaAdapter("user", "pass", false, facturamaApi);
        Check(!production.IsSandbox && await production.Email("CFDI-1", "cliente@example.mx") && facturamaApi.Calls.Single().Request.RequestUri!.ToString() == "https://api.facturama.mx/Cfdi?cfdiType=issued&cfdiId=CFDI-1&email=cliente%40example.mx", "production Facturama e-mails the CFDI from api.facturama.mx");

    }
}
