using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Api;

// What CulinaryServer already holds of each business day. A file, not the store: recording every send there would
// fill the audit trail and wake every terminal. Pending work needs no queue: a day whose figures do not match this
// ledger is pending, so nothing is lost without internet. Losing the file only sends the days again.
public sealed class StatsLedger {
    public const string DefaultFile = "/var/lib/culinaryos-app/owner-stats.json";
    readonly string? file;
    readonly Dictionary<string, JsonObject> days = [];
    long revision;
    public StatsLedger(string? file) {

        this.file = file;
        try {

            if (file is null || !File.Exists(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonObject stored) return;
            revision = stored.L("revision");
            foreach (var (date, day) in stored.Obj("days")) days[date] = (JsonObject)day!.DeepClone();

        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { days.Clear(); }

    }
    static string Hash(JsonObject day) => J.Hash(J.Canonical(day));
    public IEnumerable<string> Dates => days.Keys;
    public bool Holds(JsonObject day) => days.TryGetValue(day.S("businessDate"), out var known) && known.S("hash") == Hash(day);
    // The costs published with each finished day, which later sends of that day must repeat.
    public IReadOnlyDictionary<string, JsonObject> Costs => days.ToDictionary(x => x.Key, x => x.Value.Obj("costs"));
    // Seconds, not a counter: a restored backup or a lost ledger must still beat what the server holds.
    public long NextRevision(DateTimeOffset now) => revision = Math.Max(revision + 1, now.ToUnixTimeSeconds());
    public void Delivered(IEnumerable<JsonObject> sent) {

        foreach (var day in sent) {

            var date = day.S("businessDate");
            var costs = days.TryGetValue(date, out var known) ? (JsonObject)known.Obj("costs").DeepClone() : [];
            if (day.B("complete")) foreach (var product in day.A("products").Objects()) if (!costs.ContainsKey(product.S("productId"))) costs[product.S("productId")] = product["unitCost"]?.DeepClone();
            days[date] = J.O("hash", Hash(day), "costs", costs);

        }
        if (file is null) return;
        try {

            var temporary = file + ".tmp";
            File.WriteAllText(temporary, J.O("revision", revision, "days", new JsonObject(days.Select(x => KeyValuePair.Create<string, JsonNode?>(x.Key, x.Value.DeepClone())))).ToJsonString());
            File.Move(temporary, file, true);

        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

    }
}
public sealed partial class BranchService {
    const int StatsSchema = 1, StatsBatchDays = 10, StatsHistoryDays = 762, StatsRecentDays = 14;
    public TimeSpan StatsPause { get; set; } = TimeSpan.FromSeconds(2);
    // The whole history takes seconds to add up. Old days change only through a reopening or new settings, so those wait for this.
    public TimeSpan StatsFullEvery { get; set; } = TimeSpan.FromHours(1);
    DateTimeOffset statsFullPass = DateTimeOffset.MinValue;
    public StatsLedger Stats { get; private set; } = new(null);
    public void UseStats(StatsLedger ledger) => Stats = ledger;
    // Every call says the branch is alive, with the newest days first so a long catch-up shows today at once.
    public async Task SendStats(CancellationToken ct = default) {

        var s = await View(ct);
        if (!License(s).Usable) return;
        var now = DateTimeOffset.UtcNow;
        SortedDictionary<string, JsonObject> days;
        Dictionary<string, List<string>> errors;
        JsonObject branchInfo, live;
        // These figures read every sale of the branch: odd old data must cost a fault report, not the server that takes orders.
        try {

            var since = now - statsFullPass >= StatsFullEvery ? null : Domain.BusinessDate(s, now.AddDays(-StatsRecentDays));
            if (since is null) statsFullPass = now;
            days = Domain.OwnerDays(s, now, Stats.Costs, since);
            // A day with nothing left, because its only account was reopened or the cutoff moved, must still replace what was sent.
            foreach (var date in Stats.Dates.Where(x => since is null || string.CompareOrdinal(x, since) >= 0)) days.TryAdd(date, Restaurant.OwnerEmptyDay(date));
            errors = Domain.OwnerErrors(s, days, since);
            branchInfo = Domain.OwnerBranch(s, FaultLog.Version);
            live = Domain.OwnerLive(s, now);

        } catch (Exception e) when (e is not OperationCanceledException) { Faults.Record("worker", "error", "No se pudo calcular el resumen del panel: " + e.Message, "Panel del propietario", e.ToString()); return; }
        foreach (var (date, problems) in errors) Faults.Record("worker", "error", "El resumen del panel no cuadra y no se publica", "Panel del propietario", date + ": " + string.Join("; ", problems));
        var oldest = Domain.BusinessDate(s, now.AddDays(-StatsHistoryDays));
        var pending = days.Where(x => string.CompareOrdinal(x.Key, oldest) >= 0 && !errors.ContainsKey(x.Key) && !Stats.Holds(x.Value)).Select(x => x.Value).Reverse().ToArray();
        JsonObject[][] batches = pending.Length == 0 ? [[]] : [.. pending.Chunk(StatsBatchDays)];
        foreach (var batch in batches) {

            var revision = Stats.NextRevision(now);
            var body = new JsonArray([.. batch.Select(day => { var sent = (JsonObject)day.DeepClone(); sent["revision"] = revision; return (JsonNode?)sent; })]);
            JsonObject reply;
            // A server that does not know this route yet is not a fault: the days wait for it.
            try { reply = await Call(HttpMethod.Post, "/branch/v1/stats", J.O("schema", StatsSchema, "sentAt", J.Now(), "branch", branchInfo, "live", live, "days", body), ct); } catch (ServerRejection e) when (e.Status == 404) { return; }
            var answered = reply.A("accepted").Strings().Concat(reply.A("ignored").Strings()).ToHashSet();
            // A day the server refused stays pending and holds back no other: the refusal is already a fault on its side.
            var delivered = batch.Where(day => answered.Contains(day.S("businessDate"))).ToArray();
            if (delivered.Length > 0) Stats.Delivered(delivered);
            if (batch != batches[^1]) await Task.Delay(StatsPause, ct);

        }

    }
}
