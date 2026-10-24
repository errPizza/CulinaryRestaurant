using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Central;
using MesaNexo.Client;
using MesaNexo.Core;
namespace MesaNexo.Tests;

static class CentralTests {
    static int Port() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port; }
    public static async Task Run(string root, string python, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception(name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        async Task Deny(Func<Task> act, string name) { try { await act(); throw new Exception("Rejection expected: " + name); } catch (BusinessException) { Check(true, name); } catch (CryptographicException) { Check(true, name); } }
        var folder = Path.Combine(root, ".lab", "central-" + J.Id());
        Directory.CreateDirectory(folder);
        var key = RandomNumberGenerator.GetBytes(32);
        var database = Path.Combine(folder, "central.sqlite");
        using var store = new CentralStore(database, (byte[])key.Clone());
        store.Initialize();
        using var signer = RSA.Create(3072);
        var signing = Path.Combine(folder, "cloud-signing-test-only.pem");
        var pub = Path.Combine(folder, "public.pem");
        File.WriteAllText(signing, signer.ExportRSAPrivateKeyPem());
        File.WriteAllText(pub, signer.ExportSubjectPublicKeyInfoPem());
        var central = new CentralService(store, new Passwords(), "central-test", signing);
        using var clientKey = RSA.Create(2048);
        var req = new CertificateRequest("CN=branch-alpha-test", clientKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var eku = new OidCollection { new("1.3.6.1.5.5.7.3.2") };
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, true));
        using var ephemeralClient = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(2));
        using var clientCert = X509CertificateLoader.LoadPkcs12(ephemeralClient.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
        var data = Encoding.UTF8.GetBytes(J.Canonical(J.O("protocol", "culinaryos-events-v1", "site_id", "alpha-test", "event_id", "event-test", "record_id", "record-test", "version", 1, "data", J.O("test", true))));
        var digest = Convert.ToHexStringLower(SHA256.HashData(data));
        await Deny(() => Task.Run(() => central.Receive(data, digest, clientCert)), "certificate not registered with --provision-tenant is rejected");
        // The registry CulinaryServer lists for the panel (GET /admin/v1/registry) is what the operator provisions here.
        store.ProvisionTenant(J.O("tenantId", "restaurant-alpha", "branchId", "main", "slug", "restaurant-alpha-main", "tenantName", "Alpha", "branchName", "Main", "siteId", "alpha-test", "certificateSha256", clientCert.GetCertHashString(HashAlgorithmName.SHA256), "ownerName", "Owner", "ownerUsername", "restaurant-owner", "ownerPassword", "Test-" + J.Id(), "ownerEnrollmentCode", J.Id()), new Passwords());
        central.Receive(data, digest, clientCert);
        using (var reopened = new CentralStore(database, (byte[])key.Clone())) { reopened.Initialize(); var second = new CentralService(reopened, new Passwords(), "central-test", signing); var ack = second.Receive(data, digest, clientCert); Check(ack.S("status") == "committed" && ack.S("event_id") == "event-test", "receiver restart deduplicates persisted event and returns committed ACK"); }
        var changed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data).Replace("true", "false"));
        await Deny(() => Task.Run(() => central.Receive(changed, Convert.ToHexStringLower(SHA256.HashData(changed)), clientCert)), "same event ID with changed body rejected");
        var wrong = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data).Replace("alpha-test", "beta-test"));
        await Deny(() => Task.Run(() => central.Receive(wrong, Convert.ToHexStringLower(SHA256.HashData(wrong)), clientCert)), "mTLS certificate cannot impersonate another branch");
        using var serverCert = LabCertificate.Create(Path.Combine(folder, "tls"));
        var admin = Port();
        var receiver = Port();
        await using var app = CentralHost.Build(central, "central-test", serverCert, admin, receiver, "127.0.0.1");
        await app.StartAsync();
        HttpClient Http(bool client) { var h = new SocketsHttpHandler { UseProxy = false }; h.SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, CustomTrustStore = { serverCert }, RevocationMode = X509RevocationMode.NoCheck } }; if (client) h.SslOptions.ClientCertificates = new X509CertificateCollection { clientCert }; return new HttpClient(h) { BaseAddress = new Uri($"https://localhost:{receiver}/"), Timeout = TimeSpan.FromSeconds(5) }; }
        using (var http = Http(true)) { using var message = new HttpRequestMessage(HttpMethod.Post, "v1/events") { Content = new ByteArrayContent(data) }; message.Headers.Add("Idempotency-Key", "event-test"); message.Headers.Add("X-CulinaryOs-SHA256", digest); var response = await http.SendAsync(message); Check(response.IsSuccessStatusCode, "real HTTPS mTLS receiver accepts provisioned branch certificate"); }
        using (var http = Http(false)) { try { var response = await http.GetAsync("v1/cloud/pending"); Check(!response.IsSuccessStatusCode, "receiver denies client without mTLS certificate"); } catch (HttpRequestException) { Check(true, "receiver denies client without mTLS certificate"); } }
        using (var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, CustomTrustStore = { serverCert }, RevocationMode = X509RevocationMode.NoCheck } } }) { BaseAddress = new Uri($"https://localhost:{admin}/") }) {

            foreach (var gone in new[] { "v1/entitlement", "v1/releases/latest?platform=win-x64", "api/backups/" + J.Id(), "api/operations/" + J.Id() }) { using var response = await http.GetAsync(gone); Check(response.StatusCode == HttpStatusCode.NotFound, "central no longer serves provider route " + gone.Split('?')[0]); }

        }
        var credentials = Path.Combine(folder, "central-credentials");
        Directory.CreateDirectory(credentials);
        File.WriteAllText(Path.Combine(credentials, "central-ca"), serverCert.ExportCertificatePem());
        File.WriteAllText(Path.Combine(credentials, "central-cert"), clientCert.ExportCertificatePem());
        File.WriteAllText(Path.Combine(credentials, "central-key"), clientKey.ExportPkcs8PrivateKeyPem());
        using var runtime = new RuntimeStore(python, Path.Combine(root, "runtime", "lab_host.py"), Path.Combine(folder, "branch-store"), "alpha-test");
        var branch = new BranchService(runtime, "alpha-test", new Passwords());
        var branchPassword = "Central-" + J.Id();
        var branchCode = J.Id();
        await TestLicense.Initialize(branch, J.O("siteId", "alpha-test", "name", "Alpha local", "managerName", "Manager", "managerUsername", "local", "managerPassword", branchPassword, "enrollmentCode", branchCode, "modules", Array.Empty<string>()));
        var branchLogin = await branch.Login(J.O("username", "local", "password", branchPassword, "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "Central test terminal", "enrollmentCode", branchCode));
        await CloudScenario.Run(root, python, folder, store, central, branch, branchLogin.S("token"), credentials, receiver, clientCert, pub, evidence);
        await app.StopAsync();
        var snapshot = Path.Combine(folder, "central-backup.sqlite");
        var backupHash = store.Snapshot(snapshot);
        store.ProvisionTenant(J.O("tenantId", "restaurant-beta", "branchId", "main", "slug", "restaurant-beta-main", "tenantName", "Beta", "branchName", "Main", "siteId", "beta-test", "certificateSha256", new string('b', 64), "ownerName", "Owner", "ownerUsername", "beta-owner", "ownerPassword", "Test-" + J.Id(), "ownerEnrollmentCode", J.Id()), new Passwords());
        store.Restore(snapshot, backupHash);
        Check(store.CertificateSite(new string('b', 64)) is null && store.CertificateSite(clientCert.GetCertHashString(HashAlgorithmName.SHA256)) == "alpha-test", "central restore validates encrypted snapshot and returns to its content");
        using (var foreign = new CentralStore(Path.Combine(folder, "foreign.sqlite"), RandomNumberGenerator.GetBytes(32))) { foreign.Initialize(); await Deny(() => Task.Run(() => foreign.Restore(snapshot, backupHash)), "central refuses a snapshot sealed under another central's key"); }
        var encrypted = File.ReadAllBytes(database);
        Check(!Encoding.UTF8.GetString(encrypted).Contains("restaurant-owner\""), "tenant owner record encrypted under the central key");

    }
}
