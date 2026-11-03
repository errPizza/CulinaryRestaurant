using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
namespace MesaNexo.Tests;
// The owner's panel: the hand-calculated vector against OwnerStats, then real Z closes, reopenings and delivery to a
// server that goes away, repeats and refuses.
static class OwnerStatsTests {
    public static async Task Run(string root, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        async Task Deny(Func<Task> act, string name) { try { await act(); throw new Exception("Rejection expected: " + name); } catch (BusinessException) { Check(true, name); } }
        static long Sum(IEnumerable<JsonObject> rows, string key) => rows.Sum(x => x.L(key));
        static IEnumerable<JsonObject> Hours(IEnumerable<JsonObject> days, long z) => days.SelectMany(d => d.A("hours").Objects()).Where(x => x.L("z") == z);

        var domain = new Restaurant(new Passwords());
        var vector = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "tests", "vectors", "owner-stats", "v1.json")))!.AsObject();
        BranchState Seed() {

            var s = new BranchState();
            var branch = vector.Obj("branch");
            s.Add("meta", J.O("id", "site", "siteId", "vector", "timeZone", branch.S("timeZone"), "businessDayCutoffMinutes", branch.L("businessDayCutoffMinutes"), "zNumber", vector.A("zReports").Count));
            foreach (var (id, product) in vector.Obj("products")) s.Add("product", J.O("id", id, "name", product.S("name"), "category", product.S("category")));
            foreach (var (id, name) in vector.Obj("staff")) s.Add("user", J.O("id", id, "name", name!.GetValue<string>()));
            void Copy(string list, string kind) { foreach (var row in vector.A(list).Objects()) s.Add(kind, (JsonObject)row.DeepClone()); }
            Copy("units", "unit"); Copy("payments", "payment"); Copy("refunds", "refund"); Copy("movements", "movement"); Copy("stockMovements", "stockmovement");
            foreach (var visit in vector.A("visits").Objects()) { var v = (JsonObject)visit.DeepClone(); v["status"] = "closed"; if (v.ContainsKey("firstClosedAt")) v["reopenedAt"] = v.S("closedAt"); s.Add("visit", v); }
            foreach (var c in vector.A("cashCloses").Objects()) s.Add("cash", J.O("id", c.S("cashId"), "status", "closed", "blind", c.B("blind"), "closedAt", c.S("closedAt"), "difference", c.L("counted") - c.L("expected"), "card", J.O("difference", c.L("cardCounted") - c.L("cardExpected")), "transfer", J.O("difference", c.L("transferCounted") - c.L("transferExpected"))));
            foreach (var z in vector.A("zReports").Objects()) s.Add("zreport", J.O("number", z.L("number"), "from", z.S("from"), "to", z.S("to"), "summary", z, "digest", ""));
            return s;

        }
        var seeded = Seed();
        Directory.CreateDirectory(Path.Combine(root, ".lab"));
        var later = DateTimeOffset.Parse("2026-11-03T12:00:00Z");
        var costs = vector.Obj("products").ToDictionary(x => x.Key, x => x.Value!["unitCost"]?.DeepClone());
        var frozen = vector.Obj("expected").A("days").Objects().ToDictionary(x => x.S("businessDate"), _ => new JsonObject(costs.Select(c => KeyValuePair.Create(c.Key, c.Value?.DeepClone()))));
        var days = domain.OwnerDays(seeded, later, frozen);
        foreach (var expected in vector.Obj("expected").A("days").Objects()) {

            var date = expected.S("businessDate");
            foreach (var section in new[] { "hours", "payments", "refunds", "channels", "products", "staff", "cash", "cashCloses", "inventory" }) Check(JsonNode.DeepEquals(days[date][section], expected[section]), $"{date}: OwnerStats {section} equal the hand calculation");
            Check(days[date].B("complete"), $"{date}: a past day with nothing open is complete");

        }
        Check(days.Count == 2, "the vector holds two business days");
        // The uploads CulinaryServer tests against (test/fixtures of that repository), so both sides keep one contract.
        var vectorDays = new JsonArray([.. days.Values.Select(day => { var sent = (JsonObject)day.DeepClone(); sent["revision"] = 1L; return (JsonNode?)sent; })]);
        File.WriteAllText(Path.Combine(root, ".lab", "stats-vector.json"), J.O("schema", 1, "sentAt", later.ToString("O"), "branch", domain.OwnerBranch(seeded, "0.3.3"), "live", domain.OwnerLive(seeded, later), "days", vectorDays).ToJsonString());
        Check(domain.OwnerErrors(seeded, days).Count == 0, "the vector meets every identity");
        foreach (var row in vector.Obj("expected").A("reconciliation").Objects()) {

            var rows = Hours(days.Values, row.L("z")).ToArray();
            Check(Sum(rows, "net") == row.L("panelNet") && Sum(rows, "tickets") == row.L("panelTickets"), $"Z{row.L("z")}: the panel counts what the vector says");

        }
        Check(domain.OwnerDays(seeded, later)["2026-10-30"].A("products").Objects().First().L("unitCost", -1) == -1 && days["2026-10-30"].A("products").Objects().First().L("unitCost") == 7000, "a published day keeps its unit cost");
        var broken = Seed();
        foreach (var visit in broken.List("visit")) visit.Remove("reopenedAt");
        Check(domain.OwnerErrors(broken, domain.OwnerDays(broken, later, frozen)).ContainsKey("2026-10-30"), "a Z that differs from the panel with no reopening is an error on the day of that Z");
        var tampered = domain.OwnerDays(seeded, later, frozen);
        tampered["2026-10-31"].A("staff").Objects().First()["net"] = 1L;
        Check(domain.OwnerErrors(seeded, tampered).ContainsKey("2026-10-31"), "sales that differ by waiter stop the day");
        var edge = Seed();
        edge.Add("visit", J.O("id", "early", "status", "closed", "people", 1, "closedAt", "2026-10-29T12:00:00Z"));
        edge.Add("visit", J.O("id", "last", "status", "closed", "people", 1, "closedAt", "2026-11-01T09:00:00Z"));
        var edges = domain.OwnerDays(edge, later, frozen);
        Check(Hours(edges.Values, 0).Count() == 1 && Hours(edges.Values, 3).Count() == 1, "a sale before the first Z has no close and one at the end of the last waits for the next");
        Check(domain.BusinessDate(seeded, DateTimeOffset.Parse("2026-10-31T09:59:00Z")) == "2026-10-30" && domain.BusinessDate(seeded, DateTimeOffset.Parse("2026-10-31T10:00:00Z")) == "2026-10-31", "the cutoff opens the business day");
        Check(!domain.OwnerDays(seeded, DateTimeOffset.Parse("2026-10-31T12:00:00Z"), frozen)["2026-10-31"].B("complete"), "the day in progress is not complete");
        var ledger = new StatsLedger(null);
        var finished = (JsonObject)days["2026-10-30"].DeepClone();
        ledger.Delivered([finished]);
        Check(ledger.Holds(finished) && ledger.Costs["2026-10-30"].L("pizza") == 7000, "the ledger remembers what a finished day published");
        finished["hours"] = new JsonArray();
        Check(!ledger.Holds(finished), "changed figures make the day pending again");
        Check(ledger.NextRevision(DateTimeOffset.UnixEpoch) == 1 && ledger.NextRevision(DateTimeOffset.UnixEpoch) == 2 && ledger.NextRevision(later) == later.ToUnixTimeSeconds(), "the revision grows even with the clock set back");

        const string site = "owner-stats-site";
        var folder = Path.Combine(root, ".lab", "owner-stats-" + J.Id());
        Directory.CreateDirectory(folder);
        if (Directory.Exists(TestLicense.Folder(site))) Directory.Delete(TestLicense.Folder(site), true);
        await using var fake = new FakeCulinaryServer(site);
        await fake.App.StartAsync();
        using var runtime = LabPostgres.Test(root, folder, site);
        var service = new BranchService(runtime, site, new Passwords()) { StatsPause = TimeSpan.Zero, StatsFullEvery = TimeSpan.Zero };
        var file = Path.Combine(folder, "owner-stats.json");
        service.UseStats(new StatsLedger(file));
        var password = "Stats-" + J.Id();
        var enrollment = J.Id();
        await service.Initialize(J.O("siteId", site, "name", "Panel", "managerName", "Gerencia", "managerUsername", "gerencia", "managerPassword", password, "enrollmentCode", enrollment));
        service.UseLicensing(new LicenseOptions(TestLicense.Folder(site), fake.Url, TestLicense.HardwareFile(site), UseTpm: false));
        await service.SendStats();
        Check(fake.StatsPosts.Count == 0, "without a usable license nothing is uploaded");
        await service.Activate(J.O("licenseId", TestLicense.LicenseId(site), "activationCode", fake.Code));
        var token = (await service.Login(J.O("username", "gerencia", "password", password, "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "Oficina", "enrollmentCode", enrollment))).S("token");
        async Task<JsonObject> Act(string type, JsonObject p) => (await service.Action(token, J.O("operationId", J.Id(), "type", type, "payload", p))).Obj("result");
        async Task Serve(string visitId) { foreach (var uid in (await service.Load()).List("unit").Where(x => x.S("visitId") == visitId && x.S("status") == "received").Select(x => x.S("id")).ToArray()) { await Act("unit.state", J.O("id", uid, "status", "preparing")); await Act("unit.state", J.O("id", uid, "status", "ready")); } await Act("visit.deliverAll", J.O("id", visitId)); }
        JsonObject Settings(string zone, long cutoff) => J.O("name", "Panel", "color", "#f2b93b", "contact", "", "delayMinutes", 15, "discountLimit", 20, "stations", new[] { "Cocina" }, "timeZone", zone, "businessDayCutoffMinutes", cutoff);

        await Act("catalog.category", J.O("name", "Food"));
        var taco = await Act("catalog.product", J.O("name", "Taco", "price", 3000, "taxBps", 1600, "category", "Food", "station", "Cocina"));
        var customer = await Act("customer.save", J.O("name", "Mario Cliente", "phone", "5512345678"));
        var cash = await Act("cash.open", J.O("initial", 50000));
        var first = await Act("sale.quick", J.O("method", "cash", "customerId", customer.S("id"), "items", new[] { J.O("productId", taco.S("id"), "quantity", 2) }));
        var second = await Act("sale.quick", J.O("method", "card", "items", new[] { J.O("productId", taco.S("id"), "quantity", 1) }));
        await Serve(first.S("visitId"));
        await Serve(second.S("visitId"));
        await service.SendStats();
        var post = fake.StatsPosts[^1];
        var today = post.A("days").Objects().Single();
        Check(post.L("schema") == 1 && today.L("revision") > 0 && post.Obj("branch").S("timeZone") == "America/Mexico_City" && post.Obj("branch").L("businessDayCutoffMinutes") == 240 && post.Obj("live").B("cashOpen"), "the upload carries the day, the branch and what is live");
        Check(!today.B("complete"), "today is sent as not complete");
        Check(!post.ToJsonString().Contains("Mario") && !post.ToJsonString().Contains("5512345678") && !post.ToJsonString().Contains(first.S("visitId")), "no customer or ticket leaves the branch");
        await service.SendStats();
        Check(fake.StatsPosts[^1].A("days").Count == 0 && fake.StatsPosts.Count == 2, "with no change only the heartbeat goes out");

        await Act("cash.close", J.O("id", cash.S("id"), "counted", 56000));
        var z1 = await Act("report.z", new());
        var summary = z1.Obj("summary");
        var state = await service.Load();
        var real = domain.OwnerDays(state, DateTimeOffset.UtcNow);
        var hours = Hours(real.Values, 1).ToArray();
        Check(z1.L("number") == 1 && Sum(hours, "net") == summary.L("net") && summary.L("net") == 9000, "a real report.z equals the panel to the cent");
        foreach (var key in new[] { "tickets", "people", "gross", "tax", "voidedCount", "voidedAmount", "comps", "staffMeals" }) Check(Sum(hours, key) == summary.L(key), "report.z and the panel agree on " + key);
        Check(Sum(real.Values.SelectMany(d => d.A("payments").Objects()), "amount") == summary.L("payments") && Sum(real.Values.SelectMany(d => d.A("payments").Objects()), "tips") == summary.L("tips"), "report.z and the panel agree on payments");
        await service.SendStats();
        Check(fake.StatsDays.Values.Single().A("hours").Objects().All(x => x.L("z") == 1) && fake.StatsDays.Values.Single().A("zReports").Objects().Single().S("digest") == z1.S("digest"), "the frozen Z travels with its day and its digest");

        await Act("visit.reopen", J.O("id", first.S("visitId"), "reason", "Se cobró de más"));
        await service.SendStats();
        var reopened = fake.StatsDays.Values.Single();
        File.WriteAllText(Path.Combine(root, ".lab", "stats-real.json"), fake.StatsPosts[^1].ToJsonString());
        Check(Sum(reopened.A("hours").Objects(), "net") == 3000 && Sum(reopened.A("zReports").Objects(), "net") == 9000 && !reopened.B("complete"), "a reopened account leaves the panel while the Z keeps its frozen figures");
        await Act("visit.close", J.O("id", first.S("visitId")));
        await Act("cash.open", J.O("initial", 0));
        await Act("cash.close", J.O("id", (await service.Load()).List("cash").Single(c => c.S("status") == "open").S("id"), "counted", 0));
        var z2 = await Act("report.z", new());
        state = await service.Load();
        real = domain.OwnerDays(state, DateTimeOffset.UtcNow);
        var frozenZ1 = state.List("zreport").Single(x => x.L("number") == 1).Obj("summary");
        Check(z2.Obj("summary").L("net") == 6000 && frozenZ1.L("net") == 9000, "the reopened account is counted by both frozen closes");
        Check(Sum(Hours(real.Values, 1), "net") == 3000 && Sum(Hours(real.Values, 2), "net") == 6000, "the panel counts it once, in the later close");
        Check(frozenZ1.L("net") + z2.Obj("summary").L("net") - Sum(real.Values.SelectMany(d => d.A("hours").Objects()), "net") == 6000 && domain.OwnerErrors(state, real).Count == 0, "the difference is exactly the reopened account and raises no error");

        await Deny(() => Act("settings.save", Settings("Marte/Olympus", 240)), "an unknown time zone is refused");
        await Deny(() => Act("settings.save", Settings("America/Tijuana", 800)), "a cutoff past noon is refused");
        await Act("settings.save", Settings("America/Tijuana", 0));
        state = await service.Load();
        var instant = DateTimeOffset.Parse("2026-10-31T07:30:00Z");
        Check(domain.BusinessDate(state, instant) == "2026-10-31", "time zone and cutoff decide the business day of a branch");
        Check(state.Get("visit", second.S("visitId")).S("closedAt") != "" && domain.OwnerBranch(state, "0").S("timeZone") == "America/Tijuana", "the upload tells the server the zone of the branch");
        var hour = TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(state.Get("visit", second.S("visitId")).S("closedAt")), TimeZoneInfo.FindSystemTimeZoneById("America/Tijuana")).Hour;
        Check(domain.OwnerDays(state, DateTimeOffset.UtcNow).Values.SelectMany(d => d.A("hours").Objects()).Any(x => x.L("hour") == hour), "hours follow the clock of the branch");
        await Act("settings.save", Settings("America/Mexico_City", 240));

        await service.SendStats();
        await Act("cash.open", J.O("initial", 0));
        fake.StatsDown = true;
        await Act("sale.quick", J.O("method", "cash", "items", new[] { J.O("productId", taco.S("id"), "quantity", 1) }));
        var before = fake.StatsDays.Values.Single().L("revision");
        try { await service.SendStats(); throw new Exception("Expected the upload to fail"); } catch (ServerRejection) { Check(true, "an unreachable server surfaces to the worker, which tries again"); }
        var restarted = new StatsLedger(file);
        Check(!restarted.Holds(domain.OwnerDays(await service.Load(), DateTimeOffset.UtcNow).Values.Last()), "the change stays pending across a restart");
        service.UseStats(restarted);
        fake.StatsDown = false;
        await service.SendStats();
        Check(fake.StatsDays.Values.Single().L("revision") > before, "the pending day arrives when the server returns");
        var settled = fake.StatsPosts.Count;
        await service.SendStats();
        Check(fake.StatsPosts[^1].A("days").Count == 0 && fake.StatsPosts.Count == settled + 1, "a restart does not send delivered days again");
        fake.StatsMissing = true;
        await Act("sale.quick", J.O("method", "cash", "items", new[] { J.O("productId", taco.S("id"), "quantity", 1) }));
        var quiet = fake.StatsPosts.Count;
        await service.SendStats();
        Check(fake.StatsPosts.Count == quiet, "a server without the route is not an error and the day waits");
        fake.StatsMissing = false;
        var held = fake.StatsDays.Values.Single();
        held["revision"] = long.MaxValue / 2;
        await service.SendStats();
        Check(fake.StatsPosts[^1].A("days").Count == 1 && fake.StatsDays.Values.Single().L("revision") == long.MaxValue / 2, "a repeated or old revision never replaces what the server holds");
        await service.SendStats();
        Check(fake.StatsPosts[^1].A("days").Count == 0, "a day the server ignored counts as answered");
        held["revision"] = 0L;

        // A day left with nothing replaces what was sent, and a long catch-up goes newest first in bounded batches.
        var stale = Enumerable.Range(1, 33).Select(n => { var day = Restaurant.OwnerEmptyDay(DateTimeOffset.UtcNow.AddDays(-n - 2).ToString("yyyy-MM-dd")); day["hours"] = new JsonArray(J.O("net", 1)); return day; }).ToArray();
        service.Stats.Delivered(stale);
        service.StatsFullEvery = TimeSpan.FromHours(1);
        await service.SendStats();
        await service.SendStats();
        Check(fake.StatsPosts[^1].A("days").Count == 0 && !fake.StatsDays.ContainsKey(stale[^1].S("businessDate")), "between full passes only the recent days are added up");
        service.StatsFullEvery = TimeSpan.Zero;
        var mark = fake.StatsPosts.Count;
        await service.SendStats();
        var batches = fake.StatsPosts.Skip(mark).Select(x => x.A("days").Objects().Select(d => d.S("businessDate")).ToArray()).ToArray();
        Check(batches.Length >= 2 && batches.All(b => b.Length <= 10) && string.CompareOrdinal(batches[0][0], batches[^1][^1]) > 0, "a catch-up goes in bounded batches, newest first");
        Check(fake.StatsDays[stale[^1].S("businessDate")].A("hours").Count == 0 && fake.StatsDays[stale[^1].S("businessDate")].B("complete"), "a day with nothing left replaces what the server held");
        service.Stats.Delivered([Restaurant.OwnerEmptyDay("2020-01-01")]);
        var ancient = (JsonObject)Restaurant.OwnerEmptyDay("2020-01-01").DeepClone();
        ancient["hours"] = new JsonArray(J.O("net", 1));
        service.Stats.Delivered([ancient]);
        await service.SendStats();
        Check(!fake.StatsDays.ContainsKey("2020-01-01"), "days past the retention are not sent");
        var refused = stale;
        service.Stats.Delivered(refused);
        fake.StatsRefused.Add(refused[0].S("businessDate"));
        await service.SendStats();
        fake.StatsRefused.Clear();
        Check(fake.StatsDays[refused[^1].S("businessDate")].L("revision") > fake.StatsDays[refused[0].S("businessDate")].L("revision"), "a day the server refuses holds back no other");
        await service.SendStats();
        Check(fake.StatsPosts[^1].A("days").Objects().Single().S("businessDate") == refused[0].S("businessDate"), "the refused day stays pending");

    }
}
