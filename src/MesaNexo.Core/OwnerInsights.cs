using System.Globalization;
using System.Text.Json.Nodes;
namespace MesaNexo.Core;

// The second half of the owner's panel (docs/METRICAS-PANEL.md, schema 2): what happens around a sale rather than the
// sale itself. Kitchen times, returns, the time clock, requests and reviews are dated by when they happened; areas,
// kinds, couriers and customers by the close of the account, like the sales they split.
public sealed partial class Restaurant {
    public const int OwnerStatsSchema = 2;
    const int LapsedVipDays = 21;
    const int ExpiryWarningDays = 3;
    const int CostAlertBps = 1000;
    static JsonObject EmptyInsights() => J.O("areas", new JsonArray(), "kinds", new JsonArray(), "team", new JsonArray(), "labor", new JsonArray(), "couriers", new JsonArray(), "kitchen", new JsonArray(), "returns", new JsonArray(), "promises", new JsonArray(), "reviews", J.O("count", 0, "food", 0, "service", 0, "overall", 0, "low", 0), "reviewProducts", new JsonArray(), "expenses", new JsonArray(), "customers", J.O("tickets", 0, "net", 0, "first", 0, "repeat", 0));
    static long Seconds(DateTimeOffset? from, DateTimeOffset? to) => from is null || to is null ? 0 : Math.Max(0, (long)(to.Value - from.Value).TotalSeconds);
    // A shift split at each hour of the branch's clock, so labour lines up with the sales of that hour.
    static IEnumerable<(DateTimeOffset From, DateTimeOffset To)> HourSlices(TimeZoneInfo zone, DateTimeOffset from, DateTimeOffset to) {

        for (var t = from; t < to;) {

            var local = TimeZoneInfo.ConvertTime(t, zone);
            var next = t.AddTicks(TimeSpan.TicksPerHour - local.TimeOfDay.Ticks % TimeSpan.TicksPerHour);
            if (next > to) next = to;
            yield return (t, next);
            t = next;

        }

    }
    // Everything that is not dated by a sale, per business day. Team rows here carry only what a person did; OwnerDays
    // adds what they sold.
    Dictionary<string, (JsonObject Day, Dictionary<string, JsonObject> Team)> InsightDays(BranchState s, DateTimeOffset now, string? since) {

        var days = new Dictionary<string, (JsonObject Day, Dictionary<string, JsonObject> Team)>(StringComparer.Ordinal);
        var cache = new Dictionary<string, (string Date, int Hour)>();
        (string Date, int Hour)? At(JsonObject x, string key) { var text = x.S(key); if (text == "" || Moment(x, key) is not { } at) return null; if (!cache.TryGetValue(text, out var place)) cache[text] = place = Place(s, at); return place; }
        (JsonObject Day, Dictionary<string, JsonObject> Team)? Day(string date) {

            if (!Within(date, since)) return null;
            if (!days.TryGetValue(date, out var day)) days[date] = day = (EmptyInsights(), new Dictionary<string, JsonObject>(StringComparer.Ordinal));
            return day;

        }
        var users = s.List("user").ToDictionary(x => x.S("id"));
        JsonObject Person(string date, string staffId) {

            var team = Day(date)!.Value.Team;
            if (!team.TryGetValue(staffId, out var row)) team[staffId] = row = J.O("voids", 0, "voidAmount", 0, "discounts", 0, "discountAmount", 0, "rejected", 0, "reopens", 0, "drawerOpens", 0, "minutes", 0, "laborCost", 0);
            return row;

        }
        void Bump(JsonObject row, string key, long by = 1) => row[key] = row.L(key) + by;
        var units = s.List("unit").ToDictionary(x => x.S("id"));
        // Requests are how a waiter cancels or discounts; the one who asked is who the pattern belongs to.
        foreach (var r in s.List("request")) {

            if (At(r, "at") is not { } at || Day(at.Date) is null || r.S("actor") == "") continue;
            var row = Person(at.Date, r.S("actor"));
            if (r.S("status") == "rejected") Bump(row, "rejected");
            if (r.S("status") != "approved") continue;
            if (r.S("type") == "cancel") { Bump(row, "voids"); Bump(row, "voidAmount", units.GetValueOrDefault(r.S("unitId"))?.L("price") ?? 0); }
            if (r.S("type") is "discount" or "comp") { Bump(row, "discounts"); if (s.Related("visit", "id", r.S("visitId")).FirstOrDefault() is { } v) Bump(row, "discountAmount", Totals(s, v).L("discount")); }

        }
        foreach (var v in s.List("visit").Where(x => x.S("reopenedBy") != "")) if (At(v, "reopenedAt") is { } at && Day(at.Date) is not null) Bump(Person(at.Date, v.S("reopenedBy")), "reopens");
        // Only an opening someone asked for: the pulse sent with a cash payment has no actor.
        foreach (var job in s.List("printjob").Where(x => x.S("type") == "drawer" && x.S("actor") != "")) if (At(job, "at") is { } at && Day(at.Date) is not null) Bump(Person(at.Date, job.S("actor")), "drawerOpens");
        // The time clock, cut at each hour. Minutes and cost are rounded per person and hour, so labour by hour and by
        // person add up to the same figure.
        var zone = Zone(s);
        var slices = new Dictionary<(string Date, int Hour, string User), long>();
        foreach (var shift in s.List("attendance")) {

            if (Moment(shift, "inAt") is not { } from) continue;
            var to = Moment(shift, "outAt") ?? now;
            if (to - from > LongestShift) to = from + LongestShift;
            foreach (var (a, b) in HourSlices(zone, from, to)) { var place = Place(s, a); var key = (place.Date, place.Hour, shift.S("userId")); slices[key] = slices.GetValueOrDefault(key) + Seconds(a, b); }

        }
        var labor = new Dictionary<(string Date, int Hour), (long Minutes, long Cost)>();
        foreach (var ((date, hour, user), seconds) in slices) {

            if (Day(date) is null) continue;
            var minutes = seconds / 60;
            var cost = (minutes * (users.GetValueOrDefault(user)?.L("hourlyWage") ?? 0) + 30) / 60;
            var row = Person(date, user);
            Bump(row, "minutes", minutes);
            Bump(row, "laborCost", cost);
            var was = labor.GetValueOrDefault((date, hour));
            labor[(date, hour)] = (was.Minutes + minutes, was.Cost + cost);

        }
        foreach (var group in labor.GroupBy(x => x.Key.Date)) Day(group.Key)!.Value.Day["labor"] = Rows(group.OrderBy(x => x.Key.Hour).Where(x => x.Value.Minutes > 0).Select(x => J.O("hour", x.Key.Hour, "minutes", x.Value.Minutes, "cost", x.Value.Cost)));
        // Kitchen times, dated when the dish was ready: waiting is received to started, preparing is started to ready.
        foreach (var group in s.List("unit").Where(x => x.S("status") != "cancelled" && Moment(x, "readyAt") is not null).GroupBy(x => At(x, "readyAt")!.Value.Date)) {

            if (Day(group.Key) is not { } day) continue;
            day.Day["kitchen"] = Rows(group.GroupBy(x => x.S("productId")).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => {

                var times = g.Select(x => (Wait: Moment(x, "startedAt") is null ? 0 : Seconds(Moment(x, "receivedAt"), Moment(x, "startedAt")), Prep: Seconds(Moment(x, "startedAt") ?? Moment(x, "receivedAt"), Moment(x, "readyAt")), Pass: Moment(x, "deliveredAt") is null ? (long?)null : Seconds(Moment(x, "readyAt"), Moment(x, "deliveredAt")))).ToArray();
                return J.O("productId", g.Key, "name", g.First().S("name"), "station", g.First().S("station"), "count", times.Length, "waitSeconds", times.Sum(x => x.Wait), "prepSeconds", times.Sum(x => x.Prep), "maxSeconds", times.Max(x => x.Wait + x.Prep), "passCount", times.Count(x => x.Pass is not null), "passSeconds", times.Sum(x => x.Pass ?? 0));

            }));

        }
        foreach (var group in s.List("kitchenreturn").Where(x => Moment(x, "at") is not null).GroupBy(x => At(x, "at")!.Value.Date)) if (Day(group.Key) is { } day) day.Day["returns"] = Rows(group.GroupBy(x => (Product: x.S("productId"), Reason: x.S("reason"))).OrderBy(g => g.Key.Product, StringComparer.Ordinal).ThenBy(g => g.Key.Reason, StringComparer.Ordinal).Select(g => J.O("productId", g.Key.Product, "name", g.First().S("name"), "reason", g.Key.Reason, "count", g.Count())));
        // A promise is the pickup time, or the minutes quoted for a delivery. Pickup is kept when the order was ready;
        // a delivery when it left plus the route estimate, since the branch does not hear when it arrived.
        var promised = new List<(string Date, string Mode, long Promised, long Actual)>();
        foreach (var d in s.List("delivery")) {

            if (Moment(d, "at") is not { } placed) continue;
            var due = d.S("mode") == "pickup" ? Moment(d, "pickupAt") : d.L("promisedMinutes") > 0 ? placed.AddMinutes(d.L("promisedMinutes")) : null;
            var ready = s.Related("unit", "visitId", d.S("visitId")).Where(x => !VoidStatuses.Contains(x.S("status"))).Select(x => Moment(x, "readyAt")).ToArray();
            var done = d.S("mode") == "pickup" ? ready.Length > 0 && ready.All(x => x is not null) ? ready.Max() : null : Moment(d, "dispatchedAt")?.AddMinutes(d.L("etaMinutes"));
            if (due is null || done is null) continue;
            promised.Add((Place(s, due.Value).Date, d.S("mode"), Seconds(placed, due), Seconds(placed, done)));

        }
        foreach (var group in promised.GroupBy(x => x.Date)) if (Day(group.Key) is { } day) day.Day["promises"] = Rows(group.GroupBy(x => x.Mode).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => J.O("mode", g.Key, "count", g.Count(), "late", g.Count(x => x.Actual > x.Promised), "promisedSeconds", g.Sum(x => x.Promised), "actualSeconds", g.Sum(x => x.Actual))));
        // Reviews beside what was eaten: a review counts once for each product of its account.
        foreach (var group in s.List("review").Where(x => Moment(x, "at") is not null).GroupBy(x => At(x, "at")!.Value.Date)) {

            if (Day(group.Key) is not { } day) continue;
            var rows = group.ToArray();
            day.Day["reviews"] = J.O("count", rows.Length, "food", rows.Sum(x => x.L("food")), "service", rows.Sum(x => x.L("service")), "overall", rows.Sum(x => x.L("overall")), "low", rows.Count(x => x.L("overall") <= 2));
            day.Day["reviewProducts"] = Rows(rows.SelectMany(r => s.Related("unit", "visitId", r.S("visitId")).Where(x => !VoidStatuses.Contains(x.S("status"))).GroupBy(x => x.S("productId")).Select(g => (Product: g.Key, Name: g.First().S("name"), Overall: r.L("overall")))).GroupBy(x => x.Product).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => J.O("productId", g.Key, "name", g.First().Name, "count", g.Count(), "overall", g.Sum(x => x.Overall))));

        }
        var accounts = s.List("expenseaccount").ToDictionary(x => x.S("id"), x => x.S("name"));
        // Expenses by account, and cash paid out of the drawer without one. A refund or an expense already counted is left out.
        var spent = s.List("expense").Select(x => (Row: x, Account: accounts.GetValueOrDefault(x.S("accountId"), "Sin cuenta"))).Concat(s.List("movement").Where(x => x.S("type") == "expense" && x.S("refundId") == "" && x.S("expenseId") == "").Select(x => (Row: x, Account: "Gastos de caja")));
        foreach (var group in spent.Where(x => Moment(x.Row, "at") is not null).GroupBy(x => At(x.Row, "at")!.Value.Date)) if (Day(group.Key) is { } day) day.Day["expenses"] = Rows(group.GroupBy(x => x.Account).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => J.O("account", g.Key, "amount", g.Sum(x => x.Row.L("amount")), "count", g.Count())));
        return days;

    }
    // The sections that split the day's sales: they add up to the same net as the hours.
    void SaleInsights(BranchState s, JsonObject day, string date, (JsonObject Visit, JsonObject Totals, JsonObject[] Units)[] figures, JsonObject[] sold, Dictionary<string, JsonObject> team, SaleContext context) {

        day["areas"] = Rows(figures.GroupBy(x => context.Tables.GetValueOrDefault(x.Visit.S("tableId"))?.S("zone") ?? "").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => J.O("area", g.Key, "tickets", g.Count(), "people", g.Sum(x => x.Visit.L("people")), "net", g.Sum(x => x.Totals.L("total")))));
        day["kinds"] = Rows(sold.GroupBy(x => context.Kinds.GetValueOrDefault(context.Products.GetValueOrDefault(x.S("productId"))?.S("category") ?? "", "")).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => J.O("kind", g.Key, "quantity", g.Count(), "revenue", g.Sum(x => x.L("price")))));
        day["couriers"] = Rows(figures.Select(x => (x.Totals, Courier: context.Deliveries[x.Visit.S("id")].Select(d => d.S("courierId")).FirstOrDefault(c => c != ""))).Where(x => x.Courier is not null).GroupBy(x => x.Courier!).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => J.O("courierId", g.Key, "name", context.Couriers.GetValueOrDefault(g.Key)?.S("name") ?? "—", "deliveries", g.Count(), "net", g.Sum(x => x.Totals.L("total")))));
        var identified = figures.Where(x => x.Visit.S("customerId") != "").ToArray();
        var people = identified.Select(x => x.Visit.S("customerId")).Distinct().ToArray();
        day["customers"] = J.O("tickets", identified.Length, "net", identified.Sum(x => x.Totals.L("total")), "first", people.Count(c => context.FirstVisit.GetValueOrDefault(c) == date), "repeat", people.Count(c => string.CompareOrdinal(context.FirstVisit.GetValueOrDefault(c), date) < 0));
        foreach (var g in figures.GroupBy(x => x.Visit.S("waiterId"))) {

            if (!team.TryGetValue(g.Key, out var row)) team[g.Key] = row = J.O("voids", 0, "voidAmount", 0, "discounts", 0, "discountAmount", 0, "rejected", 0, "reopens", 0, "drawerOpens", 0, "minutes", 0, "laborCost", 0);
            row["people"] = g.Sum(x => x.Visit.L("people"));
            row["tables"] = (long)g.Select(x => x.Visit.S("tableId")).Where(x => x != "").Distinct().Count();

        }
        day["team"] = Rows(team.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => J.O("staffId", x.Key, "name", context.Users.GetValueOrDefault(x.Key)?.S("name") ?? "—", "role", context.Users.GetValueOrDefault(x.Key)?.S("role") ?? "", "people", x.Value.L("people"), "tables", x.Value.L("tables"), "voids", x.Value.L("voids"), "voidAmount", x.Value.L("voidAmount"), "discounts", x.Value.L("discounts"), "discountAmount", x.Value.L("discountAmount"), "rejected", x.Value.L("rejected"), "reopens", x.Value.L("reopens"), "drawerOpens", x.Value.L("drawerOpens"), "minutes", x.Value.L("minutes"), "laborCost", x.Value.L("laborCost"))));

    }
    sealed record SaleContext(Dictionary<string, JsonObject> Tables, Dictionary<string, string> Kinds, Dictionary<string, JsonObject> Products, ILookup<string, JsonObject> Deliveries, Dictionary<string, JsonObject> Couriers, Dictionary<string, string> FirstVisit, Dictionary<string, JsonObject> Users);
    SaleContext SaleContextOf(BranchState s, ILookup<string, JsonObject> sales) => new(
        s.List("table").ToDictionary(x => x.S("id")),
        s.List("category").GroupBy(x => x.S("name")).ToDictionary(g => g.Key, g => g.First().S("family")),
        s.List("product").ToDictionary(x => x.S("id")),
        s.List("delivery").ToLookup(x => x.S("visitId")),
        s.List("courier").ToDictionary(x => x.S("id")),
        sales.SelectMany(g => g.Where(v => v.S("customerId") != "").Select(v => (Customer: v.S("customerId"), Date: g.Key))).GroupBy(x => x.Customer).ToDictionary(g => g.Key, g => g.Select(x => x.Date).Min(StringComparer.Ordinal)!),
        s.List("user").ToDictionary(x => x.S("id")));
    // What the owner sees of the branch right now beyond the floor: tables, the clock, customers, expiring lots,
    // rising costs and the roster. Customers go only as counts: no name or phone leaves the branch.
    void LiveInsights(BranchState s, DateTimeOffset now, JsonObject live, JsonObject[] accounts) {

        var tables = s.List("table").Select(t => TableStatus(s, t)).ToArray();
        live["tables"] = J.O("total", tables.Length, "free", tables.Count(x => x == "free"), "occupied", tables.Count(x => x == "occupied"), "cleaning", tables.Count(x => x == "cleaning"), "reserved", tables.Count(x => x == "reserved"));
        // An account partly paid: some money is in, some is still owed.
        var paid = accounts.Select(v => (Paid: s.Related("payment", "visitId", v.S("id")).Sum(x => x.L("amount")), Owed: Totals(s, v).L("balance"))).Where(x => x.Paid > 0 && x.Owed > 0).Select(x => x.Paid).ToArray();
        live["partial"] = J.O("count", paid.Length, "paid", paid.Sum());
        var users = s.List("user").ToDictionary(x => x.S("id"));
        var clocked = s.List("attendance").Where(x => x.S("outAt") == "" && Moment(x, "inAt") is { } at && now - at <= LongestShift).Select(x => users.GetValueOrDefault(x.S("userId"))).Where(x => x is not null).ToArray();
        live["onClock"] = J.O("people", clocked.Length, "hourlyCost", clocked.Sum(x => x!.L("hourlyWage")));
        live["customers"] = CustomerSnapshot(s, now);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone(s)).DateTime);
        var ingredients = s.List("ingredient").ToDictionary(x => x.S("id"));
        var uses = new Dictionary<string, List<JsonObject>>();
        foreach (var product in s.List("product").Where(x => x.S("recipeVersionId") != "")) {

            try { foreach (var line in Explode(s, product.S("recipeVersionId"), 1000)) { if (!uses.TryGetValue(line.Key, out var list)) uses[line.Key] = list = []; list.Add(product); } } catch (BusinessException) { }

        }
        JsonArray Dishes(string ingredientId) => new([.. uses.GetValueOrDefault(ingredientId, []).OrderBy(x => x.S("name"), StringComparer.Ordinal).Take(5).Select(x => (JsonNode?)JsonValue.Create(x.S("name")))]);
        live["expiring"] = Rows(s.List("stocklayer").Where(x => x.L("remaining") > 0 && x.S("expiresOn") != "" && ingredients.ContainsKey(x.S("ingredientId"))).Select(x => (Layer: x, On: DateOnly.ParseExact(x.S("expiresOn"), "yyyy-MM-dd", CultureInfo.InvariantCulture))).Where(x => x.On.DayNumber - today.DayNumber <= ExpiryWarningDays).GroupBy(x => (Ingredient: x.Layer.S("ingredientId"), x.On)).OrderBy(g => g.Key.On).ThenBy(g => ingredients[g.Key.Ingredient].S("name"), StringComparer.Ordinal).Take(100).Select(g => J.O("ingredientId", g.Key.Ingredient, "name", ingredients[g.Key.Ingredient].S("name"), "unit", ingredients[g.Key.Ingredient].S("unit"), "quantity", g.Sum(x => x.Layer.L("remaining")), "cost", g.Sum(x => x.Layer.L("remainingCost")), "expiresOn", g.Key.On.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "days", g.Key.On.DayNumber - today.DayNumber, "products", Dishes(g.Key.Ingredient))));
        // The newest purchase of an ingredient against the one before it. Cost per 1000 base units: kg, l or 1000 pieces.
        var alerts = new List<JsonObject>();
        foreach (var group in s.List("stocklayer").Where(x => x.L("received") > 0 && s.Related("stockmovement", "layerId", x.S("id")).Any(m => m.S("type") is "purchase" or "receipt")).GroupBy(x => x.S("ingredientId"))) {

            var two = group.OrderByDescending(x => x.L("order")).Take(2).ToArray();
            if (two.Length < 2 || !ingredients.TryGetValue(group.Key, out var ingredient) || Moment(two[0], "at") is not { } at || now - at > TimeSpan.FromDays(30)) continue;
            long Rate(JsonObject x) => x.L("originalCost") * 1_000_000 / x.L("received");
            var before = Rate(two[1]); var after = Rate(two[0]);
            if (before <= 0 || (after - before) * 10_000 / before < CostAlertBps) continue;
            alerts.Add(J.O("ingredientId", group.Key, "name", ingredient.S("name"), "unit", ingredient.S("unit"), "before", before, "after", after, "changeBps", (after - before) * 10_000 / before, "at", two[0].S("at"), "products", Rows(uses.GetValueOrDefault(group.Key, []).OrderBy(x => x.S("name"), StringComparer.Ordinal).Take(10).Select(p => { var cost = PortionCost(s, p); return J.O("name", p.S("name"), "price", p.L("price"), "cost", cost < 0 ? null : cost); }))));

        }
        live["costAlerts"] = Rows(alerts.OrderByDescending(x => x.L("changeBps")).Take(20));
        live["roster"] = Roster(s, now);

    }
    // Lifetime value and return rhythm of the guests the branch knows, as counts and averages only.
    JsonObject CustomerSnapshot(BranchState s, DateTimeOffset now) {

        var visits = s.Related("visit", "status", "closed").Where(v => v.S("customerId") != "" && Moment(v, "closedAt") is not null).GroupBy(v => v.S("customerId")).Select(g => (Dates: g.Select(v => Moment(v, "closedAt")!.Value).Order().ToArray(), Spent: g.Sum(v => Totals(s, v).L("total")))).ToArray();
        var gaps = visits.Where(x => x.Dates.Length > 1).Select(x => (x.Dates[^1] - x.Dates[0]).TotalDays / (x.Dates.Length - 1)).ToArray();
        var vip = visits.OrderByDescending(x => x.Spent).Take(visits.Length >= 10 ? visits.Length / 10 : 0).ToArray();
        var recent = visits.Where(x => now - x.Dates[^1] <= TimeSpan.FromDays(90)).ToArray();
        return J.O("identified", visits.Length, "active90", recent.Length, "repeat90", recent.Count(x => x.Dates.Count(d => now - d <= TimeSpan.FromDays(90)) > 1), "averageGapDays", gaps.Length == 0 ? 0 : (long)Math.Round(gaps.Average()), "lifetimeAverage", visits.Length == 0 ? 0 : visits.Sum(x => x.Spent) / visits.Length, "visitsAverageTenths", visits.Length == 0 ? 0 : visits.Sum(x => x.Dates.Length) * 10 / visits.Length, "vip", vip.Length, "vipLapsed", vip.Count(x => now - x.Dates[^1] > TimeSpan.FromDays(LapsedVipDays)));

    }
    // Hires and exits of the last twelve months, by month of the branch. A user without a hiring date was there before.
    JsonObject Roster(BranchState s, DateTimeOffset now) {

        var users = s.List("user").Where(x => x.S("role") != "owner").ToArray();
        var local = TimeZoneInfo.ConvertTime(now, Zone(s)).DateTime;
        string Month(DateTime d) => d.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        string? Ended(JsonObject u) => Moment(u, "endedAt") is { } at ? Month(TimeZoneInfo.ConvertTime(at, Zone(s)).DateTime) : null;
        string Hired(JsonObject u) => u.S("hiredAt").Length >= 7 ? u.S("hiredAt")[..7] : "0000-00";
        var months = Enumerable.Range(0, 12).Select(i => Month(new DateTime(local.Year, local.Month, 1).AddMonths(i - 11))).ToArray();
        return J.O("active", users.Count(x => x.B("active", true)), "byRole", new JsonObject(users.Where(x => x.B("active", true)).GroupBy(x => x.S("role")).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => KeyValuePair.Create(g.Key, (JsonNode?)g.Count()))), "months", Rows(months.Select(m => J.O("month", m, "hires", users.Count(u => Hired(u) == m), "exits", users.Count(u => Ended(u) == m), "headcount", users.Count(u => string.CompareOrdinal(Hired(u), m) <= 0 && (Ended(u) is not { } e || string.CompareOrdinal(e, m) > 0))))));

    }
}
