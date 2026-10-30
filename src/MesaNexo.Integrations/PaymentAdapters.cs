using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Integrations;

public static class SignedWebhooks {
    public static JsonObject Stripe(byte[] body, string signature, string secret, DateTimeOffset? now = null) {

        Need(body.Length <= 112 * 1024 && secret.Length >= 20, "Webhook inválido", 403);
        var parts = signature.Split(',').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToArray();
        var timestamp = parts.FirstOrDefault(x => x[0] == "t")?.Last();
        Need(long.TryParse(timestamp, out var seconds) && Math.Abs((now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() - seconds) <= 300, "Firma fuera de vigencia", 403);
        var raw = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), raw);
        bool match = false;
        foreach (var sig in parts.Where(x => x[0] == "v1")) { try { match |= CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(sig[1])); } catch (FormatException) { } }
        Need(match, "Firma de webhook inválida", 403);
        return JsonNode.Parse(body)!.AsObject();

    }
    public static JsonObject DeliveryV1(byte[] body, string timestamp, string signature, string secret, DateTimeOffset? now = null) {

        Need(body.Length <= 112 * 1024 && secret.Length >= 32, "Webhook inválido", 403);
        Need(long.TryParse(timestamp, out var seconds) && Math.Abs((now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() - seconds) <= 300, "Webhook vencido", 403);
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray());
        byte[] supplied;
        try { supplied = Convert.FromHexString(signature); } catch { throw new BusinessException(403, "Firma inválida"); }
        Need(CryptographicOperations.FixedTimeEquals(expected, supplied), "Firma inválida", 403);
        return JsonNode.Parse(body)!.AsObject();

    }
}
// Stripe Checkout sessions as payment links, with the secret key of the restaurant's account (sk_live_ or sk_test_). Creation and
// refunds carry an Idempotency-Key, so repeating one after a lost answer returns the first result.
public sealed record CheckoutState(string Status, string SessionId = "", string Url = "", string PaymentIntent = "", string Message = "");
public sealed class StripeCheckout(HttpClient http, string secretKey, string successUrl) {
    public static readonly TerminalCapabilities Capabilities = new(true, true, true);
    async Task<(int Code, JsonNode? Body)> Call(HttpMethod method, string path, Dictionary<string, string>? form, string? idempotency, CancellationToken ct) {

        using var request = new HttpRequestMessage(method, "https://api.stripe.com/v1/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
        if (idempotency is not null) request.Headers.Add("Idempotency-Key", idempotency);
        if (form is not null) request.Content = new FormUrlEncodedContent(form);
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? data = null;
        try { data = JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { }
        if ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429) throw new HttpRequestException("Stripe HTTP " + (int)response.StatusCode);
        return ((int)response.StatusCode, data);

    }
    static string Error(JsonNode? data, int code) => data?["error"].S("message") is { Length: > 0 } m ? m : "HTTP " + code;
    public async Task<CheckoutState> Create(string linkId, string visitId, long cents, string title, DateTimeOffset expires, CancellationToken ct = default) {

        Need(cents > 0, "Importe inválido");
        var form = new Dictionary<string, string> {
            ["mode"] = "payment",
            ["line_items[0][quantity]"] = "1",
            ["line_items[0][price_data][currency]"] = "mxn",
            ["line_items[0][price_data][unit_amount]"] = cents.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["line_items[0][price_data][product_data][name]"] = title,
            ["success_url"] = successUrl,
            ["expires_at"] = expires.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["client_reference_id"] = linkId,
            ["metadata[paylink_id]"] = linkId,
            ["metadata[visit_id]"] = visitId,
            ["payment_intent_data[metadata][paylink_id]"] = linkId
        };
        var (code, data) = await Call(HttpMethod.Post, "checkout/sessions", form, linkId, ct);
        return code < 300 ? Read(data) : new("failed", Message: "Stripe no creó la liga: " + Error(data, code));

    }
    public async Task<CheckoutState> Status(string sessionId, CancellationToken ct = default) { var (code, data) = await Call(HttpMethod.Get, "checkout/sessions/" + Uri.EscapeDataString(sessionId), null, null, ct); return code < 300 ? Read(data) : new("pending", sessionId, Message: "Stripe: " + Error(data, code)); }
    // Only an open session can expire; a completed one answers with its payment, which is then applied or refunded.
    public async Task<CheckoutState> Expire(string sessionId, CancellationToken ct = default) { var (code, data) = await Call(HttpMethod.Post, "checkout/sessions/" + Uri.EscapeDataString(sessionId) + "/expire", new(), null, ct); return code < 300 ? Read(data) : await Status(sessionId, ct); }
    public async Task<CheckoutState> Refund(string paymentIntent, string linkId, CancellationToken ct = default) {

        var (code, data) = await Call(HttpMethod.Post, "refunds", new() { ["payment_intent"] = paymentIntent, ["metadata[paylink_id]"] = linkId }, "refund-" + linkId, ct);
        return code < 300 ? new("refunded", PaymentIntent: paymentIntent, Message: "Reembolsado en Stripe (" + data.S("id") + ")") : new("refund-failed", PaymentIntent: paymentIntent, Message: "Stripe no reembolsó: " + Error(data, code));

    }
    static CheckoutState Read(JsonNode? s) => (s.S("status"), s.S("payment_status")) switch {
        ("complete", "paid") or ("complete", "no_payment_required") => new("paid", s.S("id"), s.S("url"), s.S("payment_intent"), "Pagada en Stripe"),
        ("expired", _) => new("expired", s.S("id"), Message: "La liga venció sin pago"),
        _ => new("pending", s.S("id"), s.S("url"), Message: "Esperando el pago del cliente")
    };
}
public sealed class StripeSandboxPayments(HttpClient http, string secretKey) {
    public async Task<JsonObject> CreateIntent(string operationId, long amount, string tenant, string branch, string order, CancellationToken ct = default) {

        Need(secretKey.StartsWith("sk_test_") && amount > 0 && Guid.TryParse(operationId, out _), "Sólo Stripe test con UUID e importe válidos");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.stripe.com/v1/payment_intents");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
        req.Headers.Add("Idempotency-Key", operationId);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string> { { "amount", amount.ToString(System.Globalization.CultureInfo.InvariantCulture) }, { "currency", "mxn" }, { "automatic_payment_methods[enabled]", "true" }, { "metadata[tenant_id]", tenant }, { "metadata[branch_id]", branch }, { "metadata[order_id]", order } });
        using var response = await http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();

    }
}
