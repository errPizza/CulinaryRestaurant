using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Client;

public record ConnectionProfile(string ServerUrl, string SiteId, string CaFile, string CaSha256, string? ConnectAddress = null, string TenantId = "", string BranchId = "");
public enum ConnectionState { Unverified, Available, LocalServerUnreachable, InternalNetworkDisconnected, Uncertain, TlsRejected }
public sealed class ApiFailure(int status, string message, string code = "", string outcome = ""): Exception(message) { public int Status => status; public string Code => code; public string Outcome => outcome; }
public interface ISecureVault {
    Task<string?> Read(string key);
    Task Write(string key, string value);
    Task Delete(string key);
}
public static class TrustedConnection {
    public static SocketsHttpHandler Handler(ConnectionProfile profile) {

        var uri = new Uri(profile.ServerUrl);
        if (uri.Scheme != "https" || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "") throw new InvalidOperationException("La conexión requiere una dirección HTTPS sin credenciales");
        var root = X509CertificateLoader.LoadCertificateFromFile(profile.CaFile);
        var fingerprint = root.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
        if (!fingerprint.Equals(profile.CaSha256.Replace(":", "").Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("La huella de CA no coincide con el aprovisionamiento");
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        handler.SslOptions = new SslClientAuthenticationOptions { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck, CustomTrustStore = { root }, ApplicationPolicy = { new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1") } } };
        // TLS still validates SAN/hostname, dates and serverAuth. Candidate addresses never set trust.
        if (profile.ConnectAddress is string address) { if (!IPAddress.TryParse(address, out var ip)) throw new InvalidOperationException("Candidato IP inválido"); handler.ConnectCallback = async (context, ct) => { var socket = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp); try { await socket.ConnectAsync(new IPEndPoint(ip, uri.Port), ct); return new System.Net.Sockets.NetworkStream(socket, true); } catch { socket.Dispose(); throw; } }; }
        return handler;

    }
    public static HttpClient Create(ConnectionProfile p) => new(Handler(p)) { BaseAddress = new Uri(p.ServerUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
}
public sealed partial class NativeSession: IDisposable {
    string catalogHash = "";
    readonly HttpClient http; readonly ISecureVault vault; readonly ConnectionProfile profile; readonly SemaphoreSlim sending = new(1);
    public bool DeferConfirmation { get; init; }
    public string Token { get; private set; } = ""; public string UserId { get; private set; } = "";
    public ConnectionState Connection { get; private set; } = ConnectionState.Unverified;
    public string OperationState { get; private set; } = "Sin operaciones pendientes";
    public NativeSession(ConnectionProfile profile, ISecureVault vault) { this.profile = profile; this.vault = vault; http = TrustedConnection.Create(profile); }
    public async Task<JsonObject> Login(string username, string password, string deviceName, string enrollment = "", CancellationToken ct = default) {

        // Verify branch identity through an already trusted TLS channel before sending credentials.
        var identity = await Request(HttpMethod.Get, "api/identity", null, ct);
        if (identity.S("siteId") != profile.SiteId) throw new InvalidOperationException("El servidor pertenece a otra sucursal");
        if (identity.L("protocol", Contract.Api) != Contract.Api) throw new InvalidOperationException("Esta versión de la aplicación no es compatible con el servidor; actualízala");
        var deviceKey = "device_" + profile.SiteId;
        var raw = await vault.Read(deviceKey);
        var device = raw is null ? J.O("deviceId", J.Id(), "deviceSecret", Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))) : JsonNode.Parse(raw)!.AsObject();
        await vault.Write(deviceKey, device.ToJsonString());
        var p = (JsonObject)device.DeepClone();
        p["username"] = username;
        p["password"] = password;
        p["deviceName"] = deviceName;
        // Phones and tablets are mobile order takers; desktop installs count as fixed terminals.
        p["deviceKind"] = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() ? "mobile" : "terminal";
        p["enrollmentCode"] = enrollment;
        p["siteId"] = profile.SiteId;
        if (profile.TenantId != "") { p["tenantId"] = profile.TenantId; p["branchId"] = profile.BranchId; }
        var result = await Request(HttpMethod.Post, "api/login", p, ct);
        Token = result.S("token");
        UserId = result.Obj("user").S("id");
        Connection = ConnectionState.Available;
        return result;

    }
    public async Task<JsonObject> Report(string from, string until, string after = "", CancellationToken ct = default) => await Request(HttpMethod.Get, "api/reports?from=" + Uri.EscapeDataString(from) + "&until=" + Uri.EscapeDataString(until) + "&after=" + Uri.EscapeDataString(after), null, ct);
    public async Task<JsonObject> LookForUpdate(string platform, CancellationToken ct = default) => await Request(HttpMethod.Post, "api/updates/look", J.O("platform", platform, "currentVersion", Updates.Current.ToString(3)), ct);
    public async Task<JsonObject> CloseForUpdate(CancellationToken ct = default) => await Request(HttpMethod.Post, "api/updates/close-sessions", [], ct);
    public async Task<JsonObject> VerifyAudit(CancellationToken ct = default) => await Request(HttpMethod.Get, "api/audit/verify", null, ct);
    public async Task<JsonObject> Preview(string visit, CancellationToken ct = default) => await Request(HttpMethod.Get, "api/preview/" + Uri.EscapeDataString(visit), null, ct);
    // The branch server sends only what changed since the state held here; callers always get a whole, private copy.
    JsonObject? held; string heldBase = ""; readonly SemaphoreSlim stateGate = new(1);
    async Task<JsonObject> Fetch(CancellationToken ct) {

        if (profile.TenantId != "") return await Request(HttpMethod.Get, "api/state?branchId=" + Uri.EscapeDataString(profile.BranchId), null, ct);
        await stateGate.WaitAsync(ct);
        try {

            var reply = await Request(HttpMethod.Get, "api/state?since=" + Uri.EscapeDataString(heldBase), null, ct);
            var stateBase = reply.S("stateBase");
            JsonObject state;
            if (!reply.B("delta")) state = reply;
            else if (held is not null && reply.S("since") == heldBase) { try { state = StateDelta.Apply(held, reply); } catch (InvalidOperationException) { held = null; heldBase = ""; return await Refetch(ct); } } else { held = null; heldBase = ""; return await Refetch(ct); }
            state.Remove("stateBase");
            state.Remove("since");
            held = state;
            heldBase = stateBase;
            return (JsonObject)state.DeepClone();

        } finally { stateGate.Release(); }
        async Task<JsonObject> Refetch(CancellationToken ct) { var full = await Request(HttpMethod.Get, "api/state?since=", null, ct); heldBase = full.S("stateBase"); full.Remove("stateBase"); held = full; return (JsonObject)full.DeepClone(); }

    }
    public async Task<JsonObject> State(CancellationToken ct = default) { var state = await Fetch(ct); var catalog = J.O("siteId", profile.SiteId, "products", state.A("products"), "categorys", state.A("categorys")); var hash = J.Hash(J.Canonical(catalog)); if (hash != catalogHash && state.A("products").Count > 0) { catalog["at"] = J.Now(); await vault.Write("catalog_" + profile.SiteId + "_" + UserId, catalog.ToJsonString()); await vault.Write("last_catalog_" + profile.SiteId, catalog.ToJsonString()); catalogHash = hash; } return state; }
    public async Task<JsonObject?> CachedCatalog() { var raw = await vault.Read("catalog_" + profile.SiteId + "_" + UserId); return raw is null ? null : JsonNode.Parse(raw)!.AsObject(); }
    string PendingKey => "pending_" + profile.SiteId;
    public async Task<bool> HasPending() => await vault.Read(PendingKey) is not null;
    public async Task<JsonObject> Execute(string type, JsonObject payload, CancellationToken ct = default) {

        await sending.WaitAsync(ct);
        try {

            if (await HasPending()) throw new InvalidOperationException("Hay una operación incierta; reconcilia antes de continuar");
            var pending = J.O("operationId", J.Id(), "type", type, "payload", payload, "userId", UserId);
            await vault.Write(PendingKey, pending.ToJsonString());
            return await SendPending(pending, ct);

        } finally { sending.Release(); }

    }
    async Task<JsonObject> SendPending(JsonObject pending, CancellationToken ct) {

        if (pending.S("userId") != UserId) throw new InvalidOperationException("La operación pendiente pertenece a otro usuario; inicia su sesión para reconciliar");
        try { var result = await Request(HttpMethod.Post, "api/action", pending, ct); if (result.S("local") != "committed") throw new IOException("Confirmación ambigua"); await ClearConfirmedDraft(pending); if (!DeferConfirmation || pending.S("type") == "session.logout") await vault.Delete(PendingKey); OperationState = "Confirmada localmente · sincronización pendiente de verificar"; return result; } catch (ApiFailure e) when (e.Status is >= 400 and < 500 && e.Status != 409) { await vault.Delete(PendingKey); OperationState = "Rechazada por el servidor local"; throw; } catch { OperationState = "Resultado incierto · referencia " + pending.S("operationId")[..8] + " · reconcilia antes de repetir"; Connection = ConnectionState.Uncertain; throw; }

    }
    public async Task<JsonObject?> Reconcile(bool retryIfUnknown, CancellationToken ct = default) {

        await sending.WaitAsync(ct);
        try {

            var raw = await vault.Read(PendingKey);
            if (raw is null) return null;
            var pending = JsonNode.Parse(raw)!.AsObject();
            if (pending.S("userId") != UserId) throw new InvalidOperationException("Inicia sesión con el usuario de la operación pendiente");
            var status = await Request(HttpMethod.Get, "api/operations/" + pending.S("operationId"), null, ct);
            if (status.S("local") == "committed") { await ClearConfirmedDraft(pending); if (!DeferConfirmation || pending.S("type") == "session.logout") await vault.Delete(PendingKey); OperationState = "Confirmada localmente · remoto: " + status.S("remote"); return status; }
            if (retryIfUnknown) return await SendPending(pending, ct);
            OperationState = "Resultado incierto: todavía no consta en el servidor local";
            return status;

        } finally { sending.Release(); }

    }
    public async Task Acknowledge(string operationId, CancellationToken ct = default) {

        await sending.WaitAsync(ct);
        try {

            var raw = await vault.Read(PendingKey);
            if (raw is null) return;
            var pending = JsonNode.Parse(raw)!.AsObject();
            if (pending.S("operationId") != operationId || pending.S("userId") != UserId) throw new InvalidOperationException("El recibo no corresponde a la operación pendiente");
            var receipt = await Request(HttpMethod.Get, "api/operations/" + Uri.EscapeDataString(operationId), null, ct);
            if (receipt.S("local") != "committed") throw new IOException("El recibo todavía no está confirmado");
            await ClearConfirmedDraft(pending);
            await vault.Delete(PendingKey);

        } finally { sending.Release(); }

    }
    async Task ClearConfirmedDraft(JsonObject pending) { if (pending.S("type") == "command.send") await SaveDraft(pending.Obj("payload").S("visitId"), []); }
    public Task SaveDraft(string visit, JsonArray draft) => vault.Write("draft_" + profile.SiteId + "_" + UserId + "_" + visit, draft.ToJsonString());
    public async Task<JsonArray> LoadDraft(string visit) { var raw = await vault.Read("draft_" + profile.SiteId + "_" + UserId + "_" + visit); return raw is null ? [] : JsonNode.Parse(raw)!.AsArray(); }
    async Task<JsonObject> Request(HttpMethod method, string path, JsonObject? body, CancellationToken ct) {

        try {

            using var request = new HttpRequestMessage(method, path);
            if (Token != "") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            var data = string.IsNullOrEmpty(raw) ? new JsonObject() : JsonNode.Parse(raw) as JsonObject ?? throw new IOException("Respuesta inválida");
            if (!response.IsSuccessStatusCode) throw new ApiFailure((int)response.StatusCode, data.S("error", "Operación rechazada"), data.S("code"), data.S("outcome"));
            Connection = ConnectionState.Available;
            return data;

        } catch (HttpRequestException e) { Connection = e.InnerException is AuthenticationException ? ConnectionState.TlsRejected : System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable() ? ConnectionState.LocalServerUnreachable : ConnectionState.InternalNetworkDisconnected; throw new IOException(Connection == ConnectionState.TlsRejected ? "Servidor no confiable: comprueba CA, nombre y vigencia" : Connection == ConnectionState.InternalNetworkDisconnected ? "Red interna desconectada" : "Servidor local inaccesible. Internet y central sin comprobar.", e); }

    }
    public void Dispose() { _ = StopLive(); http.Dispose(); sending.Dispose(); }
}