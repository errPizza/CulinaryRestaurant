using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Bridge;
using MesaNexo.Client;
using MesaNexo.Core;
using MesaNexo.Desktop;
using MesaNexo.Integrations;
Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
var root = Path.GetFullPath(args[0]);
var python = args[1];
var directory = Path.Combine(root, ".lab", "hybrid-" + J.Id());
Directory.CreateDirectory(directory);
var password = "Hybrid-" + J.Id() + "!";
var code = J.Id();
var site = "hybrid-" + J.Id()[..16];
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();
using var certificate = LabCertificate.Create(directory);
using var store = new RuntimeStore(python, Path.Combine(root, "runtime", "lab_host.py"), directory, site);
var options = new ApiOptions(site, Port: port, Bind: "127.0.0.1", LabPython: python, LabScript: Path.Combine(root, "runtime", "lab_host.py"), LabDirectory: directory, ReadySocket: null);
await using var app = ApiHost.Build(options, store, certificate);
var service = app.Services.GetRequiredService<BranchService>();
await service.Initialize(J.O("siteId", site, "name", "Culinary Restaurant · prueba aislada", "mode", "licencia", "managerName", "Gerencia", "managerUsername", "gerencia", "managerPassword", password, "enrollmentCode", code, "modules", Restaurant.Modules));
await app.StartAsync();
var profile = new ConnectionProfile($"https://localhost:{port}", site, Path.Combine(directory, "lab-ca.crt"), certificate.GetCertHashString(HashAlgorithmName.SHA256));
var profileNode = JsonSerializer.SerializeToNode(profile)!.AsObject();
using (var setup = new NativeSession(profile, new DesktopVault(Path.Combine(directory, "setup-vault")))) {

    await setup.Login("gerencia", password, "fixture", code);
    async Task<JsonObject> Act(string type, JsonObject payload) => (await setup.Execute(type, payload)).Obj("result");
    await Act("user.save", J.O("name", "Servicio", "username", "servicio", "role", "mesero", "password", password, "active", true));
    await Act("user.save", J.O("name", "Cocina", "username", "cocina", "role", "cocina", "password", password, "active", true));
    await Act("user.save", J.O("name", "Caja", "username", "caja", "role", "cajero", "password", password, "active", true));
    await Act("catalog.category", J.O("name", "Principales"));
    await Act("catalog.product", J.O("name", "Hamburguesa de la casa", "price", 18500, "taxBps", 1600, "category", "Principales", "station", "Cocina", "supportsCooking", true, "extras", new[] { J.O("name", "Queso extra", "price", 2000) }));
    for (var i = 1; i <= 6; i++) await Act("table.save", J.O("name", "Mesa " + i.ToString("00"), "capacity", 4, "zone", i <= 3 ? "Salón" : "Terraza"));
    await Act("cash.open", J.O("initial", 50000));
    await setup.Execute("session.logout", new());

}
var clients = new Dictionary<string, BridgeHost>();
async Task<BridgeHost> Client(string id) { if (!clients.TryGetValue(id, out var bridge)) { await service.RotateEnrollment(code); bridge = new BridgeHost(new DesktopVault(Path.Combine(directory, "vault-" + J.Hash(id)))); await bridge.Call("profile.save", (JsonObject)profileNode.DeepClone()); clients[id] = bridge; } return bridge; }
// The fiscal service against a Facturama sandbox without network: "fiscal.sandbox" processes every waiting request once.
// "integration.ready" gives the server credentials for a provider, as its file in /etc/culinaryos/integrations would.
var facturama = new FixtureFacturama();
using var pac = new FacturamaAdapter("fixture", "fixture", true, facturama);
var issuer = J.O("expeditionPlace", "06600", "product", J.O("productCode", "90101501", "unitCode", "E48", "taxObject", "02"));
async Task<JsonNode?> Fixture(string command, JsonObject a) {

    if (command == "integration.ready") { service.Integrations.Ready(a.S("key")); service.UseIntegrations(service.Integrations, (JsonArray)service.Domain.LiveCatalog.A("terminals").DeepClone()); return J.O("ready", a.S("key")); }
    if (command != "fiscal.sandbox") throw new InvalidOperationException("Orden de prueba desconocida");
    facturama.Mode = a.S("mode");
    var processed = 0;
    while (await service.ProcessFiscal(pac, issuer, allRequests: true)) processed++;
    return J.O("processed", processed, "stamps", new JsonArray(facturama.Stamps.Select(x => (JsonNode)x.DeepClone()).ToArray()));

}
if (args.Contains("--serve")) {

    Console.WriteLine(J.O("ready", true, "password", password, "enrollmentCode", code, "profile", profileNode, "directory", directory).ToJsonString());
    try { while (await Console.In.ReadLineAsync() is string line) { JsonObject? message = null; JsonObject response; try { message = JsonNode.Parse(line)!.AsObject(); var data = message.S("client") == "fixture" ? await Fixture(message.S("command"), message.Obj("args")) : await (await Client(message.S("client"))).Call(message.S("command"), message.Obj("args")); response = J.O("id", message.S("id"), "ok", true, "data", data); } catch (Exception e) { response = J.O("id", message?.S("id") ?? "", "ok", false, "error", e.Message, "status", e is ApiFailure f ? f.Status : 0); } Console.WriteLine(response.ToJsonString()); await Console.Out.FlushAsync(); } } finally { foreach (var c in clients.Values) c.Dispose(); await app.StopAsync(); }
    return;

}
var checks = new JsonArray();
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(J.O("test", name, "result", "passed")); }
async Task Reject(Func<Task> action, string name) { try { await action(); throw new Exception("Expected rejection: " + name); } catch (Exception e) when (!e.Message.StartsWith("Expected rejection")) { Check(true, name); } }
try {

    var bridge = await Client("manager");
    await Reject(async () => { await bridge.Call("arbitrary.command", new()); }, "bridge rejects unknown commands");
    var login = await bridge.Call("login", J.O("username", "gerencia", "password", password, "enrollmentCode", code));
    Check(login!["token"] is null, "bearer token stays outside the webview");
    await Reject(async () => { await bridge.Call("profile.save", profileNode); }, "profile cannot change during an active session");
    var s = (await bridge.Call("state", new()))!.AsObject();
    Check(s.A("products").Count == 1, "bridge reads actual encrypted runtime through trusted HTTPS");
    var waiter = s.A("users").Objects().First(x => x.S("role") == "mesero");
    async Task<JsonObject> Execute(string type, JsonObject payload) { var response = (await bridge.Call("execute", J.O("type", type, "payload", payload)))!.AsObject(); await bridge.Call("ack", J.O("operationId", response.S("operationId"))); return response.Obj("result"); }
    var unacknowledged = (await bridge.Call("execute", J.O("type", "catalog.category", "payload", J.O("name", "Recibo IPC"))))!.AsObject();
    Check((await bridge.Call("status", new()))!.B("pending"), "committed receipt survives a missing renderer acknowledgement");
    await Reject(async () => { await bridge.Call("execute", J.O("type", "catalog.category", "payload", J.O("name", "Duplicate IPC"))); }, "new mutation is blocked while renderer receipt is unacknowledged");
    await Reject(async () => { await bridge.Call("ack", J.O("operationId", J.Id())); }, "renderer cannot acknowledge a different UUID");
    var recovered = (await bridge.Call("reconcile", J.O("retryIfUnknown", true)))!.AsObject();
    Check(recovered.S("operationId") == unacknowledged.S("operationId") && recovered.S("local") == "committed", "reconciliation returns the original committed operation after IPC response loss");
    await bridge.Call("ack", J.O("operationId", recovered.S("operationId")));
    Check(!(await bridge.Call("status", new()))!.B("pending"), "only confirmed matching receipt releases the mutation barrier");
    var opened = await Execute("visit.open", J.O("tableId", s.A("tables")[0]!.S("id"), "people", 2, "waiterId", waiter.S("id")));
    var visit = opened.S("id");
    var draft = new JsonArray(J.O("productId", s.A("products")[0]!.S("id"), "quantity", 1, "person", 1, "cooking", "Medio", "notes", "Sin cebolla", "extras", new[] { "Queso extra" }));
    await bridge.Call("draft.save", J.O("visitId", visit, "items", draft));
    Check((await bridge.Call("draft.load", J.O("visitId", visit)))!.AsArray().Count == 1, "draft is durably saved through the native vault");
    var command = await Execute("command.send", J.O("visitId", visit, "items", draft));
    Check((await bridge.Call("draft.load", J.O("visitId", visit)))!.AsArray().Count == 0, "confirmed command clears protected draft before pending receipt removal");
    var uid = command.A("unitIds")[0]!.ToString();
    await Execute("unit.state", J.O("id", uid, "status", "preparing"));
    await Execute("unit.state", J.O("id", uid, "status", "ready"));
    await Execute("unit.state", J.O("id", uid, "status", "delivered"));
    var payment = await Execute("payment.add", J.O("visitId", visit, "amount", 20500, "tip", 0, "method", "cash", "tendered", 50000));
    Check(payment.L("change") == 29500, "real payment keeps exact cents and cash change");
    await Execute("visit.close", J.O("id", visit));
    s = (await bridge.Call("state", new()))!.AsObject();
    Check(s.A("visits").Objects().First(v => v.S("id") == visit).S("status") == "closed", "full bridge POS flow commits to encrypted runtime");
    Check((await bridge.Call("catalog.cached", new()))!.A("products").Count == 1, "cached catalog is readable through the secure vault");
    await bridge.Call("motion.set", J.O("reduced", true));
    Check((await bridge.Call("motion.get", new()))!.B("reduced"), "reduced motion preference survives native storage");
    Check((await bridge.Call("theme.get", new()))!.S("theme") == "system", "appearance follows the system until chosen");
    await bridge.Call("theme.set", J.O("theme", "dark"));
    Check((await bridge.Call("theme.get", new()))!.S("theme") == "dark", "dark appearance survives native storage");
    await Reject(async () => { await bridge.Call("theme.set", J.O("theme", "neon")); }, "unknown appearance is rejected");
    await Reject(async () => { await bridge.Call("updates.prepare", new()); }, "signed updater cannot prepare a package with an active session");
    await Reject(async () => { await bridge.Call("updates.install", new()); }, "installer cannot launch without a verified prepared package");
    await bridge.Call("logout", new());
    await Reject(async () => { await bridge.Call("state", new()); }, "logout removes native session access");
    var wrong = (JsonObject)profileNode.DeepClone();
    wrong["CaSha256"] = new string('0', 64);
    await Reject(async () => { await (await Client("bad-ca")).Call("profile.save", wrong); }, "CA fingerprint mismatch is rejected before credentials");
    var insecure = (JsonObject)profileNode.DeepClone();
    insecure["ServerUrl"] = $"http://localhost:{port}";
    await Reject(async () => { await (await Client("http")).Call("profile.save", insecure); }, "HTTP cannot replace trusted HTTPS");
    Check(!Directory.GetFiles(Path.Combine(directory, "vault-" + J.Hash("manager"))).Any(f => Encoding.UTF8.GetString(File.ReadAllBytes(f)).Contains(password)), "DPAPI vault files do not expose plaintext credentials");
    Directory.CreateDirectory(Path.Combine(root, "evidence"));
    await File.WriteAllTextAsync(Path.Combine(root, "evidence", "hybrid-bridge.json"), checks.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"{checks.Count} hybrid bridge checks passed.");

} finally { foreach (var c in clients.Values) c.Dispose(); await app.StopAsync(); }
// Facturama's POST 3/cfdis, keeping the Folio and Date of every submission. Mode "lost" stamps and then drops the
// connection before answering, as when the PAC stamped and the response never arrived.
sealed class FixtureFacturama: HttpMessageHandler {
    public string Mode = "ok"; public readonly List<JsonObject> Stamps = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {

        if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != "/3/cfdis") return new(HttpStatusCode.NotFound);
        var cfdi = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
        Stamps.Add(J.O("folio", cfdi.S("Folio"), "date", cfdi.S("Date")));
        if (Mode == "lost") throw new HttpRequestException("Connection reset after the PAC stamped");
        return new(HttpStatusCode.OK) { Content = new StringContent(J.O("Id", J.Id(), "Status", "active", "Complement", J.O("TaxStamp", J.O("Uuid", Guid.NewGuid().ToString()))).ToJsonString(), Encoding.UTF8, "application/json") };

    }
}
