using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;

public sealed record PrintOutcome(string Status, string Evidence);
public interface IPrintDriver {
    string Name { get; }
    Task Preflight(JsonObject job, CancellationToken ct);
    Task<PrintOutcome> Submit(JsonObject job, CancellationToken ct);
}
// Uses the configured CUPS driver, never arbitrary RJ11/network commands or a shell.
public sealed class CupsPrintDriver(string queue, string? drawerOption = null): IPrintDriver {
    public string Name => "CUPS:" + queue;
    public Task Preflight(JsonObject job, CancellationToken ct) {

        Need(OperatingSystem.IsLinux() && File.Exists("/usr/bin/lp"), "Driver CUPS no disponible", 503);
        Need(Regex.IsMatch(queue, "^[A-Za-z0-9_.-]{1,100}$"), "Cola de impresión inválida");
        if (job.S("type") == "drawer") Need(drawerOption is not null && Regex.IsMatch(drawerOption, "^[A-Za-z0-9_.-]+=[A-Za-z0-9_.-]+$"), "Configura y verifica la opción de cajonera de este driver");
        return Task.CompletedTask;

    }
    public async Task<PrintOutcome> Submit(JsonObject job, CancellationToken ct) {

        await Preflight(job, ct);
        var start = new ProcessStartInfo("/usr/bin/lp") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-d");
        start.ArgumentList.Add(queue);
        if (job.S("type") == "drawer") { start.ArgumentList.Add("-o"); start.ArgumentList.Add(drawerOption!); }
        start.ArgumentList.Add("-t");
        start.ArgumentList.Add("Culinary Restaurant " + job.S("id"));
        using var process = Process.Start(start) ?? throw new IOException("No se inició CUPS");
        var text = new StringBuilder("Culinary Restaurant\n");
        foreach (var unit in job.Obj("snapshot").A("units").Objects()) text.AppendLine(unit.S("name") + " · " + (unit.L("price") / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        text.AppendLine("Total: " + (job.Obj("snapshot").Obj("totals").L("total") / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        if (job.S("invoiceUrl") != "") text.AppendLine("Factura en: " + job.S("invoiceUrl"));
        await process.StandardInput.WriteAsync(text.ToString().AsMemory(), ct);
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(timeout.Token);
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        return new(process.ExitCode == 0 ? "submitted" : "failed", process.ExitCode == 0 ? "Spooler aceptó; no comprueba papel/cajonera. " + output[..Math.Min(output.Length, 1000)] : "Driver devolvió fallo; revisar dispositivo antes de reintentar");

    }
}
public sealed partial class BranchService {
    public async Task<bool> ProcessPrint(IPrintDriver driver, CancellationToken ct = default) {

        // Oldest first, so tickets come out in the order they were asked for.
        var s = await Load(ct);
        if (!Domain.IsLive(s, "printer")) return false;
        var job = s.Related("printjob", "status", "pending").OrderBy(x => x.S("at"), StringComparer.Ordinal).FirstOrDefault();
        if (job is null) return false;
        await driver.Preflight(job, ct);
        var document = Document(s, job);
        var id = job.S("id");
        var attemptId = J.Id();
        job["status"] = "uncertain";
        job["attemptId"] = attemptId;
        job["driver"] = driver.Name;
        job["claimedAt"] = J.Now();
        try { await Commit(s, J.Id(), "authorized-print-service", J.Hash(id + attemptId), J.O("id", id, "attemptId", attemptId), "print.driver.claim", ct); } catch (StoreException e) when (e.Code == "COS-DATA-CONFLICT") { return false; }
        PrintOutcome outcome;
        try { outcome = await driver.Submit(document, ct); } catch { outcome = new("uncertain", "Resultado físico no comprobado. Conciliar antes de reintentar."); }
        for (int retry = 0; retry < 8; retry++) { s = await Load(ct); job = s.Get("printjob", id); Need(job.S("attemptId") == attemptId, "Intento físico cambió", 409); job["status"] = outcome.Status; job["evidence"] = outcome.Evidence; try { await Commit(s, J.Id(), "authorized-print-service", J.Hash(id + attemptId), J.O("id", id, "status", outcome.Status), "print.driver.result", ct); return true; } catch (StoreException e) when (e.Code == "COS-DATA-CONFLICT" && retry < 7) { } }
        return false;

    }
    // The job plus the names it only refers to by id, read when it is printed. A settled ticket's self-invoice token is
    // issued here, so it exists only on the paper and as a hash in the store; it is committed with the claim.
    JsonObject Document(BranchState s, JsonObject job) {

        var d = (JsonObject)job.DeepClone();
        d["restaurantName"] = s.Meta.S("name");
        if (job.S("visitId") != "" && s.Related("visit", "id", job.S("visitId")).FirstOrDefault() is { } v) {

            if (v.S("tableId") != "") d["tableName"] = s.Related("table", "id", v.S("tableId")).FirstOrDefault()?.S("name") ?? "";
            d["waiterName"] = s.Related("user", "id", v.S("waiterId")).FirstOrDefault()?.S("name") ?? "";
            if (job.B("invoiceQr") && Domain.InvoiceLink(s, v) is { } link) { d["invoiceUrl"] = link.S("url"); d["invoiceExpires"] = link.S("expires"); }

        }
        if (job.S("zreportId") != "") d["zreport"] = s.Get("zreport", job.S("zreportId")).DeepClone();
        return d;

    }
    public string PrinterState { get; private set; } = "";
    public void NotePrinter(string state) => PrinterState = state;
}
// Prints pending jobs as they appear. A job the printer cannot take now (no paper, cover open, unreachable) stays
// pending and is tried again; a job claimed and then interrupted is left for a manager, never printed twice by itself.
public sealed class PrintWorker(BranchService branch, IPrintDriver driver, ILogger<PrintWorker> log): BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken ct) {

        await Task.Yield();
        var reported = "";
        while (!ct.IsCancellationRequested) {

            var delay = 1000;
            try { if (await branch.ProcessPrint(driver, ct)) { delay = 0; reported = ""; } } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } catch (BusinessException e) { delay = 5000; if (e.Message != reported) log.LogWarning("Impresión en espera: {Reason}", e.Message); reported = e.Message; } catch (Exception) { delay = 5000; log.LogWarning("Impresión pendiente; se reintenta"); }
            branch.NotePrinter(driver is EscPosPrintDriver escpos && escpos.Last is { } status ? status.Describe() : "configurada");
            if (delay > 0) try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
            
        }

    }
}
