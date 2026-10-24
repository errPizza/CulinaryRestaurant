using System.Text.Json.Nodes;
using MesaNexo.Core;
using MesaNexo.Integrations;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;
// onlinepay.json: {"provider": "stripe", "secretKey": "sk_live_…", "successUrl": "https://…"}. successUrl is where Stripe sends the
// customer after paying (the restaurant's site or a thank-you page). The branch asks Stripe about each open link, so no webhook
// has to reach it.
public sealed class OnlinePayService(StripeCheckout stripe) {
    public static OnlinePayService From(JsonObject o) {

        Need(o.S("provider") is "stripe" or "", "Proveedor de pagos en línea no soportado: " + o.S("provider"));
        var key = Text(o, "secretKey", 200);
        Need(key.StartsWith("sk_live_") || key.StartsWith("sk_test_") || key.StartsWith("rk_live_") || key.StartsWith("rk_test_"), "secretKey: llave secreta o restringida de Stripe (sk_live_…, sk_test_…)");
        var success = new Uri(Text(o, "successUrl", 500));
        Need(success.Scheme == "https", "successUrl: dirección HTTPS");
        return new(new StripeCheckout(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) }, key, success.ToString()));

    }
    static JobResult Result(JsonObject link, CheckoutState c, string? status = null) {

        var fields = new List<(string, object?)> { ("message", c.Message) };
        if (c.SessionId != "") fields.Add(("reference", c.SessionId));
        if (c.Url != "") fields.Add(("payUrl", c.Url));
        if (c.PaymentIntent != "") fields.Add(("paymentIntent", c.PaymentIntent));
        status ??= c.Status;
        return JobResult.Of(status, status is "pending" or "cancel-requested" ? "" : "Stripe · " + c.Message + " · " + (link.L("amount") / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), c.SessionId != "" ? c.SessionId : link.S("reference"), fields.ToArray());

    }
    public async Task<JobResult> Create(BranchState s, JsonObject link, CancellationToken ct) => Result(link, await stripe.Create(link.S("id"), link.S("visitId"), link.L("amount"), "Cuenta en " + s.Meta.S("name"), DateTimeOffset.Parse(link.S("expiresAt")), ct));
    // The session is keyed by the link's id: asking again after a lost answer returns the same session.
    public async Task<JobResult?> Recover(BranchState s, JsonObject link, CancellationToken ct) => await Create(s, link, ct);
    public async Task<JobResult?> Consult(BranchState s, JsonObject link, CancellationToken ct) {

        if (link.S("reference") == "") return null;
        if (link.S("status") == "cancel-requested") { var c = await stripe.Expire(link.S("reference"), ct); return Result(link, c, c.Status == "expired" ? "cancelled" : c.Status == "pending" ? "cancel-requested" : null); }
        return Result(link, await stripe.Status(link.S("reference"), ct));

    }
    public async Task<JobResult> Refund(BranchState s, JsonObject link, CancellationToken ct) => Result(link, await stripe.Refund(link.S("paymentIntent"), link.S("id"), ct));
}
public static partial class LiveServices {
    static void OnlinePay(IServiceCollection services, IntegrationConfig config) {

        if (config.Use("onlinepay", OnlinePayService.From) is not { } pay) return;
        Worker(services, "onlinepay", branch => async ct =>
            await branch.SendNext("paylink", "payments-service", x => x.S("status") == "queued", pay.Create, pay.Recover, ct)
            || await branch.SendNext("paylink", "payments-service", x => x.S("status") == "refund-queued", pay.Refund, async (s, x, c) => await pay.Refund(s, x, c), ct, "refunding")
            || await branch.PollNext("paylink", "payments-service", x => x.S("status") is "pending" or "cancel-requested" && x.B("live"), pay.Consult, TimeSpan.FromSeconds(10), ct));

    }
}
