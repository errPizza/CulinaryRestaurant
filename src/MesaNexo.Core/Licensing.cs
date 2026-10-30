using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;

// Documents signed by the provider: compact JWS with EdDSA over Ed25519, verified on the exact bytes received. The
// root public key comes only from this assembly, never from the network or a file. The root stays offline and signs
// intermediates; a document signed by one carries its certificate in the "chain" header, and the intermediate may sign
// only the types it lists. Documents the root signed itself (licenses issued before the intermediates) stay valid.
public static class Jws {
    public const string License = "culinary-license+jws", Status = "culinary-license-status+jws", Update = "culinary-update+jws", Release = "culinary-release+jws";
    public const string Intermediate = "culinary-intermediate+jws", Floor = "culinary-key-floor+jws";
    static readonly byte[] SpkiPrefix = Convert.FromHexString("302a300506032b6570032100");
    static readonly Lazy<byte[]?> Root = new(() => {

        using var stream = typeof(Jws).Assembly.GetManifestResourceStream("culinary-root-public.pem");
        if (stream is null) return null;
        var spki = Convert.FromBase64String(string.Concat(new StreamReader(stream).ReadToEnd().Split('\n').Where(x => !x.StartsWith("-----")).Select(x => x.Trim())));
        return spki.Length == 44 && spki.AsSpan(0, 12).SequenceEqual(SpkiPrefix) ? spki[12..] : null;

    });
    public static bool HasRoot => Root.Value is not null;
    // Same derivation as CulinaryServer: the first 16 base64url characters of SHA-256 over the raw public key.
    public static string RootKid => Root.Value is { } raw ? Kid(raw) : "";
    static string Kid(byte[] raw) => Base64Url.EncodeToString(SHA256.HashData(raw))[..16];
    // Lowest intermediate generation still valid per use, raised by the root to revoke a compromised intermediate.
    public sealed record KeyFloor(long License, long Release) {
        public static readonly KeyFloor None = new(0, 0);
        public long For(string type) => type == Jws.Release ? Release : License;
        public KeyFloor Max(KeyFloor other) => new(Math.Max(License, other.License), Math.Max(Release, other.Release));
        // A missing or unverifiable floor counts as none: it can only be raised by something the root signed.
        public static KeyFloor Read(string? jws) {

            if (string.IsNullOrEmpty(jws)) return None;
            try { var floor = Signed(jws, Floor, Root.Value ?? throw new BusinessException(503, "Esta compilación no trae la llave raíz del proveedor"), out _); return new(floor.L("license"), floor.L("release")); } catch (BusinessException) { return None; }

        }
    }
    public static JsonObject Verify(string jws, string type, KeyFloor? floor = null) {

        var root = Root.Value ?? throw new BusinessException(503, "Esta compilación no trae la llave raíz del proveedor");
        var header = Header(jws);
        if (header["chain"] is null) return Signed(jws, type, root, out _);
        var certificate = Signed(header.S("chain"), Intermediate, root, out _);
        byte[] key;
        try { key = Base64Url.DecodeFromChars(certificate.S("key")); } catch (FormatException) { throw new BusinessException(403, "Llave intermedia ilegible"); }
        Need(key.Length == 32 && certificate.S("kid") == Kid(key) && header.S("kid") == certificate.S("kid"), "Llave intermedia inconsistente", 403);
        Need(certificate.A("uses").Strings().Contains(type), "La llave intermedia no puede firmar este tipo de documento", 403);
        Need(certificate.L("generation") >= (floor ?? KeyFloor.None).For(type), "La llave intermedia del proveedor fue revocada", 403);
        var payload = Signed(jws, type, key, out _);
        // Validity is checked at signing time, so a license signed while the intermediate was valid keeps verifying.
        Need(DateTimeOffset.TryParse(payload.S("iat"), out var iat) && DateTimeOffset.TryParse(certificate.S("nbf"), out var nbf) && DateTimeOffset.TryParse(certificate.S("exp"), out var exp) && iat >= nbf && iat <= exp, "Firmado fuera de la vigencia de la llave intermedia", 403);
        return payload;

    }
    // An update offer: the authorization (license intermediate, online) names the branch; the manifest (release
    // intermediate, offline) vouches for the bytes. Both must describe the same package, so the Worker alone cannot
    // authorize other bytes. Returns the authorization.
    public static JsonObject VerifyUpdate(JsonObject offer, KeyFloor? known = null) {

        var floor = (known ?? KeyFloor.None).Max(KeyFloor.Read(offer.S("keyFloor")));
        var update = Verify(offer.S("update"), Update, floor);
        var release = Verify(offer.S("release"), Release, floor);
        foreach (var field in new[] { "releaseId", "version", "platform", "filename", "sha512" }) Need(release.S(field) == update.S(field), "El manifiesto del paquete no corresponde a la actualización", 403);
        Need(release.L("bytes") == update.L("bytes"), "El manifiesto del paquete no corresponde a la actualización", 403);
        return update;

    }
    static JsonObject Header(string jws) {

        Need(jws.Length <= 32768 && jws.Count(c => c == '.') == 2, "Documento firmado inválido", 403);
        try { return Decode(jws[..jws.IndexOf('.')]); } catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or InvalidOperationException) { throw new BusinessException(403, "Documento firmado ilegible"); }

    }
    static JsonObject Signed(string jws, string type, byte[] key, out JsonObject header) {

        var parts = jws.Split('.');
        Need(parts.Length == 3 && jws.Length <= 32768, "Documento firmado inválido", 403);
        JsonObject payload;
        byte[] signature;
        try { header = Decode(parts[0]); payload = Decode(parts[1]); signature = Base64Url.DecodeFromChars(parts[2]); } catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or InvalidOperationException) { throw new BusinessException(403, "Documento firmado ilegible"); }
        Need(header.S("alg") == "EdDSA" && header.S("typ") == type, "El documento firmado no es del tipo esperado", 403);
        var signer = new Ed25519Signer();
        signer.Init(false, new Ed25519PublicKeyParameters(key));
        var input = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        signer.BlockUpdate(input, 0, input.Length);
        Need(signature.Length == 64 && signer.VerifySignature(signature), "Firma del proveedor inválida", 403);
        Need(payload["v"] is JsonValue v && v.TryGetValue<long>(out var version) && version == 1, "Versión de documento no admitida", 403);
        return payload;

    }
    static JsonObject Decode(string part) => JsonNode.Parse(Base64Url.DecodeFromChars(part)) as JsonObject ?? throw new FormatException();
}

