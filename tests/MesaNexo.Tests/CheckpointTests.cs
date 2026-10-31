using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
namespace MesaNexo.Tests;

// A bucket with R2's bucket lock on: objects can be added, never replaced or removed.
sealed class LockedBucket: HttpMessageHandler {
    public readonly SortedDictionary<string, byte[]> Objects = new(StringComparer.Ordinal);
    public bool Down;
    public int Refused;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {

        if (Down) throw new HttpRequestException("Sin conexión");
        if (request.Headers.Authorization is null && !request.Headers.Contains("Authorization")) return new(HttpStatusCode.Forbidden);
        var key = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath)["/bucket-de-prueba".Length..].TrimStart('/');
        if (request.Method == HttpMethod.Put) {

            if (Objects.ContainsKey(key)) { Refused++; return new(HttpStatusCode.PreconditionFailed); }
            Objects[key] = await request.Content!.ReadAsByteArrayAsync(ct);
            return new(HttpStatusCode.OK);

        }
        if (key != "") return Objects.TryGetValue(key, out var body) ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) } : new(HttpStatusCode.NotFound);
        var prefix = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["prefix"] ?? "";
        var keys = string.Concat(Objects.Keys.Where(x => x.StartsWith(prefix)).Select(x => "<Contents><Key>" + x + "</Key></Contents>"));
        return new(HttpStatusCode.OK) { Content = new StringContent("<?xml version=\"1.0\"?><ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">" + keys + "</ListBucketResult>") };

    }
}
static class CheckpointTests {
    public static async Task Run(string root, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        // Signature Version 4: the GET example published in the Amazon S3 API reference.
        var empty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        var at = DateTimeOffset.ParseExact("20130524T000000Z", "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var signed = ObjectBucket.Authorization("GET", "/test.txt", "", new(StringComparer.Ordinal) { ["host"] = "examplebucket.s3.amazonaws.com", ["range"] = "bytes=0-9", ["x-amz-content-sha256"] = empty, ["x-amz-date"] = "20130524T000000Z" }, empty, at, "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1");
        Check(signed.EndsWith("Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41"), "requests are signed as Amazon's published example");
        var site = "checkpoint-site";
        var folder = Path.Combine(root, ".lab", "tests", site);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        if (Directory.Exists(TestLicense.Folder(site))) Directory.Delete(TestLicense.Folder(site), true);
        using var runtime = LabPostgres.Test(root, folder, site);
        var service = new BranchService(runtime, site, new Passwords());
        var password = "Checkpoint-" + J.Id();
        var enrollment = J.Id();
        await TestLicense.Initialize(service, J.O("siteId", site, "name", "Puntos de control", "managerName", "Gerencia", "managerUsername", "gerencia", "managerPassword", password, "enrollmentCode", enrollment, "modules", new JsonArray()));
        var token = (await service.Login(J.O("username", "gerencia", "password", password, "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "Terminal", "enrollmentCode", enrollment))).S("token");
        async Task<JsonObject> Act(string type, JsonObject p) => (await service.Action(token, J.O("operationId", J.Id(), "type", type, "payload", p))).Obj("result");
        var locked = new LockedBucket();
        var bucket = new ObjectBucket(new HttpClient(locked), "https://cuenta.r2.example", "bucket-de-prueba", "acceso", "secreto");
        await Act("table.save", J.O("name", "Mesa 1", "capacity", 4, "zone", "Salón"));
        Check(await service.PublishCheckpoint(bucket), "the first head is published");
        Check(!await service.PublishCheckpoint(bucket), "nothing new, nothing published");
        await Act("table.save", J.O("name", "Mesa 2", "capacity", 4, "zone", "Salón"));
        Check(!await service.PublishCheckpoint(bucket), "within the hour the head waits");
        var cash = await Act("cash.open", J.O("initial", 0));
        await Act("cash.close", J.O("id", cash.S("id"), "counted", 0, "notes", "Cierre"));
        Check(await service.PublishCheckpoint(bucket), "a cash close publishes at once");
        locked.Down = true;
        await Act("table.save", J.O("name", "Mesa 3", "capacity", 4, "zone", "Salón"));
        try { await service.PublishCheckpoint(bucket, true); throw new Exception("Sent without a bucket"); } catch (HttpRequestException) { Check(true, "a bucket that does not answer is an error for the worker, not for the sale"); }
        locked.Down = false;
        Check(await service.PublishCheckpoint(bucket, true), "the next head covers what the failed one would have");
        var good = await service.CompareCheckpoints(bucket);
        Check(good.B("valid") && good.L("checkpoints") == 3, "an untouched branch matches what it published");
        foreach (var body in locked.Objects.Values) Check(Checkpoints.Signed(JsonNode.Parse(body)!.AsObject()), "each checkpoint carries the branch's signature");
        var altered = JsonNode.Parse(locked.Objects.Values.First())!.AsObject();
        altered["head"] = new string('0', 64);
        Check(!Checkpoints.Signed(altered), "a checkpoint changed afterwards fails its signature");
        // The attack of the finding: every local key, an old record rewritten, the whole chain recomputed.
        await Act("table.save", J.O("name", "Mesa 4", "capacity", 4, "zone", "Salón"));
        await service.PublishCheckpoint(bucket, true);
        var records = (await service.AuditRecords()).Objects().OrderBy(x => x.L("sequence")).Select(x => (JsonObject)x.DeepClone()).ToArray();
        var previous = "";
        var changes = new JsonArray();
        var ids = (await service.Load()).Committed.Records.Where(x => x.Value.Data.S("kind") == "audit").ToDictionary(x => x.Value.Data.S("id"), x => x.Key);
        for (var i = 0; i < records.Length; i++) {

            var a = records[i];
            var version = (await runtime.CallAsync(J.O("op", "get", "record_id", ids[a.S("id")])))!.L("version");
            if (i == 1) a["actor"] = "alguien-mas";
            a["previousHash"] = previous;
            var kind = a["kind"]?.DeepClone();
            a.Remove("hash");
            a.Remove("kind");
            a["hash"] = J.Hash(J.Canonical(a));
            previous = a.S("hash");
            a["kind"] = kind;
            changes.Add(J.O("record_id", ids[a.S("id")], "expected_version", version, "data", a.DeepClone()));

        }
        var meta = (await runtime.CallAsync(J.O("op", "get", "record_id", "mn_meta_site")))!;
        var rewritten = (JsonObject)meta.Obj("data").DeepClone();
        rewritten["auditHead"] = previous;
        changes.Insert(0, J.O("record_id", "mn_meta_site", "expected_version", meta.L("version"), "data", rewritten));
        var e = J.Id();
        await runtime.CallAsync(J.O("op", "transaction_v1", "protocol", 1, "event_id", e, "receipt", J.O("actor", "atacante", "fingerprint", e, "operation", "edit", "result", new JsonObject(), "at", J.Now()), "changes", changes));
        await Task.Delay(1100);
        Check((await service.AuditChain()).B("valid"), "the rewritten chain fools the local check");
        var caught = await service.CompareCheckpoints(bucket);
        Check(!caught.B("valid") && caught.A("problems").Objects().Any(x => x.S("reason") == "different"), "the published heads catch the rewrite");
        Check(locked.Objects.Keys.Count(x => x.StartsWith(Checkpoints.AlertsPrefix(site))) == 1, "the evidence is left outside the machine");
        await service.CompareCheckpoints(bucket);
        Check(locked.Objects.Keys.Count(x => x.StartsWith(Checkpoints.AlertsPrefix(site))) == 1, "the alert is written once");
        Check((await service.State(token)).Obj("connection").Obj("auditAlert").S("level") == "critical", "the manager is told");
        try { await service.Action(token, J.O("operationId", J.Id(), "type", "cash.withdraw", "payload", J.O("amount", 100))); throw new Exception("Authorization accepted"); } catch (BusinessException x) { Check(x.Status == 409 && x.Message.Contains("bitácora"), "new authorizations stop"); }
        await Act("table.save", J.O("name", "Mesa 5", "capacity", 4, "zone", "Salón"));
        Check(true, "the sale itself keeps working");
        var published = locked.Objects.ToDictionary(x => x.Key, x => x.Value);
        await service.PublishCheckpoint(bucket, true);
        var again = await service.CompareCheckpoints(bucket);
        Check(!again.B("valid") && published.All(x => locked.Objects[x.Key].SequenceEqual(x.Value)), "publishing the new heads does not erase the old ones");
// The independent check: an export of the chain and a copy of the bucket, no database and no keys.
        var export = Path.Combine(folder, "bitacora.json");
        await File.WriteAllTextAsync(export, (await service.AuditRecords()).ToJsonString());
        var copy = Path.Combine(folder, "bucket");
        foreach (var (key, body) in locked.Objects) { var path = Path.Combine(copy, key); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, body); }
        var offline = await OfflineCheck.Run(export, copy, site);
        Check(!offline.B("valid") && offline.A("problems").Count > 0, "the check outside the branch reaches the same verdict");
        // A real restore also leaves published heads behind: accepted by name, kept in the bucket and counted.
        await service.AcceptCheckpoints(bucket, "Restauración del 30 de octubre");
        var accepted = await service.CompareCheckpoints(bucket);
        Check(accepted.B("valid") && accepted.L("accepted") > 0 && locked.Objects.Keys.Any(x => x.StartsWith(Checkpoints.AcceptedPrefix(site))), "an accepted divergence is recorded and counted");
        Check((await service.State(token)).Obj("connection").Obj("auditAlert").Count == 0, "accepting clears the alert");
        // Offsite copy: real pg_dump into the bucket, and back through pg_restore on a new database.
        Check(await service.PublishBackup(runtime, bucket, folder), "a change is copied outside the branch");
        Check(!await service.PublishBackup(runtime, bucket, folder), "with no change nothing is uploaded");
        Check(!Directory.EnumerateFiles(Path.Combine(folder, "offsite")).Any(), "the local copy does not stay on the disk");
        var backupKey = locked.Objects.Keys.Single(x => x.EndsWith(".dump"));
        var hash = Encoding.ASCII.GetString(locked.Objects[backupKey + ".sha256"]).Split(' ')[0];
        Check(hash == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(locked.Objects[backupKey])), "the copy travels with the checksum culinaryos-restore asks for");
        Check(!Encoding.Latin1.GetString(locked.Objects[backupKey]).Contains("Mesa 1"), "the bucket never sees the branch's data in the clear");
        var restored = folder + "-offsite";
        if (Directory.Exists(restored)) Directory.Delete(restored, true);
        Directory.CreateDirectory(restored);
        File.Copy(Path.Combine(folder, "test-only-keyring.json"), Path.Combine(restored, "test-only-keyring.json"));
        var download = Path.Combine(restored, "copia.dump");
        await File.WriteAllBytesAsync(download, locked.Objects[backupKey]);
        await LabPostgres.RestoreTest(root, download, restored);
        using (var target = LabPostgres.Test(root, restored, site)) {

            var moved = new BranchService(target, site, new Passwords());
            Check((await moved.AuditChain()).B("valid") && (await target.VerifyOutbox()).B("valid") && (await moved.Load()).List("table").Count() >= 4, "the copy from the bucket restores and verifies");

        }
        await Act("table.save", J.O("name", "Mesa 6", "capacity", 4, "zone", "Salón"));
        locked.Down = true;
        try { await service.PublishBackup(runtime, bucket, folder); throw new Exception("Copied without a bucket"); } catch (HttpRequestException) { }
        locked.Down = false;
        Check(!Directory.EnumerateFiles(Path.Combine(folder, "offsite")).Any(), "a failed upload leaves nothing on the disk");
        Check(service.BackupAlert(DateTimeOffset.UtcNow) is null, "one failed copy is not an alert");
        Check(service.BackupAlert(DateTimeOffset.UtcNow.AddMinutes(61))?.S("level") == "notice" && service.BackupAlert(DateTimeOffset.UtcNow.AddHours(5))?.S("level") == "critical", "an hour without copies warns, and four are critical");
        Check(await service.PublishBackup(runtime, bucket, folder) && service.BackupAlert(DateTimeOffset.UtcNow.AddHours(5)) is null, "the next copy clears it");
        // Server alerts: what culinaryos-health reports, for the manager.
        var report = Path.Combine(folder, "status.json");
        var checkedAt = DateTimeOffset.UtcNow;
        string Health(DateTimeOffset generated) => J.O("generated_at", generated.ToUnixTimeSeconds(), "checks", new JsonArray(J.O("id", "COS-SMART", "parte", "Disco", "gravedad", "critico", "mensaje", "El disco anuncia una falla.", "accion", "Cámbialo."), J.O("id", "COS-DISK", "parte", "Almacenamiento", "gravedad", "correcto", "mensaje", "ok", "accion", "ok"), J.O("id", "COS-PACKAGES", "parte", "Actualizaciones", "gravedad", "aviso", "mensaje", "Hay paquetes.", "accion", "Revisa."))).ToJsonString();
        await File.WriteAllTextAsync(report, Health(checkedAt));
        var alerts = BranchService.ReadServerHealth(report, checkedAt);
        Check(alerts.Count == 1 && alerts[0].S("level") == "critical" && alerts[0].S("message").Contains("disco anuncia"), "a failing disk reaches the app, and routine notices do not");
        await File.WriteAllTextAsync(report, Health(checkedAt.AddMinutes(-20)));
        Check(BranchService.ReadServerHealth(report, checkedAt).Single().S("message").Contains("dejó de actualizarse"), "a report that stopped arriving is an alert");
        await File.WriteAllTextAsync(report, "{no es json");
        Check(BranchService.ReadServerHealth(report, checkedAt).Count == 1, "an unreadable report is an alert, not a failure");
        Check(BranchService.ReadServerHealth(Path.Combine(folder, "no-existe.json"), checkedAt).Count == 0, "outside CulinaryOS there is no report to read");
        await File.WriteAllTextAsync(report, Health(checkedAt));
        service.UseServerHealth(report);
        Check((await service.State(token)).Obj("connection").A("server").Objects().Any(x => x.S("message").Contains("disco anuncia")), "the manager's state carries the alert");

    }
}
