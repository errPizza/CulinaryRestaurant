using System.Text.Json.Nodes;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;
// The one place a card charge changes state. Every commit passes here, whoever wrote the status, so a final state stays final
// and every change leaves its entry in the charge's trail. Plan names: pendiente = queued; enviado = sending, waiting and
// cancel-requested; incierto = uncertain; reverso pendiente = refund-queued and refunding.
public sealed partial class Restaurant {
    static readonly string[] ChargeOutcomes = ["approved", "declined", "cancelled", "expired", "uncertain", "refund-queued", "refunded"];
    static readonly Dictionary<string, string[]> ChargeMoves = new() {
        [""] = ["queued", "approved", "declined", "uncertain"],
        ["queued"] = ["sending", "cancelled"],
        ["sending"] = ["waiting", "failed", .. ChargeOutcomes],
        ["waiting"] = ["cancel-requested", .. ChargeOutcomes],
        ["cancel-requested"] = ["waiting", .. ChargeOutcomes],
        // Out of uncertain only with the provider's answer (back to waiting) or a manager's capture from the terminal's report.
        ["uncertain"] = ["waiting", "approved", "declined", "refund-queued", "reversed"],
        ["approved"] = ["refund-queued"],
        ["refund-queued"] = ["refunding"],
        ["refunding"] = ["refunded", "refund-failed", "refund-manual", "uncertain"]
    };
    public void ChargeTransitions(BranchState s, string actor) {

        foreach (var change in s.Changes().Where(c => c.Obj("data").S("kind") == "terminaltxn").ToArray()) {

            var txn = s.Get("terminaltxn", change.Obj("data").S("id"));
            var before = s.Original.TryGetValue(change.S("record_id"), out var prior) ? prior.Data.S("status") : "";
            var after = txn.S("status");
            var raw = txn["raw"]?.DeepClone();
            txn.Remove("raw");
            if (before == after) continue;
            Need(ChargeMoves.GetValueOrDefault(before, []).Contains(after), $"Un cobro no pasa de «{before}» a «{after}»", 409);
            if (txn["trail"] is not JsonArray trail) txn["trail"] = trail = [];
            trail.Add(J.O("from", before, "to", after, "at", J.Now(), "actor", actor, "attemptId", txn.S("attemptId"), "providerRef", txn.S("providerRef"), "amount", txn.L("amount") + txn.L("tip"), "currency", "MXN", "raw", raw));

        }

    }
}
