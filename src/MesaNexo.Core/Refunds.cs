using System.Text.Json.Nodes;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;

// Money going back. A payment is never edited: a refund is a record of its own that points at it and cannot change, so a
// sale keeps its history and the corte shows both. Physical goods go back separately, with stock.return.
public sealed partial class Restaurant {
    // Refunds of closed accounts only: an open one is corrected before it is charged.
    bool TryRefunds(BranchState s, JsonObject u, string type, JsonObject p, out JsonObject result) {

        result = new();
        if (type == "visit.trail") { Role(u, "manager"); result = MoneyTrail(s, s.Get("visit", p.S("id"))); return true; }
        if (type == "dual.configure") {

            Role(u, "manager");
            Need(s.Approval is { } a && a.Requester != a.Approver, "El umbral lo cambian dos gerentes: lo autoriza otro gerente con su PIN", 409);
            s.Meta["dualControlAmount"] = Amount(p, "amount", 1);
            return true;

        }
        if (type == "refund.settle") {

            // A refund the provider left to a person: the manager confirms it was returned from the provider's portal.
            Role(u, "manager");
            var settled = s.Get("refund", p.S("id"));
            Need(RefundOutcome(s, settled) == "manual", "Sólo se confirma una devolución que el proveedor dejó para hacerse a mano", 409);
            var record = ProviderRecord(s, settled)!;
            record["status"] = "refunded";
            record["message"] = "Devuelto desde el portal del proveedor. Referencia: " + Text(p, "reference", 120);
            result = J.O("id", settled.S("id"), "outcome", "done");
            return true;

        }
        if (type != "payment.refund") return false;
        Role(u, "manager");
        var pay = s.Get("payment", p.S("id"));
        var v = s.Get("visit", pay.S("visitId"));
        Need(v.S("status") == "closed", "Sólo se devuelve el pago de una cuenta cerrada; corrige la cuenta abierta antes de cobrar", 409);
        var charged = pay.L("amount") + pay.L("tip");
        // A refund the provider refused gives the money back to the balance; a pending one already counts against it.
        var left = charged - s.Related("refund", "paymentId", pay.S("id")).Where(x => RefundOutcome(s, x) != "failed").Sum(x => x.L("amount"));
        var amount = p.ContainsKey("amount") ? Amount(p, "amount", 1) : left;
        Need(amount > 0 && amount <= left, "No se puede devolver más de lo cobrado", 409);
        var reason = Text(p, "reason");
        DualControl(s, u, amount);
        var txn = s.Related("terminaltxn", "paymentId", pay.S("id")).FirstOrDefault(x => x.B("live"));
        var link = s.Related("paylink", "paymentId", pay.S("id")).FirstOrDefault(x => x.B("live"));
        var provider = txn ?? link;
        // A provider returns the whole charge. Anything else is handed over by a person and only recorded here.
        Need(provider is null || amount == charged, "El proveedor devuelve el cargo completo", 409);
        var route = provider is not null ? "provider" : pay.S("method") == "cash" ? "cash" : "manual";
        if (route == "provider") { Need(provider!.S("status") is "approved" or "paid" or "refund-failed", "El cargo no está en un estado que se pueda devolver", 409); provider["status"] = "refund-queued"; provider["message"] = "Devolución solicitada: " + reason; }
        var refund = Add(s, "refund", J.O("paymentId", pay.S("id"), "visitId", v.S("id"), "amount", amount, "method", pay.S("method"), "route", route, "reason", reason, "actor", u.S("id"), "requestedBy", s.Approval?.Requester ?? u.S("id"), "approvedBy", s.Approval?.Approver ?? "", "at", J.Now(), "providerRecord", provider?.S("id") ?? "", "creditNote", s.Related("fiscal", "visitId", v.S("id")).Any(x => x.S("status").EndsWith("stamped")) ? "pending" : "none"));
        if (provider is not null) provider["refundId"] = refund.S("id");
        if (route == "cash") {

            var cash = s.Related("cash", "status", "open").FirstOrDefault() ?? throw new BusinessException(409, "Abre una caja para devolver efectivo");
            Add(s, "movement", J.O("cashId", cash.S("id"), "type", "expense", "amount", amount, "reason", "Devolución: " + reason, "refundId", refund.S("id"), "actor", u.S("id"), "at", J.Now()));

        }
        result = refund;
        return true;

    }
    // Whether money really went back. Cash and manual refunds are done when recorded; through a provider only once it
    // confirms. Until then the refund is pending and the corte does not count it. A charge can be asked again only after
    // its refund failed, so a refund the charge no longer points at is a failed one.
    public static string RefundOutcome(BranchState s, JsonObject refund) => refund.S("route") != "provider" ? "done" : ProviderRecord(s, refund) is not { } record ? "pending" : record.S("refundId") is { Length: > 0 } current && current != refund.S("id") ? "failed" : record.S("status") switch {
        "refunded" => "done",
        "refund-failed" => "failed",
        "refund-manual" => "manual",
        _ => "pending"
    };
    static JsonObject? ProviderRecord(BranchState s, JsonObject refund) => s.Related("terminaltxn", "id", refund.S("providerRecord")).Concat(s.Related("paylink", "id", refund.S("providerRecord"))).FirstOrDefault();
    // A sale from start to finish: what was sold, how it was paid, what came back, and its fiscal documents.
    JsonObject MoneyTrail(BranchState s, JsonObject v) {

        JsonArray Rows(string kind, Func<JsonObject, JsonNode> build) => new(s.Related(kind, "visitId", v.S("id")).Select(build).ToArray());
        var refunds = s.Related("refund", "visitId", v.S("id")).ToArray();
        return J.O("visitId", v.S("id"), "status", v.S("status"), "totals", Totals(s, v),
            "units", Rows("unit", x => J.O("id", x.S("id"), "name", x.S("name"), "price", x.L("price"), "taxBps", x.L("taxBps"), "status", x.S("status"))),
            "payments", Rows("payment", x => J.O("id", x.S("id"), "method", x.S("method"), "amount", x.L("amount"), "tip", x.L("tip"), "at", x.S("at"))),
            "charges", Rows("terminaltxn", x => J.O("id", x.S("id"), "status", x.S("status"), "providerRef", x.S("providerRef"), "paymentId", x.S("paymentId"))),
            "refunds", new JsonArray(refunds.Select(x => { var r = x.DeepClone().AsObject(); r["outcome"] = RefundOutcome(s, x); return (JsonNode)r; }).ToArray()),
            "refunded", refunds.Where(x => RefundOutcome(s, x) == "done").Sum(x => x.L("amount")),
            "refundsPending", refunds.Where(x => RefundOutcome(s, x) is "pending" or "manual").Sum(x => x.L("amount")),
            "fiscal", Rows("fiscal", x => J.O("id", x.S("id"), "status", x.S("status"), "uuid", x.S("fiscalUuid"), "total", x.Obj("totals").L("total"))),
            "creditNotesPending", refunds.Count(x => x.S("creditNote") == "pending"));

    }
}
