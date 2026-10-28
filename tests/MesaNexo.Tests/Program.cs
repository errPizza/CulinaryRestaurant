using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Client;
using MesaNexo.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace MesaNexo.Tests;

sealed class MemoryVault: ISecureVault {
    public Dictionary<string, string> Values = [];
    public Task<string?> Read(string key) => Task.FromResult(Values.GetValueOrDefault(key));
    public Task Write(string key, string value) { Values[key] = value; return Task.CompletedTask; }
    public Task Delete(string key) { Values.Remove(key); return Task.CompletedTask; }
}
sealed class FaultStore(IBranchStore inner): IBranchStore {
    public bool LoseNextResponse;
    public async Task<JsonNode?> CallAsync(JsonObject request, CancellationToken ct = default) {

        var r = await inner.CallAsync(request, ct);
        if (LoseNextResponse && request.S("op") == "transaction_v1") { LoseNextResponse = false; throw new StoreException("COS-DATA-UNAVAILABLE"); }
        return r;

    }
}
static class Program {
    static readonly List<JsonObject> evidence = []; static string root = "", folder = "", python = ""; static int port; static X509Certificate2 cert = null!; static PostgresBranchStore runtime = null!; static FaultStore faults = null!; static WebApplication app = null!; static BranchService service = null!; static ConnectionProfile profile = null!;
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
    static async Task Reject(Func<Task> action, string name, int? status = null) { try { await action(); throw new Exception("Expected rejection: " + name); } catch (ApiFailure e) { Check(status is null || e.Status == status, name); } catch (IOException) { Check(status is null, name); } }
    static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var p = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return p; }
    static async Task Start() {

        runtime = LabPostgres.Test(root, folder, "integration-site");
        faults = new FaultStore(runtime);
        app = ApiHost.Build(new("integration-site", port, "127.0.0.1", ReadySocket: null), faults, cert);
        await app.StartAsync();
        service = app.Services.GetRequiredService<BranchService>();
        TestLicense.Attach(service);

    }
    static async Task Stop() { await app.StopAsync(); await app.DisposeAsync(); runtime.Dispose(); }
    static async Task<NativeSession> Login(string username, string password, MemoryVault vault) {

        var code = J.Id();
        await service.RotateEnrollment(code);
        var c = new NativeSession(profile, vault);
        await c.Login(username, password, "Test terminal " + username, code);
        return c;

    }
    static async Task Main(string[] args) {

        root = Path.GetFullPath(args[0]);
        python = args[1];
        if (args.Contains("--fiscal-only")) { try { await FiscalTests.Run(root, python, evidence); } finally { await File.WriteAllTextAsync(Path.Combine(root, "evidence", "fiscal-sandbox.json"), new JsonArray(evidence.Select(x => (JsonNode)x).ToArray()).ToJsonString()); } return; }
        if (args.Contains("--live-only")) { try { await LiveIntegrationTests.Run(root, python, evidence); } finally { await File.WriteAllTextAsync(Path.Combine(root, "evidence", "live-integrations.json"), new JsonArray(evidence.Select(x => (JsonNode)x).ToArray()).ToJsonString()); } return; }
        if (args.Contains("--print-only")) { try { await PrintTests.Run(root, python, evidence); } finally { await File.WriteAllTextAsync(Path.Combine(root, "evidence", "printing.json"), new JsonArray(evidence.Select(x => (JsonNode)x).ToArray()).ToJsonString()); } return; }
        if (args.Contains("--adapters-only")) { try { await AdapterTests.Run(evidence); } finally { await File.WriteAllTextAsync(Path.Combine(root, "evidence", "adapters.json"), new JsonArray(evidence.Select(x => (JsonNode)x).ToArray()).ToJsonString()); } return; }
        if (args.Contains("--license-only")) { try { await LicenseTests.Run(root, python, evidence); } finally { await File.WriteAllTextAsync(Path.Combine(root, "evidence", "licenses.json"), new JsonArray(evidence.Select(x => (JsonNode)x).ToArray()).ToJsonString()); } return; }
        if (args.Contains("--tenant-only")) { try { await TenantTests.Run(root, evidence); } finally { await File.WriteAllTextAsync(Path.Combine(root, "evidence", "tenants.json"), new JsonArray(evidence.Select(x => (JsonNode)x).ToArray()).ToJsonString()); } return; }
        if (args.Contains("--integrations-only")) { try { await IntegrationTests.Run(root, python, evidence); } finally { await File.WriteAllTextAsync(Path.Combine(root, "evidence", "integrations.json"), new JsonArray(evidence.Select(x => (JsonNode)x).ToArray()).ToJsonString()); } return; }
        if (args.Contains("--loyalty-only")) { try { await LoyaltyTests.Run(root, python, evidence); } finally { await File.WriteAllTextAsync(Path.Combine(root, "evidence", "loyalty.json"), new JsonArray(evidence.Select(x => (JsonNode)x).ToArray()).ToJsonString()); } return; }
        if (args.Contains("--back-only")) { try { await BackofficeTests.Run(root, python, evidence); } finally { await File.WriteAllTextAsync(Path.Combine(root, "evidence", "backoffice.json"), new JsonArray(evidence.Select(x => (JsonNode)x).ToArray()).ToJsonString()); } return; }
        if (args.Contains("--front-only")) { try { await FrontofficeTests.Run(root, python, evidence); } finally { await File.WriteAllTextAsync(Path.Combine(root, "evidence", "frontoffice.json"), new JsonArray(evidence.Select(x => (JsonNode)x).ToArray()).ToJsonString()); } return; }
        if (args.Contains("--devmap-only")) { try { await DevMapTests.Run(root, python, evidence); } finally { await File.WriteAllTextAsync(Path.Combine(root, "evidence", "devmap.json"), new JsonArray(evidence.Select(x => (JsonNode)x).ToArray()).ToJsonString()); } return; }
        if (args.Contains("--buttons-only")) { await ButtonFeedbackTests.Run(root, evidence); await File.WriteAllTextAsync(Path.Combine(root, "evidence", "button-feedback.json"), new JsonArray(evidence.Select(x => (JsonNode)x).ToArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine($"{evidence.Count} button checks passed."); return; }
        if (args.Contains("--culinary-server")) { var i = Array.IndexOf(args, "--culinary-server"); await ServerSmoke.Run(root, python, args[i + 1], args[i + 2], args[i + 3], args[i + 4]); return; }
        if (args.Contains("--lock-only")) { await UiTests.RunLicenseLock(root, evidence); return; }
        if (args.Contains("--ui-only")) { var snapshot = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".lab", "ui-state.json")))!.AsObject(); await UiTests.Run(root, snapshot, null!, new MemoryVault(), evidence); await UiTests.RunLicenseLock(root, evidence); await ButtonFeedbackTests.Run(root, evidence); Console.WriteLine($"{evidence.Count} UI checks passed."); await File.WriteAllTextAsync(Path.Combine(root, "evidence", "ui-style-review.json"), new JsonArray(evidence.Select(x => (JsonNode)x).ToArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true })); return; }
        if (args.Contains("--central-only")) { await CentralTests.Run(root, python, evidence); await File.WriteAllTextAsync(Path.Combine(root, "evidence", "central.json"), new JsonArray(evidence.Select(x => (JsonNode)x).ToArray()).ToJsonString()); return; }
        if (args.Contains("--business-only")) { await BusinessTests.Run(root, python, evidence); await File.WriteAllTextAsync(Path.Combine(root, "evidence", "business.json"), new JsonArray(evidence.Select(x => (JsonNode)x).ToArray()).ToJsonString()); return; }
        if (args.Contains("--setup-only")) { try { await SetupTests.Run(root, python, evidence); } finally { await File.WriteAllTextAsync(Path.Combine(root, "evidence", "setup.json"), new JsonArray(evidence.Select(x => (JsonNode)x).ToArray()).ToJsonString()); } return; }
        if (args.Contains("--store-only")) { await StoreTests.Run(root, evidence); await StoreTests.Migration(root, python, evidence); return; }
        if (args.Contains("--migration-only")) { await MigrationTests.Run(root, python, evidence); await File.WriteAllTextAsync(Path.Combine(root, "evidence", "migration.json"), new JsonArray(evidence.Select(x => (JsonNode)x).ToArray()).ToJsonString()); return; }
        folder = Path.Combine(root, ".lab", "integration-" + J.Id());
        Directory.CreateDirectory(folder);
        port = FreePort();
        cert = LabCertificate.Create(folder);
        profile = new($"https://localhost:{port}", "integration-site", Path.Combine(folder, "lab-ca.crt"), cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256));
        try {

            await Start();
            Check(!await service.Ready(), "readiness rejects uninitialized store");
            var password = "Test-" + J.Id() + "!";
            var code = J.Id();
            await TestLicense.Initialize(service, J.O("siteId", "integration-site", "name", "Integración aislada", "mode", "licencia", "managerName", "Gerencia", "managerUsername", "gerencia", "managerPassword", password, "enrollmentCode", code, "modules", Restaurant.Modules));
            Check(await service.Ready(), "readiness checks real store and site identity");
            var mv = new MemoryVault();
            using var manager = await Login("gerencia", password, mv);
            var managerState = await manager.State();
            Check(!managerState.ToJsonString().Contains("argon2id"), "snapshots never expose password hashes");
            async Task<JsonObject> Act(NativeSession client, string type, JsonObject p) => (await client.Execute(type, p)).Obj("result");
            var waiter = await Act(manager, "user.save", J.O("name", "Mesero A", "username", "servicio", "role", "mesero", "password", password, "active", true));
            var kitchen = await Act(manager, "user.save", J.O("name", "Cocina", "username", "cocina", "role", "cocina", "password", password, "active", true));
            var waiter2 = await Act(manager, "user.save", J.O("name", "Mesero B", "username", "servicio2", "role", "mesero", "password", password, "active", true));
            var wv = new MemoryVault();
            var kv = new MemoryVault();
            var w2v = new MemoryVault();
            using var w = await Login("servicio", password, wv);
            using var k = await Login("cocina", password, kv);
            using var w2 = await Login("servicio2", password, w2v);
            await Reject(() => k.Report("", ""), "kitchen cannot access financial reports", 403);
            await Reject(() => k.Execute("cash.open", J.O("initial", 0)), "backend denies kitchen cash access", 403);
            await Reject(() => manager.Execute("owner.modules", J.O("modules", Array.Empty<string>())), "provider operations isolated from branch", 403);
            var category = await Act(manager, "catalog.category", J.O("name", "Principales"));
            var product = await Act(manager, "catalog.product", J.O("name", "Producto real de prueba", "price", 18500, "category", "Principales", "station", "Cocina", "supportsCooking", true, "extras", new[] { J.O("name", "Extra", "price", 2000) }));
            var table = await Act(manager, "table.save", J.O("name", "Mesa 01", "capacity", 4, "zone", "Salón"));
            var table2 = await Act(manager, "table.save", J.O("name", "Mesa 02", "capacity", 4, "zone", "Salón"));
            await Act(manager, "cash.open", J.O("initial", 50000));
            var visit = await Act(w, "visit.open", J.O("tableId", table.S("id"), "people", 3));
            var vid = visit.S("id");
            var draft = new JsonArray(J.O("productId", product.S("id"), "quantity", 3, "person", 1, "notes", "Sin cebolla", "cooking", "Medio", "extras", new[] { "Extra" }));
            await w.SaveDraft(vid, draft);
            Check((await w.LoadDraft(vid)).Count == 1, "protected draft survives session object persistence");
            var command = await Act(w, "command.send", J.O("visitId", vid, "items", draft));
            var units = command.A("unitIds").Strings();
            Check(units.Length == 3, "command creates individual durable units");
            var pushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var listening = false;
            await k.StartLive(() => { if (listening) pushed.TrySetResult(); return Task.CompletedTask; });
            listening = true;
            await Act(manager, "command.priority", J.O("id", command.S("id"), "priority", true, "reason", "live test"));
            await pushed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var liveState = await k.LiveSnapshot();
            Check(!liveState.A("units").ToJsonString().Contains("\"price\""), "authenticated TLS SignalR snapshot retains kitchen financial restrictions");
            await k.StopLive();
            await k.StartLive(() => Task.CompletedTask);
            Check((await k.LiveSnapshot()).A("units").Count == 3, "SignalR reconnect retrieves a consistent durable snapshot");
            await k.StopLive();
            var ks = await k.State();
            Check(ks.A("units").Count == 3 && !ks.A("units").ToJsonString().Contains("\"price\""), "kitchen receives real command without historical prices");
            foreach (var uid in units.Take(2)) { await Act(k, "unit.state", J.O("id", uid, "status", "preparing")); await Act(k, "unit.state", J.O("id", uid, "status", "ready")); }
            var partial = await w.State();
            Check(partial.A("notifications").Objects().Count(x => x.S("type") == "ready") == 2 && !partial.A("notifications").Objects().Any(x => x.S("type") == "complete"), "partial preparation persists exactly two notices");
            await Reject(() => w.Execute("payment.add", J.O("visitId", vid, "amount", 61501, "method", "cash")), "overpayment rejected", 400);
            await Act(k, "unit.state", J.O("id", units[2], "status", "preparing"));
            await Act(k, "unit.state", J.O("id", units[2], "status", "ready"));
            Check((await w.State()).A("notifications").Objects().Count(x => x.S("type") == "complete") == 1, "complete notice is durable and unique");
            await Act(w, "visit.deliverAll", J.O("id", vid));
            await Reject(() => k.Execute("unit.state", J.O("id", units[0], "status", "delivered")), "kitchen cannot confirm waiter delivery", 403);
            await Act(w, "payment.add", J.O("visitId", vid, "amount", 61500, "tip", 3000, "method", "card"));
            await Act(w, "visit.close", J.O("id", vid));
            var closed = await w.State();
            Check(closed.A("visits").Objects().Single(x => x.S("id") == vid).S("status") == "closed" && closed.A("tables").Objects().Single(x => x.S("id") == table.S("id")).S("status") == "cleaning", "login to table to kitchen to delivery to payment to close complete");
            Check(closed.A("payments").Objects().All(x => !x.B("externalProcessed")), "card payments remain manual records");
            await Act(w, "table.clean", J.O("id", table.S("id")));
            var v2 = await Act(w, "visit.open", J.O("tableId", table.S("id"), "people", 2));
            var v2id = v2.S("id");
            faults.LoseNextResponse = true;
            await Reject(() => w.Execute("command.send", J.O("visitId", v2id, "items", new[] { J.O("productId", product.S("id"), "quantity", 2) })), "lost response is shown as uncertain", 503);
            Check(await w.HasPending(), "operation UUID saved before network confirmation");
            var pending = wv.Values.Single(x => x.Key.StartsWith("pending_")).Value;
            var pendingId = JsonNode.Parse(pending)!.S("operationId");
            await Stop();
            await Start();
            using var restarted = new NativeSession(profile, wv);
            await restarted.Login("servicio", password, "restarted device");
            var reconciled = await restarted.Reconcile(true);
            Check(reconciled!.S("local") == "committed" && !await restarted.HasPending(), "client and storage restart reconcile lost response without resend");
            var restartState = await restarted.State();
            Check(restartState.A("units").Objects().Count(x => x.S("visitId") == v2id) == 2, "restart did not duplicate command units");
            // Two independent HTTPS terminals race on the same table: only one can open it.
            var competing = await Task.WhenAll(Open(w), Open(w2));
            Check(competing.Count(x => x) == 1, "concurrent terminals serialize table occupancy");
            async Task<bool> Open(NativeSession c) { try { await c.Execute("visit.open", J.O("tableId", table2.S("id"), "people", 1)); return true; } catch (ApiFailure) { return false; } }
            var v2units = restartState.A("units").Objects().Where(x => x.S("visitId") == v2id).Select(x => x.S("id")).ToArray();
            var split = await Act(restarted, "visit.split", J.O("id", v2id, "unitIds", new[] { v2units[0] }));
            await Act(restarted, "visit.merge", J.O("id", v2id, "otherId", split.S("id")));
            Check((await restarted.State()).A("units").Objects().Count(x => x.S("visitId") == v2id) == 2, "split and merge preserve units without kitchen resend");
            await Act(restarted, "visit.transfer", J.O("id", v2id, "waiterId", waiter2.S("id")));
            await Reject(() => restarted.Execute("command.send", J.O("visitId", v2id, "items", new[] { J.O("productId", product.S("id"), "quantity", 1) })), "previous waiter loses authority after transfer", 403);
            await Act(w2, "payment.add", J.O("visitId", v2id, "amount", 37000, "method", "cash"));
            var cancel = await Act(w2, "request.create", J.O("visitId", v2id, "type", "cancel", "unitId", v2units[0], "reason", "Prueba"));
            await Reject(() => manager.Execute("request.decide", J.O("id", cancel.S("id"), "approve", true)), "cancel cannot make paid account negative", 400);
            var latest = (await manager.State()).A("products").Objects().Single(x => x.S("id") == product.S("id"));
            latest["price"] = 99900;
            await Act(manager, "catalog.product", (JsonObject)latest.DeepClone());
            Check((await w2.State()).A("units").Objects().Where(x => x.S("visitId") == v2id).All(x => x.L("price") == 18500), "confirmed units preserve historical price");
            var reservation = J.O("name", "Persona de prueba", "contact", "local", "start", DateTimeOffset.UtcNow.AddDays(1).ToString("O"), "people", 2, "tableId", table.S("id"), "status", "confirmed");
            await Act(manager, "reservation.save", reservation);
            await Reject(() => manager.Execute("reservation.save", reservation), "reservation two-hour collision is enforced", 400);
            var wrongProfile = profile with { SiteId = "another-site" };
            using (var wrong = new NativeSession(wrongProfile, new MemoryVault())) { try { await wrong.Login("servicio", password, "wrong"); throw new Exception("identity should fail"); } catch (InvalidOperationException) { Check(true, "site identity verified before credentials"); } }
            var wrongName = profile with { ServerUrl = $"https://invalid-name.local:{port}", ConnectAddress = "127.0.0.1" };
            using (var wrong = new NativeSession(wrongName, new MemoryVault())) await Reject(() => wrong.State(), "TLS rejects mismatched SAN hostname");
            using (var otherCert = LabCertificate.Create(Path.Combine(folder, "wrong-ca"))) { var wrongCa = profile with { CaFile = Path.Combine(folder, "wrong-ca", "lab-ca.crt"), CaSha256 = otherCert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256) }; using var wrong = new NativeSession(wrongCa, new MemoryVault()); await Reject(() => wrong.State(), "TLS rejects server outside provisioned CA"); }
            var userState = (await manager.State()).A("users").Objects().Single(x => x.S("id") == waiter.S("id"));
            userState["active"] = false;
            await Act(manager, "user.save", (JsonObject)userState.DeepClone());
            await Reject(() => restarted.State(), "deactivating user revokes existing sessions", 401);
            Check((await manager.State()).Obj("connection").S("internet") == "unverified", "central unavailable never implies WAN down");
            var uiState = await manager.State();
            File.WriteAllText(Path.Combine(root, ".lab", "ui-state.json"), uiState.ToJsonString());
            await UiTests.Run(root, uiState, manager, mv, evidence);
            await Stop();
            await Reject(() => manager.State(), "local server inaccessible produces no success");

            await CentralTests.Run(root, python, evidence);
            await LicenseTests.Run(root, python, evidence);
            await UiTests.RunLicenseLock(root, evidence);
            await MigrationTests.Run(root, python, evidence);
            await BusinessTests.Run(root, python, evidence);
            await DevMapTests.Run(root, python, evidence);
            await FrontofficeTests.Run(root, python, evidence);
            await BackofficeTests.Run(root, python, evidence);
            await LoyaltyTests.Run(root, python, evidence);
            await IntegrationTests.Run(root, python, evidence);
            await TenantTests.Run(root, evidence);
            await AdapterTests.Run(evidence);
            await SchemaTests.Run(root, python, evidence);
            await StoreTests.Run(root, evidence);
            await StoreTests.Migration(root, python, evidence);
            await PrintTests.Run(root, python, evidence);
            await FiscalTests.Run(root, python, evidence);
            await LiveIntegrationTests.Run(root, python, evidence);
            Console.WriteLine($"{evidence.Count} integration/UI checks passed.");

        } catch (Exception e) { Console.Error.WriteLine(e); evidence.Add(J.O("result", "failed", "message", e.ToString())); Environment.ExitCode = 1; try { await Stop(); } catch { } } finally { cert.Dispose(); Directory.CreateDirectory(Path.Combine(root, "evidence")); await File.WriteAllTextAsync(Path.Combine(root, "evidence", "integration.json"), new JsonArray(evidence.Select(x => (JsonNode)x).ToArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }

    }
}