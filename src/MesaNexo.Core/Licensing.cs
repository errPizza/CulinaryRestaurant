using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;

// Documents signed by the provider's root (CulinaryServer): compact JWS with EdDSA over Ed25519, verified on the
// exact bytes received. The root public key comes only from this assembly, never from the network or a file.
public static class Jws {
    public const string License = "culinary-license+jws", Status = "culinary-license-status+jws", Update = "culinary-update+jws", Release = "culinary-release+jws";
    static readonly byte[] SpkiPrefix = Convert.FromHexString("302a300506032b6570032100");
    static readonly Lazy<byte[]?> Root = new(() => {

        using var stream = typeof(Jws).Assembly.GetManifestResourceStream("culinary-root-public.pem");
        if (stream is null) return null;
        var spki = Convert.FromBase64String(string.Concat(new StreamReader(stream).ReadToEnd().Split('\n').Where(x => !x.StartsWith("-----")).Select(x => x.Trim())));
        return spki.Length == 44 && spki.AsSpan(0, 12).SequenceEqual(SpkiPrefix) ? spki[12..] : null;

    });
    public static bool HasRoot => Root.Value is not null;
    // Same derivation as CulinaryServer: the first 16 base64url characters of SHA-256 over the raw public key.
    public static string RootKid => Root.Value is { } raw ? Base64Url.EncodeToString(SHA256.HashData(raw))[..16] : "";
    public static JsonObject Verify(string jws, string type) {

        var root = Root.Value ?? throw new BusinessException(503, "Esta compilación no trae la llave raíz del proveedor");
        var parts = jws.Split('.');
        Need(parts.Length == 3 && jws.Length <= 32768, "Documento firmado inválido", 403);
        JsonObject header, payload;
        byte[] signature;
        try { header = Decode(parts[0]); payload = Decode(parts[1]); signature = Base64Url.DecodeFromChars(parts[2]); } catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or InvalidOperationException) { throw new BusinessException(403, "Documento firmado ilegible"); }
        Need(header.S("alg") == "EdDSA" && header.S("typ") == type, "El documento firmado no es del tipo esperado", 403);
        var signer = new Ed25519Signer();
        signer.Init(false, new Ed25519PublicKeyParameters(root));
        var input = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        signer.BlockUpdate(input, 0, input.Length);
        Need(signature.Length == 64 && signer.VerifySignature(signature), "Firma del proveedor inválida", 403);
        Need(payload["v"] is JsonValue v && v.TryGetValue<long>(out var version) && version == 1, "Versión de documento no admitida", 403);
        return payload;

    }
    static JsonObject Decode(string part) => JsonNode.Parse(Base64Url.DecodeFromChars(part)) as JsonObject ?? throw new FormatException();
}

// What a branch may do under its license. State "active" is the only usable one; every other state locks the
// branch and is shown, with plan and expiry, on the lock screen.
public sealed record LicenseView(string State, JsonObject? Payload) {
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
        "offline" => "No se pudo validar la licencia con Culinary en los últimos 3 días",
        "clock" => "La fecha del equipo está atrasada; corrígela para continuar",
        "mismatch" => "La licencia no corresponde a este equipo",
        _ => "Culinary Restaurant no tiene una licencia activada"
    };
    // Public: shown before sign-in, so it carries no business data.
    public JsonObject Summary() {

        var o = J.O("state", State, "message", Message);
        if (Payload is null) return o;
        foreach (var key in new[] { "licenseId", "plan", "customer", "branchName", "expiresAt", "validatedAt", "offlineUntil", "activatedAt" }) o[key] = Payload[key]?.DeepClone();
        return o;

    }
    // A notice is applied only after its challenge was checked against the request that received it.
    public static LicenseView Evaluate(JsonObject payload, JsonObject? notice, string siteId, string keySpki, DateTimeOffset now, bool clockBehind) {

        if (payload.S("siteId") != siteId || payload.Obj("key").S("spki") != keySpki) return new("mismatch", payload);
        if (notice is not null && notice.L("revision") >= payload.L("revision") && notice.S("status") is "revoked" or "expired") return new(notice.S("status"), payload);
        if (payload.S("status") == "revoked") return new("revoked", payload);
        if (clockBehind) return new("clock", payload);
        if (payload["expiresAt"] is JsonValue && Time(payload, "expiresAt") <= now) return new("expired", payload);
        if (Time(payload, "offlineUntil") <= now) return new("offline", payload);
        return new("active", payload);

    }
    // The newest moment a trusted party signed: the branch's own clock may never read earlier than this.
    public static DateTimeOffset Signed(JsonObject payload) => new[] { Time(payload, "validatedAt"), Time(payload, "iat") }.Max();
    static DateTimeOffset Time(JsonObject p, string key) => DateTimeOffset.TryParse(p.S(key), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.MinValue;
}
