using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Integrations;
// Deliverect POS API (verified 2026-10-23 at developers.deliverect.com): a client-credentials token whose audience is the API
// base URL, order statuses with POST /orderStatus/{orderId} and the menu with POST /productAndCategories. The production base
// URL is the one Deliverect gives after certification; staging is https://api.staging.deliverect.com.
public sealed class DeliverectClient(HttpClient http, string baseUrl, string clientId, string clientSecret) {
    string token = ""; DateTimeOffset until; readonly SemaphoreSlim gate = new(1, 1);
    async Task<string> Token(CancellationToken ct) {

        await gate.WaitAsync(ct);
        try {

            if (token != "" && DateTimeOffset.UtcNow < until) return token;
            using var response = await http.PostAsync(baseUrl.TrimEnd('/') + "/oauth/token", new StringContent(J.O("client_id", clientId, "client_secret", clientSecret, "audience", baseUrl.TrimEnd('/'), "grant_type", "client_credentials").ToJsonString(), Encoding.UTF8, "application/json"), ct);
            var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            Need(response.IsSuccessStatusCode && data.S("access_token") != "", "Deliverect rechazó las credenciales del cliente");
            token = data.S("access_token");
            until = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, data.L("expires_in", 3600)) - 60);
            return token;

        } finally { gate.Release(); }

    }
    async Task<(int Code, string Text)> Post(string path, JsonObject body, CancellationToken ct) {

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/" + path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token(ct));
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if ((int)response.StatusCode == 401) { token = ""; throw new HttpRequestException("Deliverect rechazó el token"); }
        if ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429) throw new HttpRequestException("Deliverect HTTP " + (int)response.StatusCode);
        return ((int)response.StatusCode, text.Length > 300 ? text[..300] : text);

    }
    // Repeating a status is harmless: Deliverect keeps the order's latest one.
    public async Task<SendResult> Status(string orderId, int code, string reason, string receiptId, CancellationToken ct = default) {

        var body = J.O("orderId", orderId, "status", code, "timeStamp", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'"), "receiptId", receiptId);
        if (reason != "") body["reason"] = reason;
        var (status, text) = await Post("orderStatus/" + Uri.EscapeDataString(orderId), body, ct);
        return status < 300 ? new("sent", orderId, "Estado " + code + " enviado a Deliverect") : new("failed", orderId, "Deliverect rechazó el estado " + code + ": " + text);

    }
    public async Task<SendResult> Menu(string accountId, string locationId, JsonArray products, JsonArray categories, CancellationToken ct = default) {

        var (status, text) = await Post("productAndCategories", J.O("accountId", accountId, "locationId", locationId, "products", products, "categories", categories), ct);
        return status < 300 ? new("sent", Message: "Menú publicado en Deliverect") : new("failed", Message: "Deliverect rechazó el menú: " + text);

    }
}
public static class DeliverectSignature {
    // HMAC-SHA256 of the raw body with the location's shared secret, hex in x-server-authorization-hmac-sha256.
    public static JsonObject Verify(byte[] body, string signature, string secret) {

        Need(body.Length <= 256 * 1024 && secret.Length > 0, "Webhook inválido", 403);
        byte[] supplied;
        try { supplied = Convert.FromHexString(signature.Trim()); } catch (FormatException) { throw new BusinessException(403, "Firma inválida"); }
        Need(CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body), supplied), "Firma inválida", 403);
        return JsonNode.Parse(body) as JsonObject ?? throw new BusinessException(400, "Pedido JSON requerido");

    }
}
