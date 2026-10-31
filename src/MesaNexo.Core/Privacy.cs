using System.Text.Json.Nodes;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;

// A customer's access and deletion requests. Deleting anonymizes in place: sales, cash and audit keep their numbers and lose
// the person. Issued CFDI stay, because the law requires keeping them. docs/PRIVACIDAD-DATOS.md lists what this does not reach.
public sealed partial class Restaurant {
    bool TryPrivacy(BranchState s, JsonObject u, string type, JsonObject p, out JsonObject result) {

        result = new();
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
}
