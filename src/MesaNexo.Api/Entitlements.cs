using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;

public sealed partial class BranchService {
    public async Task ApplyEntitlement(string envelopeFile, string trustedPublicKey, CancellationToken ct = default) {

        // Public key is provisioned administratively, never taken from the envelope/network.
        var envelope = JsonNode.Parse(await File.ReadAllTextAsync(envelopeFile, ct))!;
        await ApplyEntitlement(envelope.AsObject(), trustedPublicKey, ct);

    }
    public async Task ApplyEntitlement(JsonObject envelope, string trustedPublicKey, CancellationToken ct = default) {

        var data = envelope.Obj("data");
        using var key = RSA.Create();
        key.ImportFromPem(await File.ReadAllTextAsync(trustedPublicKey, ct));
        Need(key.VerifyData(Encoding.UTF8.GetBytes(J.Canonical(data)), Convert.FromBase64String(envelope.S("signature")), HashAlgorithmName.SHA256, RSASignaturePadding.Pss), "Firma de contratación inválida", 403);
        Need(data.S("format") == "mesa-nexo-entitlement-v1" && data.S("siteId") == siteId, "Contratación de otra sucursal", 403);
        var s = await Load(ct);
        Need(data.L("revision") > s.Meta.L("entitlementRevision", -1), "Revisión repetida o antigua", 409);
        var modules = data.A("modules").Strings();
        Need(modules.All(Restaurant.Modules.Contains), "Módulos desconocidos");
        Need(modules.Contains("courses") || !s.List("unit").Any(x => x.S("status") == "held"), "Libera retenidos antes de retirar tiempos");
        var limits = data.Obj("limits");
        Need(limits.L("maxUsers") >= s.List("user").Count() && limits.L("maxTables") >= s.List("table").Count(), "El plan queda por debajo del inventario existente");
        foreach (var reply in data.A("supportReplies").Objects()) {

            Need(reply.S("siteId") == siteId, "Respuesta de soporte de otra sucursal", 403);
            var ticket = s.Get("ticket", reply.S("ticketId"));
            var message = reply.Obj("message");
            Need(message.S("id").Length > 0 && message.S("message").Length is > 0 and <= 1000, "Respuesta de soporte inválida");
            if (!ticket.A("messages").Objects().Any(x => x.S("id") == message.S("id"))) { Need(ticket.A("messages").Count < 20, "Continúa el soporte en otro ticket"); ticket.A("messages").Add(message.DeepClone()); }

        }
        s.Meta["modules"] = J.N(modules);
        s.Meta["status"] = Choice(data, "status", "active", "suspended");
        s.Meta["maxUsers"] = limits.L("maxUsers");
        s.Meta["maxTables"] = limits.L("maxTables");
        s.Meta["entitlementRevision"] = data.L("revision");
        var c = data.Obj("commercial");
        foreach (var field in new[] { "mode", "usageUntil", "supportUntil", "hostingUntil", "updates", "updatesUntil", "resaleAuthorized", "maintenance" }) s.Meta[field] = c[field]?.DeepClone();
        await Commit(s, J.Id(), "provider-signed", J.Hash(J.Canonical(data)), J.O("applied", true), "entitlement.apply", ct);

    }
}