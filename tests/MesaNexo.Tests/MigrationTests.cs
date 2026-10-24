using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
using MesaNexo.Desktop;
namespace MesaNexo.Tests;

static class MigrationTests {
    public static async Task Run(string root, string python, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception(name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        var folder = Path.Combine(root, ".lab", "migration-" + J.Id());
        Directory.CreateDirectory(folder);
        var s = J.O("schema", 1, "restaurants", new[] { J.O("id", "oldrestaurant", "name", "Synthetic migration fixture") }, "branches", new[] { J.O("id", "oldbranch", "restaurantId", "oldrestaurant") });
        foreach (var key in new[] { "tables", "products", "categories", "users", "visits", "commands", "units", "notifications", "requests", "payments", "reservations", "reviews", "cash", "movements", "promotions", "audit", "tickets", "grants" }) s[key] = new JsonArray();
        JsonObject Row(string id) => J.O("id", id, "restaurantId", "oldrestaurant", "branchId", "oldbranch");
        var table = Row("oldtable");
        table["name"] = "Mesa migrada";
        table["capacity"] = 4;
        table["status"] = "free";
        table["zone"] = "Salón";
        s.A("tables").Add(table);
        var user = Row("olduser");
        user["name"] = "Cuenta heredada";
        user["username"] = "legacy";
        user["password"] = "DO-NOT-IMPORT-THIS-DEMO-HASH";
        user["role"] = "mesero";
        user["active"] = true;
        s.A("users").Add(user);
        var visit = Row("oldvisit");
        visit["tableId"] = "oldtable";
        visit["waiterId"] = "olduser";
        visit["people"] = 1;
        visit["status"] = "closed";
        s.A("visits").Add(visit);
        var command = Row("oldcommand");
        command["visitId"] = "oldvisit";
        command["unitIds"] = new JsonArray("oldunit");
        s.A("commands").Add(command);
        var unit = Row("oldunit");
        unit["visitId"] = "oldvisit";
        unit["commandId"] = "oldcommand";
        unit["price"] = 12500;
        unit["status"] = "delivered";
        unit["name"] = "Producto histórico";
        s.A("units").Add(unit);
        var cash = Row("oldcash");
        cash["status"] = "closed";
        cash["initial"] = 0;
        s.A("cash").Add(cash);
        var payment = Row("oldpay");
        payment["visitId"] = "oldvisit";
        payment["cashId"] = "oldcash";
        payment["amount"] = 12500;
        payment["tip"] = 0;
        payment["method"] = "cash";
        s.A("payments").Add(payment);
        var source = Path.Combine(folder, "copy.json");
        File.WriteAllText(source, s.ToJsonString());
        var output = Path.Combine(folder, "export");
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        foreach (var arg in new[] { Path.Combine(root, "tools", "migrate_prototype.py"), "--source-copy", source, "--output", output, "--restaurant-id", "oldrestaurant", "--branch-id", "oldbranch", "--site-id", "migrated-site" }) start.ArgumentList.Add(arg);
        using (var process = Process.Start(start)!) { await process.WaitForExitAsync(); Check(process.ExitCode == 0, "migration export validates explicit synthetic copy and creates backup report"); }
        var planFile = Path.Combine(output, "import-plan.json");
        var raw = File.ReadAllText(planFile);
        Check(!raw.Contains("DO-NOT-IMPORT") && File.Exists(Path.Combine(output, "source-copy.json")), "migration excludes legacy passwords and retains source backup");
        using var runtime = new RuntimeStore(python, Path.Combine(root, "runtime", "lab_host.py"), Path.Combine(folder, "store"), "migrated-site");
        var branch = new BranchService(runtime, "migrated-site", new Passwords());
        await TestLicense.Initialize(branch, J.O("siteId", "migrated-site", "name", "Migración aislada", "mode", "licencia", "managerName", "Gerente nuevo", "managerUsername", "nuevo", "managerPassword", "Migration-" + J.Id(), "enrollmentCode", J.Id(), "modules", Array.Empty<string>()));
        var result = await branch.ImportPlan(planFile, J.Hash(raw));
        var loaded = await branch.Load();
        Check(result.B("validated") && loaded.Get("unit", "oldunit").L("price") == 12500 && loaded.Get("payment", "oldpay").L("amount") == 12500, "migration imports financial history into real encrypted CulinaryOS store");
        Check(!loaded.Get("user", "olduser").B("active") && loaded.Get("user", "olduser")["password"] is null, "migrated users cannot authenticate with demo credentials");
        var originalCount = loaded.Count;
        try { await branch.ImportPlan(planFile, J.Hash(raw)); throw new Exception("Expected initialized rejection"); } catch (BusinessException) { Check((await branch.Load()).Count == originalCount, "repeat migration cannot overwrite existing branch records"); }
        var legacyKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(s.ToJsonString());
        var encrypted = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(legacyKey, 16)) aes.Encrypt(nonce, plain, encrypted, tag);
        var backup = Path.Combine(folder, "legacy-backup.json");
        var keyFile = Path.Combine(folder, "original-test.key");
        File.WriteAllBytes(keyFile, legacyKey);
        File.WriteAllText(backup, J.O("format", "mesa-nexo-aes-256-gcm", "schema", 1, "iv", Convert.ToBase64String(nonce), "tag", Convert.ToBase64String(tag), "ciphertext", Convert.ToBase64String(encrypted)).ToJsonString());
        await Migration.DecryptBackup(backup, keyFile, Path.Combine(folder, "decrypted.json"));
        Check(JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "decrypted.json")))!.I("schema") == 1, "legacy AES-GCM backup requires its original key and format");
        File.WriteAllBytes(keyFile, RandomNumberGenerator.GetBytes(32));
        try { await Migration.DecryptBackup(backup, keyFile, Path.Combine(folder, "wrong.json")); throw new Exception("Expected bad key"); } catch (CryptographicException) { Check(!File.Exists(Path.Combine(folder, "wrong.json")), "wrong legacy key never produces an imported plaintext"); }
        if (OperatingSystem.IsWindows()) { var vaultFolder = Path.Combine(folder, "vault"); var vault = new DesktopVault(vaultFolder); var protectedValue = "pending-operation-" + J.Id(); await vault.Write("pending-test", protectedValue); var bytes = File.ReadAllBytes(Directory.GetFiles(vaultFolder).Single()); Check(!Encoding.UTF8.GetString(bytes).Contains(protectedValue), "Windows DPAPI encrypts durable client pending operation"); var reopened = new DesktopVault(vaultFolder); Check(await reopened.Read("pending-test") == protectedValue, "Windows vault survives new client instance with same operation id"); await reopened.Delete("pending-test"); }

    }
}