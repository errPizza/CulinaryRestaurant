using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Api;

public sealed partial class BranchService {
    public async Task<bool> ProcessInventory(CancellationToken ct = default) {

        for (int attempt = 0; attempt < 8; attempt++) {

            var s = await Load(ct);
            if (s.Count == 0) return false;
            var job = s.Related("stockjob", "status", "pending").FirstOrDefault();
            if (job is null) return false;
            var actor = J.O("id", "inventory-worker", "role", "manager");
            var fingerprint = J.Hash(J.Canonical(job));
            var id = J.Hash("stockjob:" + job.S("id") + ":" + fingerprint)[..32];
            Domain.Act(s, actor, "stock.process", J.O("id", job.S("id")));
            try { await Commit(s, id, actor.S("id"), fingerprint, J.O("jobId", job.S("id"), "status", job.S("status")), "stock.worker", ct); return true; } catch (StoreException e) when (e.Code == "COS-DATA-CONFLICT" && attempt < 7) { }

        }
        return false;

    }
}
public sealed partial class BranchService {
    volatile JsonObject? inventoryAlert;
    public async Task<JsonObject> CheckInventory(CancellationToken ct = default) {

        var result = Restaurant.InventoryBalance(await View(ct));
        inventoryAlert = result.B("balanced") ? null : J.O("level", "critical", "message", "Las existencias no cuadran con sus movimientos. Llama a soporte antes de hacer un conteo o un ajuste.");
        return result;

    }
}
public sealed class InventoryBalanceWorker(BranchService branch, ILogger<InventoryBalanceWorker> log): BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken ct) {

        await Task.Yield();
        while (!ct.IsCancellationRequested) {

            try { var result = await branch.CheckInventory(ct); if (!result.B("balanced")) log.LogCritical("COS-INVENTORY-BALANCE: {Result}", result.ToJsonString()); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } catch (Exception e) when (e is BusinessException or StoreException) { log.LogWarning("COS-INVENTORY-BALANCE: {Message}", e.Message); }
            try { await Task.Delay(TimeSpan.FromHours(6), ct); } catch (OperationCanceledException) { break; }

        }

    }
}
public sealed class InventoryWorker(BranchService branch, ILogger<InventoryWorker> log): BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken ct) {

        await Task.Yield();
        while (!ct.IsCancellationRequested) { try { if (await branch.ProcessInventory(ct)) continue; } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } catch { log.LogWarning("Consumo de inventario pendiente; se conserva para recuperación"); } try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; } }

    }
}
