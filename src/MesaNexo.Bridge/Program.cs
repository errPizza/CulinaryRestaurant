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
            try { if (line.Length > 2_000_000) throw new InvalidOperationException("Solicitud demasiado grande"); message = JsonNode.Parse(line)!.AsObject(); var data = await bridge.Call(message.S("command"), message.Obj("args")); response = J.O("id", message.S("id"), "ok", true, "data", data); } catch (Exception e) { response = J.O("id", message?.S("id") ?? "", "ok", false, "error", e is ApiFailure or InvalidOperationException or IOException or BusinessException ? e.Message : "No se pudo completar la operación", "status", e is ApiFailure f ? f.Status : e is BusinessException b ? b.Status : 0); }
            await Console.Out.WriteLineAsync(response.ToJsonString());
            await Console.Out.FlushAsync();

        }

    }
}
public sealed class BridgeHost(ISecureVault vault): IDisposable {
    NativeSession? session; ConnectionProfile? profile; string? preparedPackage; JsonObject? preparedManifest;
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
        try { var result = await candidate.Login(username, password, Environment.MachineName, enrollment); session = candidate; try { await candidate.StartLive(() => Task.CompletedTask); } catch { } var safe = (JsonObject)result.DeepClone(); safe.Remove("token"); return safe; } catch { candidate.Dispose(); throw; }

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
            case "package.upload": {

                    await using var input = File.OpenRead(a.S("path"));
                    return await Session.UploadPackage(Path.GetFileName(a.S("path")), input);

                }
            case "backup.export": {

                    var destination = Path.GetFullPath(a.S("path"));
                    var temporary = destination + "." + J.Id() + ".partial";
                    try { await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) { await Session.ExportBackup(a.S("id"), output, a.S("sha256")); output.Flush(true); } File.Move(temporary, destination, true); return J.O("saved", true); } finally { if (File.Exists(temporary)) File.Delete(temporary); }

                }
            case "state":
                return await Session.State();
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
            case "motion.get":
                return J.O("reduced", await vault.Read("motion.reduced") == "true");
            case "motion.set":
                await vault.Write("motion.reduced", a.B("reduced") ? "true" : "false");
                return J.O("saved", true);
            case "theme.get": { var theme = await vault.Read("ui.theme"); return J.O("theme", theme is "light" or "dark" ? theme : "system"); }
            case "theme.set": { var theme = a.S("theme"); Rules.Need(theme is "light" or "dark" or "system", "Tema no válido"); await vault.Write("ui.theme", theme); return J.O("saved", true); }
            case "updates.prepare": {

                    if (session is not null) throw new InvalidOperationException("Cierra la sesión antes de preparar una actualización");
                    if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("La instalación asistida de esta etapa requiere Windows");
                    var p = await Profile() ?? throw new InvalidOperationException("Configura la conexión");
                    var manifest = await Updates.Verify(a.S("bundle"), a.S("package"), a.S("publicKey"), a.S("keySha256"), p.SiteId, "win-x64");
                    await CheckInstallPermission(p);
                    preparedPackage = null;
                    preparedManifest = null;
                    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MesaNexo", "updates", "tauri", J.Id());
                    Directory.CreateDirectory(directory);
                    var target = Path.Combine(directory, Path.GetFileName(a.S("package")));
                    await using (var input = File.OpenRead(a.S("package"))) await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough)) { await input.CopyToAsync(output); output.Flush(true); }
                    await using (var input = File.OpenRead(target)) Rules.Need(Convert.ToHexStringLower(await SHA512.HashDataAsync(input)) == manifest.S("sha512"), "El paquete cambió al prepararlo");
                    preparedPackage = target;
                    preparedManifest = manifest;
                    return manifest;

                }
            case "updates.install": {

                    if (session is not null || preparedPackage is null || preparedManifest is null) throw new InvalidOperationException("Primero valida el paquete con la sesión cerrada");
                    var p = await Profile() ?? throw new InvalidOperationException("Configura la conexión");
                    await CheckInstallPermission(p);
                    await using (var input = File.OpenRead(preparedPackage)) Rules.Need(Convert.ToHexStringLower(await SHA512.HashDataAsync(input)) == preparedManifest.S("sha512"), "El paquete preparado cambió");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(preparedPackage) { UseShellExecute = true });
                    preparedPackage = null;
                    preparedManifest = null;
                    return J.O("started", true);

                }
            case "updates.verify": {

                    if (session is not null) throw new InvalidOperationException("Cierra la sesión antes de verificar una actualización");
                    var p = await Profile() ?? throw new InvalidOperationException("Configura la conexión");
                    return await Updates.Verify(a.S("bundle"), a.S("package"), a.S("publicKey"), a.S("keySha256"), p.SiteId, OperatingSystem.IsWindows() ? "win-x64" : "linux-x64");

                }
            default:
                throw new InvalidOperationException("Comando del cliente no permitido");
        }

    }
    async Task CheckInstallPermission(ConnectionProfile p) {

        var raw = await vault.Read("device_" + p.SiteId) ?? throw new InvalidOperationException("Este dispositivo todavía no fue autorizado");
        var device = JsonNode.Parse(raw)!.AsObject();
        device["siteId"] = p.SiteId;
        using var http = TrustedConnection.Create(p);
        using var response = await http.PostAsJsonAsync("api/device/can-install", device);
        var permission = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Rules.Need(response.IsSuccessStatusCode && permission.B("allowed"), "Cierra sesión y todas las cuentas; verifica cobertura y autorización del dispositivo");

    }
    public void Dispose() { session?.Dispose(); gate.Dispose(); }
}
