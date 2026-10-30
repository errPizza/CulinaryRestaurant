using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Integrations;
// What a terminal provider says about a charge. Status: waiting (on the device), approved, declined, cancelled, expired,
// refunded or uncertain. Card data never comes back beyond the brand and the last four digits.
public sealed record TerminalAnswer(string Status, string ProviderRef = "", string Brand = "", string Last4 = "", string Auth = "", string Message = "", string Raw = "");
// What a provider can prove after a lost answer. Idempotent: repeating a creation cannot charge twice, so it can be sent again.
// Lookup: it can be asked about a charge, given its id. Reverse: it returns a charge through its API. Without them the charge
// goes to a person, with the terminal's report.
public sealed record TerminalCapabilities(bool Idempotent, bool Lookup, bool Reverse);
static class Money {
    public static string Pesos(long cents) => (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
    public static string Brand(string id) => id.ToLowerInvariant() switch { "visa" or "debvisa" => "Visa", "master" or "debmaster" or "mastercard" => "Mastercard", "amex" => "American Express", "carnet" or "debcarnet" => "Carnet", "" => "", _ => "Otra" };
    // The provider's answer as evidence, without card or customer data.
    public static string Raw(JsonNode? data) { var copy = data?.DeepClone(); Clean(copy); var text = copy?.ToJsonString() ?? ""; return text.Length > 2000 ? text[..2000] : text; }
    static void Clean(JsonNode? node) {

        if (node is JsonArray list) foreach (var item in list) Clean(item);
        if (node is not JsonObject o) return;
        foreach (var key in o.Select(x => x.Key).ToArray()) { var k = key.ToLowerInvariant(); if (new[] { "card", "payer", "holder", "token", "cvv", "pan", "email", "phone", "name", "identification", "document", "last_digits", "first_digits" }.Any(k.Contains)) o.Remove(key); else Clean(o[key]); }

    }
}
// Mercado Pago Point, Orders API (verified 2026-10-23 at mercadopago.com.mx/developers/es/docs/mp-point). The terminal must
// be in PDV mode. X-Idempotency-Key is the charge's own id, so repeating a creation returns the same order instead of a second one.
public sealed class MercadoPagoPoint(HttpClient http, string accessToken) {
    public static readonly TerminalCapabilities Capabilities = new(true, true, true);
    async Task<(HttpStatusCode Code, JsonNode? Body)> Call(HttpMethod method, string path, JsonObject? body, string? idempotency, CancellationToken ct, params (string, string)[] headers) {

        using var request = new HttpRequestMessage(method, "https://api.mercadopago.com/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (idempotency is not null) request.Headers.Add("X-Idempotency-Key", idempotency);
        foreach (var (k, v) in headers)
            request.Headers.Add(k, v);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        else if (method == HttpMethod.Post) request.Content = new StringContent("", Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? data = null;
        try { data = text == "" ? null : JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { }
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests) throw new HttpRequestException("Mercado Pago HTTP " + (int)response.StatusCode);
        return (response.StatusCode, data);

    }
    static string Error(JsonNode? data, HttpStatusCode code) => data.A("errors").Objects().FirstOrDefault()?.S("message") is { Length: > 0 } m ? m : data.S("message") is { Length: > 0 } n ? n : "HTTP " + (int)code;
    public async Task<TerminalAnswer> Charge(string id, string reference, long cents, string deviceId, CancellationToken ct = default) {

        var body = J.O("type", "point", "external_reference", reference, "expiration_time", "PT5M", "description", "Culinary Restaurant " + reference, "transactions", J.O("payments", new JsonArray(J.O("amount", Money.Pesos(cents)))), "config", J.O("point", J.O("terminal_id", deviceId, "print_on_terminal", "no_ticket")));
        var (code, data) = await Call(HttpMethod.Post, "v1/orders", body, id, ct);
        return (int)code < 300 ? Read(data) : new("declined", Message: "Mercado Pago rechazó el cobro: " + Error(data, code), Raw: Money.Raw(data));

    }
    public async Task<TerminalAnswer> Status(string orderId, CancellationToken ct = default) { var (code, data) = await Call(HttpMethod.Get, "v1/orders/" + Uri.EscapeDataString(orderId), null, null, ct); return (int)code < 300 ? Read(data) : new("uncertain", orderId, Message: "Mercado Pago no encontró la orden: " + Error(data, code)); }
    public async Task<TerminalAnswer> Cancel(string orderId, string id, CancellationToken ct = default) {

        var (code, data) = await Call(HttpMethod.Post, "v1/orders/" + Uri.EscapeDataString(orderId) + "/cancel", null, "cancel-" + id, ct, ("x-allow-cancelable-status", "at_terminal"));
        // 202: the terminal was asked to cancel; the order's status says whether it did.
        return code == HttpStatusCode.OK ? Read(data) : code == HttpStatusCode.Accepted ? new("waiting", orderId, Message: "Cancelación solicitada a la terminal") : new("waiting", orderId, Message: "No se pudo cancelar: " + Error(data, code) + ". Cancélalo en la terminal");

    }
    public async Task<TerminalAnswer> Refund(string orderId, string id, CancellationToken ct = default) { var (code, data) = await Call(HttpMethod.Post, "v1/orders/" + Uri.EscapeDataString(orderId) + "/refund", null, "refund-" + id, ct); return (int)code < 300 ? new("refunded", orderId, Message: "Cargo devuelto por Mercado Pago") : new("refund-failed", orderId, Message: "Mercado Pago no devolvió el cargo: " + Error(data, code)); }
    static TerminalAnswer Read(JsonNode? order) {

        var id = order.S("id");
        // An answer that names no order proves nothing about the charge.
        if (id == "") return new("uncertain", Message: "Mercado Pago respondió sin el id de la orden", Raw: Money.Raw(order));
        var payment = order?["transactions"].A("payments").Objects().FirstOrDefault();
        var method = payment.Obj("payment_method");
        return order.S("status") switch {
            "processed" => new("approved", id, Money.Brand(method.S("id")), Message: "Aprobada por Mercado Pago", Raw: Money.Raw(order)),
            "canceled" => new("cancelled", id, Message: "Cancelado en Mercado Pago", Raw: Money.Raw(order)),
            "expired" => new("expired", id, Message: "El cliente no pagó a tiempo en la terminal", Raw: Money.Raw(order)),
            "failed" => new("declined", id, Message: "Pago rechazado" + (payment.S("status_detail") is { Length: > 0 } d ? " (" + d + ")" : ""), Raw: Money.Raw(order)),
            "refunded" => new("refunded", id, Message: "Cargo devuelto", Raw: Money.Raw(order)),
            _ => new("waiting", id, Message: "En la terminal: el cliente paga con su tarjeta")
        };

    }
}
// Clip PinPad API (verified 2026-10-23 at developer.clip.mx). Authorization is Basic base64(apiKey:secretKey). The device must
// have the PinPad app active; a request can be deleted only until the device takes it.
public sealed class ClipPinpad(HttpClient http, string apiKey, string secretKey) {
    // No idempotency key, and it can only be asked about a request whose id it already gave.
    public static readonly TerminalCapabilities Capabilities = new(false, true, false);
    readonly string token = Convert.ToBase64String(Encoding.UTF8.GetBytes(apiKey + ":" + secretKey));
    async Task<(HttpStatusCode Code, JsonNode? Body)> Call(HttpMethod method, string path, JsonObject? body, CancellationToken ct, params (string, string)[] headers) {

        using var request = new HttpRequestMessage(method, "https://api.payclip.io/f2f/pinpad/v1/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        foreach (var (k, v) in headers)
            request.Headers.Add(k, v);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? data = null;
        try { data = text == "" ? null : JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { }
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests) throw new HttpRequestException("Clip HTTP " + (int)response.StatusCode);
        return (response.StatusCode, data);

    }
    public async Task<TerminalAnswer> Charge(string reference, long amount, long tip, string serial, CancellationToken ct = default) {

        var body = J.O("amount", Money.Pesos(amount), "tip_amount", Money.Pesos(tip), "reference", reference, "serial_number_pos", serial, "preferences", J.O("is_auto_return_enabled", true, "is_tip_enabled", false, "is_auto_print_receipt_enabled", false, "is_split_payment_enabled", false));
        var (code, data) = await Call(HttpMethod.Post, "payment", body, ct);
        if ((int)code < 300 && data.S("pinpad_request_id") is { Length: > 0 } id) return new("waiting", id, Message: "En la terminal Clip: el cliente paga con su tarjeta");
        // Accepted without its id: the device may have the charge.
        if ((int)code < 300) return new("uncertain", Message: "Clip aceptó el cobro sin devolver su id", Raw: Money.Raw(data));
        return new("declined", Message: "Clip no envió el cobro: " + (data.S("message") is { Length: > 0 } m ? m : data.S("code")), Raw: Money.Raw(data));

    }
    public async Task<TerminalAnswer> Status(string requestId, CancellationToken ct = default) {

        var (code, data) = await Call(HttpMethod.Get, "payment?pinpadRequestId=" + Uri.EscapeDataString(requestId), null, ct, ("Pinpad-Include-Detail", "true"));
        if (code == HttpStatusCode.NotFound) return new("cancelled", requestId, Message: "La solicitud ya no existe en Clip");
        if ((int)code >= 300) return new("uncertain", requestId, Message: "Clip respondió HTTP " + (int)code);
        var result = data?["detail"].A("results").Objects().FirstOrDefault();
        var card = result.Obj("payment_method");
        return data.S("status") switch {
            "COMPLETED" when result.S("status") == "approved" => new("approved", requestId, Money.Brand(card.S("id")), card.Obj("card").S("last_digits"), Message: "Aprobada por Clip", Raw: Money.Raw(data)),
            "COMPLETED" or "FAILED" => new("declined", requestId, Message: "Pago rechazado en Clip", Raw: Money.Raw(data)),
            _ => new("waiting", requestId, Message: "En la terminal Clip: el cliente paga con su tarjeta")
        };

    }
    public async Task<TerminalAnswer> Cancel(string requestId, CancellationToken ct = default) {

        var (code, data) = await Call(HttpMethod.Delete, "payment/" + Uri.EscapeDataString(requestId), null, ct);
        return (int)code < 300 ? new("cancelled", requestId, Message: "Cancelado en Clip") : new("waiting", requestId, Message: "La terminal ya tomó el cobro: cancélalo en la app PinPad (" + (data.S("message") is { Length: > 0 } m ? m : "HTTP " + (int)code) + ")");

    }
}
