using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Api;

// The retention purge of docs/PRIVACIDAD-DATOS.md. It is off until /etc/culinaryos/privacy.json says
// {"purge": {"enabled": true, "days": 90}}: the periods wait for a lawyer's review, and turning it on is the provider's call.
public sealed record PrivacyPurge(int Days) {
    public const string DefaultFile = "/etc/culinaryos/privacy.json";
    public static PrivacyPurge? From(string? file) {

        if (file is null || !File.Exists(file)) return null;
        var purge = JsonNode.Parse(File.ReadAllText(file))?["purge"] as JsonObject;
        if (purge?["enabled"]?.GetValue<bool>() != true) return null;
        var days = purge["days"]?.GetValue<int>() ?? 90;
        Rules.Need(days is >= 30 and <= 3650, "privacy.json: days entre 30 y 3650");
        return new(days);

    }
}
public sealed partial class BranchService {
    // One batch per commit, until nothing older than the period is left.
    public async Task<JsonObject> PurgePersonalData(int days, CancellationToken ct = default) {

        var actor = J.O("id", "privacy-worker", "role", "manager");
        long total = 0;
        for (int attempt = 0, batches = 0; attempt < 8 && batches < 100;) {

            var s = await Load(ct);
            if (s.Count == 0) break;
            var result = Domain.Act(s, actor, "privacy.purge", J.O("days", days));
            if (result.L("purged") == 0) break;
            var fingerprint = J.Hash(J.Canonical(result));
            try { await Commit(s, J.Hash("privacy.purge:" + s.Meta.L("revision") + ":" + fingerprint)[..32], actor.S("id"), fingerprint, result, "privacy.purge", ct); total += result.L("purged"); batches++; attempt = 0; if (!result.B("more")) break; } catch (StoreException e) when (e.Code == "COS-DATA-CONFLICT") { attempt++; }

        }
        return J.O("purged", total, "days", days);

    }
}
public sealed class PrivacyPurgeWorker(BranchService branch, PrivacyPurge purge, ILogger<PrivacyPurgeWorker> log): BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken ct) {

        await Task.Yield();
        while (!ct.IsCancellationRequested) {

            try { var result = await branch.PurgePersonalData(purge.Days, ct); if (result.L("purged") > 0) log.LogWarning("COS-PRIVACY-PURGE: {Purged} registros depurados a {Days} días", result.L("purged"), purge.Days); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } catch (Exception e) when (e is BusinessException or StoreException) { log.LogWarning("COS-PRIVACY-PURGE: {Message}", e.Message); }
            try { await Task.Delay(TimeSpan.FromHours(24), ct); } catch (OperationCanceledException) { break; }

        }

    }
}
