using System.Globalization;
using System.Text.Json.Nodes;
namespace MesaNexo.Core;

// The summaries behind the owner's panel, defined in docs/METRICAS-PANEL.md. A sale counts when it closes and a payment
// when it is taken, as in the Z close, so the panel and the closings use one set of rules. Nothing here writes.
public sealed partial class Restaurant {
    const string DefaultTimeZone = "America/Mexico_City";
    const long DefaultCutoffMinutes = 240;
    static TimeZoneInfo Zone(BranchState s) => TimeZoneInfo.FindSystemTimeZoneById(s.Meta.S("timeZone", DefaultTimeZone));
    static long CutoffMinutes(BranchState s) => s.Meta.L("businessDayCutoffMinutes", DefaultCutoffMinutes);
    static readonly string[] VoidStatuses = ["cancelled", "unavailable"];
    static DateTimeOffset? Moment(JsonObject x, string key) => DateTimeOffset.TryParse(x.S(key), CultureInfo.InvariantCulture, out var at) ? at : null;
    static JsonNode Rows(IEnumerable<JsonObject> rows) => new JsonArray([.. rows.Select(x => (JsonNode?)x)]);
    // Dated by the clock of the branch: a sale at 01:30 still belongs to the night before the cutoff, in hour 1.
    (string Date, int Hour) Place(BranchState s, DateTimeOffset at) {

        var local = TimeZoneInfo.ConvertTime(at, Zone(s));
        return (local.DateTime.AddMinutes(-CutoffMinutes(s)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), local.Hour);

    }
    public string BusinessDate(BranchState s, DateTimeOffset at) => Place(s, at).Date;
    public JsonObject OwnerBranch(BranchState s, string version) => J.O("timeZone", s.Meta.S("timeZone", DefaultTimeZone), "businessDayCutoffMinutes", CutoffMinutes(s), "currency", "MXN", "appVersion", version, "lastZ", s.Meta.L("zNumber"));
    public static JsonObject OwnerEmptyDay(string date) => J.O("businessDate", date, "complete", true, "hours", new JsonArray(), "payments", new JsonArray(), "refunds", new JsonArray(), "channels", new JsonArray(), "products", new JsonArray(), "staff", new JsonArray(), "cash", J.O("income", 0, "expense", 0, "withdrawal", 0), "cashCloses", new JsonArray(), "inventory", J.O("waste", 0, "shortage", 0, "surplus", 0), "zReports", new JsonArray());
    static bool Within(string date, string? since) => since is null || string.CompareOrdinal(date, since) >= 0;
    // Every business day with activity, from `since` when given. `frozen` holds the unit costs already published per
    // day: a finished day is not revalued when stock costs move later.
    public SortedDictionary<string, JsonObject> OwnerDays(BranchState s, DateTimeOffset now, IReadOnlyDictionary<string, JsonObject>? frozen = null, string? since = null) {

        var cache = new Dictionary<string, (string Date, int Hour)>();
        (string Date, int Hour) At(JsonObject x, string key) { var text = x.S(key); if (!cache.TryGetValue(text, out var place)) cache[text] = place = Place(s, Moment(x, key)!.Value); return place; }
        ILookup<string, JsonObject> Dated(IEnumerable<JsonObject> rows, string key = "at") => rows.Where(x => Moment(x, key) is not null).ToLookup(x => At(x, key).Date);
        var reports = s.List("zreport").Where(x => Moment(x, "to") is not null).OrderBy(x => x.L("number")).ToArray();
        var ends = reports.Select(x => Moment(x, "to")!.Value).ToArray();
        var nextZ = s.Meta.L("zNumber") + 1;
        // The close that covers a moment: the first ending after it, the next one while none does, 0 before the first.
        long Covering(DateTimeOffset at) {

            var found = Array.BinarySearch(ends, at);
            var i = found < 0 ? ~found : found + 1;
            while (i < ends.Length && ends[i] <= at) i++;
            return i == ends.Length ? nextZ : i == 0 && at < Moment(reports[0], "from") ? 0 : reports[i].L("number");

        }
        var sales = Dated(s.Related("visit", "status", "closed"), "closedAt");
        var payments = Dated(s.List("payment"));
        var refunds = Dated(s.List("refund"));
        var movements = Dated(s.List("movement"));
        var waste = Dated(s.List("stockmovement").Where(x => x.S("type") is "waste" or "count-shortage" or "count-surplus"));
        var closes = Dated(s.Related("cash", "status", "closed"), "closedAt");
        var zDays = Dated(reports, "to");
        var adjustments = s.List("cashadjustment").ToLookup(x => x.S("cashId"));
        var products = s.List("product").ToDictionary(x => x.S("id"));
        var users = s.List("user").ToDictionary(x => x.S("id"));
        var waiters = s.List("visit").ToDictionary(x => x.S("id"), x => x.S("waiterId"));
        var costs = new Dictionary<string, long>();
        long? Cost(string date, string productId) {

            if (frozen?.GetValueOrDefault(date)?.TryGetPropertyValue(productId, out var kept) == true) return kept?.GetValue<long>();
            if (!products.TryGetValue(productId, out var product)) return null;
            if (!costs.TryGetValue(productId, out var cost)) costs[productId] = cost = PortionCost(s, product);
            return cost < 0 ? null : cost;

        }
        var open = s.Related("visit", "status", "open").SelectMany(v => new[] { "openedAt", "closedAt" }.Where(key => Moment(v, key) is not null).Select(key => At(v, key).Date)).ToHashSet();
        var today = Place(s, now).Date;
        var dates = new[] { sales, payments, refunds, movements, waste, closes, zDays }.SelectMany(x => x.Select(g => g.Key)).ToHashSet();
        var days = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var date in dates.Where(x => Within(x, since))) {

            var figures = sales[date].Select(v => (Visit: v, Totals: Totals(s, v), Units: s.Related("unit", "visitId", v.S("id")).OrderBy(x => x.S("id"), StringComparer.Ordinal).ToArray())).ToArray();
            var sold = figures.SelectMany(x => x.Units).Where(x => !VoidStatuses.Contains(x.S("status"))).ToArray();
            var day = OwnerEmptyDay(date);
            day["complete"] = string.CompareOrdinal(date, today) < 0 && !open.Contains(date);
            day["hours"] = Rows(figures.GroupBy(x => (Hour: At(x.Visit, "closedAt").Hour, Z: Covering(Moment(x.Visit, "closedAt")!.Value))).OrderBy(g => g.Key).Select(g => {

                var voided = g.SelectMany(x => x.Units).Where(x => VoidStatuses.Contains(x.S("status"))).ToArray();
                return J.O("hour", g.Key.Hour, "z", g.Key.Z, "tickets", g.Count(), "people", g.Sum(x => x.Visit.L("people")), "gross", g.Sum(x => x.Totals.L("gross")), "discounts", g.Sum(x => x.Totals.L("discount")), "net", g.Sum(x => x.Totals.L("total")), "tax", g.Sum(x => x.Totals.L("tax")), "deliveryFees", g.Sum(x => x.Visit.L("deliveryFee")), "voidedCount", voided.Length, "voidedAmount", voided.Sum(x => x.L("price")), "comps", g.Count(x => x.Visit.Obj("discount").S("kind") == "percent" && x.Visit.Obj("discount")["value"]?.GetValue<decimal>() == 100), "staffMeals", g.Count(x => x.Visit.S("channel") == "staff"));

            }));
            day["payments"] = Rows(payments[date].GroupBy(x => (At(x, "at").Hour, Method: x.S("method"))).OrderBy(g => g.Key.Hour).ThenBy(g => g.Key.Method, StringComparer.Ordinal).Select(g => J.O("hour", g.Key.Hour, "method", g.Key.Method, "amount", g.Sum(x => x.L("amount")), "tips", g.Sum(x => x.L("tip")), "count", g.Count())));
            day["refunds"] = Rows(refunds[date].GroupBy(x => x.S("method")).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => J.O("method", g.Key, "amount", g.Sum(x => x.L("amount")), "count", g.Count())));
            day["channels"] = Rows(figures.GroupBy(x => x.Visit.S("channel") is { Length: > 0 } channel ? channel : "table").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => J.O("channel", g.Key, "tickets", g.Count(), "net", g.Sum(x => x.Totals.L("total")))));
            day["products"] = Rows(sold.GroupBy(x => x.S("productId")).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => {

                var product = products.GetValueOrDefault(g.Key);
                return J.O("productId", g.Key, "name", product?.S("name") ?? g.First().S("name"), "category", product?.S("category") ?? "", "quantity", g.Count(), "revenue", g.Sum(x => x.L("price")), "unitCost", Cost(date, g.Key));

            }));
            // A tip counts with its payment, so the day's tips add up however the account crossed the cutoff.
            var tips = payments[date].GroupBy(x => waiters.GetValueOrDefault(x.S("visitId"), "")).Where(g => g.Sum(x => x.L("tip")) != 0).ToDictionary(g => g.Key, g => g.Sum(x => x.L("tip")));
            var waiterSales = figures.GroupBy(x => x.Visit.S("waiterId")).ToDictionary(g => g.Key, g => (Tickets: g.Count(), Net: g.Sum(x => x.Totals.L("total"))));
            day["staff"] = Rows(waiterSales.Keys.Union(tips.Keys).Order(StringComparer.Ordinal).Select(id => J.O("staffId", id, "name", users.GetValueOrDefault(id)?.S("name") ?? "—", "tickets", waiterSales.GetValueOrDefault(id).Tickets, "net", waiterSales.GetValueOrDefault(id).Net, "tips", tips.GetValueOrDefault(id))));
            long Moved(string type) => movements[date].Where(x => x.S("type") == type && x.S("refundId") == "").Sum(x => x.L("amount"));
            day["cash"] = J.O("income", Moved("income"), "expense", Moved("expense"), "withdrawal", Moved("withdrawal"));
            day["cashCloses"] = Rows(closes[date].OrderBy(x => x.S("closedAt"), StringComparer.Ordinal).Select(c => J.O("cashId", c.S("id"), "closedAt", c.S("closedAt"), "blind", c.B("blind"), "cashDifference", c.L("difference"), "cardDifference", c.Obj("card").L("difference"), "transferDifference", c.Obj("transfer").L("difference"), "adjustments", adjustments[c.S("id")].Sum(x => x.S("direction") == "increase" ? x.L("amount") : -x.L("amount")))));
            long Spoiled(string type) => waste[date].Where(x => x.S("type") == type).Sum(x => Math.Abs(x.L("cost")));
            day["inventory"] = J.O("waste", Spoiled("waste"), "shortage", Spoiled("count-shortage"), "surplus", Spoiled("count-surplus"));
            day["zReports"] = Rows(zDays[date].Select(z => { var r = z.Obj("summary"); return J.O("number", z.L("number"), "from", z.S("from"), "to", z.S("to"), "tickets", r.L("tickets"), "people", r.L("people"), "gross", r.L("gross"), "discounts", r.L("discounts"), "net", r.L("net"), "tax", r.L("tax"), "payments", r.L("payments"), "tips", r.L("tips"), "refunds", r.L("refunds"), "digest", z.S("digest")); }));
            days[date] = day;

        }
        return days;

    }
    // The identities of a published day. A difference between the panel and a frozen Z is fine only when a reopening explains it.
    public Dictionary<string, List<string>> OwnerErrors(BranchState s, IReadOnlyDictionary<string, JsonObject> days, string? since = null) {

        var errors = new Dictionary<string, List<string>>();
        void Fail(string date, string message) { if (!errors.TryGetValue(date, out var list)) errors[date] = list = []; list.Add(message); }
        static long Sum(JsonNode day, string list, string key) => day.A(list).Objects().Sum(x => x.L(key));
        foreach (var (date, day) in days) {

            var net = Sum(day, "hours", "net");
            if (net != Sum(day, "channels", "net") || net != Sum(day, "staff", "net")) Fail(date, "ventas distintas por hora, canal y mesero");
            if (Sum(day, "products", "revenue") + Sum(day, "hours", "deliveryFees") != Sum(day, "hours", "gross")) Fail(date, "productos más envío distintos de la venta bruta");
            if (day.A("hours").Objects().Any(x => x.L("gross") - x.L("discounts") != x.L("net"))) Fail(date, "bruta menos descuentos distinta de ventas");
            if (Sum(day, "payments", "tips") != Sum(day, "staff", "tips")) Fail(date, "propinas distintas por pago y por mesero");

        }
        var hours = days.Values.SelectMany(day => day.A("hours").Objects()).ToLookup(x => x.L("z"));
        var lastReopened = s.List("visit").Select(v => Moment(v, "reopenedAt")).Max();
        foreach (var z in s.List("zreport").Where(x => Moment(x, "from") is not null && Moment(x, "to") is not null && Within(Place(s, Moment(x, "from")!.Value).Date, since))) {

            var rows = hours[z.L("number")].ToArray();
            if (!(lastReopened >= Moment(z, "from")) && (rows.Sum(x => x.L("net")) != z.Obj("summary").L("net") || rows.Sum(x => x.L("tickets")) != z.Obj("summary").L("tickets"))) Fail(Place(s, Moment(z, "to")!.Value).Date, $"corte Z {z.L("number")} distinto del panel sin una reapertura que lo explique");

        }
        return errors;

    }
    // What the owner sees of the branch right now. Unlike a day, it is replaced on every send.
    public JsonObject OwnerLive(BranchState s, DateTimeOffset now) {

        var accounts = s.Related("visit", "status", "open").ToArray();
        var incoming = s.Related("purchase", "status", "ordered").SelectMany(x => x.A("lines").Objects()).ToLookup(x => x.S("ingredientId"));
        var low = s.List("ingredient").Where(i => i.L("minStock") > 0).Select(i => { var stock = s.Related("stock", "ingredientId", i.S("id")).ToArray(); return (Row: i, Available: stock.Sum(x => x.L("quantity") - x.L("reserved")), Incoming: incoming[i.S("id")].Sum(x => x.L("quantity"))); }).Where(x => x.Available + x.Incoming <= x.Row.L("minStock")).OrderBy(x => x.Row.S("name"), StringComparer.Ordinal).Take(100);
        return J.O("at", now.ToString("O"), "openAccounts", accounts.Length, "occupiedTables", s.Related("table", "status", "occupied").Count(), "openAmount", accounts.Sum(v => Totals(s, v).L("total")), "pendingRequests", s.Related("request", "status", "pending").Count(), "cashOpen", s.Related("cash", "status", "open").Any(), "lowStock", Rows(low.Select(x => J.O("ingredientId", x.Row.S("id"), "name", x.Row.S("name"), "unit", x.Row.S("unit"), "available", x.Available, "min", x.Row.L("minStock")))));

    }
}
