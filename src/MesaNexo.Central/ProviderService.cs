using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Central;

public sealed partial class ProviderService(CentralStore store, IPasswordHasher passwords, string providerId, string packages, string? signingKey) {
    public void Initialize(JsonObject config) {

        Need(store.Read() is null, "El proveedor ya está inicializado");
        store.Transaction(s => { s["providerId"] = providerId; s["user"] = J.O("id", J.Id(), "name", Text(config, "name", 100), "username", Text(config, "username", 100), "role", "owner", "password", passwords.Hash(config.S("password"))); s["enrollmentHash"] = J.Hash(Text(config, "enrollmentCode", 200)); foreach (var k in new[] { "sessions", "devices", "restaurants", "plans", "releases", "publications", "audit", "tickets", "backups", "supportOutbox" }) s[k] = new JsonArray(); s.A("plans").Add(J.O("id", "base", "name", "Operación", "maxUsers", 15, "maxTables", 30, "maxBranches", 1, "price", "Por confirmar")); s.A("plans").Add(J.O("id", "plus", "name", "Expansión", "maxUsers", 50, "maxTables", 100, "maxBranches", 5, "price", "Por confirmar")); return true; });

    }
    static JsonObject Safe(JsonObject user) { var c = (JsonObject)user.DeepClone(); c.Remove("password"); return c; }
    JsonObject Auth(JsonObject s, string token, out JsonObject session) {

        session = s.A("sessions").Objects().FirstOrDefault(x => x.S("tokenHash") == J.Hash(token) && !x.B("revoked") && DateTimeOffset.Parse(x.S("expires")) > DateTimeOffset.UtcNow) ?? throw new BusinessException(401, "Inicia sesión del proveedor");
        var user = s.Obj("user");
        Need(user.S("role") == "owner", "Permiso central requerido", 403);
        var deviceId = session.S("deviceId");
        var device = s.A("devices").Objects().Single(x => x.S("id") == deviceId);
        Need(device.B("authorized"), "Dispositivo central revocado", 401);
        return user;

    }
    public JsonObject Login(JsonObject p) {

        Need(p.S("siteId") == providerId, "Identidad de proveedor incorrecta", 403);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        return store.Transaction(s => {

            var user = s.Obj("user");
            Need(user.S("username") == p.S("username") && passwords.Verify(user.S("password"), p.S("password")), "Credenciales incorrectas", 401);
            Need(Guid.TryParse(p.S("deviceId"), out var did) && p.S("deviceSecret").Length >= 32, "Dispositivo inválido");
            var id = did.ToString("N");
            var device = s.A("devices").Objects().FirstOrDefault(x => x.S("id") == id);
            if (device is null) { Need(p.S("enrollmentCode") != "" && J.Hash(p.S("enrollmentCode")) == s.S("enrollmentHash"), "Código de alta requerido", 403); device = J.O("id", id, "name", Text(p, "deviceName", 100), "secretHash", J.Hash(p.S("deviceSecret")), "authorized", true); s.A("devices").Add(device); s["enrollmentHash"] = ""; }
            Need(device.B("authorized") && device.S("secretHash") == J.Hash(p.S("deviceSecret")), "Dispositivo no autorizado", 403);
            var session = J.O("id", J.Id(), "tokenHash", J.Hash(token), "deviceId", id, "expires", DateTimeOffset.UtcNow.AddHours(12).ToString("O"), "revoked", false);
            s.A("sessions").Add(session);
            return J.O("token", token, "user", Safe(user), "siteId", providerId, "sessionId", session.S("id"));

        });

    }
    public JsonObject State(string token) {

        var s = store.Read()!;
        var user = Auth(s, token, out _);
        var output = J.O("user", Safe(user), "siteId", providerId, "restaurant", J.O("name", "Proveedor Culinary Restaurant", "revision", s.L("revision")), "connection", J.O("local", "available", "internet", "unverified", "central", "available"));
        foreach (var k in new[] { "restaurants", "plans", "releases", "publications", "audit", "tickets", "backups", "supportOutbox" }) output[k] = s[k]?.DeepClone();
        return output;

    }
    public JsonObject Action(string token, JsonObject request) {

        var current = store.Read()!;
        var user = Auth(current, token, out _);
        Need(Guid.TryParse(request.S("operationId"), out var guid), "UUID requerido");
        var id = guid.ToString("N");
        var type = Text(request, "type", 100);
        var payload = request.Obj("payload");
        var fingerprint = J.Hash(J.Canonical(J.O("type", type, "payload", payload, "actor", user.S("id"))));
        JsonObject? backupResult = null;
        if (type == "owner.backup") {

            var folder = Path.Combine(Path.GetDirectoryName(packages)!, "backups");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, id + ".sqlite");
            var digest = File.Exists(file) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))) : store.Snapshot(file);
            backupResult = J.O("id", id, "at", J.Now(), "sha256", digest, "bytes", new FileInfo(file).Length, "encrypted", true);

        }
        var result = store.Apply(id, fingerprint, user.S("id"), s => {

            var authenticated = Auth(s, token, out var session);
            JsonObject Find(string kind, string key) => s.A(kind).Objects().FirstOrDefault(x => x.S("id") == key) ?? throw new BusinessException(404, "Registro central no encontrado");
            JsonObject result = J.O("ok", true);
            var pid = payload.S("id");
            switch (type) {
                case "owner.backup":
                    result = backupResult!;
                    s.A("backups").Add(result.DeepClone());
                    break;
                case "session.logout":
                    session["revoked"] = true;
                    break;
                case "device.enrollment":
                    Need(payload.S("code").Length >= 20, "Código demasiado corto");
                    s["enrollmentHash"] = J.Hash(payload.S("code"));
                    break;
                case "owner.password":
                    Need(passwords.Verify(authenticated.S("password"), payload.S("currentPassword")), "Contraseña actual incorrecta", 403);
                    authenticated["password"] = passwords.Hash(payload.S("newPassword"));
                    foreach (var sess in s.A("sessions").Objects()) sess["revoked"] = true;
                    break;
                case "owner.restaurant": {

                        var isNew = pid == "";
                        var r = isNew ? J.O("id", J.Id(), "modules", Array.Empty<string>(), "revision", 0) : Find("restaurants", pid);
                        var mode = Choice(payload, "mode", "renta", "licencia", "codigo");
                        var plan = Find("plans", payload.S("planId"));
                        var old = r.Obj("commercial");
                        if (isNew || old.S("mode") != mode) r["commercial"] = Commercial(mode);
                        r["name"] = Text(payload, "name", 150);
                        r["status"] = Choice(payload, "status", "active", "suspended");
                        r["planId"] = plan.S("id");
                        r["deployment"] = Choice(payload, "deployment", "central", "dedicated", "distributor");
                        r["distributor"] = payload.S("distributor");
                        r["dedicatedUrl"] = payload.S("dedicatedUrl");
                        r["revision"] = r.L("revision") + 1;
                        r["siteId"] = Text(payload, "siteId", 32);
                        Need(System.Text.RegularExpressions.Regex.IsMatch(r.S("siteId"), "^[a-z0-9-]{1,32}$"), "Identidad de sucursal inválida");
                        Need(!s.A("restaurants").Objects().Any(x => x.S("siteId") == r.S("siteId") && x.S("id") != r.S("id")), "Identidad ya registrada");
                        r["clientCertificateSha256"] = payload.S("clientCertificateSha256").Replace(":", "").ToLowerInvariant();
                        if (isNew) { r["tenantId"] = r.S("id"); s.A("restaurants").Add(r); }
                        result = (JsonObject)r.DeepClone();
                        break;

                    }
                case "owner.branch": {

                        var parent = Find("restaurants", payload.S("restaurantId"));
                        var plan = Find("plans", parent.S("planId"));
                        Need(parent.A("modules").Strings().Contains("multiBranch") && plan.L("maxBranches") > 1, "Contrata varias sucursales y un plan compatible", 403);
                        var tenant = parent.S("tenantId", parent.S("id"));
                        Need(s.A("restaurants").Objects().Count(x => x.S("tenantId", x.S("id")) == tenant) < plan.L("maxBranches"), "Límite de sucursales del plan");
                        var site = Text(payload, "siteId", 32);
                        Need(System.Text.RegularExpressions.Regex.IsMatch(site, "^[a-z0-9-]{1,32}$") && !s.A("restaurants").Objects().Any(x => x.S("siteId") == site), "Identidad inválida o existente");
                        var branch = (JsonObject)parent.DeepClone();
                        branch["id"] = J.Id();
                        branch["tenantId"] = tenant;
                        branch["siteId"] = site;
                        branch["name"] = Text(payload, "name", 150);
                        branch["clientCertificateSha256"] = payload.S("clientCertificateSha256").Replace(":", "").ToLowerInvariant();
                        branch["revision"] = 1;
                        s.A("restaurants").Add(branch);
                        result = (JsonObject)branch.DeepClone();
                        break;

                    }
                case "owner.plan": { var plan = pid == "" ? J.O("id", J.Id()) : Find("plans", pid); plan["name"] = Text(payload, "name", 100); plan["maxUsers"] = Amount(payload, "maxUsers", 1, 10000); plan["maxTables"] = Amount(payload, "maxTables", 1, 10000); plan["maxBranches"] = Amount(payload, "maxBranches", 1, 1000); plan["price"] = "Por confirmar"; if (pid == "") s.A("plans").Add(plan); break; }
                case "owner.modules": { var r = Find("restaurants", pid); var modules = payload.A("modules").Strings(); Need(modules.All(Restaurant.Modules.Contains), "Módulo desconocido"); var tenant = r.S("tenantId", r.S("id")); var branches = s.A("restaurants").Objects().Where(x => x.S("tenantId", x.S("id")) == tenant).ToArray(); Need(modules.Contains("multiBranch") || branches.Length <= 1, "Consolida sucursales antes de retirar el módulo"); foreach (var b in branches) { b["modules"] = J.N(modules.Distinct().ToArray()); b["revision"] = b.L("revision") + 1; } break; }
                case "owner.service": {

                        var r = Find("restaurants", pid);
                        var c = r.Obj("commercial");
                        var kind = Choice(payload, "service", "usage", "hosting", "support", "maintenance", "updates", "resale");
                        if (new[] { "maintenance", "updates", "resale" }.Contains(kind)) { c[kind == "resale" ? "resaleAuthorized" : kind] = payload.B("enabled"); if (kind == "updates") { var expiry = payload.S("until"); Need(expiry == "" || DateTimeOffset.TryParse(expiry, out _), "Fecha de cobertura inválida"); c["updatesUntil"] = expiry; } } else { Need(kind != "usage" || c.S("mode") == "renta", "La licencia permanente conserva su derecho de uso"); Need(!DateTimeOffset.TryParse(c.S(kind + "IncludedUntil"), out var included) || included <= DateTimeOffset.UtcNow, "Servicio ya incluido; no se cobra nuevamente"); Need(DateTimeOffset.TryParse(payload.S("until"), out var until), "Fecha inválida"); c[kind + "Until"] = until.ToString("O"); if (kind == "usage" && c.S("mode") == "renta") c["hostingUntil"] = until.ToString("O"); }
                        r["revision"] = r.L("revision") + 1;
                        break;

                    }
                case "owner.release": {

                        Need(System.Text.RegularExpressions.Regex.IsMatch(payload.S("version"), "^\\d+\\.\\d+\\.\\d+$"), "Versión semántica requerida");
                        var filename = PackageName(payload.S("filename"));
                        var file = Path.Combine(packages, filename);
                        Need(File.Exists(file), "Carga primero un paquete real");
                        Need(signingKey is not null && File.Exists(signingKey), "Falta la credencial privada de firma de releases; publicación bloqueada", 503);
                        using var stream = File.OpenRead(file);
                        var hash = Convert.ToHexStringLower(SHA512.HashData(stream));
                        var manifest = J.O("version", payload.S("version"), "filename", filename, "sha512", hash, "bytes", stream.Length, "platform", Choice(payload, "platform", "win-x64", "linux-x64", "android-arm64"), "schema", 1, "minimumApi", 1, "at", J.Now());
                        using var signer = RSA.Create();
                        signer.ImportFromPem(File.ReadAllText(signingKey!));
                        var signature = Convert.ToBase64String(signer.SignData(System.Text.Encoding.UTF8.GetBytes(J.Canonical(manifest)), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
                        result = J.O("id", J.Id(), "manifest", manifest, "signature", signature, "description", Text(payload, "description"), "changes", Text(payload, "changes"));
                        s.A("releases").Add(result.DeepClone());
                        break;

                    }
                case "owner.publish": { Find("releases", payload.S("releaseId")); var targets = payload.A("restaurantIds").Strings(); Need(targets.Length > 0, "Selecciona destinatarios"); foreach (var target in targets) Find("restaurants", target); result = J.O("id", J.Id(), "releaseId", payload.S("releaseId"), "restaurantIds", targets.Distinct().ToArray(), "active", true, "at", J.Now()); s.A("publications").Add(result.DeepClone()); break; }
                case "owner.withdraw":
                    Find("publications", pid)["active"] = false;
                    break;
                case "owner.ticket.reply": {

                        var ticket = Find("tickets", pid);
                        var message = J.O("id", J.Id(), "actor", authenticated.S("id"), "name", authenticated.S("name"), "message", Text(payload, "message"), "at", J.Now());
                        s.A("supportOutbox").Add(J.O("siteId", ticket.S("siteId"), "ticketId", ticket.S("ticketId"), "message", message));
                        var r = s.A("restaurants").Objects().Single(x => x.S("siteId") == ticket.S("siteId"));
                        r["revision"] = r.L("revision") + 1;
                        result = J.O("queued", true, "messageId", message.S("id"));
                        break;

                    }
                case "owner.updateBundle":
                    result = UpdateBundle(s, Find("restaurants", pid), payload.S("platform"));
                    break;
                case "owner.entitlement": {

                        var r = Find("restaurants", pid);
                        var plan = Find("plans", r.S("planId"));
                        Need(signingKey is not null && File.Exists(signingKey), "Falta clave de firma", 503);
                        var data = J.O("format", "mesa-nexo-entitlement-v1", "siteId", r.S("siteId"), "revision", r.L("revision"), "status", r.S("status"), "commercial", r.Obj("commercial"), "modules", r.A("modules"), "limits", plan, "issuedAt", J.Now(), "supportReplies", s.A("supportOutbox").Objects().Where(x => x.S("siteId") == r.S("siteId")).Take(1).ToArray());
                        using var signer = RSA.Create();
                        signer.ImportFromPem(File.ReadAllText(signingKey!));
                        result = J.O("data", data, "signature", Convert.ToBase64String(signer.SignData(System.Text.Encoding.UTF8.GetBytes(J.Canonical(data)), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
                        break;

                    }
                default:
                    throw new BusinessException(404, "Operación central no implementada");
            }
            s["revision"] = s.L("revision") + 1;
            s.A("audit").Add(J.O("id", id, "actor", user.S("id"), "operation", type, "at", J.Now(), "requestFingerprint", fingerprint));
            return result;

        });
        return J.O("operationId", id, "local", "committed", "result", result);

    }
    public JsonObject Status(string token, string id) { var s = store.Read()!; var user = Auth(s, token, out _); Need(Guid.TryParse(id, out var guid), "UUID inválido"); var result = store.Receipt(guid.ToString("N"), user.S("id")); return J.O("local", result is null ? "unknown" : "committed", "remote", "not_applicable", "result", result); }
    public bool AllowedCertificate(X509Certificate2 certificate) {

        if (certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow) return false;
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (eku is null || !eku.EnhancedKeyUsages.Cast<Oid>().Any(x => x.Value == "1.3.6.1.5.5.7.3.2")) return false;
        var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
        return store.Read()!.A("restaurants").Objects().Any(x => x.S("clientCertificateSha256") == fingerprint);

    }
    public JsonObject Receive(byte[] raw, string digest, X509Certificate2 certificate) {

        Need(AllowedCertificate(certificate), "Certificado no aprovisionado", 403);
        Need(Convert.ToHexStringLower(SHA256.HashData(raw)) == digest, "Digest del cuerpo inválido");
        var body = JsonNode.Parse(raw)!;
        Need(body.S("protocol") == "culinaryos-events-v1", "Protocolo inválido");
        var site = body.S("site_id");
        Need(System.Text.RegularExpressions.Regex.IsMatch(body.S("event_id"), "^[A-Za-z0-9_-]{1,96}$") && System.Text.RegularExpressions.Regex.IsMatch(body.S("record_id"), "^[A-Za-z0-9_-]{1,96}$") && body.L("version") >= 1, "Identificadores de evento inválidos");
        var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
        Need(store.Read()!.A("restaurants").Objects().Any(x => x.S("siteId") == site && x.S("clientCertificateSha256") == fingerprint), "Certificado de otra sucursal", 403);
        return store.Receive(site, body.S("event_id"), digest, raw);

    }
    JsonObject UpdateBundle(JsonObject state, JsonObject restaurant, string platform) {

        var c = restaurant.Obj("commercial");
        Need(c.B("updates") && (!DateTimeOffset.TryParse(c.S("updatesUntil"), out var until) || until > DateTimeOffset.UtcNow), "Cobertura de actualizaciones vencida", 403);
        Need(c.S("mode") != "renta" || (DateTimeOffset.TryParse(c.S("usageUntil"), out var use) && use > DateTimeOffset.UtcNow), "Derecho de uso vencido", 403);
        var release = state.A("publications").Objects().Where(p => p.B("active") && p.A("restaurantIds").Strings().Contains(restaurant.S("id"))).Reverse().Select(p => state.A("releases").Objects().Single(r => r.S("id") == p.S("releaseId"))).FirstOrDefault(r => r.Obj("manifest").S("platform") == platform);
        Need(release is not null, "Sin publicación autorizada", 404);
        Need(signingKey is not null && File.Exists(signingKey), "Falta clave de firma", 503);
        var expiry = DateTimeOffset.UtcNow.AddDays(1);
        foreach (var field in new[] { "updatesUntil", "usageUntil" })
        if (DateTimeOffset.TryParse(c.S(field), out var coverage) && coverage < expiry) expiry = coverage;
        var data = J.O("contract", "mesa-nexo-update-v1", "siteId", restaurant.S("siteId"), "manifest", release!.Obj("manifest"), "expires", expiry.ToString("O"));
        using var signer = RSA.Create();
        signer.ImportFromPem(File.ReadAllText(signingKey!));
        return J.O("data", data, "signature", Convert.ToBase64String(signer.SignData(System.Text.Encoding.UTF8.GetBytes(J.Canonical(data)), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));

    }
    JsonObject RestaurantForCertificate(X509Certificate2 certificate) { Need(AllowedCertificate(certificate), "Certificado no autorizado", 403); var fp = certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant(); return store.Read()!.A("restaurants").Objects().Single(x => x.S("clientCertificateSha256") == fp); }
    public JsonObject Entitlement(X509Certificate2 certificate) {

        var s = store.Read()!;
        var r = RestaurantForCertificate(certificate);
        var plan = s.A("plans").Objects().Single(x => x.S("id") == r.S("planId"));
        Need(signingKey is not null && File.Exists(signingKey), "Falta firma de contratación", 503);
        var data = J.O("format", "mesa-nexo-entitlement-v1", "siteId", r.S("siteId"), "revision", r.L("revision"), "status", r.S("status"), "commercial", r.Obj("commercial"), "modules", r.A("modules"), "limits", plan, "issuedAt", J.Now(), "supportReplies", s.A("supportOutbox").Objects().Where(x => x.S("siteId") == r.S("siteId")).Take(1).ToArray());
        using var signer = RSA.Create();
        signer.ImportFromPem(File.ReadAllText(signingKey!));
        return J.O("data", data, "signature", Convert.ToBase64String(signer.SignData(System.Text.Encoding.UTF8.GetBytes(J.Canonical(data)), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));

    }
    public JsonObject Latest(X509Certificate2 certificate, string platform) { var s = store.Read()!; return UpdateBundle(s, RestaurantForCertificate(certificate), platform); }
    public string Download(X509Certificate2 certificate, string filename) {

        filename = PackageName(filename);
        var s = store.Read()!;
        var r = RestaurantForCertificate(certificate);
        var release = s.A("releases").Objects().FirstOrDefault(x => x.Obj("manifest").S("filename") == filename) ?? throw new BusinessException(404, "Paquete no encontrado");
        var authorized = UpdateBundle(s, r, release.Obj("manifest").S("platform"));
        Need(authorized.Obj("data").Obj("manifest").S("filename") == filename, "Destinatario o publicación no autorizados", 403);
        return Path.Combine(packages, filename);

    }
    public string BackupFile(string token, string id) {

        var s = store.Read()!;
        Auth(s, token, out _);
        Need(Guid.TryParse(id, out var guid), "ID de respaldo inválido");
        id = guid.ToString("N");
        Need(s.A("backups").Objects().Any(x => x.S("id") == id), "Respaldo no registrado", 404);
        var path = Path.Combine(Path.GetDirectoryName(packages)!, "backups", id + ".sqlite");
        Need(File.Exists(path), "Archivo no disponible", 404);
        return path;

    }
    public void AuthorizeUpload(string token) => Auth(store.Read()!, token, out _);
    public static string PackageName(string name) { Need(System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._-]{0,150}\\.(exe|deb|apk)$"), "Nombre de paquete inválido"); return name; }
    static JsonObject Commercial(string mode) { var now = DateTimeOffset.UtcNow; return J.O("mode", mode, "activation", now.ToString("O"), "price", "Por confirmar", "usageUntil", mode == "renta" ? now.AddDays(30).ToString("O") : null, "supportUntil", mode == "licencia" ? now.AddYears(1).ToString("O") : null, "supportIncludedUntil", mode == "licencia" ? now.AddYears(1).ToString("O") : null, "hostingUntil", mode == "licencia" ? now.AddMonths(2).ToString("O") : mode == "renta" ? now.AddDays(30).ToString("O") : null, "hostingIncludedUntil", mode == "licencia" ? now.AddMonths(2).ToString("O") : null, "installationIncluded", mode != "codigo", "nonExclusive", true, "modificationRights", mode == "codigo", "resaleAuthorized", false, "updates", mode != "codigo", "updatesUntil", mode == "licencia" ? now.AddYears(1).ToString("O") : null, "maintenance", false, "sourceResponsibility", mode == "codigo" ? "comprador" : "proveedor"); }
}