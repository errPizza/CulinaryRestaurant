using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Central;

public sealed partial class CentralService {
    public JsonObject LoginTenant(JsonObject p) => store.TenantLogin(p, passwords);
    public JsonObject StateTenant(string token, string branch) {

        var analytics = new CommerceService(store).Analytics(token, branch);
        return J.O("siteId", centralId, "tenantId", analytics.S("tenantId"), "branchId", branch, "user", J.O("id", "tenant-owner", "name", "Dueño del restaurante", "role", "dueno"), "restaurant", J.O("name", analytics.S("name"), "revision", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), "analytics", analytics, "connection", J.O("central", "available", "local", "mirror-only", "internet", "unverified"));

    }
    public void LogoutTenant(string token) => store.TenantLogout(token);
    public JsonObject PendingCloud(X509Certificate2 certificate) {

        var site = SiteFor(certificate);
        var scope = store.CertificateScope(certificate.GetCertHashString(HashAlgorithmName.SHA256));
        var commands = store.PendingCommands(scope);
        Rules.Need(signingKey is not null && File.Exists(signingKey), "Falta firma central", 503);
        var data = J.O("format", "culinary-cloud-v1", "tenantId", scope.TenantId, "branchId", scope.BranchId, "siteId", site, "expires", DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"), "commands", commands);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(signingKey!));
        return J.O("data", data, "signature", Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(J.Canonical(data)), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));

    }
    public JsonObject AckCloud(X509Certificate2 certificate, JsonObject p) { SiteFor(certificate); return store.CommandAck(store.CertificateScope(certificate.GetCertHashString(HashAlgorithmName.SHA256)), p.S("id"), p.Obj("ack")); }
}
