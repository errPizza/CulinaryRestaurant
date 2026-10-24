using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;

// A refusal from CulinaryServer. Notice is the root-signed status it may attach, bound to Nonce, the challenge of
// the request that received it; nothing here locks the branch until that binding is checked.
public sealed class ServerRejection(int status, string code, string message, string? notice, string nonce): Exception(message) {
    public int Status => status;
    public string Code => code;
    public string? Notice => notice;
    public string Nonce => nonce;
}
// Client for the branch routes of CulinaryServer (docs/API.md of that repository). Every call after activation is
// signed with the branch's license key over a single-use challenge.
public sealed class CulinaryServer: IDisposable {
    readonly HttpClient http;
    public CulinaryServer(string url, HttpMessageHandler? handler = null) {

        var uri = new Uri(url);
        Need(uri.Scheme == "https" || uri.IsLoopback, "CulinaryServer requiere HTTPS");
        Need(uri.UserInfo == "" && uri.Query == "" && uri.Fragment == "", "Dirección de CulinaryServer inválida");
        http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(20) };

    }
    public void Dispose() => http.Dispose();
    public async Task<JsonObject> Signed(HttpMethod method, string path, string licenseId, ILicenseKey key, JsonObject? body, CancellationToken ct) {

        var challenge = await Send(new HttpRequestMessage(HttpMethod.Post, "/branch/v1/challenges") { Content = Json(Encoding.UTF8.GetBytes(J.O("licenseId", licenseId).ToJsonString())) }, "", ct);
        var nonce = challenge.S("nonce");
        var bytes = body is null ? [] : Encoding.UTF8.GetBytes(body.ToJsonString());
        var input = string.Join("\n", "culinary-request-v1", "branch", method.Method.ToUpperInvariant(), path, licenseId, nonce, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = Json(bytes);
        request.Headers.Add("X-Culinary-Subject", licenseId);
        request.Headers.Add("X-Culinary-Challenge", nonce);
        request.Headers.Add("X-Culinary-Signature", Base64Url.EncodeToString(key.Sign(Encoding.UTF8.GetBytes(input))));
        return await Send(request, nonce, ct);

    }
    // Downloads carry their own bearer permission instead of a challenge, so they can resume with Range.
    public async Task<HttpResponseMessage> Download(string path, string token, CancellationToken ct) {

        Need(path.StartsWith("/branch/v1/downloads/"), "Ruta de descarga inválida", 502);
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) { response.Dispose(); throw new ServerRejection((int)response.StatusCode, "download", "Descarga no autorizada", null, ""); }
        return response;

    }
    static ByteArrayContent Json(byte[] bytes) { var content = new ByteArrayContent(bytes); content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); return content; }
    async Task<JsonObject> Send(HttpRequestMessage request, string nonce, CancellationToken ct) {

        using (request) {

            using var response = await http.SendAsync(request, ct);
            var raw = await response.Content.ReadAsByteArrayAsync(ct);
            Need(raw.Length <= 256 * 1024, "Respuesta de CulinaryServer demasiado grande", 502);
            var body = raw.Length == 0 ? new JsonObject() : JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
            if (response.IsSuccessStatusCode) return body;
            var error = body.Obj("error");
            throw new ServerRejection((int)response.StatusCode, error.S("code"), error.S("message", "CulinaryServer rechazó la solicitud"), error["notice"] is JsonValue ? error.S("notice") : null, nonce);

        }

    }
}
