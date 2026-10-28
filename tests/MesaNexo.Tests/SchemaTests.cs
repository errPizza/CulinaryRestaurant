using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
namespace MesaNexo.Tests;

static class SchemaTests {
    public static async Task Run(string root, string python, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception(name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        var folder = Path.Combine(root, ".lab", "schema-" + J.Id());
        using var runtime = LabPostgres.Test(root, folder, "schema-site");
        var service = new BranchService(runtime, "schema-site", new Passwords());
        await TestLicense.Initialize(service, J.O("siteId", "schema-site", "name", "Schema fixture", "mode", "licencia", "managerName", "Manager", "managerUsername", "manager", "managerPassword", "Test-" + J.Id(), "enrollmentCode", J.Id(), "modules", Array.Empty<string>()));
        var old = await service.Load();
        old.Meta["schemaVersion"] = 1;
        old.Meta["revision"] = old.Meta.L("revision") + 1;
        await runtime.CallAsync(J.O("op", "transaction_v1", "protocol", 1, "event_id", J.Id(), "receipt", J.O("actor", "legacy-schema-fixture", "fingerprint", J.Id(), "result", new JsonObject()), "changes", new JsonArray(old.Changes().Select(x => (JsonNode)x).ToArray())));
        var migrated = await service.UpgradeSchema();
        Check(migrated.L("schemaVersion") == 2 && migrated.Obj("backup").B("encrypted"), "explicit schema migration creates service-owned encrypted backup before commit");
        Check((await service.UpgradeSchema()).B("alreadyApplied"), "schema migration is idempotent without resetting records");
        var restored = Path.Combine(folder, "recovery");
        Directory.CreateDirectory(restored);
        var backup = Path.Combine(folder, "schema-backups", migrated.Obj("backup").S("backup"));
        File.Copy(Path.Combine(folder, "test-only-keyring.json"), Path.Combine(restored, "test-only-keyring.json"));
        await LabPostgres.RestoreTest(root, backup, restored);
        using var recovered = LabPostgres.Test(root, restored, "schema-site");
        var recovery = new BranchService(recovered, "schema-site", new Passwords());
        var state = await recovery.Load();
        Check(state.Meta.L("schemaVersion") == 1 && state.List("user").Count() == 1, "encrypted backup restores prior schema and users into isolated staging");
        var originalHealth = (await recovered.CallAsync(J.O("op", "health")))!;
        Check(originalHealth.L("pending") >= 2 && await recovery.Ready(), "recovered original runtime retains pending outbox and branch readiness");

    }
}
