using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;

public sealed partial class BranchService {
    public async Task<JsonObject> UpgradeSchema(CancellationToken ct = default) {

        var s = await Load(ct);
        Need(s.Count > 0, "Inicializa antes de migrar");
        if (s.Meta.L("schemaVersion", 1) >= Contract.Schema) return J.O("schemaVersion", Contract.Schema, "alreadyApplied", true);
        Need(!s.List("visit").Any(v => v.S("status") == "open"), "Cierra cuentas antes de migrar");
        Need(!s.List("session").Any(x => !x.B("revoked") && DateTimeOffset.Parse(x.S("expires")) > DateTimeOffset.UtcNow), "Cierra sesiones y detén el backend antes de migrar");
        Need(!s.List("stockjob").Any(x => x.S("status") == "pending"), "Procesa trabajos pendientes antes de migrar");
        var backup = (await store.CallAsync(J.O("op", "snapshot_v1", "protocol", 1), ct))!.AsObject();
        Need(backup.S("site_id") == siteId && backup.B("encrypted"), "Respaldo no corresponde a la sucursal");
        s.Meta["schemaVersion"] = Contract.Schema;
        s.Meta["schemaBackup"] = backup.DeepClone();
        s.Meta["schemaMigratedAt"] = J.Now();
        return await Commit(s, J.Id(), "local-maintenance", J.Hash(J.Canonical(backup)), J.O("schemaVersion", Contract.Schema, "backup", backup), "schema.upgrade", ct);

    }
}
