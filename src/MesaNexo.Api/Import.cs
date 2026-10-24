using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;

public sealed partial class BranchService {
    public async Task<JsonObject> ImportPlan(string planFile, string expectedSha256, CancellationToken ct = default) {

        var raw = await File.ReadAllTextAsync(planFile, ct);
        var sha = J.Hash(raw);
        Need(sha.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase), "La huella del plan no coincide");
        var plan = JsonNode.Parse(raw)!.AsObject();
        Need(plan.S("format") == "mesa-nexo-import-v1" && plan.S("siteId") == siteId, "Plan o sucursal incompatibles");
        var all = plan.A("records").Objects().ToArray();
        Need(all.Length > 0, "Plan vacío");
        var allowed = new[] { "table", "product", "category", "user", "visit", "command", "unit", "notification", "request", "payment", "reservation", "review", "cash", "movement", "promotion", "audit", "ticket", "grant" };
        var unique = new HashSet<string>();
        foreach (var record in all) { Need(allowed.Contains(record.S("kind")), "Tipo de registro no permitido"); Need(System.Text.RegularExpressions.Regex.IsMatch(record.S("id"), "^[A-Za-z0-9_-]{1,60}$"), "ID inválido"); Need(unique.Add(BranchState.Key(record.S("kind"), record.S("id"))), "Registro duplicado"); Need(!record.ContainsKey("password") && !record.ContainsKey("tokenHash") && !record.ContainsKey("credentialHash"), "El plan no puede importar credenciales"); if (record.S("kind") == "user") Need(!record.B("active"), "Los usuarios migrados deben estar desactivados"); }
        var s = await Load(ct);
        var resuming = s.Meta.S("migrationSha256") == sha && s.Meta.S("status") == "maintenance";
        if (!resuming) { Need(!s.List("visit").Any() && !s.List("table").Any() && !s.List("product").Any() && !s.List("session").Any(), "Importa solamente en una sucursal inicializada sin operación ni sesiones"); s.Meta["status"] = "maintenance"; s.Meta["migrationSha256"] = sha; s.Meta["migrationImported"] = 0; await Commit(s, J.Hash(sha + "start")[..32], "local-migration", sha, J.O("maintenance", true), "migration.start", ct); }
        var offset = (await Load(ct)).Meta.I("migrationImported");
        while (offset < all.Length) {

            s = await Load(ct);
            var count = 0;
            var bytes = 0;
            while (offset + count < all.Length && count < 50) { var row = all[offset + count]; var len = System.Text.Encoding.UTF8.GetByteCount(row.ToJsonString()); if (count > 0 && bytes + len > 32 * 1024) break; Need(len < 40 * 1024, "Registro demasiado grande"); var copy = (JsonObject)row.DeepClone(); copy["siteId"] = siteId; var key = BranchState.Key(copy.S("kind"), copy.S("id")); Need(!s.Contains(key), "El plan colisiona con un registro existente"); s.Add(copy.S("kind"), copy); count++; bytes += len; }
            s.Meta["migrationImported"] = offset + count;
            await Commit(s, J.Hash(sha + "batch" + offset)[..32], "local-migration", sha, J.O("imported", offset + count), "migration.batch", ct);
            offset += count;

        }
        s = await Load(ct);
        foreach (var unit in s.List("unit")) { s.Get("visit", unit.S("visitId")); s.Get("command", unit.S("commandId")); }
        foreach (var command in s.List("command"))
        foreach (var uid in command.A("unitIds").Strings()) s.Get("unit", uid);
        foreach (var payment in s.List("payment")) { s.Get("visit", payment.S("visitId")); s.Get("cash", payment.S("cashId")); }
        foreach (var v in s.List("visit")) { s.Get("table", v.S("tableId")); Need(Domain.Totals(s, v).L("balance") >= 0, "Saldo negativo: la sucursal permanece en mantenimiento"); }
        s.Meta["status"] = "active";
        s.Meta["migrationCompletedAt"] = J.Now();
        return await Commit(s, J.Hash(sha + "complete")[..32], "local-migration", sha, J.O("imported", offset, "siteId", siteId, "validated", true), "migration.complete", ct);

    }
}