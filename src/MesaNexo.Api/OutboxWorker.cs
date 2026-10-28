using MesaNexo.Core;
namespace MesaNexo.Api;
// Outbox upkeep in small steps the store bounds itself: without a central pending events are kept; a store migrated
// from SQLite that once dropped some queues, when a central appears, the baseline that replaces them.
public sealed class OutboxWorker(IBranchStore store, ILogger<OutboxWorker> log): BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken ct) {

        await Task.Yield();
        while (!ct.IsCancellationRequested) { var more = false; try { more = (await store.CallAsync(J.O("op", "outbox_maintain_v1", "protocol", 1), ct)).B("more"); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } catch { log.LogWarning("Mantenimiento del outbox pendiente; se reintenta"); } try { await Task.Delay(more ? 100 : 30000, ct); } catch (OperationCanceledException) { break; } }

    }
}