// What a branch may do under its license. State "active" is the only usable one; every other state locks the
// branch and is shown, with plan and expiry, on the lock screen. A lock only refuses requests: nothing here deletes,
// changes or re-encrypts the branch's data, and the same data comes back when the license does.
public sealed record LicenseView(string State, JsonObject? Payload, JsonObject? Warning = null) {
    public static readonly LicenseView Missing = new("missing", null);
    public bool Usable => State == "active";
    public string[] Features => Payload?.A("features").Strings() ?? [];
    // Commercial level (lite/pro) and paid add-ons (mobileOrdering, deliveryHub, webOrdering) of the 2026 proposal.
    public string Tier => Payload?.S("tier") is "pro" ? "pro" : "lite";
    public string[] Addons => Payload?.A("addons").Strings() ?? [];
    // Everything the branch may switch on: the contracted features, what Pro includes and the add-ons.
    public string[] Modules => [.. Features.Concat(Tier == "pro" ? ["reservations"] : []).Concat(Addons).Distinct().Order()];
    // Fixed terminals; mobile order takers do not count. Older licenses without the field follow the tier.
    public long MaxTerminals => Payload.Obj("limits")["maxTerminals"] is JsonValue ? Payload.Obj("limits").L("maxTerminals") : Tier == "pro" ? 10 : 2;
    public long MaxUsers => Payload.Obj("limits").L("maxUsers");
    public long MaxTables => Payload.Obj("limits").L("maxTables");
    public string Message => State switch {
        "active" => "Licencia vigente",
        "expired" => "La licencia de Culinary Restaurant venció",
        "revoked" => "La licencia de Culinary Restaurant fue cancelada",
        "offline" => "No se pudo validar la licencia con Culinary en los últimos 7 días",
        "clock" => "La fecha del equipo está atrasada; corrígela para continuar",
        "mismatch" => "La licencia no corresponde a este equipo",
        "transferred" => "La licencia de Culinary Restaurant pasó a otro equipo",
_ => "Culinary Restaurant no tiene una licencia activada"
    };
    // Public: shown before sign-in, so it carries no business data.
    public JsonObject Summary() {

        var o = J.O("state", State, "message", Message);
        if (Payload is null) return o;
        foreach (var key in new[] { "licenseId", "plan", "customer", "branchName", "expiresAt", "validatedAt", "offlineUntil", "activatedAt" }) o[key] = Payload[key]?.DeepClone();
        if (Warning is not null) o["warning"] = Warning.DeepClone();
        return o;

    }
    // A notice is applied only after its challenge was checked against the request that received it.
    public static LicenseView Evaluate(JsonObject payload, JsonObject? notice, string siteId, string keySpki, DateTimeOffset now, bool clockBehind, bool hardwareRefused = false) {

        if (payload.S("siteId") != siteId || payload.Obj("key").S("spki") != keySpki) return new("mismatch", payload);
        if (notice is not null && notice.L("revision") >= payload.L("revision") && notice.S("status") is "revoked" or "expired" or "transferred") return new(notice.S("status"), payload);
        if (payload.S("status") == "revoked") return new("revoked", payload);
        if (clockBehind) return new("clock", payload);
        if (payload["expiresAt"] is JsonValue && Time(payload, "expiresAt") <= now) return new("expired", payload);
        if (Time(payload, "offlineUntil") <= now) return new("offline", payload);
        return new("active", payload, Warn(payload, now, hardwareRefused));

    }
    // Whichever lock comes first, from 14 days before the term ends or once the service has been silent for a day
    // (the branch renews every 6 hours). A silent service counts down the signed offlineUntil, never the term.
    static JsonObject? Warn(JsonObject p, DateTimeOffset now, bool hardwareRefused) {

        var ends = p["expiresAt"] is JsonValue ? Time(p, "expiresAt") : DateTimeOffset.MaxValue;
        var grace = Time(p, "offlineUntil");
        var service = (hardwareRefused || now - Time(p, "validatedAt") >= TimeSpan.FromDays(1)) && grace < ends;
var left = (service ? grace : ends) - now;
        if (left > TimeSpan.FromDays(14)) return null;
        var days = (int)Math.Ceiling(left.TotalDays);
        var span = days <= 1 ? "menos de un día" : days + " días";
        var message = service && hardwareRefused ? $"Culinary ya no reconoce este equipo porque cambiaron varias piezas. La sucursal sigue trabajando; pide a tu proveedor la transferencia de la licencia. Sin ella, se bloquea en {span}."
            : service ? $"No se ha podido validar la licencia con Culinary. La sucursal sigue trabajando; revisa el internet del servidor. Sin validarla, se bloquea en {span}."
            : p.S("plan") == "trial" ? $"El periodo de prueba termina en {span}. Contrata un plan con tu proveedor para seguir trabajando."
            : $"La licencia vence en {span}. Renuévala con tu proveedor para seguir trabajando.";
        return J.O("kind", service ? hardwareRefused ? "hardware" : "service" : "expiry", "days", days, "level", days <= 3 ? "critical" : "notice", "message", message);

    }
    public static DateTimeOffset OfflineUntil(JsonObject payload) => Time(payload, "offlineUntil");
    // The newest moment a trusted party signed: the branch's own clock may never read earlier than this.
    public static DateTimeOffset Signed(JsonObject payload) => new[] { Time(payload, "validatedAt"), Time(payload, "iat") }.Max();
    static DateTimeOffset Time(JsonObject p, string key) => DateTimeOffset.TryParse(p.S(key), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.MinValue;
}
