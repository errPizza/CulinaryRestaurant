using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Client;
using MesaNexo.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
namespace MesaNexo.Tests;
// First-run Setup mode against a real HTTPS backend and an empty isolated store.
static class SetupTests {
    public static async Task Run(string root, string python, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var p = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return p; }
        var folder = Path.Combine(root, ".lab", "setup-" + J.Id());
        Directory.CreateDirectory(folder);
        var cert = LabCertificate.Create(folder);
        var setupCode = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        async Task<(WebApplication app, PostgresBranchStore store, ConnectionProfile profile)> Start(string site, string? code) {

            var port = FreePort();
            var store = LabPostgres.Test(root, Path.Combine(folder, site), site);
            var app = ApiHost.Build(new(site, port, "127.0.0.1", ReadySocket: null, SetupCode: code), store, cert);
            await app.StartAsync();
            return (app, store, new ConnectionProfile($"https://localhost:{port}", site, Path.Combine(folder, "lab-ca.crt"), cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)));

        }
        async Task<(int status, JsonObject body)> Post(ConnectionProfile p, JsonObject body) { using var http = TrustedConnection.Create(p); using var r = await http.PostAsJsonAsync("api/setup", body); return ((int)r.StatusCode, JsonNode.Parse(await r.Content.ReadAsStringAsync()) as JsonObject ?? new()); }
        async Task<JsonObject> Status(ConnectionProfile p) { using var http = TrustedConnection.Create(p); return JsonNode.Parse(await http.GetStringAsync("api/setup"))!.AsObject(); }
        var password = "Setup-" + J.Id();
        JsonObject Form(string site, string code, string confirm) => J.O("siteId", site, "setupCode", code, "name", "Restaurante de prueba", "managerName", "Gerencia Setup", "managerUsername", "gerente", "managerPassword", password, "managerPasswordConfirm", confirm);

        var (app, store, profile) = await Start("setup-site", setupCode);
        try {

            var service = app.Services.GetRequiredService<BranchService>();
            Check(!await service.Ready() && await service.AwaitingSetup(), "empty store is awaiting setup, not active");
            Check((await Status(profile)).B("needsSetup"), "setup status announces first-run mode over trusted TLS");
            Check((await Post(profile, Form("setup-site", "incorrecto-" + J.Id() + J.Id(), password))).status == 403, "wrong setup code is rejected");
            Check((await Post(profile, Form("otra-sucursal", setupCode, password))).status == 403, "setup for another site is rejected");
            Check((await Post(profile, Form("setup-site", setupCode, password + "x"))).status == 400, "mismatched manager passwords are rejected");
            var shortForm = Form("setup-site", setupCode, "corta");
            shortForm["managerPassword"] = "corta";
            Check((await Post(profile, shortForm)).status == 400 && await service.AwaitingSetup(), "short manager password is rejected without creating the branch");
            var (status, created) = await Post(profile, Form("setup-site", setupCode, password));
            Check(status == 200 && created.B("initialized") && created.S("username") == "gerente" && created.S("enrollmentCode").Length >= 20, "setup creates the branch and returns a one-time device code");
            Check(await service.Ready() && !await service.AwaitingSetup() && !(await Status(profile)).B("needsSetup"), "branch becomes active and setup mode closes");
            Check((await Post(profile, Form("setup-site", setupCode, password))).status == 409, "setup cannot overwrite an initialized branch");
            using (var early = new NativeSession(profile, new MemoryVault())) { var status423 = 0; try { await early.Login("gerente", password, "Setup kiosk", created.S("enrollmentCode")); } catch (ApiFailure e) { status423 = e.Status; } Check(status423 == 423, "a freshly set up branch asks for its license before anyone signs in"); }
            await TestLicense.Grant(service, new JsonObject());
            using var session = new NativeSession(profile, new MemoryVault());
            var login = await session.Login("gerente", password, "Setup kiosk", created.S("enrollmentCode"));
            var state = await session.State();
            Check(login.Obj("user").S("role") == "manager" && state.Obj("restaurant").S("name") == "Restaurante de prueba", "manager signs in on the first device with the setup code");
            using var other = new NativeSession(profile, new MemoryVault());
            var reused = false;
            try { await other.Login("gerente", password, "Second device", created.S("enrollmentCode")); reused = true; } catch (ApiFailure) { }
            Check(!reused, "setup device code is single use");

        } finally { await app.StopAsync(); await app.DisposeAsync(); store.Dispose(); }

        var (plain, plainStore, plainProfile) = await Start("plain-site", null);
        try {

            Check(!(await Status(plainProfile)).B("needsSetup") && (await Post(plainProfile, Form("plain-site", setupCode, password))).status == 404, "without a setup code the backend never offers setup");

        } finally { await plain.StopAsync(); await plain.DisposeAsync(); plainStore.Dispose(); }

    }
}
