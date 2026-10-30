using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Client;
using MesaNexo.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
namespace MesaNexo.Tests;

// The branch routes of CulinaryServer, as docs/API.md of that repository defines them: single-use challenges,
// requests signed over the same seven lines, root-signed licenses, notices bound to the request's nonce.
sealed class FakeCulinaryServer: IAsyncDisposable {
    public readonly WebApplication App;
    public readonly string Url;
    public string Code = "K7Q2M-9XWZT-4HPLA-R3VNE", Plan = "monthly", Status = "active";
    public string[] Features = ["reservations"];
    public long MaxTables = 1, Revision = 1;
    public string? Spki, NoticeChallenge;
    public JsonObject? Fingerprint;
    public string? CentralCertificate;
    public DateTimeOffset Expires = DateTimeOffset.UtcNow.AddDays(30);
    public readonly List<JsonObject> Tickets = [];
    public JsonObject? Update;
    // Update offers as the Worker sends them: authorization from the license intermediate, manifest from the release one.
    public TestLicense.Key UpdateSigner = TestLicense.Intermediate(TestLicense.LicenseUses), ReleaseSigner = TestLicense.Intermediate(TestLicense.ReleaseUses);
    public JsonObject? ReleaseOverride;
    public byte[] Package = [];
    readonly HashSet<string> issued = [], used = [];
    public int Renewals;
    // Activation spends the code but answers a license the branch cannot install, as when its install fails.
    public bool Unusable;
    // The siteId the license was issued for; activation from a branch with another one is refused.
    public string Site;
    public FakeCulinaryServer(string site) {

        Site = site;
        var port = Free();
        Url = "http://127.0.0.1:" + port;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(Url);
        builder.Logging.ClearProviders();
        App = builder.Build();
        static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        IResult Error(int status, string code, string message, string? notice = null) => Results.Json(J.O("error", J.O("code", code, "message", message, "notice", notice)), statusCode: status);
        string License(DateTimeOffset now) => TestLicense.Sign(Jws.License, J.O("v", 1, "licenseId", TestLicense.LicenseId(site), "revision", Revision, "tenantId", "grupo", "siteId", site, "customer", "Restaurante La Plaza", "branchName", "Centro", "plan", Plan, "status", "active", "features", Features, "limits", J.O("maxUsers", 15, "maxTables", MaxTables), "activatedAt", Iso(now), "expiresAt", Plan == "permanent" ? null : Iso(Expires), "validatedAt", Iso(now), "offlineUntil", Iso(now.AddDays(7)), "key", J.O("alg", "ES256", "spki", Spki), "binding", "fingerprint", "iat", Iso(now)));
        // Verifies exactly what the Worker verifies; returns the parsed body and the nonce, or null.
        async Task<(JsonObject Body, string Nonce)?> Signed(HttpContext c, string? spki = null) {

            using var buffer = new MemoryStream();
            await c.Request.Body.CopyToAsync(buffer);
            var bytes = buffer.ToArray();
            var subject = c.Request.Headers["X-Culinary-Subject"].ToString();
            var nonce = c.Request.Headers["X-Culinary-Challenge"].ToString();
            var signature = Base64Url.DecodeFromChars(c.Request.Headers["X-Culinary-Signature"].ToString());
            if (subject != TestLicense.LicenseId(site) || !issued.Contains(nonce) || !used.Add(nonce)) return null;
            var body = bytes.Length == 0 ? new JsonObject() : JsonNode.Parse(bytes)!.AsObject();
            var key = spki ?? Spki ?? body.Obj("key").S("spki");
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Base64Url.DecodeFromChars(key), out _);
            var input = string.Join("\n", "culinary-request-v1", "branch", c.Request.Method, c.Request.Path + c.Request.QueryString, subject, nonce, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            return ec.VerifyData(Encoding.UTF8.GetBytes(input), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation) ? (body, nonce) : null;

        }
        IResult? Refused(string nonce) {

            if (Status == "active" && Expires > DateTimeOffset.UtcNow) return null;
            var state = Status is "revoked" or "transferred" ? Status : "expired";
var notice = TestLicense.Sign(Jws.Status, J.O("v", 1, "licenseId", TestLicense.LicenseId(site), "revision", Revision, "siteId", site, "status", state, "plan", Plan, "expiresAt", Iso(Expires), "revokedAt", null, "challenge", NoticeChallenge ?? nonce, "iat", Iso(DateTimeOffset.UtcNow)));
            return Error(403, "license_" + state, state == "revoked" ? "Licencia revocada" : "Licencia no vigente", notice);

        }
        App.MapPost("/branch/v1/challenges", () => { var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(56)); issued.Add(nonce); return Results.Json(J.O("nonce", nonce, "expiresAt", Iso(DateTimeOffset.UtcNow.AddMinutes(2)))); });
        App.MapPost("/branch/v1/activate", async (HttpContext c) => {

            if (await Signed(c) is not { } r) return Error(401, "unauthorized", "Firma inválida");
            if (r.Body.S("activationCode") != Code || Spki is not null) return Error(403, "activation_rejected", "Código inválido");
            if (r.Body.S("siteId") != Site) return Error(409, "site_mismatch", "La licencia es de la sucursal " + Site + ", no de " + r.Body.S("siteId"));
            Spki = r.Body.Obj("key").S("spki");
            Fingerprint = r.Body.Obj("binding").Obj("fingerprint");
            return Results.Json(J.O("license", Unusable ? "x.y.z" : License(DateTimeOffset.UtcNow)));

        });
        App.MapPost("/branch/v1/license/renew", async (HttpContext c) => {

            if (await Signed(c) is not { } r) return Error(401, "unauthorized", "Firma inválida");
            if (Refused(r.Nonce) is { } refused) return refused;
            var given = r.Body.Obj("fingerprint");
            if (new[] { "board", "disk", "mac" }.Count(k => given.S(k) == Fingerprint!.S(k)) < 2) return Error(403, "binding_mismatch", "Otro equipo");
            Fingerprint = given;
            Renewals++;
return Results.Json(J.O("license", License(DateTimeOffset.UtcNow)));

        });
        App.MapPut("/branch/v1/registry", async (HttpContext c) => { if (await Signed(c) is not { } r) return Error(401, "unauthorized", "Firma inválida"); CentralCertificate = r.Body.S("centralCertificateSha256"); return Results.Json(J.O("siteId", site, "tenantId", "grupo", "centralCertificateSha256", CentralCertificate)); });
        App.MapPost("/branch/v1/tickets", async (HttpContext c) => { if (await Signed(c) is not { } r) return Error(401, "unauthorized", "Firma inválida"); var t = J.O("id", Guid.NewGuid().ToString("D"), "subject", r.Body.S("subject"), "status", "open", "messages", new[] { J.O("id", Guid.NewGuid().ToString("D"), "author", "branch", "body", r.Body.S("message"), "createdAt", Iso(DateTimeOffset.UtcNow)) }); Tickets.Add(t); return Results.Json(J.O("ticket", t), statusCode: 201); });
        App.MapPost("/branch/v1/tickets/{id}/messages", async (HttpContext c, string id) => { if (await Signed(c) is not { } r) return Error(401, "unauthorized", "Firma inválida"); var t = Tickets.Single(x => x.S("id") == id); t.A("messages").Add(J.O("id", Guid.NewGuid().ToString("D"), "author", "branch", "body", r.Body.S("message"), "createdAt", Iso(DateTimeOffset.UtcNow))); t["status"] = "open"; return Results.Json(J.O("ticket", t.DeepClone()), statusCode: 201); });
        App.MapGet("/branch/v1/tickets", async (HttpContext c) => await Signed(c) is null ? Error(401, "unauthorized", "Firma inválida") : Results.Json(J.O("tickets", new JsonArray(Tickets.Select(x => x.DeepClone()).ToArray()))));
        App.MapPost("/branch/v1/updates/check", async (HttpContext c) => {

            if (await Signed(c) is not { } r) return Error(401, "unauthorized", "Firma inválida");
            if (Update is null) return Results.Json(J.O("update", null));
            var manifest = ReleaseOverride ?? J.O("v", 1, "releaseId", Update.S("releaseId"), "version", Update.S("version"), "platform", Update.S("platform"), "filename", Update.S("filename"), "bytes", Update.L("bytes"), "sha512", Update.S("sha512"), "iat", J.Now());
            return Results.Json(J.O("update", TestLicense.SignWith(UpdateSigner, Jws.Update, Update), "release", TestLicense.SignWith(ReleaseSigner, Jws.Release, manifest), "download", J.O("path", "/branch/v1/downloads/" + Update.S("releaseId"), "token", "download-token", "expiresAt", Iso(DateTimeOffset.UtcNow.AddHours(1)))));

        });
        App.MapGet("/branch/v1/downloads/{id}", (HttpContext c) => c.Request.Headers.Authorization.ToString() == "Bearer download-token" ? Results.Bytes(Package, "application/octet-stream") : Error(401, "unauthorized", "Permiso inválido"));

    }
    static int Free() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); }
}
static class LicenseTests {
    public static async Task Run(string root, string python, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        async Task Deny(Func<Task> act, string name, int? status = null) { try { await act(); throw new Exception("Rejection expected: " + name); } catch (BusinessException e) { Check(status is null || e.Status == status, name); } }
        const string site = "license-site";
        var folder = Path.Combine(root, ".lab", "license-" + J.Id());
        Directory.CreateDirectory(folder);
        if (Directory.Exists(TestLicense.Folder(site))) Directory.Delete(TestLicense.Folder(site), true);
        await using var fake = new FakeCulinaryServer(site);
        await fake.App.StartAsync();
        using var runtime = LabPostgres.Test(root, folder, site);
        var service = new BranchService(runtime, site, new Passwords());
        var password = "License-" + J.Id();
        var enrollment = J.Id();
        await service.Initialize(J.O("siteId", site, "name", "Licencias", "managerName", "Gerencia", "managerUsername", "gerencia", "managerPassword", password, "enrollmentCode", enrollment));
        var hardware = TestLicense.HardwareFile(site);
        var central = Path.Combine(folder, "central-cert");
        using (var key = RSA.Create(2048)) { var request = new CertificateRequest("CN=license-site", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1)); File.WriteAllText(central, cert.ExportCertificatePem()); }
        service.UseLicensing(new LicenseOptions(TestLicense.Folder(site), fake.Url, hardware, UseTpm: false, CentralCertificate: central));
        var deviceId = J.Id();
        var deviceSecret = J.Id();
        Task<JsonObject> SignIn(string code = "") => service.Login(J.O("username", "gerencia", "password", password, "deviceId", deviceId, "deviceSecret", deviceSecret, "deviceName", "Terminal de licencias", "enrollmentCode", code));
        Check((await service.LicenseStatus()).S("state") == "missing", "a new branch has no license until it is activated");
        await Deny(() => SignIn(enrollment), "sign-in shows the lock screen before any license is activated", 423);
        await Deny(() => service.Activate(J.O("licenseId", TestLicense.LicenseId(site), "activationCode", "WRONG-CODE-WRONG-CODE-1")), "wrong activation code is refused by CulinaryServer", 403);
        fake.Site = "otra-sucursal";
        await Deny(() => service.Activate(J.O("licenseId", TestLicense.LicenseId(site), "activationCode", fake.Code)), "a license for another siteId is refused before the code is spent", 403);
        Check(fake.Spki is null, "the refused activation leaves the license unbound");
        fake.Site = site;
        fake.Unusable = true;
        await Deny(() => service.Activate(J.O("licenseId", TestLicense.LicenseId(site), "activationCode", fake.Code)), "a license the branch cannot install is not kept", 403);
        Check(fake.Spki == service.LicenseKey().S("spki") && fake.Fingerprint!.S("board").Length == 64, "activation binds the branch key and hardware fingerprint to the license");
        fake.Unusable = false;
        var activated = await service.Activate(J.O("licenseId", TestLicense.LicenseId(site), "activationCode", fake.Code));
        Check(activated.S("state") == "active" && activated.S("plan") == "monthly" && fake.Renewals == 1, "retrying a code already spent by this machine recovers its license through a signed renewal");
        fake.Renewals = 0;
        await Deny(() => service.Activate(J.O("licenseId", TestLicense.LicenseId(site), "activationCode", fake.Code)), "a branch with a valid license refuses a second activation", 409);
        var token = (await SignIn(enrollment)).S("token");
        async Task<JsonObject> Act(string type, JsonObject p) => (await service.Action(token, J.O("operationId", J.Id(), "type", type, "payload", p))).Obj("result");
        var state = await service.State(token);
        Check(state.Obj("restaurant").A("modules").Strings().SequenceEqual(["reservations"]) && state.Obj("restaurant").Obj("license").S("plan") == "monthly", "state reports features and license from the signed license");
        var table = await Act("table.save", J.O("name", "Mesa 1", "capacity", 4, "zone", "Salón"));
        await Deny(() => Act("table.save", J.O("name", "Mesa 2", "capacity", 4, "zone", "Salón")), "table limit of the license is enforced by the branch server");
        await Act("reservation.save", J.O("name", "Reserva", "contact", "local", "start", DateTimeOffset.UtcNow.AddDays(1).ToString("O"), "people", 2, "tableId", table.S("id"), "status", "confirmed", "accepted", true));
        Check(true, "a licensed feature works");
        await Deny(() => Act("promotion.save", J.O("name", "2x1", "productIds", Array.Empty<string>(), "percent", 10, "startHour", 0, "endHour", 24, "active", true)), "an unlicensed feature is blocked by the branch server", 403);
        // Store edits: features in meta are ignored; a tampered license locks the branch.
        // An attacker with the store's keys writing records directly; the store requires the meta record in every transaction.
        async Task Raw(string record, Func<JsonObject, JsonObject> change) {

            JsonObject Change(string id, JsonNode row, Func<JsonObject, JsonObject> edit) => J.O("record_id", id, "expected_version", row.L("version"), "data", edit((JsonObject)row.Obj("data").DeepClone()));
            var meta = (await runtime.CallAsync(J.O("op", "get", "record_id", "mn_meta_site")))!;
            var changes = record == "mn_meta_site" ? new[] { Change(record, meta, change) } : new[] { Change("mn_meta_site", meta, m => m), Change(record, (await runtime.CallAsync(J.O("op", "get", "record_id", record)))!, change) };
            var e = J.Id();
            await runtime.CallAsync(J.O("op", "transaction_v1", "protocol", 1, "event_id", e, "receipt", J.O("actor", "attacker", "fingerprint", e, "operation", "edit", "result", new JsonObject(), "at", J.Now()), "changes", changes));
            // Reads see another process's writes up to a second late (BranchService.View).
            await Task.Delay(1100);

        }
        await Raw("mn_meta_site", m => { m["modules"] = J.N(Restaurant.Modules); m["maxTables"] = 999; return m; });
        await Deny(() => Act("promotion.save", J.O("name", "2x1", "productIds", Array.Empty<string>(), "percent", 10, "startHour", 0, "endHour", 24, "active", true)), "features written into the store's meta grant nothing", 403);
        var original = (await runtime.CallAsync(J.O("op", "get", "record_id", "mn_license_current")))!.Obj("data").S("license");
        var parts = original.Split('.');
        var forged = J.N(JsonNode.Parse(Base64Url.DecodeFromChars(parts[1])))!.AsObject();
        forged["features"] = J.N(Restaurant.Modules);
        forged["expiresAt"] = "2099-01-01T00:00:00.000Z";
        var forgedJws = parts[0] + "." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(forged.ToJsonString())) + "." + parts[2];
        await Deny(() => service.InstallLicense(forgedJws), "a license edited after signing is rejected", 403);
        await Raw("mn_license_current", r => { r["license"] = forgedJws; return r; });
        Check((await service.LicenseStatus()).S("state") == "mismatch", "a forged license written into the store locks the branch");
        await Deny(() => service.State(token), "a branch with a forged license serves no state", 423);
        await Raw("mn_license_current", r => { r["license"] = original; return r; });
        Check((await service.LicenseStatus()).S("state") == "active", "restoring the signed license unlocks the branch");
        // Copies: another site, or the same site on another machine (another key).
        using (var otherRuntime = LabPostgres.Test(root, Path.Combine(folder, "other"), "other-site")) {

            var other = new BranchService(otherRuntime, "other-site", new Passwords());
            other.UseLicensing(new LicenseOptions(Path.Combine(folder, "other-key"), HardwareFile: hardware, UseTpm: false));
            await Deny(() => other.InstallLicense(original), "a license copied to another branch is rejected", 403);

        }
        var clone = new BranchService(runtime, site, new Passwords());
        clone.UseLicensing(new LicenseOptions(Path.Combine(folder, "clone-key"), HardwareFile: hardware, UseTpm: false));
        Check(clone.License(await clone.Load()).State == "mismatch", "the same store on a machine without the branch key is locked");
        await Deny(() => clone.InstallLicense(original), "a license cannot be installed on a machine without its key", 403);
        // Renewal, registry and support with signed requests.
        var before = (await service.LicenseStatus()).S("validatedAt");
        await Task.Delay(20);
        await service.Renew();
        Check(fake.Renewals == 1 && string.CompareOrdinal((await service.LicenseStatus()).S("validatedAt"), before) > 0, "renewal with a signed challenge refreshes validatedAt");
        Check(await service.RegisterCentral() && fake.CentralCertificate!.Length == 64, "branch registers its central mTLS certificate fingerprint");
        var ticket = await Act("ticket.create", J.O("subject", "No imprime", "message", "La impresora de cocina no responde"));
        await service.SyncSupport();
        Check(fake.Tickets.Single().S("subject") == "No imprime", "a local support ticket reaches CulinaryServer");
        fake.Tickets[0].A("messages").Add(J.O("id", Guid.NewGuid().ToString("D"), "author", "admin", "adminId", "a", "body", "Reinicia el servicio de impresión", "createdAt", J.Now()));
        fake.Tickets[0]["status"] = "answered";
        await service.SyncSupport();
        await service.SyncSupport();
        var synced = (await service.Load()).Get("ticket", ticket.S("id"));
        Check(synced.S("status") == "answered" && synced.A("messages").Objects().Count(m => m.S("actor") == "provider") == 1 && fake.Tickets[0].A("messages").Count == 2, "the provider's reply arrives once and nothing is sent twice");
        // Updates: the branch asks with its license; the terminal verifies the signed update itself.
        await Act("session.logout", new());
        var device = J.O("siteId", site, "deviceId", deviceId, "deviceSecret", deviceSecret);
        Check((await service.CheckUpdate(J.O("siteId", site, "deviceId", deviceId, "deviceSecret", deviceSecret, "platform", "win-x64", "currentVersion", "0.2.0"))).ContainsKey("update"), "no publication for this license answers without an update");
        fake.Package = Encoding.UTF8.GetBytes("MZ TEST FIXTURE ONLY - never a distributable installer");
        fake.Update = J.O("v", 1, "licenseId", TestLicense.LicenseId(site), "siteId", site, "releaseId", Guid.NewGuid().ToString("D"), "version", "9.0.0", "platform", "win-x64", "filename", "culinary-9.0.0.exe", "bytes", fake.Package.Length, "sha512", Convert.ToHexStringLower(SHA512.HashData(fake.Package)), "notes", "Prueba", "iat", J.Now(), "exp", DateTimeOffset.UtcNow.AddHours(1).ToString("O"));
        var check = await service.CheckUpdate(J.O("siteId", site, "deviceId", deviceId, "deviceSecret", deviceSecret, "platform", "win-x64", "currentVersion", "0.2.0"));
        Check(check.S("version") == "9.0.0", "the branch obtains the update published to its license");
        using (var download = await service.DownloadUpdate(J.O("siteId", site, "deviceId", deviceId, "deviceSecret", deviceSecret, "releaseId", fake.Update.S("releaseId")))) {

            var package = Path.Combine(folder, "updates", "culinary-9.0.0.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(package)!);
            await File.WriteAllBytesAsync(package, await download.Content.ReadAsByteArrayAsync());
            var update = Updates.Signed(check, site, "win-x64");
            await Updates.Verify(update, package, "win-x64");
            Check(true, "the terminal verifies the signed update and installer bytes");
            await Deny(() => Task.Run(() => Updates.Signed(check, "other-site", "win-x64")), "an update for another branch is rejected by the terminal", 403);
            await File.AppendAllTextAsync(package, "x");
            await Deny(() => Updates.Verify(update, package, "win-x64"), "a changed installer is rejected by the terminal");

        }
        // The online license intermediate cannot vouch for bytes: the manifest must come from the release intermediate.
        Task<JsonObject> CheckAgain() => service.CheckUpdate(J.O("siteId", site, "deviceId", deviceId, "deviceSecret", deviceSecret, "platform", "win-x64", "currentVersion", "0.2.0"));
        fake.ReleaseOverride = (JsonObject)fake.Update.DeepClone();
        fake.ReleaseOverride["sha512"] = new string('0', 128);
        await Deny(CheckAgain, "a release manifest for other bytes is rejected even with a valid authorization", 403);
        fake.ReleaseOverride = null;
        var releaseSigner = fake.ReleaseSigner;
        fake.ReleaseSigner = fake.UpdateSigner;
        await Deny(CheckAgain, "the license intermediate cannot sign a release manifest", 403);
        fake.ReleaseSigner = releaseSigner;
        Check((await CheckAgain()).S("version") == "9.0.0", "manifest and authorization from their own intermediates are accepted");
        fake.Update = null;
        // A lower revision never replaces a higher one.
        fake.Revision = 3;
        await service.Renew();
        var older = TestLicense.Sign(Jws.License, TestLicense.Payload(service, Restaurant.Modules, maxTables: 999, revision: 2));
        await Deny(() => service.InstallLicense(older), "an older license revision cannot replace the installed one", 409);
        // Service down: four days without an answer keep the branch selling, with a countdown.
        var options = new LicenseOptions(TestLicense.Folder(site), fake.Url, hardware, UseTpm: false, CentralCertificate: central);
        await service.InstallLicense(TestLicense.Sign(Jws.License, TestLicense.Payload(service, ["reservations"], maxTables: 1, revision: 4, validated: DateTimeOffset.UtcNow.AddDays(-4), expires: DateTimeOffset.UtcNow.AddDays(20))), true);
        service.UseLicensing(options, new CulinaryServer("http://127.0.0.1:9"));
        try { await service.Renew(); throw new Exception("Renewal without a server accepted"); } catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
        var silent = await service.LicenseStatus();
        Check(silent.S("state") == "active" && silent.Obj("warning").S("kind") == "service" && silent.Obj("warning").L("days") == 3, "four days with the license service down keep the branch selling, with a countdown");
        Check((await SignIn()).S("token") != "", "sign-in keeps working while the license service is down");
        service.UseLicensing(options);
        // Offline: past offlineUntil without renewing, the branch locks.
        await service.InstallLicense(TestLicense.Sign(Jws.License, TestLicense.Payload(service, ["reservations"], maxTables: 1, revision: 5, validated: DateTimeOffset.UtcNow.AddDays(-8), expires: DateTimeOffset.UtcNow.AddDays(20))), true);
        Check((await service.LicenseStatus()).S("state") == "offline", "seven days without validation lock the branch");
        await Deny(() => SignIn(), "an offline-expired license shows the lock screen at sign-in", 423);
        fake.Revision = 5;
        await service.Renew();
        Check((await service.LicenseStatus()).S("state") == "active", "renewing again unlocks the branch");
        // The term's countdown reaches the manager first; the team sees it only in the last three days.
        await service.Action((await SignIn()).S("token"), J.O("operationId", J.Id(), "type", "user.save", "payload", J.O("name", "Servicio", "username", "servicio", "role", "mesero", "password", password)));
        async Task<(JsonObject Manager, JsonObject Waiter)> Warnings(int days) {

            await service.InstallLicense(TestLicense.Sign(Jws.License, TestLicense.Payload(service, ["reservations"], maxTables: 1, revision: 5, expires: DateTimeOffset.UtcNow.AddDays(days))), true);
            var manager = await service.State((await SignIn()).S("token"));
            var waiter = await service.State((await service.Login(J.O("username", "servicio", "password", password, "deviceId", deviceId, "deviceSecret", deviceSecret, "deviceName", "Terminal de licencias", "enrollmentCode", ""))).S("token"));
            return (manager.Obj("restaurant").Obj("license").Obj("warning"), waiter.Obj("restaurant").Obj("license").Obj("warning"));

        }
        var (lead, staff) = await Warnings(10);
        Check(lead.S("level") == "notice" && staff.Count == 0, "ten days before the term ends only the manager is warned");
        (lead, staff) = await Warnings(2);
        Check(lead.S("level") == "critical" && staff.S("level") == "critical", "in the last three days the whole team is warned");
        await service.Renew();
        var open = (await SignIn()).S("token");
        // The certificate in use: 30 days left means the daily renewal failed.
        JsonObject CertificateIn(JsonObject state) => state.Obj("connection").Obj("certificate");
        service.UseCertificate(DateTimeOffset.UtcNow.AddDays(40));
        Check(CertificateIn(await service.State(open)).Count == 0, "a certificate that can still renew raises no warning");
        service.UseCertificate(DateTimeOffset.UtcNow.AddDays(14));
        var certificate = CertificateIn(await service.State(open));
        Check(certificate.L("days") == 14 && certificate.S("level") == "notice", "a certificate that did not renew warns the manager");
        service.UseCertificate(DateTimeOffset.UtcNow.AddDays(7));
        Check(CertificateIn(await service.State(open)).S("level") == "critical", "seven days from expiry the warning is critical");
        service.UseCertificate(DateTimeOffset.UtcNow.AddDays(365));
        // Clock: CulinaryServer's time ahead of the branch's clock by more than the tolerance.
        await service.InstallLicense(TestLicense.Sign(Jws.License, TestLicense.Payload(service, ["reservations"], maxTables: 1, revision: 5, validated: DateTimeOffset.UtcNow.AddHours(2))), true);
        Check((await service.LicenseStatus()).S("state") == "clock", "a branch clock set behind signed time locks the branch");
        fake.Revision = 6;
        await service.Renew();
        var unlocked = (await service.Load()).Committed;
        string KeyFiles() => string.Join(",", Directory.GetFiles(TestLicense.Folder(site)).Select(f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f)))));
        var keyFiles = KeyFiles();
        // Notices: a notice for another request is ignored; one for this request locks at once.
        fake.Expires = DateTimeOffset.UtcNow.AddMinutes(-1);
        fake.NoticeChallenge = "another-request";
        try { await service.Renew(); } catch (ServerRejection) { }
        Check((await service.LicenseStatus()).S("state") == "active", "a replayed notice from another request does not lock the branch");
        fake.NoticeChallenge = null;
        try { await service.Renew(); throw new Exception("Expired renewal accepted"); } catch (ServerRejection e) { Check(e.Code == "license_expired", "CulinaryServer refuses to renew an expired license"); }
        var locked = await service.LicenseStatus();
        Check(locked.S("state") == "expired" && locked.S("plan") == "monthly" && locked.S("expiresAt") != "", "an expired notice for this request locks the branch at once, with plan and expiry for the lock screen");
        Check(!locked.ToJsonString().Contains("Mesa 1") && !locked.ContainsKey("features"), "the lock screen summary carries no business data");
        await Deny(() => SignIn(), "an expired license shows only the lock screen at sign-in", 423);
        await Deny(() => service.Report(open, "", "", null), "an open session exports no report once the license expired", 423);
        await Deny(() => service.State(open), "an open session loses the state once the license expired", 423);
        Check(!(await service.CanInstall(device)).B("allowed"), "an expired license blocks client updates");
        var during = (await service.Load()).Committed;
        Check(unlocked.Records.Where(x => x.Key is not ("mn_license_current" or "mn_meta_site")).All(x => during.TryGet(x.Key, out var kept) && kept.Version == x.Value.Version && kept.Data.ToJsonString() == x.Value.Data.ToJsonString()) && KeyFiles() == keyFiles, "a lock leaves every record and key file as it was");
        // The provider renews: the next renewal unlocks without losing data.
        fake.Expires = DateTimeOffset.UtcNow.AddDays(30);
        fake.Revision = 7;
        await service.Renew();
        var back = (await SignIn()).S("token");
        Check((await service.State(back)).A("tables").Count == 1, "after renewal the branch unlocks with its data intact");
        // Plans of the 2026 proposal: Lite with 2 fixed terminals and no add-ons, then Pro with the add-ons.
        await service.InstallLicense(TestLicense.Sign(Jws.License, TestLicense.Payload(service, [], maxTables: 1, revision: 8, tier: "lite", addons: [], maxTerminals: 2)), true);
        var lite = (await service.State(back)).Obj("restaurant");
        Check(lite.S("tier") == "lite" && lite.L("maxTerminals") == 2 && lite.A("modules").Count == 0, "a Lite license without add-ons exposes no extra modules and two terminals");
        await Deny(() => service.Action(back, J.O("operationId", J.Id(), "type", "reservation.save", "payload", J.O("name", "Reserva", "contact", "local", "start", DateTimeOffset.UtcNow.AddDays(2).ToString("O"), "people", 2, "tableId", table.S("id"), "status", "confirmed", "accepted", true))), "reservations need Pro", 403);
        await Deny(() => service.Action(back, J.O("operationId", J.Id(), "type", "integration.configure", "payload", J.O("provider", "aggregators", "mode", "simulated"))), "the delivery platforms need the Delivery Hub add-on", 403);
        Task<JsonObject> Enroll(string kind) { var code = J.Id() + J.Id(); return service.RotateEnrollment(code).ContinueWith(_ => service.Login(J.O("username", "gerencia", "password", password, "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "Equipo " + kind, "deviceKind", kind, "enrollmentCode", code))).Unwrap(); }
        await Enroll("terminal");
        await Deny(() => Enroll("terminal"), "a third fixed terminal exceeds the Lite allowance", 403);
        await Deny(() => Enroll("mobile"), "a phone needs the mobile order takers add-on", 403);
        await service.InstallLicense(TestLicense.Sign(Jws.License, TestLicense.Payload(service, [], maxTables: 1, revision: 9, tier: "pro", addons: ["mobileOrdering", "deliveryHub"])), true);
        var pro = (await service.State(back)).Obj("restaurant");
        Check(pro.A("modules").Strings().SequenceEqual(["deliveryHub", "mobileOrdering", "reservations"]), "Pro includes reservations and adds the contracted add-ons");
        await Enroll("terminal");
        for (var i = 0; i < 3; i++) await Enroll("mobile");
        Check(true, "with the add-on, mobile order takers are unlimited and do not use terminal slots");
        // Intermediates: the root stays offline and certifies keys for specific document types and generations.
        JsonObject Next(long revision) => TestLicense.Payload(service, [], maxTables: 1, revision: revision, tier: "pro", addons: ["mobileOrdering", "deliveryHub"]);
        var first = TestLicense.Intermediate(TestLicense.LicenseUses, 1);
        await service.InstallLicense(TestLicense.SignWith(first, Jws.License, Next(10)), true);
        Check((await service.LicenseStatus()).S("state") == "active", "a license signed by the license intermediate installs");
        await Deny(() => service.InstallLicense(TestLicense.SignWith(TestLicense.Intermediate(TestLicense.ReleaseUses), Jws.License, Next(11))), "the release intermediate cannot sign a license", 403);
        await Deny(() => service.InstallLicense(TestLicense.SignWith(TestLicense.Intermediate(TestLicense.LicenseUses, expires: DateTimeOffset.UtcNow.AddDays(-1)), Jws.License, Next(11))), "a license signed after its intermediate expired is rejected", 403);
        await service.InstallLicense(TestLicense.SignWith(first, Jws.License, Next(11)), false, default, TestLicense.SignWith(first, Jws.Floor, J.O("v", 1, "license", 9, "release", 9, "iat", J.Now())));
        Check((await service.LicenseStatus()).S("state") == "active", "a key floor not signed by the root is ignored");
        var floor = TestLicense.Floor(2, 0);
        await service.InstallLicense(TestLicense.SignWith(TestLicense.Intermediate(TestLicense.LicenseUses, 2), Jws.License, Next(12)), false, default, floor);
        Check((await service.Load()).Get("license", "current").S("keyFloor") == floor, "a root-signed key floor is kept with the license");
        await Deny(() => service.InstallLicense(TestLicense.SignWith(first, Jws.License, Next(13))), "below the key floor a revoked intermediate signs nothing", 403);
        var restarted = new BranchService(runtime, site, new Passwords());
        restarted.UseLicensing(new LicenseOptions(TestLicense.Folder(site), HardwareFile: hardware, UseTpm: false));
        Check(restarted.License(await restarted.Load()).State == "active", "after a restart the kept floor still accepts the current generation");
        await Deny(() => restarted.InstallLicense(TestLicense.SignWith(first, Jws.License, Next(13))), "after a restart the kept floor still rejects the revoked generation", 403);
        // Repairs: one part at a time follows the machine; two at once need the provider, and no repair touches the data.
        fake.Revision = 13;
        fake.Features = ["reservations"];
        void Parts(string board, string disk, string mac) => File.WriteAllText(hardware, J.O("board", board, "disk", disk, "mac", mac).ToJsonString());
        Parts("4c4c4544-0042-3510-8051-b4c04f563232", "S4EVNX0N" + site.Length, "52:54:00:aa:bb:cc");
        Check((await service.Renew()).State == "active", "a new network card keeps the license");
        Parts("4c4c4544-0042-3510-8051-b4c04f563232", "DISCO-NUEVO", "52:54:00:aa:bb:cc");
        Check((await service.Renew()).State == "active", "a new disk later keeps the license");
        Parts("otra-placa", "OTRO-DISCO", "52:54:00:aa:bb:cc");
        try { await service.Renew(); throw new Exception("Other hardware accepted"); } catch (ServerRejection e) { Check(e.Code == "binding_mismatch", "two parts changed at once are refused"); }
        var moved = await service.LicenseStatus();
        Check(moved.S("state") == "active" && moved.Obj("warning").S("kind") == "hardware" && moved.Obj("warning").S("message").Contains("transferencia"), "refused hardware keeps selling and asks for a transfer, not for internet");
        Parts("otra-placa", "OTRO-DISCO", "52:54:00:dd:ee:ff");
        // The provider releases this machine: the old key is told so on its next contact.
        fake.Status = "transferred";
        try { await service.Renew(); throw new Exception("Released machine renewed"); } catch (ServerRejection e) { Check(e.Code == "license_transferred", "the released machine is refused"); }
        Check((await service.LicenseStatus()).S("state") == "transferred", "the released machine locks on its next contact, without waiting for the grace");
        await Deny(() => SignIn(), "a released machine shows only the lock screen", 423);
        fake.Status = "active";
        // The TPM died: the key is gone. The branch keeps its data and comes back through a new activation.
        var keyPath = Path.Combine(TestLicense.Folder(site), "branch-license.p8");
        File.Delete(keyPath);
        var repaired = new BranchService(runtime, site, new Passwords());
        repaired.UseLicensing(new LicenseOptions(TestLicense.Folder(site), fake.Url, hardware, UseTpm: false, CentralCertificate: central));
        Check(repaired.License(await repaired.Load()).State == "mismatch", "a machine that lost its key is locked");
        fake.Spki = null;
        fake.Code = "CODIGO-DE-TRANSFERENCIA-01";
        fake.Revision = 14;
        Check((await repaired.Activate(J.O("licenseId", TestLicense.LicenseId(site), "activationCode", fake.Code))).S("state") == "active", "the provider's new code activates the repaired machine");
        var after = (await repaired.Load()).Committed;
        Check(unlocked.Records.Where(x => x.Value.Data.S("kind") is "table" or "ticket").All(x => after.TryGet(x.Key, out var kept) && kept.Version == x.Value.Version), "repairs and transfers leave the branch's data as it was");
// The rule itself, at fixed times.
        var at = DateTimeOffset.Parse("2026-11-01T12:00:00Z");
        var spki = service.LicenseKey().S("spki");
        LicenseView At(JsonObject payload, DateTimeOffset now, JsonObject? notice = null, bool behind = false) => LicenseView.Evaluate(payload, notice, site, spki, now, behind);
        JsonObject Term(double validated, double expires, string plan = "annual") => TestLicense.Payload(service, [], plan: plan, validated: at.AddDays(validated), expires: at.AddDays(expires));
        foreach (var days in new[] { 14, 7, 3, 1 }) { var w = At(Term(0, days), at).Warning; Check(w?.S("kind") == "expiry" && w.L("days") == days && w.S("level") == (days <= 3 ? "critical" : "notice"), "the expiry warning at " + days + " days"); }
        Check(At(Term(0, 15), at).Warning is null && At(TestLicense.Payload(service, [], plan: "permanent", validated: at), at).Warning is null, "no warning beyond 14 days or on a permanent license");
        Check(At(Term(0, 3, "trial"), at).Warning!.S("message").StartsWith("El periodo de prueba termina"), "a trial warns about the trial");
        var quiet = At(Term(-2, 300), at);
        Check(quiet.Usable && quiet.Warning!.S("kind") == "service" && quiet.Warning.L("days") == 5, "a day without validation counts down the offline grace");
        var failing = At(Term(-2, 1), at);
        Check(failing.Usable && failing.Warning!.S("kind") == "expiry" && failing.Warning.L("days") == 1, "a failed renewal shows the term when it ends before the grace");
        Check(At(Term(-2, -1), at).State == "expired", "the offline grace never extends an expired term");
        Check(At(Term(-7, 300), at).State == "offline" && At(Term(-6.9, 300), at).Usable, "the branch locks exactly when the signed offlineUntil passes");
        var clock = At(Term(0, 300), at, behind: true);
        Check(clock.State == "clock" && clock.Warning is null, "a clock set behind locks without a countdown");
        Check(At(Term(-1, 300), at, J.O("revision", 1, "status", "revoked")).State == "revoked", "a revocation does not wait for the grace");

    }
}
