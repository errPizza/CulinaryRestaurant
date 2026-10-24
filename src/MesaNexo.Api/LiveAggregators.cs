using System.Text.Json.Nodes;
using MesaNexo.Core;
using MesaNexo.Integrations;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;
// aggregators.json (example in deploy/integrations/aggregators.example.json): {"provider": "deliverect", baseUrl, clientId,
// clientSecret, accountId, locationId}. Orders reach the branch through the Central (the public portal receives Deliverect's
// signed webhook); this service sends back the order statuses and publishes the menu.
public sealed class AggregatorService(DeliverectClient deliverect, string accountId, string locationId) {
    public static AggregatorService From(JsonObject o) {

        Need(o.S("provider") is "deliverect" or "", "Integrador de plataformas no soportado: " + o.S("provider"));
        var url = new Uri(Text(o, "baseUrl", 200));
        Need(url.Scheme == "https", "baseUrl: dirección HTTPS de la API de Deliverect");
        return new(new DeliverectClient(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) }, url.ToString(), Text(o, "clientId", 200), Text(o, "clientSecret", 300)), Text(o, "accountId", 60), Text(o, "locationId", 60));

    }
    // The products the platforms may sell: visible, available and without choices only the staff can take.
    static bool Publishable(JsonObject x) => x.B("visible") && x.B("available") && !x.A("modifiers").Objects().Any(g => g.B("required")) && !(x.B("supportsCooking") && x.B("cookingRequired"));
    public async Task<JobResult> Send(BranchState s, JsonObject job, CancellationToken ct) {

        if (job.S("type") == "status") {

            var r = await deliverect.Status(job.S("deliverectId"), (int)job.L("code"), job.S("reason"), job.S("receiptId"), ct);
            return JobResult.Of(r.Status, r.Status == "sent" ? "" : "Deliverect · " + r.Message, job.S("deliverectId"), ("message", r.Message));

        }
        var visible = s.List("product").Where(x => x.B("visible")).ToArray();
        var products = visible.Where(Publishable).OrderBy(x => x.S("id"), StringComparer.Ordinal).ToArray();
        static string Category(string name) => J.Hash("category:" + name)[..16];
        // Taxes go with three implied decimals: 16 % is 16000; the product's rate is in basis points.
        var items = new JsonArray(products.Select(x => (JsonNode)J.O("productType", 1, "plu", x.S("id"), "name", x.S("name"), "description", x.S("description"), "price", x.L("price"), "deliveryTax", x.L("taxBps") * 10, "takeawayTax", x.L("taxBps") * 10, "eatInTax", x.L("taxBps") * 10, "posCategoryIds", new JsonArray(Category(x.S("category"))), "visible", true)).ToArray());
        var categories = new JsonArray(products.Select(x => x.S("category")).Distinct().Select(c => (JsonNode)J.O("posCategoryId", Category(c), "name", c == "" ? "General" : c)).ToArray());
        var menu = await deliverect.Menu(accountId, locationId, items, categories, ct);
        return JobResult.Of(menu.Status, "Deliverect · " + menu.Message + (menu.Status == "sent" ? " · " + products.Length + " productos" : ""), "", ("message", menu.Message), ("published", products.Length), ("excluded", visible.Length - products.Length));

    }
}
public static partial class LiveServices {
    static void Aggregators(IServiceCollection services, IntegrationConfig config) {

        if (config.Use("aggregators", AggregatorService.From) is not { } platforms) return;
        // A status or menu sent twice changes nothing at Deliverect, so a lost answer is simply sent again.
        Worker(services, "aggregators", branch => ct => branch.SendNext("platformsync", "aggregators-service", x => x.S("status") == "queued", platforms.Send, async (s, x, c) => await platforms.Send(s, x, c), ct));

    }
}
