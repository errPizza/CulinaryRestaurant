using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Integrations;
// Vendor contract verified 2026-10-15. Credentials stay on the server. No constructor makes a call.
public sealed class FacturamaAdapter: IPacAdapter, IDisposable {
    readonly HttpClient http; public bool IsSandbox { get; }
    public FacturamaAdapter(string username, string password, bool sandbox = true, HttpMessageHandler? testHandler = null) {

        IsSandbox = sandbox;
        Need(username.Length > 0 && password.Length > 0, "Credenciales PAC requeridas");
        http = new(testHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(sandbox ? "https://apisandbox.facturama.mx/" : "https://api.facturama.mx/"), Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));

    }
    public async Task<PacResult> Stamp(JsonObject frozen, CancellationToken ct = default) {

        Text(frozen, "Folio", 100);
        Need(DateTime.TryParseExact(frozen.S("Date"), "yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _), "Fecha fiscal congelada requerida");
        Need(frozen.S("Currency") == "MXN" && frozen.A("Items").Count > 0, "CFDI sin conceptos o moneda inválida");
        return await Send(HttpMethod.Post, "3/cfdis", frozen, null, ct);

    }
    public Task<PacResult> Consult(string id, CancellationToken ct = default) => Send(HttpMethod.Get, "cfdi/" + Uri.EscapeDataString(id) + "?type=issued", null, null, ct);
    public Task<PacResult> Cancel(string id, string fiscalUuid, string motive, string? replacement = null, CancellationToken ct = default) {

        Need(new[] { "01", "02", "03", "04" }.Contains(motive) && Guid.TryParse(fiscalUuid, out _), "Cancelación fiscal inválida");
        Need(motive != "01" || Guid.TryParse(replacement, out _), "Motivo 01 requiere UUID sustituto");
        var path = "cfdi/" + Uri.EscapeDataString(id) + "?type=issued&motive=" + motive;
        if (motive == "01") path += "&uuidReplacement=" + Uri.EscapeDataString(replacement!);
        return Send(HttpMethod.Delete, path, null, fiscalUuid, ct);

    }
    async Task<PacResult> Send(HttpMethod method, string path, JsonObject? body, string? expectedUuid, CancellationToken ct) {

        try {

            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return new((int)response.StatusCode is >= 400 and < 500 && response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests) ? "rejected" : "pending-reconciliation", Message: "PAC HTTP " + (int)response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int n;
            while ((n = await stream.ReadAsync(chunk, ct)) > 0) { Need(buffer.Length + n <= 256 * 1024, "Respuesta PAC excesiva", 503); buffer.Write(chunk, 0, n); }
            var data = JsonNode.Parse(buffer.ToArray())!.AsObject();
            var uuid = data.Obj("Complement").Obj("TaxStamp").S("Uuid");
            if (method == HttpMethod.Delete) { Need(data.S("Uuid").Equals(expectedUuid, StringComparison.OrdinalIgnoreCase), "Acuse fiscal de otro comprobante", 503); return new(data.S("Status") == "canceled" ? "cancelled" : "cancellation-pending", FiscalUuid: expectedUuid!, Message: "Estado informado por PAC: " + data.S("Status")); }
            if (!Guid.TryParse(uuid, out _)) return new("pending-reconciliation", data.S("Id"), Message: "Respuesta sin timbre verificable; consultar PAC antes de reintentar");
            return new(data.S("Status") == "canceled" ? "cancelled" : "stamped", data.S("Id"), uuid, "Respuesta HTTPS del PAC");

        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new("pending-reconciliation", Message: "Respuesta PAC incierta; conservar Folio y Date"); } catch (HttpRequestException) { return new("pending-reconciliation", Message: "PAC no disponible; conservar Folio y Date"); }

    }
    // Facturama e-mails the PDF and XML itself (API Web: POST Cfdi?cfdiType=issued&cfdiId=…&email=…, empty body).
    public async Task<bool> Email(string id, string email, CancellationToken ct = default) {

        Need(id.Length is > 0 and <= 100 && System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$") && email.Length <= 150, "Correo del receptor inválido");
        try { using var response = await http.PostAsync("Cfdi?cfdiType=issued&cfdiId=" + Uri.EscapeDataString(id) + "&email=" + Uri.EscapeDataString(email), new StringContent("{}", Encoding.UTF8, "application/json"), ct); return response.IsSuccessStatusCode; } catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { return false; }

    }
    public void Dispose() => http.Dispose();
}
public static class JwtTokens {
    static string B64(byte[] raw) => Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Sign(JsonObject header, JsonObject claims, Func<byte[], byte[]> sign) { var body = B64(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + B64(Encoding.UTF8.GetBytes(claims.ToJsonString())); return body + "." + B64(sign(Encoding.ASCII.GetBytes(body))); }
}
public sealed class GoogleAccessToken(JsonObject serviceAccount, HttpClient http) {
    string token = ""; DateTimeOffset expires; readonly SemaphoreSlim gate = new(1);
    public async Task<string> Get(CancellationToken ct = default) {

        await gate.WaitAsync(ct);
        try { if (expires > DateTimeOffset.UtcNow.AddMinutes(2)) return token; using var key = RSA.Create(); key.ImportFromPem(serviceAccount.S("private_key")); var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); var jwt = JwtTokens.Sign(J.O("alg", "RS256", "typ", "JWT"), J.O("iss", serviceAccount.S("client_email"), "scope", "https://www.googleapis.com/auth/firebase.messaging", "aud", "https://oauth2.googleapis.com/token", "iat", now, "exp", now + 3600), b => key.SignData(b, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)); using var response = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string> { { "grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer" }, { "assertion", jwt } }), ct); response.EnsureSuccessStatusCode(); var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!; token = data.S("access_token"); expires = DateTimeOffset.UtcNow.AddSeconds(data.L("expires_in")); Need(token.Length > 0, "OAuth sin token", 503); return token; } finally { gate.Release(); }

    }
}
public sealed class FcmAdapter(HttpClient http, string project, Func<CancellationToken, Task<string>> accessToken) {
    public async Task<string> Send(string deviceToken, string title, string body, bool validateOnly = true, CancellationToken ct = default) {

        Need(System.Text.RegularExpressions.Regex.IsMatch(project, "^[a-z0-9-]{4,100}$") && deviceToken.Length is > 10 and < 4096, "Proyecto/token FCM inválido");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://fcm.googleapis.com/v1/projects/" + project + "/messages:send");
        req.Headers.Authorization = new("Bearer", await accessToken(ct));
        req.Content = new StringContent(J.O("validate_only", validateOnly, "message", J.O("token", deviceToken, "notification", J.O("title", title, "body", body))).ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.S("name");

    }
}
public sealed class ApnsAdapter(HttpClient http, string teamId, string keyId, string topic, string privateKeyPem) {
    public async Task<string> Send(string deviceToken, string title, string body, bool sandbox = true, CancellationToken ct = default) {

        Need(System.Text.RegularExpressions.Regex.IsMatch(deviceToken, "^[0-9a-fA-F]{64,200}$"), "Token APNs inválido");
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var jwt = JwtTokens.Sign(J.O("alg", "ES256", "kid", keyId), J.O("iss", teamId, "iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), b => key.SignData(b, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var payload = J.O("aps", J.O("alert", J.O("title", title, "body", body), "sound", "default")).ToJsonString();
        Need(Encoding.UTF8.GetByteCount(payload) <= 4096, "Notificación excesiva");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://" + (sandbox ? "api.sandbox.push.apple.com" : "api.push.apple.com") + "/3/device/" + deviceToken) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        req.Headers.Authorization = new("bearer", jwt);
        req.Headers.Add("apns-topic", topic);
        req.Headers.Add("apns-push-type", "alert");
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();
        return response.Headers.TryGetValues("apns-id", out var ids) ? ids.First() : "accepted-by-apns";

    }
}
