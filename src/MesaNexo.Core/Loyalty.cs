using System.Text.Json.Nodes;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;
// Stage 3: price lists, customer wallet with cashback, and employee meals charged to payroll.
public sealed partial class Restaurant {
    static readonly string[] PriceChannels = ["dine-in", "quick", "pickup", "delivery"];
    DateTimeOffset BranchNow(BranchState s) => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone(s));
    static string VisitChannel(JsonObject v) => v.S("tableId") != "" ? "dine-in" : v.S("channel") is "delivery" or "pickup" ? v.S("channel") : "quick";
    long WalletBalance(BranchState s, string customerId) => s.Related("walletmove", "customerId", customerId).Sum(x => x.L("amount"));
    // A visit can pin a list ("base" = catalog price); otherwise the first active automatic list for its channel and hour applies.
    JsonObject? ActivePriceList(BranchState s, JsonObject v) {

        var pinned = v.S("priceListId");
        if (pinned == "base") return null;
        if (pinned != "") return s.Related("pricelist", "id", pinned).FirstOrDefault(x => x.B("active"));
        var hour = BranchNow(s).Hour;
        var channel = VisitChannel(v);
        return s.List("pricelist").Where(x => x.B("active") && x.B("auto") && x.A("channels").Strings().Contains(channel) && hour >= x.L("startHour") && hour < x.L("endHour", 24)).OrderBy(x => x.L("priority")).ThenBy(x => x.S("id"), StringComparer.Ordinal).FirstOrDefault();

    }
    long ListPrice(BranchState s, JsonObject v, JsonObject product, out string listId) {

        var list = ActivePriceList(s, v);
        listId = list?.S("id") ?? "";
        if (list is null) return product.L("price");
        var fixedPrice = list.A("prices").Objects().FirstOrDefault(x => x.S("productId") == product.S("id"));
        if (fixedPrice is not null) return fixedPrice.L("price");
        return Math.Max(0, Cents.Bps(product.L("price"), 10000 + list.L("adjustBps")));

    }
    // Wallet and payroll settlements are payments outside the drawer; they keep the open cash id only for reporting.
    JsonObject InternalPayment(BranchState s, JsonObject u, JsonObject v, long amount, string method) {

        Need(amount > 0 && amount <= Totals(s, v).L("balance"), "El pago supera el saldo");
        var cash = s.Related("cash", "status", "open").FirstOrDefault();
        return Add(s, "payment", J.O("visitId", v.S("id"), "cashId", cash?.S("id") ?? "", "amount", amount, "tip", 0, "method", method, "actor", u.S("id"), "at", J.Now(), "externalProcessed", false, "folioId", "", "tendered", amount, "change", 0));

    }
    void LoyaltyEarn(BranchState s, JsonObject v) {

        var loyalty = s.Meta.Obj("loyalty");
        if (!loyalty.B("enabled") || v.S("customerId") == "" || loyalty.L("cashbackBps") <= 0) return;
        var basis = s.Related("payment", "visitId", v.S("id")).Where(x => x.S("method") is not ("wallet" or "payroll")).Sum(x => x.L("amount"));
        var earned = Cents.BpsFloor(basis, loyalty.L("cashbackBps"));
        if (earned <= 0) return;
        Add(s, "walletmove", J.O("customerId", v.S("customerId"), "type", "earn", "amount", earned, "visitId", v.S("id"), "cycle", v.L("closeCycle"), "at", J.Now()));
        v["loyaltyEarned"] = v.L("loyaltyEarned") + earned;

    }
    string MonthKey(BranchState s) => BranchNow(s).ToString("yyyy-MM");
    long StaffMonthly(BranchState s, string employeeId, string month) => s.Related("staffcharge", "employeeId", employeeId).Where(x => x.S("month") == month).Sum(x => x.L("amount"));
    bool TryLoyalty(BranchState s, JsonObject u, string type, JsonObject p, out JsonObject result) {

        result = J.O("ok", true);
        var id = p.S("id");
        switch (type) {
            case "pricelist.save": {

                    Role(u, "manager");
                    var name = Text(p, "name", 100);
                    Need(!s.List("pricelist").Any(x => x.S("name").Equals(name, StringComparison.OrdinalIgnoreCase) && x.S("id") != id), "Lista de precios repetida");
                    var adjust = p.L("adjustBps");
                    Need(adjust is >= -9000 and <= 10000, "Ajuste entre −90 % y +100 %");
                    var channels = p.A("channels").Strings();
                    Need(channels.All(PriceChannels.Contains) && channels.Distinct().Count() == channels.Length, "Canal inválido");
                    var prices = new JsonArray();
                    var rows = p.A("prices");
                    Need(rows.Count <= 200 && rows.Objects().Count() == rows.Count, "Máximo 200 precios por lista");
                    Need(rows.Objects().Select(x => x.S("productId")).Distinct().Count() == rows.Count, "Producto repetido en la lista");
                    foreach (var row in rows.Objects()) { s.Get("product", row.S("productId")); prices.Add(J.O("productId", row.S("productId"), "price", Amount(row, "price"))); }
                    var start = Amount(p, "startHour", 0, 23);
                    var end = Amount(p, "endHour", 1, 24);
                    Need(start < end, "Horario inválido");
                    var auto = p.B("auto");
                    Need(!auto || channels.Length > 0, "Una lista automática necesita al menos un canal");
                    var list = id == "" ? Add(s, "pricelist", J.O("at", J.Now())) : s.Get("pricelist", id);
                    list["name"] = name;
                    list["adjustBps"] = adjust;
                    list["prices"] = prices;
                    list["channels"] = J.N(channels);
                    list["auto"] = auto;
                    list["startHour"] = start;
                    list["endHour"] = end;
                    list["priority"] = Amount(p, "priority", 0, 100);
                    list["active"] = p.B("active", true);
                    result = list;
                    break;

                }
            case "visit.pricelist": {

                    Role(u, "manager");
                    var v = s.Get("visit", id);
                    Need(v.S("status") == "open", "La cuenta está cerrada");
                    var list = p.S("priceListId");
                    if (list is not ("" or "base")) Need(s.Get("pricelist", list).B("active"), "Lista inactiva");
                    v["priceListId"] = list;
                    result = J.O("id", id, "priceListId", list);
                    break;

                }
            case "visit.customer": {

                    Role(u, "mesero", "manager");
                    var v = Own(s, u, id);
                    var customerId = p.S("customerId");
                    if (customerId == "") { v.Remove("customerId"); result = J.O("id", id); break; }
                    var customer = s.Get("customer", customerId);
                    v["customerId"] = customer.S("id");
                    if (v.S("customerName") == "" || v.S("tableId") != "") v["customerName"] = customer.S("name");
                    result = J.O("id", id, "customerId", customer.S("id"), "balance", WalletBalance(s, customer.S("id")));
                    break;

                }
            case "loyalty.configure":
                Role(u, "manager");
                s.Meta["loyalty"] = J.O("enabled", p.B("enabled"), "cashbackBps", Amount(p, "cashbackBps", 0, 2000));
                break;
            case "wallet.topup": {

                    Role(u, "manager");
                    var customer = s.Get("customer", p.S("customerId"));
                    var amount = Amount(p, "amount", 1, 1_000_000);
                    var method = Choice(p, "method", "cash", "card", "transfer");
                    if (method == "cash") { var cash = s.Related("cash", "status", "open").FirstOrDefault(); Need(cash is not null, "Abre una caja para recibir la recarga en efectivo"); Add(s, "movement", J.O("cashId", cash!.S("id"), "type", "income", "amount", amount, "reason", "Recarga de monedero · " + customer.S("name"), "actor", u.S("id"), "at", J.Now())); }
                    result = Add(s, "walletmove", J.O("customerId", customer.S("id"), "type", "topup", "amount", amount, "method", method, "actor", u.S("id"), "at", J.Now()));
                    break;

                }
            case "wallet.adjust": { Role(u, "manager"); var customer = s.Get("customer", p.S("customerId")); var amount = p.L("amount"); Need(amount != 0 && Math.Abs(amount) <= 1_000_000, "Ajuste fuera de rango"); Need(WalletBalance(s, customer.S("id")) + amount >= 0, "El monedero no puede quedar negativo"); result = Add(s, "walletmove", J.O("customerId", customer.S("id"), "type", "adjust", "amount", amount, "reason", Text(p, "reason", 200), "actor", u.S("id"), "at", J.Now())); break; }
            case "wallet.pay": {

                    Role(u, "mesero", "manager");
                    Need(u.S("role") == "manager" || u.Obj("permissions").B("payments", true), "Permiso de pagos deshabilitado", 403);
                    var v = Own(s, u, p.S("visitId"));
                    Need(v.S("customerId") != "", "Asocia un cliente a la cuenta");
                    var amount = Amount(p, "amount", 1);
                    Need(s.List("folio").All(f => f.S("visitId") != v.S("id") || f.B("void")), "Con subfolios usa otra forma de pago");
                    Need(WalletBalance(s, v.S("customerId")) >= amount, "Saldo insuficiente en el monedero");
                    var payment = InternalPayment(s, u, v, amount, "wallet");
                    Add(s, "walletmove", J.O("customerId", v.S("customerId"), "type", "redeem", "amount", -amount, "visitId", v.S("id"), "paymentId", payment.S("id"), "actor", u.S("id"), "at", J.Now()));
                    result = payment;
                    break;

                }
            case "staff.configure":
                Role(u, "manager");
                s.Meta["staff"] = J.O("discountBps", Amount(p, "discountBps", 0, 10000), "monthlyLimit", Amount(p, "monthlyLimit"));
                break;
            case "staff.meal": {

                    Role(u, "manager", "mesero");
                    var employee = s.Get("user", p.S("employeeId", u.S("id")));
                    Need(employee.B("active"), "Empleado inactivo");
                    Need(u.S("role") == "manager" || employee.S("id") == u.S("id"), "Sólo un gerente registra el consumo de otra persona", 403);
                    var settle = Choice(p, "settle", "payroll", "cash", "card");
                    var config = s.Meta.Obj("staff");
                    if (settle != "payroll") Need(s.Related("cash", "status", "open").Any(), "Abre una caja para cobrar el consumo");
                    var visit = Act(s, u, "visit.quick", J.O("customerName", "Empleado · " + employee.S("name")));
                    var v = s.Get("visit", visit.S("id"));
                    v["channel"] = "staff";
                    v["employeeId"] = employee.S("id");
                    v["priceListId"] = "base";
                    Act(s, u, "command.send", J.O("visitId", v.S("id"), "items", p.A("items").DeepClone()));
                    if (config.L("discountBps") > 0) v["discount"] = J.O("kind", "percent", "value", config.L("discountBps") / 100m, "reason", "Consumo de empleado", "staff", true);
                    var total = Totals(s, v).L("total");
                    string paymentId = "";
                    if (total > 0 && settle == "payroll") { var month = MonthKey(s); var limit = config.L("monthlyLimit"); Need(limit == 0 || StaffMonthly(s, employee.S("id"), month) + total <= limit, "Supera el tope mensual de consumo de " + employee.S("name")); paymentId = InternalPayment(s, u, v, total, "payroll").S("id"); Add(s, "staffcharge", J.O("employeeId", employee.S("id"), "visitId", v.S("id"), "amount", total, "month", month, "status", "open", "at", J.Now())); } else if (total > 0) paymentId = Act(s, u, "payment.add", J.O("visitId", v.S("id"), "amount", total, "method", settle)).S("id");
                    v["prepaid"] = true;
                    result = J.O("visitId", v.S("id"), "total", total, "paymentId", paymentId, "settle", settle);
                    break;

                }
            case "staff.settle": {

                    Role(u, "manager");
                    var employee = s.Get("user", p.S("employeeId"));
                    var month = Text(p, "month", 7);
                    Need(System.Text.RegularExpressions.Regex.IsMatch(month, "^[0-9]{4}-[0-9]{2}$"), "Mes inválido");
                    var charges = s.Related("staffcharge", "employeeId", employee.S("id")).Where(x => x.S("month") == month && x.S("status") == "open").ToArray();
                    Need(charges.Length > 0, "Sin consumos pendientes en ese mes");
                    foreach (var charge in charges) { charge["status"] = "deducted"; charge["deductedBy"] = u.S("id"); charge["deductedAt"] = J.Now(); }
                    result = J.O("employeeId", employee.S("id"), "month", month, "count", charges.Length, "amount", charges.Sum(x => x.L("amount")));
                    break;

                }
            default:
                return false;
        }
        return true;

    }
}
