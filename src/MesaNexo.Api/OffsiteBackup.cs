using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Api;

// Keeps the 15-minute recovery point: a pg_dump of the branch sent to its own bucket. The rows in it stay sealed under the
// keyring, which the bucket never sees; only record ids, sizes and times are readable there. culinaryos-restore takes the
// file as it is.
public sealed class OffsiteBackupWorker(BranchService branch, PostgresBranchStore store, ObjectBucket bucket, string directory, ILogger<OffsiteBackupWorker> log): BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken ct) {

        await Task.Yield();
        while (!ct.IsCancellationRequested) {

            try { await branch.PublishBackup(store, bucket, directory, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or StoreException) { log.LogWarning("COS-OFFSITE-BACKUP: {Message}", e.Message); }
            try { await Task.Delay(TimeSpan.FromMinutes(15), ct); } catch (OperationCanceledException) { break; }

        }

    }
}
public sealed partial class BranchService {
    long backedUpSequence; DateTimeOffset? backupFailing;
    public async Task<bool> PublishBackup(PostgresBranchStore store, ObjectBucket bucket, string directory, CancellationToken ct = default) {

        var sequence = (await View(ct)).Meta.L("auditSequence");
        // A closed restaurant changes nothing, and uploads nothing.
        if (sequence == 0 || sequence == backedUpSequence) return false;
        var folder = Path.Combine(directory, "offsite");
        Directory.CreateDirectory(folder);
        var name = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + sequence + ".dump";
        var file = Path.Combine(folder, name);
        try {

            await store.Dump(file, ct);
            string hash;
            await using (var input = File.OpenRead(file)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
            var key = siteId + "/backups/" + name;
            await bucket.PutFile(key, file, ct);
            await bucket.Put(key + ".sha256", Encoding.ASCII.GetBytes(hash + "  " + name + "\n"), ct);
            backedUpSequence = sequence;
            backupFailing = null;
            return true;

        } catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { backupFailing ??= DateTimeOffset.UtcNow; throw; } finally { if (File.Exists(file)) File.Delete(file); }

    }
    // One failed upload is the next one's job; an hour of them means the recovery point is slipping.
    public JsonObject? BackupAlert(DateTimeOffset now) => backupFailing is { } since && now - since >= TimeSpan.FromHours(1) ? J.O("level", now - since >= TimeSpan.FromHours(4) ? "critical" : "notice", "message", "La copia externa de la sucursal no se ha podido enviar desde hace más de " + (int)(now - since).TotalHours + " h. Revisa el internet del servidor; si sigue, llama a soporte.") : null;
    string? healthFile; JsonArray healthAlerts = []; DateTimeOffset healthRead = DateTimeOffset.MinValue;
    public void UseServerHealth(string file) { healthFile = file; healthRead = DateTimeOffset.MinValue; }
    static readonly string[] WatchedChecks = ["COS-DISK", "COS-READONLY", "COS-SMART", "COS-TEMPERATURE", "COS-SERVICES", "COS-BACKUP"];
    // What the machine says about itself, from culinaryos-health. A report that stopped arriving is itself an alert.
    public static JsonArray ReadServerHealth(string? file, DateTimeOffset now) {

        var alerts = new JsonArray();
        if (file is null || !File.Exists(file)) return alerts;
        try {

            var status = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            if (status["generated_at"]?.GetValue<double>() is not { } at || now.ToUnixTimeSeconds() - at > 900) { alerts.Add(J.O("level", "notice", "message", "El diagnóstico del servidor dejó de actualizarse. Llama a soporte.")); return alerts; }
            foreach (var check in status.A("checks").Objects().Where(x => WatchedChecks.Contains(x.S("id")) && x.S("gravedad") is "aviso" or "critico")) alerts.Add(J.O("level", check.S("gravedad") == "critico" ? "critical" : "notice", "message", check.S("parte") + ": " + check.S("mensaje") + " " + check.S("accion")));

        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { alerts.Add(J.O("level", "notice", "message", "No se pudo leer el diagnóstico del servidor. Llama a soporte.")); }
        return alerts;

    }
    // The outbox as the manager reads it. Nothing in it is dropped; these say when a person has to act.
    public static JsonObject? OutboxAlert(JsonObject status) => status["alert"]?.GetValue<string>() switch {
        "blocked" => J.O("level", "critical", "message", "Sincronización detenida: la central rechaza un evento (" + status.Obj("head").S("error") + ") tras " + status.Obj("head").L("attempts") + " intentos y los " + status.L("pending") + " pendientes esperan detrás de él. No se pierde nada; llama a soporte para revisarlo."),
        "auth" => J.O("level", "critical", "message", "La central no acepta el certificado de la sucursal. Los eventos se guardan y se enviarán al resolverlo; llama a soporte."),
        "held" => J.O("level", "notice", "message", status.L("held") + " evento(s) apartado(s) por soporte siguen sin llegar a la central. Se conservan hasta que soporte los libere."),
        "size" => J.O("level", "notice", "message", "Hay " + status.L("pending") + " eventos sin enviar a la central y ocupan mucho espacio. Revisa el internet del servidor."),
        "age" => J.O("level", "notice", "message", "La central lleva más de un día sin confirmar eventos. Revisa el internet del servidor; si sigue, llama a soporte."),
        _ => null
    };
    JsonArray ServerAlerts() {

        var now = DateTimeOffset.UtcNow;
        // State is polled every few seconds; the report only changes every two minutes.
        if (now - healthRead > TimeSpan.FromMinutes(1)) { healthAlerts = ReadServerHealth(healthFile, now); healthRead = now; }
        var alerts = (JsonArray)healthAlerts.DeepClone();
        if (BackupAlert(now) is { } backup) alerts.Add(backup);
        if (inventoryAlert is { } inventory) alerts.Add(inventory.DeepClone());
        return alerts;

    }
}
