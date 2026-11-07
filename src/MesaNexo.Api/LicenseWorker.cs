using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;

// Renews at least daily while CulinaryServer is reachable (the license itself allows 7 days offline) and keeps the
// multi-company central's certificate registered. Support runs on its own, much shorter, clock; so do fault reports,
// the owner's summaries, the look at what the provider published and the CSD the owner sends from Culinary Dueño.
public sealed class LicenseWorker(BranchService branch, ILogger<LicenseWorker> log): BackgroundService {
    protected override Task ExecuteAsync(CancellationToken ct) => Task.WhenAll(Licensing(ct), Support(ct), Every(TimeSpan.FromMinutes(1), branch.SendFaults, ct), Every(TimeSpan.FromMinutes(3), branch.SendStats, ct), Every(TimeSpan.FromMinutes(15), branch.LookForReleases, ct), Every(TimeSpan.FromMinutes(1), branch.SyncFiscalIntake, ct));
    async Task Every(TimeSpan period, Func<CancellationToken, Task> work, CancellationToken ct) {

        await Task.Yield();
        while (!ct.IsCancellationRequested) {

            try { if ((await branch.LicenseStatus(ct)).S("state") != "missing") await work(ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } catch (Exception e) when (e is ServerRejection or BusinessException or HttpRequestException or TaskCanceledException or StoreException) { log.LogWarning("CulinaryServer: {Message}", e.Message); }
            try { await Task.Delay(period, ct); } catch (OperationCanceledException) { break; }

        }

    }
    // A local message leaves on the next look; replies are fetched every 30 s while a ticket is open, and the owner's
    // messages every 2 minutes.
    async Task Support(CancellationToken ct) {

        await Task.Yield();
        var last = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested) {

            var delay = TimeSpan.FromSeconds(10);
            try {

                if ((await branch.LicenseStatus(ct)).S("state") != "missing" && await branch.SupportDue(DateTimeOffset.UtcNow - last, ct)) {

                    await branch.SyncSupport(ct);
                    await branch.SyncOwnerMessages(ct);
                    last = DateTimeOffset.UtcNow;

                }

            } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } catch (Exception e) when (e is ServerRejection or BusinessException or HttpRequestException or TaskCanceledException or StoreException) { delay = TimeSpan.FromMinutes(1); log.LogWarning("Soporte: {Message}", e.Message); }
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }

        }

    }
    async Task Licensing(CancellationToken ct) {

        await Task.Yield();
        var registered = false;
        while (!ct.IsCancellationRequested) {

            var delay = TimeSpan.FromHours(6);
            try {

                if ((await branch.LicenseStatus(ct)).S("state") == "missing") delay = TimeSpan.FromMinutes(1);
                else {

                    await branch.Renew(ct);
                    registered = registered || await branch.RegisterCentral(ct);

                }

            } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } catch (Exception e) when (e is ServerRejection or BusinessException or HttpRequestException or TaskCanceledException or StoreException) { delay = TimeSpan.FromMinutes(5); log.LogWarning("CulinaryServer: {Message}", e.Message); }
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }

        }

    }
}
public sealed partial class BranchService {
    DateTimeOffset? certificateExpires;
    public void UseCertificate(DateTimeOffset notAfter) => certificateExpires = notAfter;
    // Renewal runs daily from 35 days out and restarts this process, so 30 days left on the certificate in use means it failed.
    JsonObject? CertificateWarning(string role) {

        if (certificateExpires is not { } ends) return null;
        var days = (int)Math.Ceiling((ends - DateTimeOffset.UtcNow).TotalDays);
        if (days > 30 || role != "manager" && days > 7) return null;
        return J.O("days", days, "level", days <= 7 ? "critical" : "notice", "message", days <= 0 ? "El certificado de este servidor venció. Reinicia el servidor y llama a soporte." : $"El certificado de este servidor vence en {days} {(days == 1 ? "día" : "días")} y no se renovó solo. Reinicia el servidor; si el aviso sigue, llama a soporte.");

    }
    // The certificate this branch presents to MesaNexo.Central; the panel lists it for --provision-tenant.
    public async Task<bool> RegisterCentral(CancellationToken ct = default) {

        if (licensing?.CentralCertificate is not { } file || !File.Exists(file)) return false;
        using var certificate = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(file, ct));
        await Call(HttpMethod.Put, "/branch/v1/registry", J.O("centralCertificateSha256", Convert.ToHexStringLower(SHA256.HashData(certificate.RawData))), ct);
        return true;

    }
    public async Task<bool> SupportDue(TimeSpan since, CancellationToken ct = default) {

        var tickets = (await View(ct)).List("ticket").ToArray();
        if (tickets.Any(t => t.A("messages").Objects().Any(m => m.S("actor") != "provider" && !m.B("sent")))) return true;
        return since >= TimeSpan.FromMinutes(2) || since >= TimeSpan.FromSeconds(30) && tickets.Any(t => t.S("channel") != "owner" && t.S("status") != "closed");

    }
    // Local tickets are the source of what the branch wrote; CulinaryServer is the source of the provider's replies.
    public async Task SyncSupport(CancellationToken ct = default) {

        var local = (await View(ct)).List("ticket").Select(x => (JsonObject)x.DeepClone()).ToArray();
        var sent = new Dictionary<string, (string RemoteId, string[] Messages)>();
        foreach (var ticket in local.Where(t => t.S("channel") != "owner")) {

            var messages = ticket.A("messages").Objects().Where(m => m.S("actor") != "provider" && !m.B("sent")).ToArray();
            if (messages.Length == 0) continue;
            var remote = ticket.S("remoteId");
            var delivered = new List<string>();
            if (remote == "") {

                var subject = ticket.S("subject");
                var created = await Call(HttpMethod.Post, "/branch/v1/tickets", J.O("subject", subject.Length > 150 ? subject[..150] : subject, "message", messages[0].S("message")), ct);
                remote = created.Obj("ticket").S("id");
                delivered.Add(messages[0].S("id"));
                messages = messages[1..];

            }
            foreach (var m in messages) { await Call(HttpMethod.Post, "/branch/v1/tickets/" + remote + "/messages", J.O("message", m.S("message")), ct); delivered.Add(m.S("id")); }
            sent[ticket.S("id")] = (remote, delivered.ToArray());

        }
        var replies = (await Call(HttpMethod.Get, "/branch/v1/tickets", null, ct)).A("tickets").Objects().ToDictionary(x => x.S("id"));
        await Exclusive(async () => {

            var s = await Load(ct);
            var changed = false;
            foreach (var ticket in s.List("ticket").Where(t => t.S("channel") != "owner").ToArray()) {

                if (sent.TryGetValue(ticket.S("id"), out var delivery)) {

                    ticket["remoteId"] = delivery.RemoteId;
                    foreach (var m in ticket.A("messages").Objects()) if (delivery.Messages.Contains(m.S("id"))) m["sent"] = true;
                    changed = true;

                }
                if (!replies.TryGetValue(ticket.S("remoteId"), out var remote)) continue;
                var messages = ticket.A("messages");
                foreach (var reply in remote.A("messages").Objects().Where(m => m.S("author") == "admin" && !messages.Objects().Any(x => x.S("id") == "server-" + m.S("id")))) {

                    messages.Add(J.O("id", "server-" + reply.S("id"), "actor", "provider", "name", "Soporte de Culinary", "message", reply.S("body"), "at", reply.S("createdAt")));
                    changed = true;

                }
                if (ticket.S("status") != remote.S("status")) { ticket["status"] = remote.S("status"); changed = true; }

            }
            if (changed) await Commit(s, J.Id(), "culinary-server", J.Hash("support:" + J.Now()), J.O("synchronized", true), "support.sync", ct);
            return changed;

        }, ct);

    }
    public const string OwnerThread = "owner";
    const int OwnerThreadKept = 200;
    // The owner's thread lives in the store as one ticket of the "owner" channel: the managers read it on the support
    // page and answer with ticket.reply. CulinaryServer keeps the thread; the branch sends its answers by their own id.
    public async Task SyncOwnerMessages(CancellationToken ct = default) {

        var mine = (await View(ct)).List("ticket").FirstOrDefault(t => t.S("channel") == "owner");
        var delivered = new List<string>();
        foreach (var m in mine?.A("messages").Objects().Where(m => m.S("actor") != "owner" && !m.B("sent")).ToArray() ?? []) {

            await Call(HttpMethod.Post, "/branch/v1/owner-messages", J.O("id", m.S("id"), "name", m.S("name") == "" ? "Sucursal" : m.S("name"), "message", m.S("message")), ct);
            delivered.Add(m.S("id"));

        }
        var remote = (await Call(HttpMethod.Get, "/branch/v1/owner-messages", null, ct)).A("messages").Objects().Where(m => m.S("author") == "owner").ToArray();
        if (mine is null && remote.Length == 0) return;
        await Exclusive(async () => {

            var s = await Load(ct);
            var thread = s.List("ticket").FirstOrDefault(t => t.S("channel") == "owner");
            var changed = false;
            if (thread is null) {

                thread = s.Add("ticket", J.O("id", OwnerThread, "siteId", s.Meta.S("siteId"), "channel", "owner", "subject", "Mensajes del dueño", "status", "open", "at", J.Now(), "messages", new JsonArray()));
                changed = true;

            }
            var messages = thread.A("messages");
            foreach (var m in messages.Objects()) if (delivered.Contains(m.S("id")) && !m.B("sent")) { m["sent"] = true; changed = true; }
            foreach (var m in remote.Where(m => !messages.Objects().Any(x => x.S("id") == "server-" + m.S("id")))) {

                messages.Add(J.O("id", "server-" + m.S("id"), "actor", "owner", "name", m.S("name"), "message", m.S("body"), "toAll", m.B("toAll"), "at", m.S("createdAt"), "sent", true));
                changed = true;

            }
            if (!changed) return false;
            var ordered = messages.Objects().OrderBy(m => DateTimeOffset.TryParse(m.S("at"), out var t) ? t : DateTimeOffset.MinValue).TakeLast(OwnerThreadKept).Select(m => m.DeepClone()).ToArray();
            thread["messages"] = new JsonArray(ordered);
            thread["lastAt"] = ordered.Length > 0 ? ordered[^1]!.AsObject().S("at") : thread.S("at");
            await Commit(s, J.Id(), "culinary-server", J.Hash("owner-messages:" + J.Now()), J.O("synchronized", true), "owner.sync", ct);
            return true;

        }, ct);

    }
    // Terminal updates: authorized by the terminal's device credentials (sessions closed, like CanInstall) and fetched
    // from CulinaryServer with this branch's license. The terminal verifies the signed update again before installing.
    readonly Dictionary<string, JsonObject> downloads = [];
    // A signed-in manager may look and download with sessions open; everyone is signed out only when the install starts.
    async Task NeedInstallAllowed(JsonObject p, string? token, CancellationToken ct) {

        if (token is not null) { Role(Authenticate(await View(ct), token, out _), "manager"); return; }
        var permission = await CanInstall(p, ct);
        Need(permission.B("allowed"), permission.S("reason"), 403);

    }
    public async Task<JsonObject> CheckUpdate(JsonObject p, string? token = null, CancellationToken ct = default) {

        await NeedInstallAllowed(p, token, ct);
        return await Offer(p, ct);

    }
    // Nothing closes yet: the manager sees what is on offer, what stands in the way and who would be signed out.
    public async Task<JsonObject> LookForUpdate(string token, JsonObject p, CancellationToken ct = default) {

        await NeedInstallAllowed(p, token, ct);
        var reply = await Offer(p, ct);
        var s = await View(ct);
        Authenticate(s, token, out var own);
        reply["blocked"] = UpdateBlock(s);
        reply["otherSessions"] = s.List("session").Where(LiveSession).LongCount(x => x.S("id") != own.S("id"));
        return reply;

    }
    async Task<JsonObject> Offer(JsonObject p, CancellationToken ct) {

        var platform = Choice(p, "platform", "win-x64", "linux-x64", "android-arm64");
        Need(System.Text.RegularExpressions.Regex.IsMatch(p.S("currentVersion"), "^\\d{1,5}\\.\\d{1,5}\\.\\d{1,5}$"), "Versión actual inválida");
        var reply = await Call(HttpMethod.Post, "/branch/v1/updates/check", J.O("platform", platform, "currentVersion", p.S("currentVersion")), ct);
        // Nothing newer: CulinaryServer says which version is published, so the screen tells "up to date" from "newer here".
        if (reply["update"] is not JsonValue) return J.O("update", null, "latestVersion", reply["latestVersion"]?.DeepClone(), "currentVersion", p.S("currentVersion"));
        var update = Jws.VerifyUpdate(reply, floor);
        Need(update.S("siteId") == siteId && update.S("platform") == platform, "La actualización no es para esta sucursal", 403);
        lock (downloads) downloads[update.S("releaseId")] = reply.Obj("download");
        return J.O("update", reply.S("update"), "release", reply.S("release"), "keyFloor", reply.S("keyFloor"), "version", update.S("version"), "notes", update.S("notes"), "bytes", update.L("bytes"));

    }
    public async Task<HttpResponseMessage> DownloadUpdate(JsonObject p, string? token = null, CancellationToken ct = default) {

        await NeedInstallAllowed(p, token, ct);
        JsonObject? download;
        lock (downloads) downloads.TryGetValue(p.S("releaseId"), out download);
        Need(download is not null && DateTimeOffset.TryParse(download.S("expiresAt"), out var expires) && expires > DateTimeOffset.UtcNow, "Busca la actualización de nuevo", 409);
        return await Server.Download(download!.S("path"), download.S("token"), ct);

    }
    // Newest published version per platform. Terminals of another platform read their own line from the state.
    JsonObject releases = [];
    public async Task LookForReleases(CancellationToken ct = default) { var reply = await Call(HttpMethod.Post, "/branch/v1/updates/latest", [], ct); Volatile.Write(ref releases, reply.Obj("releases")); }
    public FaultLog Faults { get; private set; } = new(null);
    public void UseFaults(FaultLog faults) => Faults = faults;
    public async Task SendFaults(CancellationToken ct = default) {

        var batch = Faults.Pending();
        if (batch.Length == 0) return;
        await Call(HttpMethod.Post, "/branch/v1/faults", J.O("faults", batch.Select(x => x.DeepClone()).ToArray()), ct);
        Faults.Delivered(batch);

    }
    // Terminals report through their device credentials, so nobody else on the network can fill the log.
    public async Task<JsonObject> ReportFault(JsonObject p, CancellationToken ct = default) {

        VerifiedDevice(await View(ct), p);
        Faults.Record(Choice(p, "source", "web", "client", "kiosk"), Choice(p, "severity", "error", "critical"), p.S("what"), p.S("affected"), p.S("detail"), p.Obj("context"), p.S("version"), p.S("platform"));
        return J.O("recorded", true);

    }
}
