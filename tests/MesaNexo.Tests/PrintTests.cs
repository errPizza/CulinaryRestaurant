using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
using Microsoft.Extensions.Logging.Abstractions;
namespace MesaNexo.Tests;
// A network receipt printer for tests: it listens on a RAW port, answers DLE EOT 1/2/4 like an Epson TM and keeps
// the data of every connection. Paper, cover and offline flags change its answers; Silent never answers status;
// DropAfter resets the connection once that many data bytes arrived, as a printer that loses power mid-ticket.
sealed class SimulatedPrinter: IDisposable {
    readonly TcpListener listener = new(IPAddress.Loopback, 0); readonly CancellationTokenSource stop = new(); readonly List<byte[]> printed = [];
    public volatile bool Paper = true, CoverOpen, Offline, Silent; public int? DropAfter;
    public string Uri => "tcp://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
    public byte[][] Printed { get { lock (printed) return printed.ToArray(); } }
    public SimulatedPrinter() { listener.Start(); _ = Accept(); }
    async Task Accept() { while (!stop.IsCancellationRequested) { TcpClient client; try { client = await listener.AcceptTcpClientAsync(stop.Token); } catch { return; } _ = Serve(client); } }
    byte Reply(byte n) => n switch { 1 => (byte)(0x12 | (Offline ? 0x08 : 0)), 2 => (byte)(0x12 | (CoverOpen ? 0x04 : 0) | (Paper ? 0 : 0x20)), 4 => (byte)(0x12 | (Paper ? 0 : 0x60)), _ => 0x12 };
    async Task Serve(TcpClient client) {

        var data = new List<byte>();
        var request = new List<byte>();
        var buffer = new byte[4096];
        var stream = client.GetStream();
        try {

            while (true) {

                var n = await stream.ReadAsync(buffer, stop.Token);
                if (n == 0) break;
                var reset = false;
                for (var i = 0; i < n && !reset; i++) {

                    request.Add(buffer[i]);
                    if (request.Count == 1 && request[0] != 0x10 || request.Count == 2 && request[1] != 0x04) { data.AddRange(request); request.Clear(); } else if (request.Count == 3) { if (!Silent) await stream.WriteAsync(new[] { Reply(request[2]) }, stop.Token); request.Clear(); }
                    reset = DropAfter is int limit && data.Count >= limit;

                }
                if (reset) { client.LingerState = new LingerOption(true, 0); break; }

            }

        } catch (Exception) { } finally { client.Dispose(); if (data.Count > 0) lock (printed) printed.Add(data.ToArray()); }

    }
    public void Dispose() { stop.Cancel(); listener.Stop(); }
}
// Reads back what a printer received: the text in code page 850 with the ESC/POS commands removed.
static class EscPosReader {
    static EscPosReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public static string Text(byte[] d) {

        var text = new List<byte>();
        for (var i = 0; i < d.Length;) {

            if (d[i] == 0x1B && i + 1 < d.Length) { i += d[i + 1] switch { 0x40 => 2, 0x74 or 0x61 or 0x45 => 3, 0x70 => 5, _ => 2 }; continue; }
            if (d[i] == 0x1D && i + 1 < d.Length) { i += d[i + 1] switch { 0x21 => 3, 0x56 => 4, _ => 2 }; continue; }
            text.Add(d[i]);
            i++;

        }
        return Encoding.GetEncoding(850).GetString(text.ToArray());

    }
    public static bool Has(byte[] d, params byte[] sequence) => Enumerable.Range(0, Math.Max(0, d.Length - sequence.Length + 1)).Any(i => d.AsSpan(i, sequence.Length).SequenceEqual(sequence));
}
static class PrintTests {
    static readonly byte[] Cut = [0x1D, 0x56, 0x42, 0x03], Pulse = [0x1B, 0x70, 0x00, 0x19, 0xFA];
    public static async Task Run(string root, string python, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); evidence.Add(J.O("test", name, "result", "passed", "printer", "simulated ESC/POS over TCP; no hardware")); Console.WriteLine("PASS " + name); }
        async Task Deny(Func<Task> action, string contains, string name) { try { await action(); throw new Exception("Expected rejection: " + name); } catch (BusinessException e) { Check(e.Message.Contains(contains), name); } }
        static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
        var folder = Path.Combine(root, ".lab", "print-" + J.Id());
        var password = "Test-" + J.Id();
        var enrollment = J.Id();
        using var runtime = new RuntimeStore(python, Path.Combine(root, "runtime", "lab_host.py"), folder, "print-site");
        var service = new BranchService(runtime, "print-site", new Passwords());
        var live = new IntegrationConfig();
        live.Ready("printer");
        service.UseIntegrations(live);
        await TestLicense.Initialize(service, J.O("siteId", "print-site", "name", "Café La Niña", "mode", "licencia", "managerName", "Gerencia", "managerUsername", "manager", "managerPassword", password, "enrollmentCode", enrollment, "modules", Restaurant.Modules));
        var token = (await service.Login(J.O("siteId", "print-site", "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "print test", "username", "manager", "password", password, "enrollmentCode", enrollment))).S("token");
        async Task<JsonObject> Act(string type, JsonObject p) => (await service.Action(token, J.O("operationId", J.Id(), "type", type, "payload", p))).Obj("result");
        var waiter = await Act("user.save", J.O("name", "Mesero Ñuño", "username", "waiter", "role", "mesero", "password", password));
        await Act("catalog.category", J.O("name", "Bebidas"));
        var cafe = await Act("catalog.product", J.O("name", "Café de olla", "price", 4500, "category", "Bebidas", "station", "Barra"));
        var pina = await Act("catalog.product", J.O("name", "Piña colada", "price", 9000, "category", "Bebidas", "station", "Barra"));
        var table = await Act("table.save", J.O("name", "Mesa 01", "capacity", 4, "zone", "Salón"));
        async Task<string> Bill(string? tableId = null) {

            var visit = tableId is null ? await Act("visit.quick", new()) : await Act("visit.open", J.O("tableId", tableId, "people", 2, "waiterId", waiter.S("id")));
            await Act("command.send", J.O("visitId", visit.S("id"), "items", new[] { J.O("productId", cafe.S("id"), "quantity", 2), J.O("productId", pina.S("id"), "quantity", 1) }));
            return (await Act("visit.issueBill", J.O("id", visit.S("id")))).S("id");

        }
        async Task<JsonObject> Job(string id) => (await service.Load()).Get("printjob", id);

        // The document itself, without any printer.
        var zreport = EscPosDocument.Render(J.O("type", "zreport", "restaurantName", "Café La Niña", "zreport", J.O("number", 7, "digest", "0123456789abcdef0123", "summary", J.O("from", "2026-10-22T14:00:00Z", "to", "2026-10-23T05:00:00Z", "tickets", 12, "people", 30, "gross", 123450, "discounts", 0, "net", 123450, "tax", 17027, "average", 10287, "payments", 123450, "tips", 5000, "methods", J.O("cash", J.O("amount", 80000, "tips", 0, "count", 2), "card", J.O("amount", 43450, "tips", 5000, "count", 10)), "voidedCount", 1, "voidedAmount", 4500, "waiters", new[] { J.O("name", "Mesero Ñuño", "tickets", 12, "amount", 123450) }, "categories", new[] { J.O("name", "Bebidas", "quantity", 40, "amount", 123450) }))), 48);
        var zText = EscPosReader.Text(zreport);
        Check(zText.Contains("CORTE Z No. 7") && zText.Contains("Efectivo (2)") && zText.Contains("$1,234.50") && zText.Contains("Mesero Ñuño (12)") && EscPosReader.Has(zreport, Cut), "Z report prints period, sales, payment methods and waiters, then cuts");
        var longName = EscPosDocument.Render(J.O("type", "bill", "restaurantName", "Café La Niña", "visitId", "abcdef0123456789", "at", "2026-10-22T20:00:00Z", "snapshot", J.O("totals", J.O("gross", 30000, "discount", 0, "total", 30000, "paid", 0, "balance", 30000, "tax", 4138), "units", new[] { J.O("name", "Hamburguesa doble con tocino, queso gouda, cebolla caramelizada y papas gajo", "price", 30000, "status", "delivered") })), 48);
        Check(EscPosReader.Text(longName).Split('\n').All(line => line.Length <= 48), "long product names wrap within 48 columns of 80 mm paper");
        Check(EscPosReader.Has(longName, 0x1B, 0x40) && EscPosReader.Has(longName, 0x1B, 0x74, 0x02) && EscPosReader.Has(zreport, 0xA4), "documents reset the printer, select code page 850 and encode ñ for it");

        using var printer = new SimulatedPrinter();
        var driver = new EscPosPrintDriver(new PrinterOptions(printer.Uri));
        var first = await Bill(table.S("id"));
        Check(await service.ProcessPrint(driver), "pending bill is printed");
        var bill = printer.Printed.Single();
        var text = EscPosReader.Text(bill);
        Check(text.Contains("Café La Niña") && text.Contains("Mesa 01") && text.Contains("Mesero Ñuño") && text.Contains("2 x Café de olla") && text.Contains("$90.00") && text.Contains("1 x Piña colada") && text.Contains("TOTAL") && text.Contains("$180.00") && text.Contains("IVA incluido"), "bill shows restaurant, table, waiter, grouped lines and totals");
        Check(EscPosReader.Has(bill, Cut) && !EscPosReader.Has(bill, Pulse), "bill cuts the paper and does not open the drawer");
        var done = await Job(first);
        Check(done.S("status") == "submitted" && done.S("evidence").Contains("lista") && done.S("driver").StartsWith("ESC/POS"), "printed job records the driver and the printer status as evidence");
        Check(!await service.ProcessPrint(driver) && printer.Printed.Length == 1, "a printed job is never printed again");

        printer.Paper = false;
        var noPaper = await Bill();
        await Deny(() => service.ProcessPrint(driver), "sin papel", "printer without paper is reported before claiming the job");
        Check((await Job(noPaper)).S("status") == "pending" && printer.Printed.Length == 1, "a job the printer cannot take stays pending and untouched");
        printer.Paper = true;
        Check(await service.ProcessPrint(driver) && printer.Printed.Length == 2, "the pending job prints once paper is back");
        printer.CoverOpen = true;
        var coverOpen = await Bill();
        await Deny(() => service.ProcessPrint(driver), "tapa abierta", "open cover holds printing");
        printer.CoverOpen = false;
        Check(await service.ProcessPrint(driver) && (await Job(coverOpen)).S("status") == "submitted", "job held by the open cover prints after closing it");

        printer.Paper = false;
        var drawer = await Act("drawer.request", J.O("reason", "Cambio de fondo"));
        Check(await service.ProcessPrint(driver), "drawer opens even without paper");
        var kick = printer.Printed.Last();
        Check(EscPosReader.Has(kick, Pulse) && !EscPosReader.Has(kick, Cut) && EscPosReader.Text(kick).Trim() == "" && (await Job(drawer.S("id"))).S("status") == "submitted", "drawer job sends only the pulse: no paper, no cut");
        printer.Paper = true;

        printer.DropAfter = 20;
        var dropped = await Bill();
        var before = printer.Printed.Length;
        Check(await service.ProcessPrint(driver), "a job interrupted mid-ticket is still recorded");
        Check((await Job(dropped)).S("status") == "uncertain" && (await Job(dropped)).S("evidence").Contains("revisar"), "an interrupted ticket is uncertain, for a manager to check the paper");
        printer.DropAfter = null;
        Check(!await service.ProcessPrint(driver) && printer.Printed.Length == before + 1, "an uncertain ticket is never reprinted by itself");
        var retry = await Act("print.retry", J.O("id", dropped, "reason", "Salió cortado"));
        Check(await service.ProcessPrint(driver) && EscPosReader.Text(printer.Printed.Last()).Contains("REIMPRESIÓN") && (await Job(retry.S("id"))).S("status") == "submitted", "a manager's retry prints once more, marked as a reprint");

        var dead = new EscPosPrintDriver(new PrinterOptions("tcp://127.0.0.1:" + FreePort()));
        var offline = await Bill();
        await Deny(() => service.ProcessPrint(dead), "sin conexión", "an unreachable printer leaves the job pending");
        Check((await Job(offline)).S("status") == "pending" && await service.ProcessPrint(driver), "the job prints when a printer answers again");

        printer.Silent = true;
        var silent = await Bill();
        Check(await service.ProcessPrint(driver) && (await Job(silent)).S("evidence").Contains("sin estado"), "a printer without real-time status still prints and says the status was not confirmed");
        printer.Silent = false;

        var device = Path.Combine(folder, "lp0");
        File.WriteAllBytes(device, []);
        var usb = new EscPosPrintDriver(new PrinterOptions(new Uri(device).AbsoluteUri, 32));
        await Act("drawer.request", J.O("reason", "USB"));
        Check(await service.ProcessPrint(usb) && EscPosReader.Has(File.ReadAllBytes(device), Pulse), "a USB device path receives the same ESC/POS bytes");

        // The service a branch runs: it prints what appears and reports the printer in /api/state.
        var worker = new PrintWorker(service, driver, NullLogger<PrintWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try {

            var count = printer.Printed.Length;
            await Bill(null);
            for (var i = 0; i < 100 && printer.Printed.Length == count; i++) await Task.Delay(100);
            Check(printer.Printed.Length == count + 1, "print service prints new jobs without being asked");
            // The printer has the bytes before the service records the result and notes the printer state.
            string reported = "";
            for (var i = 0; i < 50 && reported != "lista"; i++) { reported = (await service.State(token)).Obj("connection").S("printer"); if (reported != "lista") await Task.Delay(100); }
            Check(reported == "lista", "printer status reaches /api/state");

        } finally { await worker.StopAsync(CancellationToken.None); }

    }
}
