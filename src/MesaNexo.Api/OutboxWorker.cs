using MesaNexo.Core;
namespace MesaNexo.Api;
// Outbox upkeep in small steps the store bounds itself: without a central pending events are kept; a store migrated
// from SQLite that once dropped some queues, when a central appears, the baseline that replaces them. An outbox past
// its size or age mark is reported, never trimmed.
public sealed class OutboxWorker(IBranchStore store, ILogger<OutboxWorker> log): BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken ct) {

        await Task.Yield();
        string? alerted = null;
        while (!ct.IsCancellationRequested) { var more = false; try { var step = await store.CallAsync(J.O("op", "outbox_maintain_v1", "protocol", 1), ct); more = step.B("more"); var alert = step?["alert"]?.GetValue<string>(); if (alert != alerted) { if (alert is not null) log.LogWarning("COS-OUTBOX-{Alert}: {Pending} eventos sin confirmar por la central; no se descarta ninguno", alert.ToUpperInvariant(), step.L("pending")); alerted = alert; } } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } catch { log.LogWarning("Mantenimiento del outbox pendiente; se reintenta"); } try { await Task.Delay(more ? 100 : 30000, ct); } catch (OperationCanceledException) { break; } }

    }
}
