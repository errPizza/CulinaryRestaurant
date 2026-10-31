using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MesaNexo.Core;
using MesaNexo.Integrations;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;
// terminal.json (example in deploy/integrations/terminal.example.json):
//   "mercadopago": {accessToken}, "clip": {apiKey, secretKey}, and
//   "terminals": [{id, name, provider: "mercadopago" | "clip" | "bbva", deviceId}]
// deviceId is the Mercado Pago terminal id (from /terminals/v1/list, in PDV mode) or the Clip reader's serial number. BBVA has no
// public API: its terminals are charged on the device and recorded with the voucher's authorization (terminal.manual).
public sealed class TerminalService(MercadoPagoPoint? mercadoPago, ClipPinpad? clip, Dictionary<string, JsonObject> devices) {
    public JsonArray Catalog => new(devices.Values.Select(t => { var c = Capabilities(t.S("provider")); return (JsonNode)J.O("id", t.S("id"), "name", t.S("name"), "provider", t.S("provider"), "manual", t.S("provider") == "bbva", "capabilities", J.O("idempotent", c.Idempotent, "lookup", c.Lookup, "reverse", c.Reverse)); }).ToArray());
    // BBVA is charged on the device: nothing can be asked of it, and the voucher is the evidence.
    public static TerminalCapabilities Capabilities(string provider) => provider switch { "mercadopago" => MercadoPagoPoint.Capabilities, "clip" => ClipPinpad.Capabilities, _ => new(false, false, false) };
    public bool Connected => devices.Values.Any(t => t.S("provider") != "bbva");
    public static TerminalService From(JsonObject o) {

        var devices = new Dictionary<string, JsonObject>();
        foreach (var t in o.A("terminals").Objects()) {

            var id = Text(t, "id", 40);
            Need(Regex.IsMatch(id, "^[a-z0-9-]{1,40}$"), "terminals[].id: letras minúsculas, números y guiones");
            Need(!devices.ContainsKey(id), "Terminal repetida: " + id);
            var provider = Choice(t, "provider", "mercadopago", "clip", "bbva");
            var device = J.O("id", id, "name", Text(t, "name", 60), "provider", provider);
            if (provider != "bbva") device["deviceId"] = Text(t, "deviceId", 80);
            devices[id] = device;

        }
        Need(devices.Count > 0, "Agrega al menos una terminal en terminals");
        MercadoPagoPoint? mp = null;
        ClipPinpad? clip = null;
        if (devices.Values.Any(t => t.S("provider") == "mercadopago")) mp = new(Http(), Text(o.Obj("mercadopago"), "accessToken", 300));
        if (devices.Values.Any(t => t.S("provider") == "clip")) { var c = o.Obj("clip"); clip = new(Http(), Text(c, "apiKey", 200), Text(c, "secretKey", 200)); }
        return new(mp, clip, devices);

    }
    static HttpClient Http() => new(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(10) }) { Timeout = TimeSpan.FromSeconds(20) };
    static readonly string[] Final = ["approved", "declined", "cancelled", "expired", "refunded", "refund-failed", "refund-manual"];
    static JobResult Result(JsonObject txn, TerminalAnswer a, string? status = null) {

        var fields = new List<(string, object?)> { ("message", a.Message) };
        if (a.ProviderRef != "") fields.Add(("providerRef", a.ProviderRef));
        if (a.Brand != "") fields.Add(("brand", a.Brand));
        if (a.Last4 != "") fields.Add(("last4", a.Last4));
        if (a.Auth != "") fields.Add(("authCode", a.Auth));
        status ??= a.Status;
        // Only an outcome is stored: while a charge waits, its polls add nothing.
        if (a.Raw != "" && status != "waiting" && status != "cancel-requested") fields.Add(("raw", a.Raw));
        return JobResult.Of(status, Final.Contains(status) || status == "uncertain" ? txn.S("terminalName") + " · " + a.Message : "", txn.S("reference"), fields.ToArray());

    }
    JsonObject Device(JsonObject txn) => devices.GetValueOrDefault(txn.S("terminalId")) ?? throw new BusinessException(400, "La terminal " + txn.S("terminalId") + " ya no está en terminal.json");
    public async Task<JobResult> Send(BranchState s, JsonObject txn, CancellationToken ct) {

        var device = Device(txn);
        var answer = txn.S("provider") switch {
            "mercadopago" => await mercadoPago!.Charge(txn.S("id"), txn.S("reference"), txn.L("amount") + txn.L("tip"), device.S("deviceId"), ct),
            "clip" => await clip!.Charge(txn.S("reference"), txn.L("amount"), txn.L("tip"), device.S("deviceId"), ct),
            _ => throw new BusinessException(400, "Esta terminal se registra con el voucher")
        };
        return Result(txn, answer);

    }
    // Mercado Pago keys the creation by the charge's id, so asking again returns the order that was created, if any. Clip has no
    // such key: without its request id the charge stays uncertain until someone checks the terminal.
    public async Task<JobResult?> Recover(BranchState s, JsonObject txn, CancellationToken ct) => Capabilities(txn.S("provider")).Idempotent ? await Send(s, txn, ct) : null;
    // A charge on the device is asked about until it ends; a cancellation asked from the POS is sent once, then followed.
    public async Task<JobResult?> Consult(BranchState s, JsonObject txn, CancellationToken ct) {

        var reference = txn.S("providerRef");
        if (reference == "") return null;
        var cancel = txn.S("status") == "cancel-requested";
        TerminalAnswer answer;
        if (cancel && !txn.B("cancelSent")) answer = txn.S("provider") == "mercadopago" ? await mercadoPago!.Cancel(reference, txn.S("id"), ct) : await clip!.Cancel(reference, ct);
        else answer = txn.S("provider") == "mercadopago" ? await mercadoPago!.Status(reference, ct) : await clip!.Status(reference, ct);
        var result = Result(txn, answer, cancel && answer.Status == "waiting" ? "cancel-requested" : null);
        if (cancel) result.Fields["cancelSent"] = true;
        return result;

    }
    public async Task<JobResult> Refund(BranchState s, JsonObject txn, CancellationToken ct) => Capabilities(txn.S("provider")).Reverse && txn.S("providerRef") != ""
        ? Result(txn, await mercadoPago!.Refund(txn.S("providerRef"), txn.S("id"), ct))
        : Result(txn, new("refund-manual", Message: "Devuelve el cargo desde el portal de " + (txn.S("provider") == "clip" ? "Clip" : "BBVA") + ": la cuenta ya estaba cubierta"));
}
public static partial class LiveServices {
    static void Terminals(IServiceCollection services, IntegrationConfig config, JsonArray terminals) {

        if (config.Use("terminal", TerminalService.From) is not { } service) return;
        foreach (var t in service.Catalog) terminals.Add(t!.DeepClone());
        if (!service.Connected) return;
        Worker(services, "terminal", branch => async ct =>
            await branch.SendNext("terminaltxn", "terminal-service", x => x.S("status") == "queued", service.Send, service.Recover, ct)
            || await branch.SendNext("terminaltxn", "terminal-service", x => x.S("status") == "refund-queued", service.Refund, async (s, x, c) => await service.Refund(s, x, c), ct, "refunding")
            || await branch.PollNext("terminaltxn", "terminal-service", x => x.S("status") is "waiting" or "cancel-requested", service.Consult, TimeSpan.FromSeconds(3), ct), 1000);

    }
}
