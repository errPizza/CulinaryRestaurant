using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
using MesaNexo.Integrations;
namespace MesaNexo.Tests;

sealed class ContractHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle): HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request);
}
sealed class FixturePac: IPacAdapter {
    public bool IsSandbox => true; public JsonObject? Last; public int Calls;
    public Task<PacResult> Stamp(JsonObject frozen, CancellationToken ct = default) { Last = (JsonObject)frozen.DeepClone(); Calls++; return Task.FromResult(new PacResult("stamped", "test-provider-id", "0d140b8b-42af-4b2a-a57b-bebcaa89fd0d", "Synthetic transport fixture only")); }
    public Task<PacResult> Consult(string id, CancellationToken ct = default) => Task.FromResult(new PacResult("stamped", id, "0d140b8b-42af-4b2a-a57b-bebcaa89fd0d", "Fixture consult"));
    public Task<PacResult> Cancel(string id, string fiscalUuid, string motive, string? replacement = null, CancellationToken ct = default) => Task.FromResult(new PacResult("cancelled", id, fiscalUuid, "Fixture cancellation"));
}
sealed class FixturePrinter: IPrintDriver {
    public string Name => "test-double-no-hardware"; public int Calls;
    public Task Preflight(JsonObject job, CancellationToken ct) => Task.CompletedTask;
    public Task<PrintOutcome> Submit(JsonObject job, CancellationToken ct) { Calls++; return Task.FromResult(new PrintOutcome("uncertain", "Injected lost physical response; no device was used")); }
}
static class AdapterTests {
    public static async Task Run(List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception(name); evidence.Add(J.O("test", name, "result", "passed", "transport", "synthetic HTTP handler; no external calls")); Console.WriteLine("PASS " + name); }
        void Deny(Action action, string name) { try { action(); throw new Exception("Expected rejection " + name); } catch (BusinessException) { Check(true, name); } }
        int calls = 0;
        var requests = new List<JsonObject>();
        using (var adapter = new FacturamaAdapter("fixture", "fixture", true, new ContractHttp(async req => { var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync())!.AsObject(); requests.Add(body); Check(req.RequestUri!.Host == "apisandbox.facturama.mx" && req.RequestUri.AbsolutePath == "/3/cfdis", "PAC adapter uses documented sandbox REST endpoint"); calls++; return new(calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(calls == 1 ? "{}" : J.O("Id", "fixture", "Status", "active", "Complement", J.O("TaxStamp", J.O("Uuid", "0d140b8b-42af-4b2a-a57b-bebcaa89fd0d"))).ToJsonString()) }; }))) { var frozen = J.O("Folio", "fixture-stable", "Date", "2026-10-15T12:00:00", "Currency", "MXN", "Items", new[] { J.O("Total", 1m) }); var missing = await adapter.Stamp(frozen); var confirmed = await adapter.Stamp(frozen); Check(missing.Status == "pending-reconciliation" && missing.FiscalUuid == "", "PAC transport failure never invents fiscal stamp"); Check(confirmed.Status == "stamped" && requests[0].S("Folio") == requests[1].S("Folio") && requests[0].S("Date") == requests[1].S("Date"), "PAC retry preserves original Folio and Date"); }
        using (var adapter = new FacturamaAdapter("fixture", "fixture", true, new ContractHttp(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"Id\":\"without-stamp\"}") })))) Check((await adapter.Stamp(J.O("Folio", "stable", "Date", "2026-10-15T12:00:00", "Currency", "MXN", "Items", new[] { new JsonObject() }))).Status == "pending-reconciliation", "successful HTTP without TaxStamp is not reported as stamped");
        using (var adapter = new FacturamaAdapter("fixture", "fixture", true, new ContractHttp(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(J.O("Status", "pending", "Uuid", "0d140b8b-42af-4b2a-a57b-bebcaa89fd0d").ToJsonString()) })))) Check((await adapter.Cancel("fixture", "0d140b8b-42af-4b2a-a57b-bebcaa89fd0d", "02")).Status == "cancellation-pending", "PAC cancellation pending remains pending until provider confirmation");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var bodyBytes = Encoding.UTF8.GetBytes("{\"id\":\"evt_fixture\",\"type\":\"payment_intent.succeeded\"}");
        var secret = "whsec_" + J.Id();
        var sig = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + ".").Concat(bodyBytes).ToArray()));
        Check(SignedWebhooks.Stripe(bodyBytes, "t=" + timestamp + ",v1=" + sig, secret).S("id") == "evt_fixture", "Stripe webhook verifies raw body timestamp and HMAC");
        Deny(() => SignedWebhooks.Stripe(Encoding.UTF8.GetBytes("{}"), "t=" + timestamp + ",v1=" + sig, secret), "Stripe webhook rejects tampered raw body");
        Deny(() => SignedWebhooks.Stripe(bodyBytes, "t=" + timestamp + ",v1=" + sig, secret, DateTimeOffset.UtcNow.AddMinutes(6)), "Stripe webhook rejects expired signature");
        using (var http = new HttpClient(new ContractHttp(async req => { var data = JsonNode.Parse(await req.Content!.ReadAsStringAsync())!; Check(req.RequestUri!.Host == "fcm.googleapis.com" && data.B("validate_only") && req.Headers.Authorization?.Scheme == "Bearer", "FCM contract defaults to validation only and OAuth Bearer"); return new(HttpStatusCode.OK) { Content = new StringContent("{\"name\":\"projects/fixture/messages/synthetic\"}") }; }))) Check((await new FcmAdapter(http, "fixture-project", _ => Task.FromResult("fixture-access-token")).Send("fixture-device-token", "Fixture", "No recipient contacted")).Contains("synthetic"), "FCM adapter reads provider message identifier without claiming device delivery");
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using (var http = new HttpClient(new ContractHttp(req => { Check(req.Version == HttpVersion.Version20 && req.RequestUri!.Host == "api.sandbox.push.apple.com" && req.Headers.Contains("apns-topic") && req.Headers.Authorization?.Scheme == "bearer", "APNs contract uses sandbox HTTP2 topic and signed token"); var response = new HttpResponseMessage(HttpStatusCode.OK); response.Headers.Add("apns-id", "fixture-apns-id"); return Task.FromResult(response); }))) Check(await new ApnsAdapter(http, "FIXTURETEAM", "FIXTUREKEY", "invalid.example.fixture", ec.ExportECPrivateKeyPem()).Send(new string('a', 64), "Fixture", "No device contacted") == "fixture-apns-id", "APNs accepted identifier is distinct from device delivery");
        var call = AsteriskFrames.Parse(new Dictionary<string, string> { { "Event", "NewCallerid" }, { "Uniqueid", "fixture-call" }, { "CallerIDNum", "000" }, { "CallerIDName", "Synthetic" } });
        Check(call?.CallId == "fixture-call" && AsteriskFrames.Parse(new Dictionary<string, string> { { "Event", "Hangup" } }) is null, "AMI parser recognizes caller-ID event only; fixture is not a real call");

    }
}
