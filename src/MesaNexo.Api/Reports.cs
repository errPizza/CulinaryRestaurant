using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;

public sealed partial class BranchService {
    public async Task<JsonObject> Report(string token, string? from, string? until, string? after, CancellationToken ct = default) {

        var s = await Load(ct);
        var user = Authenticate(s, token, out _);
        Role(user, "manager", "mesero");
        Need(user.S("role") == "manager" || user.Obj("permissions").B("shiftReports", true), "Sin permiso de reportes", 403);
        var start = string.IsNullOrWhiteSpace(from) ? DateTimeOffset.MinValue : DateTimeOffset.Parse(from);
        var end = string.IsNullOrWhiteSpace(until) ? DateTimeOffset.MaxValue : DateTimeOffset.Parse(until);
        Need(end >= start, "Periodo inválido");
        var visits = s.List("visit").Where(v => (user.S("role") == "manager" || v.S("waiterId") == user.S("id")) && DateTimeOffset.TryParse(v.S("openedAt"), out var opened) && opened >= start && opened <= end).OrderBy(v => v.S("id"), StringComparer.Ordinal).ToArray();
        var ids = visits.Select(x => x.S("id")).ToHashSet();
        var money = visits.Select(v => Domain.Totals(s, v)).ToArray();
        var payments = s.List("payment").Where(x => ids.Contains(x.S("visitId"))).ToArray();
        var page = visits.Where(v => string.IsNullOrEmpty(after) || StringComparer.Ordinal.Compare(v.S("id"), after) > 0).Take(101).ToArray();
        var hasMore = page.Length > 100;
        page = page.Take(100).ToArray();
        var rows = page.Select(v => { var c = (JsonObject)v.DeepClone(); c.Remove("trackingKey"); foreach (var value in Domain.Totals(s, v)) c[value.Key] = value.Value?.DeepClone(); return c; }).ToArray();
        return J.O("from", from, "until", until, "basis", "visit-opened-at", "count", visits.Length, "after", hasMore ? rows.Last().S("id") : null, "visits", rows, "total", money.Sum(x => x.L("total")), "paid", payments.Sum(x => x.L("amount")), "tips", payments.Sum(x => x.L("tip")), "balance", money.Sum(x => x.L("balance")));

    }
}