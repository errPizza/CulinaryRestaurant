using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;

// Renews at least daily while CulinaryServer is reachable (the license itself allows 3 days offline), carries
// support tickets both ways and keeps the multi-company central's certificate registered.
public sealed class LicenseWorker(BranchService branch, ILogger<LicenseWorker> log): BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken ct) {

        await Task.Yield();
        var registered = false;
        while (!ct.IsCancellationRequested) {

            var delay = TimeSpan.FromHours(6);
            try {

                if ((await branch.LicenseStatus(ct)).S("state") == "missing") delay = TimeSpan.FromMinutes(1);
                else {

                    await branch.Renew(ct);
                    registered = registered || await branch.RegisterCentral(ct);
                    await branch.SyncSupport(ct);

                }

            } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } catch (Exception e) when (e is ServerRejection or BusinessException or HttpRequestException or TaskCanceledException or StoreException) { delay = TimeSpan.FromMinutes(5); log.LogWarning("CulinaryServer: {Message}", e.Message); }
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }

        }

    }
}
public sealed partial class BranchService {
    // The certificate this branch presents to MesaNexo.Central; the panel lists it for --provision-tenant.
    public async Task<bool> RegisterCentral(CancellationToken ct = default) {

        if (licensing?.CentralCertificate is not { } file || !File.Exists(file)) return false;
        using var certificate = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(file, ct));
        await Call(HttpMethod.Put, "/branch/v1/registry", J.O("centralCertificateSha256", Convert.ToHexStringLower(SHA256.HashData(certificate.RawData))), ct);
        return true;

    }
    // Local tickets are the source of what the branch wrote; CulinaryServer is the source of the provider's replies.
    public async Task SyncSupport(CancellationToken ct = default) {

        var local = (await View(ct)).List("ticket").Select(x => (JsonObject)x.DeepClone()).ToArray();
        var sent = new Dictionary<string, (string RemoteId, string[] Messages)>();
        foreach (var ticket in local) {

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
            foreach (var ticket in s.List("ticket").ToArray()) {

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
    // Terminal updates: authorized by the terminal's device credentials (sessions closed, like CanInstall) and fetched
    // from CulinaryServer with this branch's license. The terminal verifies the signed update again before installing.
    readonly Dictionary<string, JsonObject> downloads = [];
    public async Task<JsonObject> CheckUpdate(JsonObject p, CancellationToken ct = default) {

        Need((await CanInstall(p, ct)).B("allowed"), "Cierra la sesión y todas las cuentas; se requiere una licencia vigente", 403);
        var platform = Choice(p, "platform", "win-x64", "linux-x64", "android-arm64");
        Need(System.Text.RegularExpressions.Regex.IsMatch(p.S("currentVersion"), "^\\d{1,5}\\.\\d{1,5}\\.\\d{1,5}$"), "Versión actual inválida");
        var reply = await Call(HttpMethod.Post, "/branch/v1/updates/check", J.O("platform", platform, "currentVersion", p.S("currentVersion")), ct);
        if (reply["update"] is not JsonValue) return J.O("update", null);
        var update = Jws.Verify(reply.S("update"), Jws.Update);
        Need(update.S("siteId") == siteId && update.S("platform") == platform, "La actualización no es para esta sucursal", 403);
        lock (downloads) downloads[update.S("releaseId")] = reply.Obj("download");
        return J.O("update", reply.S("update"), "version", update.S("version"), "notes", update.S("notes"), "bytes", update.L("bytes"));

    }
    public async Task<HttpResponseMessage> DownloadUpdate(JsonObject p, CancellationToken ct = default) {

        Need((await CanInstall(p, ct)).B("allowed"), "Cierra la sesión y todas las cuentas; se requiere una licencia vigente", 403);
        JsonObject? download;
        lock (downloads) downloads.TryGetValue(p.S("releaseId"), out download);
        Need(download is not null && DateTimeOffset.TryParse(download.S("expiresAt"), out var expires) && expires > DateTimeOffset.UtcNow, "Busca la actualización de nuevo", 409);
        return await Server.Download(download!.S("path"), download.S("token"), ct);

    }
}
