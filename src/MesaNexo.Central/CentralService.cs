using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Central;

// The multi-company central of a restaurant group: owner analytics, cloud orders and the mirror of branch events.
// Licenses, releases and support belong to CulinaryServer; branches reach this service only with their mTLS
// certificate, registered here with --provision-tenant.
public sealed partial class CentralService(CentralStore store, IPasswordHasher passwords, string centralId, string? signingKey) {
    public bool AllowedCertificate(X509Certificate2 certificate) {

        if (certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow) return false;
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (eku is null || !eku.EnhancedKeyUsages.Cast<Oid>().Any(x => x.Value == "1.3.6.1.5.5.7.3.2")) return false;
        return store.CertificateSite(Fingerprint(certificate)) is not null;

    }
    static string Fingerprint(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant();
    string SiteFor(X509Certificate2 certificate) { Need(AllowedCertificate(certificate), "Certificado no aprovisionado", 403); return store.CertificateSite(Fingerprint(certificate))!; }
    public JsonObject Receive(byte[] raw, string digest, X509Certificate2 certificate) {

        var registered = SiteFor(certificate);
        Need(Convert.ToHexStringLower(SHA256.HashData(raw)) == digest, "Digest del cuerpo inválido");
        var body = JsonNode.Parse(raw)!;
        Need(body.S("protocol") == "culinaryos-events-v1", "Protocolo inválido");
        var site = body.S("site_id");
        Need(System.Text.RegularExpressions.Regex.IsMatch(body.S("event_id"), "^[A-Za-z0-9_-]{1,96}$") && System.Text.RegularExpressions.Regex.IsMatch(body.S("record_id"), "^[A-Za-z0-9_-]{1,96}$") && body.L("version") >= 1, "Identificadores de evento inválidos");
        Need(site == registered, "Certificado de otra sucursal", 403);
        return store.Receive(site, body.S("event_id"), digest, raw);

    }
}
