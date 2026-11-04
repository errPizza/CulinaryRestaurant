using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    // A rotation of the branch CA seen from a terminal: it learns the new CA over the connection it already trusts, keeps
    // working when the server certificate changes issuer, and drops the old CA when it is retired. It never adopts a set
    // that leaves out every CA it trusts now.
    public static async Task CaRotation(string root, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var p = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return p; }
        static X509Certificate2 Ca(string name) {

            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

        }
        static X509Certificate2 Server(X509Certificate2 ca, ECDsa key) {

            var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            req.CertificateExtensions.Add(san.Build());
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
            using var issued = req.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), RandomNumberGenerator.GetBytes(12));
            using var withKey = issued.CopyWithPrivateKey(key);
            return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);

        }
        static string Sha(X509Certificate2 c) => c.GetCertHashString(HashAlgorithmName.SHA256);
        var folder = Path.Combine(root, ".lab", "ca-rotation-" + J.Id());
        var server = Path.Combine(folder, "server");
        var terminal = Path.Combine(folder, "terminal");
        Directory.CreateDirectory(server);
        Directory.CreateDirectory(terminal);
        using var oldCa = Ca("Culinary Restaurant rotation old");
        using var newCa = Ca("Culinary Restaurant rotation new");
        using var stranger = Ca("Otra sucursal");
        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(Path.Combine(server, "restaurant-ca.crt"), oldCa.ExportCertificatePem());
        File.WriteAllText(Path.Combine(terminal, "old.pem"), oldCa.ExportCertificatePem());
        using var store = LabPostgres.Test(root, Path.Combine(folder, "store"), "rotation-site");
        async Task<(WebApplication App, int Port)> Start(X509Certificate2 cert) { var port = FreePort(); var app = ApiHost.Build(new("rotation-site", port, "127.0.0.1", ReadySocket: null, LabDirectory: server), store, cert); await app.StartAsync(); return (app, port); }
        async Task<bool> Reaches(ConnectionProfile p) { try { using var http = TrustedConnection.Create(p); return (await http.GetStringAsync("api/identity")).Contains("rotation-site"); } catch (HttpRequestException) { return false; } }
        async Task<ConnectionProfile?> Learn(ConnectionProfile p) { using var session = new NativeSession(p, new MemoryVault()); return await session.RefreshTrust(); }

        using var firstCert = Server(oldCa, serverKey);
        var (first, port) = await Start(firstCert);
        var profile = new ConnectionProfile($"https://localhost:{port}", "rotation-site", Path.Combine(terminal, "old.pem"), Sha(oldCa));
        try {

            Check(await Reaches(profile) && await Learn(profile) is null, "with one CA published there is nothing to learn");
            // Announce: the server publishes both CAs while its certificate is still the old one.
            File.WriteAllText(Path.Combine(server, "restaurant-ca-bundle.pem"), oldCa.ExportCertificatePem() + "\n" + newCa.ExportCertificatePem());
            var learned = await Learn(profile);
            Check(learned is not null && learned.CaSha256.Split(',').Order().SequenceEqual(new[] { Sha(oldCa), Sha(newCa) }.Order()) && File.ReadAllText(learned.CaFile).Split("BEGIN CERTIFICATE").Length == 3 && await Reaches(learned), "a terminal learns the new CA over the connection it trusts and keeps reaching the server");
            profile = learned!;

        } finally { await first.StopAsync(); await first.DisposeAsync(); }
        // Switch: the server certificate now comes from the new CA.
        using var secondCert = Server(newCa, serverKey);
        var (second, port2) = await Start(secondCert);
        try {

            profile = profile with { ServerUrl = $"https://localhost:{port2}" };
            var unaware = new ConnectionProfile(profile.ServerUrl, "rotation-site", Path.Combine(terminal, "old.pem"), Sha(oldCa));
            Check(await Reaches(profile), "a terminal that learned both CAs keeps working after the switch");
            Check(!await Reaches(unaware), "a terminal that never learned the new CA rejects the server, and has to be provisioned again");
            // Retire: only the new CA is published, and the terminal drops the old one.
            File.Delete(Path.Combine(server, "restaurant-ca-bundle.pem"));
            File.WriteAllText(Path.Combine(server, "restaurant-ca.crt"), newCa.ExportCertificatePem());
            var retired = await Learn(profile);
            Check(retired is not null && retired.CaSha256 == Sha(newCa) && await Reaches(retired), "after the retirement the terminal trusts only the new CA");
            Check(TrustedConnection.Adopt(retired!, [stranger.ExportCertificatePem()]) is null, "a set that leaves out every CA the terminal trusts is never adopted");
            Check(TrustedConnection.Adopt(retired!, [newCa.ExportCertificatePem(), firstCert.ExportCertificatePem()]) is null, "a certificate that is not a CA is never adopted");
            using var fourth = Ca("cuarta");
            Check(TrustedConnection.Adopt(retired!, [newCa.ExportCertificatePem(), stranger.ExportCertificatePem(), oldCa.ExportCertificatePem(), fourth.ExportCertificatePem()]) is null, "more than three CAs are never adopted");
            var mixed = Path.Combine(terminal, "mixed.pem");
            File.WriteAllText(mixed, newCa.ExportCertificatePem() + "\n" + stranger.ExportCertificatePem());
            var refused = false;
            try { TrustedConnection.Roots(retired! with { CaFile = mixed }); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "a CA file with a CA that is not pinned is refused");

        } finally { await second.StopAsync(); await second.DisposeAsync(); }

    }
}
