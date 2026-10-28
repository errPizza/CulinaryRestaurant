using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
using Npgsql;
namespace MesaNexo.Tests;

// The PostgreSQL branch store against the contract the CulinaryOS SQLite store and mesanexo_ipc answered.
static class StoreTests {
    // A branch that ran on the SQLite store moves to PostgreSQL with the same state, receipts and outbox.
    public static async Task Migration(string root, string python, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        var folder = Path.Combine(root, ".lab", "sqlite-move-" + J.Id());
        var site = "move-site";
        JsonObject before;
        List<string> pendingBefore;
        string acknowledged;
        using (var sqlite = new RuntimeStore(python, Path.Combine(root, "runtime", "lab_host.py"), Path.Combine(folder, "sqlite"), site)) {

            var branch = new BranchService(sqlite, site, new Passwords());
            await TestLicense.Initialize(branch, J.O("siteId", site, "name", "Mudanza", "mode", "licencia", "managerName", "Gerencia", "managerUsername", "gerencia", "managerPassword", "Move-" + J.Id(), "enrollmentCode", J.Id(), "modules", new JsonArray()));
            for (var i = 0; i < 5; i++) { var s = await branch.Load(); s.Add("customer", J.O("name", "Cliente ñ " + i, "phone", "55" + i)); s.Meta["revision"] = s.Meta.L("revision") + 1; await sqlite.CallAsync(J.O("op", "transaction_v1", "protocol", 1, "event_id", J.Id(), "receipt", J.O("actor", "move-test", "fingerprint", J.Id(), "result", J.O()), "changes", new JsonArray(s.Changes().Select(x => (JsonNode)x).ToArray()))); }
            before = (JsonObject)(await sqlite.CallAsync(J.O("op", "scan_v1", "protocol", 1, "max_records", 4096, "max_bytes", 1024 * 1024)))!;

        }
        // Acknowledge one event in SQLite, as the old sync worker would have.
        var acknowledge = new System.Diagnostics.ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-B", "-c", "import sqlite3,sys,time;db=sqlite3.connect(sys.argv[1]);r=db.execute('SELECT id FROM events ORDER BY rowid LIMIT 1').fetchone();db.execute('UPDATE events SET acked=? WHERE id=?',(time.time(),r[0]));db.commit();print(r[0]);print('\\n'.join(x[0] for x in db.execute('SELECT id FROM events WHERE acked IS NULL ORDER BY rowid')))", Path.Combine(folder, "sqlite", "store.sqlite") }) acknowledge.ArgumentList.Add(arg);
        using (var p = System.Diagnostics.Process.Start(acknowledge)!) { var lines = (await p.StandardOutput.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); await p.WaitForExitAsync(); acknowledged = lines[0]; pendingBefore = [.. lines.Skip(1)]; }
        var export = new System.Diagnostics.ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        foreach (var arg in new[] { "-B", Path.Combine(root, "runtime", "export_store.py"), "--lib", Path.Combine(root, "vendor", "culinaryos"), "--database", Path.Combine(folder, "sqlite", "store.sqlite"), "--keyring", Path.Combine(folder, "sqlite", "test-only-keyring.json"), "--site", site }) export.ArgumentList.Add(arg);
        export.Environment["PYTHONIOENCODING"] = "utf-8";
        using var target = LabPostgres.Test(root, Path.Combine(folder, "postgres"), site);
        using (var p = System.Diagnostics.Process.Start(export)!) {

            var result = await StoreMigration.Import(target, p.StandardOutput);
            await p.WaitForExitAsync();
            Check(p.ExitCode == 0 && result.B("migrated") && result.L("events") == pendingBefore.Count + 1, "the SQLite export moves into PostgreSQL in one pass");

        }
        var after = (JsonObject)(await target.CallAsync(J.O("op", "scan_v1", "protocol", 1, "max_records", 4096, "max_bytes", 1024 * 1024)))!;
        Check(J.Canonical(after["records"]) == J.Canonical(before["records"]) && after.L("revision") == before.L("revision"), "every record keeps its version and content after the move");
        Check(new BranchService(target, site, new Passwords()) is var moved && (await moved.Load()).List("customer").Any(c => c.S("name") == "Cliente ñ 4"), "the branch reads its state from PostgreSQL after the move");
        var pending = await target.Pending(64);
        Check(pending.Select(x => x.Wire.S("event_id")).SequenceEqual(pendingBefore), "pending events keep their order and digests after the move");
        Check((await target.CallAsync(J.O("op", "event_status_v1", "event_id", acknowledged)))!.S("remote") == "acknowledged", "an event the old store had acknowledged stays acknowledged");
        try { await StoreMigration.Import(target, new StringReader("")); Check(false, "a second migration over data"); } catch (BusinessException) { Check(true, "a migration never writes over a store that has data"); }

    }
    public static async Task Run(string root, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        async Task<string> Fails(Task action) { try { await action; return "none"; } catch (StoreException e) { return e.Code; } }
        var folder = Path.Combine(root, ".lab", "store-" + J.Id());
        using var store = LabPostgres.Test(root, folder, "store-site");
        JsonObject Tx(string eventId, long meta, params (string Id, long Expected, JsonObject Data)[] changes) {

            var list = new JsonArray(J.O("record_id", "mn_meta_site", "expected_version", meta, "data", J.O("revision", meta + 1)));
            foreach (var (id, expected, data) in changes) list.Add(J.O("record_id", id, "expected_version", expected, "data", data));
            return J.O("op", "transaction_v1", "protocol", 1, "event_id", eventId, "receipt", J.O("actor", "tester", "fingerprint", eventId + "-fp"), "changes", list);

        }
        var first = J.Id();
        var committed = (await store.CallAsync(Tx(first, 0, ("mn_visit_a", 0, J.O("total", 100)), ("mn_unit_a", 0, J.O("name", "Café ☕")))))!;
        var visit = (await store.CallAsync(J.O("op", "get", "record_id", "mn_visit_a")))!;
        Check(!committed.B("duplicate") && visit.L("version") == 1 && visit.Obj("data").L("total") == 100, "a transaction commits every record and its receipt together");
        Check((await store.CallAsync(J.O("op", "get", "record_id", "mn_unit_a")))!.Obj("data").S("name") == "Café ☕", "records keep text outside ASCII");
        Check((await store.CallAsync(Tx(first, 0, ("mn_visit_a", 0, J.O("total", 100)))))!.B("duplicate"), "a repeated operation id with the same receipt answers duplicate instead of committing twice");
        var reused = Tx(first, 0, ("mn_visit_a", 0, J.O("total", 100)));
        reused.Obj("receipt")["fingerprint"] = "other";
        Check(await Fails(store.CallAsync(reused)) == "COS-DATA-IDEMPOTENCY", "an operation id reused for other content is refused");
        Check(await Fails(store.CallAsync(Tx(J.Id(), 1, ("mn_visit_a", 1, J.O("total", 200)), ("mn_visit_b", 5, J.O("total", 1))))) == "COS-DATA-CONFLICT", "a stale version refuses the whole transaction");
        Check((await store.CallAsync(J.O("op", "get", "record_id", "mn_visit_a")))!.Obj("data").L("total") == 100 && await store.CallAsync(J.O("op", "get", "record_id", "mn_visit_b")) is null, "a refused transaction leaves no record written");
        Check(await Fails(store.CallAsync(Tx(J.Id(), 1, ("mn_big_a", 0, J.O("blob", new string('x', 60 * 1024)))))) == "COS-DATA-INVALID", "a transaction over 56 KiB is refused before writing");
        Check(await Fails(store.CallAsync(J.O("op", "transaction_v1", "protocol", 1, "event_id", J.Id(), "receipt", J.O(), "changes", new JsonArray(J.O("record_id", "mn_visit_c", "expected_version", 0, "data", J.O()))))) == "COS-DATA-INVALID", "every transaction must carry the site revision");
        // Two writers on the same revision: exactly one commits.
        var racing = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Fails(store.CallAsync(Tx(J.Id(), 1, ("mn_race_" + i, 0, J.O("i", i)))))));
        Check(racing.Count(x => x == "none") == 1 && racing.Count(x => x == "COS-DATA-CONFLICT") == 5, "concurrent transactions on one revision: one commits, the rest conflict");
        long meta = (await store.CallAsync(J.O("op", "get", "record_id", "mn_meta_site")))!.L("version");
        for (var i = 0; i < 40; i++) { await store.CallAsync(Tx(J.Id(), meta, ("mn_audit_" + i.ToString("D3"), 0, J.O("i", i, "pad", new string('p', 3000))))); meta++; }
        var pages = new List<JsonObject>();
        var after = "";
        do { var page = (JsonObject)(await store.CallAsync(J.O("op", "scan_v1", "protocol", 1, "after", after, "max_bytes", 16 * 1024, "max_records", 8)))!; pages.Add(page); after = page.S("after"); } while (after != "");
        var scanned = pages.SelectMany(p => p.A("records").Objects()).Select(r => r.S("record_id")).ToList();
        Check(pages.Count > 3 && scanned.SequenceEqual(scanned.Order(StringComparer.Ordinal)) && scanned.Distinct().Count() == scanned.Count && pages.All(p => p.L("revision") == meta), "scan pages are bounded, ordered, complete and name their revision");
        var excluded = (await store.CallAsync(J.O("op", "scan_v1", "protocol", 1, "exclude", new JsonArray("mn_audit_"), "max_records", 4096, "max_bytes", 1024 * 1024)))!.A("records").Objects().Select(r => r.S("record_id")).ToList();
        Check(excluded.Count > 0 && !excluded.Any(x => x.StartsWith("mn_audit_")) && excluded.Contains("mn_visit_a"), "an excluded kind is cut out of the scan");
        var recent = (await store.CallAsync(J.O("op", "recent_v1", "protocol", 1, "prefix", "mn_audit_", "limit", 5, "max_records", 3)))!;
        var rest = (await store.CallAsync(J.O("op", "recent_v1", "protocol", 1, "prefix", "mn_audit_", "limit", 2, "before", recent.L("before"))))!;
        Check(recent.A("records").Objects().Select(r => r.Obj("data").L("i")).SequenceEqual([39, 38, 37]) && rest.A("records").Objects().Select(r => r.Obj("data").L("i")).SequenceEqual([36, 35]), "recent returns the newest records of a kind, in pages");
        Check((await store.CallAsync(J.O("op", "event_status_v1", "event_id", first)))!.S("remote") == "pending" && (await store.CallAsync(J.O("op", "event_status_v1", "event_id", J.Id())))!.S("local") == "unknown", "event status tells committed and pending from unknown");
        // The database alone opens nothing: rows are sealed under the keyring.
        await using (var db = await store.Data.OpenConnectionAsync()) {

            await using var cmd = new NpgsqlCommand("SELECT envelope FROM records WHERE id='mn_unit_a'", db);
            var raw = System.Text.Encoding.UTF8.GetString((byte[])(await cmd.ExecuteScalarAsync())!);
            Check(!raw.Contains("Café") && !raw.Contains("name"), "records are sealed before they reach PostgreSQL");

        }
        File.WriteAllText(Path.Combine(folder, "test-only-keyring.json"), Keyring.Generate().ToJsonString());
        try { using var wrong = LabPostgres.Test(root, folder, "store-site"); Check(false, "a wrong keyring opens the store"); } catch (StoreException e) { Check(e.Code == "COS-DATA-INTEGRITY", "a store refuses to open under another keyring"); }
        Directory.CreateDirectory(folder + "-other");
        File.Copy(Path.Combine(folder, "postgres-database.txt"), Path.Combine(folder + "-other", "postgres-database.txt"), true);
        try { using var other = LabPostgres.Test(root, folder + "-other", "other-site"); Check(false, "another site opens the store"); } catch (StoreException e) { Check(e.Code == "COS-DATA-SITE", "a store belongs to one site"); }
        // Without a central, pending events are kept whatever their age; acknowledged ones move to the archive.
        await using (var db = await store.Data.OpenConnectionAsync()) { await using var age = new NpgsqlCommand("UPDATE events SET created = created - 30 * 86400", db); await age.ExecuteNonQueryAsync(); }
        var pendingBefore = (await store.CallAsync(J.O("op", "outbox_status_v1")))!.L("pending");
        var standalone = (await store.CallAsync(J.O("op", "outbox_maintain_v1", "protocol", 1)))!;
        Check(standalone.S("mode") == "standalone" && (await store.CallAsync(J.O("op", "outbox_status_v1")))!.L("pending") == pendingBefore && pendingBefore > 40, "thirty days without a central drop no pending event");
        var sent = await store.Pending(64);
        Check(sent.Count == pendingBefore && sent.Select(x => x.Wire.S("event_id")).Distinct().Count() == sent.Count, "pending events come out once each, oldest first");
        await store.Acknowledge(sent[0].Wire.S("event_id"), sent[0].Digest);
        Check(await Fails(store.Acknowledge(sent[0].Wire.S("event_id"), sent[0].Digest)) == "COS-DATA-CONFLICT", "an event is acknowledged once");
        await using (var db = await store.Data.OpenConnectionAsync()) { await using var age = new NpgsqlCommand("UPDATE events SET acked = acked - 30 * 86400 WHERE acked IS NOT NULL", db); await age.ExecuteNonQueryAsync(); }
        await store.CallAsync(Tx(J.Id(), meta, ("mn_visit_z", 0, J.O("total", 1))));
        Check((await store.CallAsync(J.O("op", "event_status_v1", "event_id", sent[0].Wire.S("event_id"))))!.S("remote") == "acknowledged" && (await store.CallAsync(J.O("op", "outbox_status_v1")))!.L("pending") == pendingBefore, "an old acknowledged event moves to the archive and still reads acknowledged");
        var snapshot = (await store.CallAsync(J.O("op", "snapshot_v1", "protocol", 1)))!;
        Check(snapshot.S("sha256").Length == 64 && File.Exists(Path.Combine(folder, "schema-backups", snapshot.S("backup"))), "a snapshot is a pg_dump in the service-owned folder");

    }
}
