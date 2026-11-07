using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
namespace MesaNexo.Tests;
// Integrations follow the license, and the credentials a manager enters stay sealed outside the branch store.
static class IntegrationLicenseTests {
    static JsonArray A(params string[] items) => new(items.Select(x => (JsonNode?)x).ToArray());
    public static async Task Run(string root, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        async Task Deny(Func<Task> action, string contains, string name) { try { await action(); throw new Exception("Expected rejection: " + name); } catch (BusinessException e) { Check(e.Message.Contains(contains), name + " (" + e.Message + ")"); } }
        var site = "intlic-site";
        var folder = Path.Combine(root, ".lab", "intlic-" + J.Id());
        Directory.CreateDirectory(folder);
        var password = "Test-" + J.Id();
        var enrollment = J.Id();
        using var runtime = LabPostgres.Test(root, folder, site);
        var service = new BranchService(runtime, site, new Passwords());
        await TestLicense.Initialize(service, J.O("siteId", site, "name", "Fonda Licencia", "mode", "licencia", "managerName", "Gerencia", "managerUsername", "manager", "managerPassword", password, "enrollmentCode", enrollment, "modules", new JsonArray()));
        async Task License(JsonArray? integrations) {

            var payload = TestLicense.Payload(service, [], revision: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), addons: []);
            if (integrations is not null) payload["integrations"] = integrations;
            await service.InstallLicense(TestLicense.Sign(Jws.License, payload), true);

        }
        var token = (await service.Login(J.O("siteId", site, "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "office", "username", "manager", "password", password, "enrollmentCode", enrollment))).S("token");
        async Task<JsonObject> Act(string type, JsonObject p, string? as_ = null) => (await service.Action(as_ ?? token, J.O("operationId", J.Id(), "type", type, "payload", p))).Obj("result");
        async Task<string[]> Allowed() => (await service.State(token)).Obj("restaurant").A("integrations").Strings();

        // ---------- what the license includes ----------
        await License(null);
        Check((await Allowed()).SequenceEqual(Restaurant.IntegrationKeys.Where(k => k != "aggregators")), "a license signed before the list keeps every integration except the Delivery Hub platforms");
        await License(A("terminal", "maps"));
        Check((await Allowed()).SequenceEqual(["terminal", "maps"]), "/api/state lists only the integrations the license includes");
        await Deny(() => Act("integration.configure", J.O("provider", "pac", "mode", "simulated")), "no incluye", "an integration outside the license cannot be switched on");
        await Act("integration.configure", J.O("provider", "terminal", "mode", "simulated"));
        await Act("integration.configure", J.O("provider", "maps", "mode", "simulated"));
        await License(A("terminal"));
        await Deny(() => Act("geo.lookup", J.O("text", "Av. Reforma 1, Ciudad de México")), "Activa", "an integration the license dropped stops working even if it was on");
        await Act("integration.configure", J.O("provider", "maps", "mode", "off"));
        Check(true, "switching off an integration outside the license stays allowed");

        // ---------- credentials from the app ----------
        var vaultFolder = Path.Combine(folder, "vault");
        await Deny(() => Act("integration.credentials", J.O("provider", "terminal", "credentials", J.O())), "no puede guardar", "without a vault the server says support must place the credentials");
        service.Vault = new IntegrationVault(vaultFolder, runtime.Keys, site);
        var restarted = new TaskCompletionSource();
        service.RestartForIntegrations = () => restarted.TrySetResult();
        var secret = "APP_USR-" + J.Id() + J.Id();
        var credentials = J.O("mercadopago", J.O("accessToken", secret), "terminals", new JsonArray(J.O("id", "caja-1", "name", "Caja 1", "provider", "mercadopago", "deviceId", "PAX_A910__SMARTPOS0000000001")));
        await Deny(() => Act("integration.credentials", J.O("provider", "pac", "credentials", credentials)), "no incluye", "credentials for an integration outside the license are refused");
        await Deny(() => Act("integration.credentials", J.O("provider", "terminal", "credentials", J.O("terminals", "no"))), "Revisa los datos", "credentials the provider's own builder rejects are refused before they are stored");
        var waiter = await Act("user.save", J.O("name", "Ana", "username", "ana", "role", "mesero", "password", password));
        var tabletCode = J.Id();
        await service.RotateEnrollment(tabletCode);
        var waiterToken = (await service.Login(J.O("siteId", site, "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "tablet", "username", "ana", "password", password, "enrollmentCode", tabletCode))).S("token");
        await Deny(() => Act("integration.credentials", J.O("provider", "terminal", "credentials", credentials), waiterToken), "gerente", "only a manager enters credentials");
        Check(!Directory.Exists(vaultFolder) || Directory.GetFiles(vaultFolder).Length == 0, "nothing reaches the vault from a refused attempt");
        var saved = await Act("integration.credentials", J.O("provider", "terminal", "credentials", credentials));
        Check(saved.S("mode") == "live" && saved.Obj("hints").S("mercadopago.accessToken") == "••••" + secret[^4..], "the record keeps the mode and the last four characters, nothing more");
        Check(service.Vault.Load("terminal")!.Obj("mercadopago").S("accessToken") == secret, "the vault opens what was saved under the branch keyring");
        Check(!File.ReadAllText(Path.Combine(vaultFolder, "terminal.sealed")).Contains(secret), "the vault file holds no readable secret");
        var state = (await service.State(token)).ToJsonString();
        var records = string.Join(" ", (await service.Load()).Original.Values.Select(r => r.Data.ToJsonString()));
        Check(!state.Contains(secret) && !records.Contains(secret), "the secret never enters the branch store or a client's state");
        Check(await Task.WhenAny(restarted.Task, Task.Delay(5000)) == restarted.Task, "saving credentials asks the host to restart its live services");
        var log = (await service.Load()).List("integrationlog").Last();
        Check(log.S("provider") == "terminal" && log.S("operation") == "credentials" && !log.ToJsonString().Contains(secret), "the integration log records who saved credentials, without them");
        var other = new IntegrationVault(vaultFolder, runtime.Keys, "another-site");
        try { other.Load("terminal"); Check(false, "a sealed file opens only for its site"); } catch (StoreException) { Check(true, "a sealed file opens only for its site"); }
        var config = service.Vault.Into(new IntegrationConfig());
        Check(config.File("terminal")?.Obj("mercadopago").S("accessToken") == secret && config.File("pac") is null, "on start the vault fills the providers without a file of their own");

        // ---------- the Linux update journal on every screen ----------
        var now = DateTimeOffset.UtcNow;
        JsonObject Journal(string stage, double minutesAgo, string reason = "") => J.O("stage", stage, "offer", "0.3.5", "reason", reason, "history", new JsonArray(J.O("stage", "received", "at", now.AddMinutes(-minutesAgo - 1).ToUnixTimeMilliseconds() / 1000.0), J.O("stage", stage, "at", now.AddMinutes(-minutesAgo).ToUnixTimeMilliseconds() / 1000.0)));
        Check(UpdateProgress.From(Journal("backup", 1), now) is { } running && running.B("active") && running.S("version") == "0.3.5", "an update on its way is active, with the version it installs");
        Check(UpdateProgress.From(Journal("installing", 50), now) is null, "a stalled update stops covering the screens");
        Check(UpdateProgress.From(Journal("rolled-back", 5, "el backend no respondió"), now) is { } back && !back.B("active") && back.S("reason") == "el backend no respondió", "a rollback is reported with its reason");
        Check(UpdateProgress.From(Journal("healthy", 40), now) is null && UpdateProgress.From(J.O("stage", "none"), now) is null, "old endings and an empty journal show nothing");
        var journal = Path.Combine(folder, "state.json");
        await File.WriteAllTextAsync(journal, Journal("installing", 0).ToJsonString());
        service.UpdateJournal = journal;
        Check((await service.State(waiterToken)).Obj("connection").Obj("installing").S("stage") == "installing", "every terminal learns from the server that it is installing an update");

    }
}
