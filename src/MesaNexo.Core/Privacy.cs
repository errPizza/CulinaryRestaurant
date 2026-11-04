using System.Text.Json.Nodes;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;

// A customer's access and deletion requests. Deleting anonymizes in place: sales, cash and audit keep their numbers and lose
// the person. Issued CFDI stay, because the law requires keeping them. docs/PRIVACIDAD-DATOS.md lists what this does not reach.
public sealed partial class Restaurant {
    bool TryPrivacy(BranchState s, JsonObject u, string type, JsonObject p, out JsonObject result) {

        result = new();
        if (type == "privacy.purge") { Role(u, "manager"); result = Purge(s, u, Amount(p, "days", 30, 3650)); return true; }
        if (type is not ("customer.export" or "customer.erase")) return false;
        Role(u, "manager");
        var customer = s.Get("customer", p.S("id"));
        var id = customer.S("id");
        var visits = s.Related("visit", "customerId", id).ToArray();
        var fiscal = visits.SelectMany(v => s.Related("fiscal", "visitId", v.S("id"))).ToArray();
        var messages = customer.S("phone") == "" ? [] : s.List("message").Where(x => x.S("to") == customer.S("phone")).ToArray();
        var calls = s.List("call").Where(x => x.S("customerId") == id).ToArray();
        var deliveries = s.Related("delivery", "customerId", id).ToArray();
        if (type == "customer.export") {

            JsonArray Copy(IEnumerable<JsonObject> rows) => new(rows.Select(x => x.DeepClone()).ToArray());
            result = J.O("customer", customer.DeepClone(), "addresses", Copy(s.Related("address", "customerId", id)), "visits", new JsonArray(visits.Select(v => (JsonNode)J.O("id", v.S("id"), "closedAt", v.S("closedAt"), "total", Totals(s, v).L("total"))).ToArray()), "deliveries", Copy(deliveries), "wallet", Copy(s.Related("walletmove", "customerId", id)), "calls", Copy(calls), "messages", Copy(messages), "fiscalReceivers", new JsonArray(fiscal.Select(f => f.Obj("receiver").DeepClone()).ToArray()), "exportedAt", J.Now());
            return true;

        }
        Need(customer.S("erasedAt") == "", "Los datos de este cliente ya se eliminaron", 409);
        Need(!visits.Any(v => v.S("status") == "open"), "El cliente tiene una cuenta abierta", 409);
        Need(WalletBalance(s, id) == 0, "Entrega o ajusta el saldo del monedero antes de eliminar sus datos", 409);
        var reason = Text(p, "reason");
        static void Keep(JsonObject row, params string[] fields) { foreach (var key in row.Select(x => x.Key).Where(x => !fields.Contains(x) && x is not ("id" or "kind" or "siteId")).ToArray()) row.Remove(key); }
        var erased = J.Now();
        Keep(customer);
        customer["name"] = "Cliente eliminado";
        customer["phone"] = "";
        customer["notes"] = "";
        customer["erasedAt"] = erased;
        foreach (var address in s.Related("address", "customerId", id).ToArray()) { Keep(address, "customerId"); address["label"] = ""; address["text"] = ""; }
        foreach (var v in visits) v["customerName"] = "";
        foreach (var d in deliveries) { d["pickupName"] = ""; d.Remove("addressSnapshot"); }
        foreach (var c in calls) { c["number"] = ""; c["name"] = ""; }
        foreach (var m in messages) { m["to"] = ""; m["body"] = ""; m["values"] = new JsonArray(); }
        result = Add(s, "erasure", J.O("customerId", id, "reason", reason, "actor", u.S("id"), "at", erased, "fiscalRetained", fiscal.Length));
        return true;

    }
    // Retention for data kept only to serve one visit: reservations after their date, calls, messages and platform orders.
    // They are anonymized in place, a batch at a time, and each run leaves a `purge` record with counts and no personal
    // data. Customers, wallet, fiscal receivers and audit are never touched here: they have their own legal periods.
    const int PurgeBatch = 200;
    static readonly string[] OrderPersonal = ["customerName", "customerPhone", "phone", "address", "deliveryAddress", "notes"];
    JsonObject Purge(BranchState s, JsonObject u, long days) {

        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        bool Old(JsonObject x, string field) => x.S("purgedAt") == "" && DateTimeOffset.TryParse(x.S(field), out var at) && at < cutoff;
        var counts = new JsonObject();
        var left = PurgeBatch;
        var now = J.Now();
        void Each(string kind, string field, Action<JsonObject> clear) {

            var rows = s.List(kind).Where(x => Old(x, field)).Take(left).ToArray();
            foreach (var row in rows) { clear(row); row["purgedAt"] = now; }
            counts[kind] = (long)rows.Length;
            left -= rows.Length;

        }
        Each("reservation", "start", r => { r["name"] = "Reservación depurada"; r["contact"] = ""; r["notes"] = ""; });
        Each("call", "at", c => { c["number"] = ""; c["name"] = ""; });
        Each("message", "at", m => { m["to"] = ""; m["body"] = ""; m["values"] = new JsonArray(); });
        Each("aggregatororder", "receivedAt", o => { foreach (var key in OrderPersonal) if (o.ContainsKey(key)) o[key] = ""; });
        var total = PurgeBatch - left;
        if (total > 0) Add(s, "purge", J.O("days", days, "counts", counts, "actor", u.S("id"), "at", now));
        return J.O("purged", total, "counts", counts, "more", left == 0);

    }
}
