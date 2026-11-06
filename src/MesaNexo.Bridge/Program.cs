using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using MesaNexo.Client;
using MesaNexo.Desktop;
namespace MesaNexo.Bridge;

public static class Program {
    public static async Task Main() {

        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        using var bridge = new BridgeHost(new DesktopVault());
        // Private, inherited pipes only. Never exposes a loopback HTTP listener.
        while (await Console.In.ReadLineAsync() is string line) {

            JsonObject? message = null;
            JsonObject response;
            try { if (line.Length > 2_000_000) throw new InvalidOperationException("Solicitud demasiado grande"); message = JsonNode.Parse(line)!.AsObject(); var data = await bridge.Call(message.S("command"), message.Obj("args")); response = J.O("id", message.S("id"), "ok", true, "data", data); } catch (Exception e) { bridge.Unexpected(message?.S("command") ?? "", e); response = J.O("id", message?.S("id") ?? "", "ok", false, "error", e is ApiFailure or InvalidOperationException or IOException or BusinessException ? e.Message : "No se pudo completar la operación", "status", e is ApiFailure f ? f.Status : e is BusinessException b ? b.Status : 0); }
            await Console.Out.WriteLineAsync(response.ToJsonString());
            await Console.Out.FlushAsync();

        }

    }
}
public sealed class BridgeHost(ISecureVault vault): IDisposable {
    NativeSession? session; ConnectionProfile? profile; string? preparedPackage; JsonObject? preparedManifest;
    readonly List<JsonObject> faults = []; DateTimeOffset faultsTried = DateTimeOffset.MinValue;
    readonly SemaphoreSlim gate = new(1);
    public async Task<JsonNode?> Call(string command, JsonObject args) {

        await gate.WaitAsync();
        try { return await Dispatch(command, args); } finally { gate.Release(); }

    }
    NativeSession Session => session ?? throw new InvalidOperationException("Inicia sesión para continuar");
    // Written by the CulinaryOS first boot, readable only by the kiosk account until Setup finishes.
    const string SetupCodeFile = "/var/lib/culinary-restaurant-setup/setup-code";
    async Task<JsonObject> SignIn(ConnectionProfile p, string username, string password, string enrollment) {

        var candidate = new NativeSession(p, vault) { DeferConfirmation = true };
        try { var result = await candidate.Login(username, password, Environment.MachineName, enrollment); session = candidate; if (await candidate.RefreshTrust() is { } trusted && await vault.Read("profile") is not null) { await vault.Write("profile", System.Text.Json.JsonSerializer.Serialize(trusted)); profile = trusted; } try { await candidate.StartLive(() => Task.CompletedTask); } catch { } var safe = (JsonObject)result.DeepClone(); safe.Remove("token"); return safe; } catch { candidate.Dispose(); throw; }

    }
    async Task<ConnectionProfile?> Profile() {

        if (profile is not null) return profile;
        var raw = await vault.Read("profile");
        if (raw is not null) { profile = System.Text.Json.JsonSerializer.Deserialize<ConnectionProfile>(raw); return profile; }
        // Public connection defaults installed by the CulinaryOS administrator.
        // Device credentials and pending operations remain in Secret Service.
        const string systemProfile = "/etc/culinaryos/restaurant-connection.json";
        if (raw is null && OperatingSystem.IsLinux() && File.Exists(systemProfile)) raw = await File.ReadAllTextAsync(systemProfile);
        if (raw is not null) {

            var candidate = System.Text.Json.JsonSerializer.Deserialize<ConnectionProfile>(raw) ?? throw new InvalidOperationException("Perfil de conexión inválido");
            using (var handler = TrustedConnection.Handler(candidate)) { }
            profile = candidate;

        }
        return profile;

    }
    async Task<JsonNode?> Dispatch(string command, JsonObject a) {

        switch (command) {
            case "profile.get":
                return System.Text.Json.JsonSerializer.SerializeToNode(await Profile());
            case "profile.save": {

                    if (session is not null) throw new InvalidOperationException("Cierra la sesión antes de cambiar la conexión");
                    var next = System.Text.Json.JsonSerializer.Deserialize<ConnectionProfile>(a.ToJsonString()) ?? throw new InvalidOperationException("Perfil inválido");
                    if (next.SiteId.Length is < 1 or > 100) throw new InvalidOperationException("Identidad de sucursal requerida");
                    // Validate URL, CA hash and native TLS policy before persisting the profile.
                    using (var handler = TrustedConnection.Handler(next)) { }
                    var old = await Profile();
                    if (old is not null && old.SiteId != next.SiteId && await vault.Read("pending_" + old.SiteId) is not null) throw new InvalidOperationException("Reconcilia la operación pendiente de la sucursal anterior");
                    await vault.Write("profile", System.Text.Json.JsonSerializer.Serialize(next));
                    profile = next;
                    return J.O("saved", true);

                }
            case "login": {

                    var p = await Profile() ?? throw new InvalidOperationException("Configura la conexión HTTPS y su CA");
                    if (session is not null) throw new InvalidOperationException("Ya hay una sesión activa");
                    return await SignIn(p, a.S("username"), a.S("password"), a.S("enrollmentCode"));

                }
            case "setup.status": {

                    var p = await Profile();
                    if (p is null || session is not null) return J.O("needsSetup", false);
                    using var http = TrustedConnection.Create(p);
                    using var response = await http.GetAsync("api/setup");
                    if (!response.IsSuccessStatusCode) return J.O("needsSetup", false);
                    var status = JsonNode.Parse(await response.Content.ReadAsStringAsync());
                    return J.O("needsSetup", status.B("needsSetup") && status.S("siteId") == p.SiteId, "codeAvailable", OperatingSystem.IsLinux() && File.Exists(SetupCodeFile));

                }
            case "setup.complete": {

                    var p = await Profile() ?? throw new InvalidOperationException("Configura la conexión HTTPS y su CA");
                    if (session is not null) throw new InvalidOperationException("Ya hay una sesión activa");
                    var code = a.S("setupCode").Trim();
                    if (code == "" && OperatingSystem.IsLinux() && File.Exists(SetupCodeFile)) code = (await File.ReadAllTextAsync(SetupCodeFile)).Trim();
                    var body = J.O("siteId", p.SiteId, "setupCode", code, "name", a.S("name"), "managerName", a.S("managerName"), "managerUsername", a.S("managerUsername"), "managerPassword", a.S("managerPassword"), "managerPasswordConfirm", a.S("managerPasswordConfirm"));
                    JsonObject created;
                    using (var http = TrustedConnection.Create(p)) { using var response = await http.PostAsJsonAsync("api/setup", body); var data = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject ?? new(); if (!response.IsSuccessStatusCode) throw new ApiFailure((int)response.StatusCode, data.S("error", "No se pudo completar la configuración")); created = data; }
                    // The code is useless once the branch exists; remove the kiosk copy so it is not left behind.
                    try { if (OperatingSystem.IsLinux()) File.Delete(SetupCodeFile); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    // Authorize this device with the fresh one-time code. If that fails, show the code so it is not lost.
                    try { var result = await SignIn(p, created.S("username"), a.S("managerPassword"), created.S("enrollmentCode")); result["setup"] = true; return result; } catch (Exception e) when (e is ApiFailure or IOException or HttpRequestException or InvalidOperationException or TaskCanceledException) { return J.O("setup", true, "signedIn", false, "username", created.S("username"), "enrollmentCode", created.S("enrollmentCode")); }

                }
            case "session.reset":
                session?.Dispose();
                session = null;
                return J.O("cleared", true);
            case "logout": {

                    var current = Session;
                    if (await current.HasPending()) throw new InvalidOperationException("Reconcilia la operación incierta antes de cerrar sesión");
                    await current.Execute("session.logout", new());
                    current.Dispose();
                    session = null;
                    return J.O("ok", true);

                }
            // Before sign-in: the lock screen asks the branch for its license, and a locked branch takes a new activation.
            case "license.status": {

                    var p = await Profile() ?? throw new InvalidOperationException("Configura la conexión HTTPS y su CA");
                    using var http = TrustedConnection.Create(p);
                    using var response = await http.GetAsync("api/license");
                    var data = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject ?? new();
                    if (!response.IsSuccessStatusCode) throw new ApiFailure((int)response.StatusCode, data.S("error", "No se pudo consultar la licencia"));
                    return data;

                }
            case "license.activate": {

                    var p = await Profile() ?? throw new InvalidOperationException("Configura la conexión HTTPS y su CA");
                    if (session is not null) throw new InvalidOperationException("Cierra la sesión antes de activar");
                    using var http = TrustedConnection.Create(p);
                    using var response = await http.PostAsJsonAsync("api/license/activate", J.O("licenseId", a.S("licenseId"), "activationCode", a.S("activationCode")));
                    var data = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject ?? new();
                    if (!response.IsSuccessStatusCode) throw new ApiFailure((int)response.StatusCode, data.S("error", "No se pudo activar la licencia"));
                    return data;

                }
            case "state": {

                    // An unchanged poll stops here: the window has nothing to parse and the shift stays quiet.
                    var state = await Session.State();
                    // The branch knows what was published for each platform; only this client knows what it runs.
                    var notice = state.Obj("connection").Obj("updates").Obj(UpdatePlatform);
                    if (Version.TryParse(notice.S("version"), out var offered) && offered > Updates.Current) state["updateNotice"] = notice.DeepClone();
                    var version = J.Hash(state.ToJsonString());
                    if (a.S("known") == version) return J.O("unchanged", true);
                    state["stateVersion"] = version;
                    return state;

                }
            case "execute": {

                    var response = await Session.Execute(a.S("type"), a.Obj("payload"));
                    var result = response.Obj("result");
                    if (a.S("type") == "fiscal.token" && result.S("url") != "") { using var data = QRCoder.QRCodeGenerator.GenerateQrCode(result.S("url"), QRCoder.QRCodeGenerator.ECCLevel.Q); using var qr = new QRCoder.PngByteQRCode(data); result["qr"] = "data:image/png;base64," + Convert.ToBase64String(qr.GetGraphic(5)); }
                    if (a.S("type") == "table.qr" && result.S("link") != "") { using var data = QRCoder.QRCodeGenerator.GenerateQrCode(result.S("link"), QRCoder.QRCodeGenerator.ECCLevel.M); using var qr = new QRCoder.PngByteQRCode(data); result["qr"] = "data:image/png;base64," + Convert.ToBase64String(qr.GetGraphic(8)); }
                    return response;

                }
            case "ack":
                await Session.Acknowledge(a.S("operationId"));
                return J.O("acknowledged", true);
            case "reconcile":
                return await Session.Reconcile(a.B("retryIfUnknown"));
            case "status": {

                    await FlushFaults();
                    var saved = profile is null ? null : await vault.Read("pending_" + profile.SiteId);
                    var pending = saved is null ? null : JsonNode.Parse(saved)!.AsObject();
                    return J.O("authenticated", session is not null, "pendingOperation", pending is null ? null : J.O("type", pending.S("type"), "visitId", pending.Obj("payload").S("visitId")), "pending", session is not null && await session.HasPending(), "operation", session?.OperationState ?? "Sin sesión", "connection", session?.Connection.ToString() ?? "Unverified", "live", session?.LiveState ?? "Stopped");

                }
            case "draft.load":
                return await Session.LoadDraft(a.S("visitId"));
            case "draft.save":
                await Session.SaveDraft(a.S("visitId"), a.A("items"));
                return J.O("saved", true);
            case "catalog.cached": {

                    var p = await Profile();
                    var raw = p is null ? null : await vault.Read("last_catalog_" + p.SiteId);
                    return raw is null ? null : JsonNode.Parse(raw);

                }
            case "report":
                return await Session.Report(a.S("from"), a.S("until"), a.S("after"));
            case "audit.verify":
                return await Session.VerifyAudit();
            case "preview":
                return await Session.Preview(a.S("visitId"));
            case "fiscal.pdf":
                return await Session.FiscalPdf(a.S("id"));
            case "motion.get":
                return J.O("reduced", await vault.Read("motion.reduced") == "true");
            case "motion.set":
                await vault.Write("motion.reduced", a.B("reduced") ? "true" : "false");
                return J.O("saved", true);
            case "theme.get": { var theme = await vault.Read("ui.theme"); return J.O("theme", theme is "light" or "dark" ? theme : "system"); }
            case "theme.set": { var theme = a.S("theme"); Rules.Need(theme is "light" or "dark" or "system", "Tema no válido"); await vault.Write("ui.theme", theme); return J.O("saved", true); }
            case "fault.report":
                Queue(J.O("source", "web", "severity", a.S("severity", "error"), "what", a.S("what"), "affected", a.S("affected"), "detail", a.S("detail"), "context", a.Obj("context").DeepClone()));
                await FlushFaults();
                return J.O("queued", true);
            // Once after each update: the window tells whoever opens it what just arrived. A fresh install has nothing to announce.
            case "updates.arrived": {

                    var running = Updates.Current.ToString(3);
                    var seen = await vault.Read("update.seen");
                    if (seen == running) return J.O("arrived", false);
                    await vault.Write("update.seen", running);
                    return J.O("arrived", seen is not null, "version", running, "notes", await vault.Read("update.notes") ?? "");

                }
            // Signed in: what is on offer and what stands in the way. Nothing downloads or closes yet.
            case "updates.look": {

                    var reply = await Session.LookForUpdate(UpdatePlatform);
                    if (reply["update"] is not JsonValue) return J.O("update", null);
                    return J.O("update", true, "version", reply.S("version"), "notes", reply.S("notes"), "bytes", reply.L("bytes"), "blocked", reply.S("blocked"), "otherSessions", reply.L("otherSessions"));

                }
            case "updates.prepare": {

                    var p = await Profile() ?? throw new InvalidOperationException("Configura la conexión");
                    preparedPackage = null;
                    preparedManifest = null;
                    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MesaNexo", "updates", "tauri", J.Id());
                    if (session is null) await EndStrandedSessions(p);
                    var update = await Updates.Fetch(p, await Device(p), UpdatePlatform, directory, token: session?.Token);
                    if (update is null) return J.O("update", null);
                    preparedPackage = Path.Combine(directory, update.S("filename"));
                    preparedManifest = update;
                    return J.O("update", true, "version", update.S("version"), "notes", update.S("notes"), "bytes", update.L("bytes"));

                }
            case "updates.install": {

                    if (preparedPackage is null || preparedManifest is null) throw new InvalidOperationException("Primero valida el paquete");
                    var p = await Profile() ?? throw new InvalidOperationException("Configura la conexión");
                    Updates.Signed(preparedManifest, p.SiteId, UpdatePlatform);
                    await Updates.Verify(preparedManifest, preparedPackage, UpdatePlatform);
                    // Last, so a package that fails its checks signs nobody out.
                    if (session is { } open) {

                        if (await open.HasPending()) throw new InvalidOperationException("Reconcilia la operación incierta antes de actualizar");
                        await open.CloseForUpdate();
                        open.Dispose();
                        session = null;

                    }
                    await CheckInstallPermission(p);
                    await vault.Write("update.notes", preparedManifest.S("notes"));
                    if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(preparedPackage) { UseShellExecute = true });
                    else {

                        // culinary-update.path takes it from here as root and checks everything again.
                        const string inbox = "/var/lib/culinary-update/inbox";
                        File.Copy(preparedPackage, Path.Combine(inbox, "package.deb"), true);
                        await File.WriteAllTextAsync(Path.Combine(inbox, "offer.json"), J.O("update", preparedManifest.S("update"), "release", preparedManifest.S("release"), "keyFloor", preparedManifest.S("keyFloor")).ToJsonString());
                        await File.WriteAllTextAsync(Path.Combine(inbox, "ready"), "");

                    }
                    preparedPackage = null;
                    preparedManifest = null;
                    return J.O("started", true);

                }
            default:
                throw new InvalidOperationException("Comando del cliente no permitido");
        }

    }
    static string UpdatePlatform => OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
    async Task<JsonObject> Device(ConnectionProfile p) => JsonNode.Parse(await vault.Read("device_" + p.SiteId) ?? throw new InvalidOperationException("Este dispositivo todavía no fue autorizado"))!.AsObject();
    // Only called with no session here: whatever the branch still counts for this device died with the app. An older branch answers
    // 404, and the update check that follows reports its own block.
    async Task EndStrandedSessions(ConnectionProfile p) {

        var device = await Device(p);
        device["siteId"] = p.SiteId;
        using var http = TrustedConnection.Create(p);
        using var response = await http.PostAsJsonAsync("api/device/end-sessions", device);

    }
    async Task CheckInstallPermission(ConnectionProfile p) {

        var device = await Device(p);
        device["siteId"] = p.SiteId;
        using var http = TrustedConnection.Create(p);
        using var response = await http.PostAsJsonAsync("api/device/can-install", device);
        var permission = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Rules.Need(response.IsSuccessStatusCode && permission.B("allowed"), permission.S("error", permission.S("reason", "Verifica la licencia y la autorización del dispositivo")));

    }
    // A failure the bridge did not expect. The branch being out of reach is not one: the window already says so.
    public void Unexpected(string command, Exception e) {

        if (e is ApiFailure or InvalidOperationException or IOException or BusinessException or HttpRequestException or TaskCanceledException) return;
        Queue(J.O("source", "client", "severity", "error", "what", $"{command}: {e.GetType().Name}: {e.Message}", "affected", $"El comando {command} del cliente no terminó; quien lo pidió vio un error", "detail", e.ToString(), "context", J.O("command", command, "exception", e.GetType().Name)));

    }
    void Queue(JsonObject fault) {

        fault["version"] = Updates.Current.ToString(3);
        fault["platform"] = UpdatePlatform;
        fault.Obj("context")["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        lock (faults) if (faults.Count < 20) faults.Add(fault);

    }
    // What the kiosk supervisor saw die never reached this process; it leaves its account in a file.
    void CollectKioskFaults() {

        if (!OperatingSystem.IsLinux()) return;
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } home ? home : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/state");
        var file = Path.Combine(state, "culinary-restaurant/faults.jsonl");
        if (!File.Exists(file)) return;
        try {

            foreach (var line in File.ReadLines(file).TakeLast(10)) if (JsonNode.Parse(line) is JsonObject crash) Queue(J.O("source", "kiosk", "severity", "critical", "what", crash.S("what"), "affected", crash.S("affected"), "detail", crash.S("detail"), "context", J.O("seconds", crash.S("seconds"))));
            File.Delete(file);

        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }

    }
    async Task FlushFaults() {

        CollectKioskFaults();
        JsonObject[] waiting;
        lock (faults) waiting = [.. faults];
        // The window polls every two seconds: only a report that could not be delivered waits for the next attempt.
        if (waiting.Length == 0 || DateTimeOffset.UtcNow - faultsTried < TimeSpan.FromSeconds(30)) return;
        if (await Profile() is not { } p || await vault.Read("device_" + p.SiteId) is not { } raw) return;
        var device = JsonNode.Parse(raw)!.AsObject();
        try {

            using var http = TrustedConnection.Create(p);
            foreach (var fault in waiting) {

                fault["siteId"] = p.SiteId;
                fault["deviceId"] = device.S("deviceId");
                fault["deviceSecret"] = device.S("deviceSecret");
                using var response = await http.PostAsJsonAsync("api/faults", fault);
                if (!response.IsSuccessStatusCode) { faultsTried = DateTimeOffset.UtcNow; return; }
                lock (faults) faults.Remove(fault);

            }

        } catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { faultsTried = DateTimeOffset.UtcNow; }

    }
    public void Dispose() { session?.Dispose(); gate.Dispose(); }
}
