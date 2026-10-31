using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;

public sealed partial class BranchService {
    static JsonObject EvidenceValue(JsonObject d) {

        var o = new JsonObject();
        foreach (var key in new[] { "id", "kind", "siteId", "tableId", "visitId", "unitId", "name", "productId", "ingredientId", "warehouseId", "recipeVersionId", "cost", "costMethod", "reference", "type", "requestedBy", "actor", "decidedBy", "waiterId", "reason", "unlockReason", "unlockBy", "approvedBy", "senderId", "receiverId", "status", "amount", "price", "quantity", "value", "discount", "method", "expected", "counted", "difference", "total", "gross", "tax", "paid", "balance", "cash", "card", "transfer", "at", "closedAt" })
        if (d.TryGetPropertyValue(key, out var value)) o[key] = value?.DeepClone();
        return o;

    }
    void AppendAudit(BranchState s, string eventId, string actor, string fingerprint, string type) {

        var immutable = new[] { "audit", "recipeversion", "stockmovement", "cashadjustment", "refund" };
        var all = s.Changes().ToArray();
        // Records cannot be removed, so a historical record is altered only if it appears among the changes.
        foreach (var c in all) if (s.Original.TryGetValue(c.S("record_id"), out var prior)) { var d = prior.Data; Need(!immutable.Contains(d.S("kind")) && !(d.S("kind") == "cash" && d.S("status") == "closed"), "Registro histórico inalterable por la aplicación", 409); }
        var changes = all.Where(x => !new[] { "meta", "user", "session", "device", "grant", "invoicetoken", "customer", "address", "fiscal" }.Contains(x.Obj("data").S("kind"))).ToArray();
        var details = new JsonArray();
        foreach (var c in changes.Take(20)) { s.Original.TryGetValue(c.S("record_id"), out var before); var after = c.Obj("data"); details.Add(J.O("record", c.S("record_id"), "before", before is null ? null : EvidenceValue(before.Data), "after", EvidenceValue(after), "beforeHash", before is null ? null : J.Hash(J.Canonical(before.Data)), "afterHash", J.Hash(J.Canonical(after)))); }
        var a = J.O("id", eventId, "siteId", siteId, "actor", actor, "operation", type, "at", J.Now(), "requestFingerprint", fingerprint, "sequence", s.Meta.L("auditSequence") + 1, "previousHash", s.Meta.S("auditHead"), "changes", details, "changedCount", changes.Length, "changeDigest", J.Hash(J.Canonical(new JsonArray(changes.Select(c => (JsonNode)J.O("record", c.S("record_id"), "hash", J.Hash(J.Canonical(c.Obj("data"))))).ToArray()))));
        a["hash"] = J.Hash(J.Canonical(a));
        s.Meta["auditHead"] = a.S("hash");
        s.Meta["auditSequence"] = a.L("sequence");
        s.Add("audit", a);

    }
    public async Task<JsonObject> VerifyAudit(string token, CancellationToken ct = default) {

        // Memory keeps only the newest audit records; the chain is verified against every record in the store.
        var view = await View(ct);
        Role(Authenticate(view, token, out _), "manager");
        return await AuditChain(ct);

    }
    // Reads every record, so a record this keyring cannot open fails here too.
    public async Task<JsonObject> AuditChain(CancellationToken ct = default) {

        var s = new BranchState(await Read(ct, bounded: false), true);
        var heads = Checkpoints.Heads(s.List("audit"), out var valid);
        long seq = heads.Count;
        valid &= seq == s.Meta.L("auditSequence") && heads.GetValueOrDefault(seq, "") == s.Meta.S("auditHead");
        return J.O("valid", valid, "chainedRecords", seq, "legacyRecords", s.List("audit").Count(x => !x.ContainsKey("sequence")), "scope", "Detección local; la comparación con los puntos de control externos es la que detecta una reescritura");

    }
}
