using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Central;
using MesaNexo.Core;
namespace MesaNexo.Tests;

static class TenantTests {
    public static Task Run(string root, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception(name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        void Deny(Action action, string name) { try { action(); throw new Exception("Expected rejection " + name); } catch (BusinessException) { Check(true, name); } }
        var folder = Path.Combine(root, ".lab", "tenants-" + J.Id());
        Directory.CreateDirectory(folder);
        var db = Path.Combine(folder, "tenants.sqlite");
        var key = RandomNumberGenerator.GetBytes(32);
        using var store = new CentralStore(db, (byte[])key.Clone());
        store.Initialize();
        var password = "Tenant-" + J.Id();
        JsonObject Provision(string t, string b, string site, string fp) => J.O("tenantId", t, "branchId", b, "slug", t + "-" + b, "siteId", site, "certificateSha256", fp, "tenantName", t, "branchName", b, "ownerName", "Owner", "ownerUsername", "owner", "ownerPassword", password, "ownerEnrollmentCode", new string('e', 32));
        store.ProvisionTenant(Provision("alpha", "a", "site-a", new string('a', 64)), new Passwords());
        store.ProvisionTenant(Provision("beta", "b", "site-b", new string('b', 64)), new Passwords());
        var login = store.TenantLogin(J.O("tenantId", "alpha", "branchId", "a", "username", "owner", "password", password, "deviceId", J.Id(), "deviceSecret", J.Id(), "enrollmentCode", new string('e', 32)), new Passwords());
        var token = login.S("token");
        Check(store.AuthorizeTenant(token, "a").TenantId == "alpha", "central owner authorization binds tenant and membership");
        Deny(() => store.AuthorizeTenant(token, "b"), "owner cannot query another tenant branch");
        Deny(() => store.TenantLogin(J.O("tenantId", "alpha", "branchId", "b", "username", "owner", "password", password, "deviceId", J.Id(), "deviceSecret", J.Id(), "enrollmentCode", new string('e', 32)), new Passwords()), "login cannot switch to foreign branch");
        Check(store.CertificateScope(new string('a', 64)) == new TenantScope("alpha", "a"), "mTLS fingerprint resolves one compound tenant branch scope");
        Deny(() => store.CertificateScope(new string('c', 64)), "unregistered certificate has no tenant scope");
        void Project(string site, string kind, string id, JsonObject d) { d["siteId"] = site; d["kind"] = kind; d["id"] = id; var e = J.Id(); var wire = J.O("data", J.O("changes", new[] { J.O("expected_version", 0, "data", d) }, "receipt", J.O("at", J.Now()))); var raw = Encoding.UTF8.GetBytes(wire.ToJsonString()); store.Receive(site, e, Convert.ToHexStringLower(SHA256.HashData(raw)), raw); }
        Project("site-a", "product", "same-product", J.O("name", "Alpha product", "price", 101, "available", true, "visible", true));
        Project("site-b", "product", "same-product", J.O("name", "Beta product", "price", 999, "available", true, "visible", true));
        Project("site-a", "meta", "site", J.O("name", "Alpha", "revision", 1));
        var commerce = new CommerceService(store);
        Check(commerce.Catalog("alpha-a").A("products").Objects().Single().S("name") == "Alpha product" && commerce.Catalog("beta-b").A("products").Objects().Single().L("price") == 999, "public slug projects only its authorized tenant catalog even with equal product IDs");
        var access = J.Id() + J.Id();
        var request = J.O("requestId", J.Id(), "clientToken", access, "name", "Synthetic Customer", "phone", "000", "mode", "pickup", "paymentMethod", "cash", "items", new[] { J.O("productId", "same-product", "quantity", 2) });
        var order = commerce.Checkout("alpha-a", request);
        Check(order.S("status") == "pending-branch" && !order.B("acceptedLocally"), "cloud checkout remains pending before local durable acceptance");
        Check(commerce.Checkout("alpha-a", request).B("duplicate") && store.PendingCommands(new("alpha", "a")).Length == 1, "repeated public checkout persists one order and command");
        Deny(() => commerce.Status("beta-b", order.S("id"), access), "order capability cannot cross tenants");
        Deny(() => commerce.Status("alpha-a", order.S("id"), J.Id()), "order status requires correct opaque capability");
        var command = store.PendingCommands(new("alpha", "a")).Single();
        var ack = J.O("status", "accepted-local", "digest", J.Hash(J.Canonical(command)), "visitId", J.Id());
        Deny(() => store.CommandAck(new("beta", "b"), order.S("id"), ack), "foreign branch cannot acknowledge order");
        store.CommandAck(new("alpha", "a"), order.S("id"), ack);
        Check(store.CommandAck(new("alpha", "a"), order.S("id"), ack).B("duplicate") && commerce.Status("alpha-a", order.S("id"), access).B("acceptedLocally"), "acceptance ACK is durable and deduplicated");
        request["requestId"] = J.Id();
        request["clientToken"] = J.Id() + J.Id();
        request["paymentMethod"] = "online";
        var online = commerce.Checkout("alpha-a", request);
        Check(online.S("status") == "requires-verified-payment" && store.PendingCommands(new("alpha", "a")).Length == 0, "online order cannot enter local queue without verified provider payment");
        Check(commerce.Analytics(token, "a").S("source") == "branch-event-mirror" && commerce.Analytics(token, "a").ContainsKey("lagSeconds"), "owner analytics names source and synchronization lag");
        Deny(() => commerce.Analytics(token, "b"), "analytics API service denies foreign tenant before reading projections");
        using (var restarted = new CentralStore(db, (byte[])key.Clone())) { restarted.Initialize(); Check(new CommerceService(restarted).Status("alpha-a", order.S("id"), access).B("acceptedLocally"), "central restart preserves locally accepted order status"); }
        var invoiceToken = J.Id() + J.Id();
        Project("site-a", "invoicetoken", "invoice-token-fixture", J.O("tokenHash", J.Hash(invoiceToken), "visitId", "closed-ticket-fixture", "expires", DateTimeOffset.UtcNow.AddHours(1).ToString("O")));
        var invoice = commerce.Invoice(invoiceToken, J.O("rfc", "XAXX010101000", "name", "PUBLICO EN GENERAL", "postalCode", "01000", "regime", "616", "use", "S01"));
        Check(invoice.S("status") == "pending-branch" && !invoice.ContainsKey("fiscalUuid"), "public invoice capability queues request without inventing fiscal stamp");
        Deny(() => commerce.Invoice(invoiceToken, J.O("rfc", "XAXX010101000", "name", "PUBLICO EN GENERAL", "postalCode", "01000", "regime", "616", "use", "S01")), "public invoice token is single use");
        var invoiceCommand = store.PendingCommands(new("alpha", "a")).Single();
        store.CommandAck(new("alpha", "a"), invoiceCommand.S("id"), J.O("status", "rejected-local", "digest", J.Hash(J.Canonical(invoiceCommand)), "reason", "Synthetic invoice fixture finished"));
        Project("site-a", "promotion", "web-coupon", J.O("name", "Web ten", "coupon", "WEB10", "active", true, "percent", 10, "startHour", 0, "endHour", 24, "productIds", new[] { "same-product" }));
        var couponRequest = (JsonObject)request.DeepClone();
        couponRequest["requestId"] = J.Id();
        couponRequest["clientToken"] = J.Id() + J.Id();
        couponRequest["paymentMethod"] = "cash";
        couponRequest["coupon"] = "web10";
        var couponOrder = commerce.Checkout("alpha-a", couponRequest);
        var couponCommand = store.PendingCommands(new("alpha", "a")).Single(x => x.S("id") == couponOrder.S("id"));
        Check(couponCommand.Obj("payload").L("quotedTotal") == 182 && couponCommand.Obj("payload").L("discount") == 20, "public coupon validates eligibility and rounds discount to exact minor units");
        var accessOnline = request.S("clientToken");
        store.BindPaymentIntent(new("alpha", "a"), online.S("id"), accessOnline, "pi_fixture");
        var eventBody = J.O("id", "evt_payment_fixture", "type", "payment_intent.succeeded", "data", J.O("object", J.O("id", "pi_fixture", "status", "succeeded", "currency", "mxn", "amount_received", 202, "metadata", J.O("tenant_id", "alpha", "branch_id", "a", "order_id", online.S("id")))));
        var bytes = Encoding.UTF8.GetBytes(eventBody.ToJsonString());
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var webhookSecret = "whsec_" + J.Id();
        var signature = "t=" + now + ",v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(webhookSecret), Encoding.UTF8.GetBytes(now + ".").Concat(bytes).ToArray()));
        Deny(() => store.ReceiveStripe(new("beta", "b"), bytes, signature, webhookSecret), "verified provider webhook cannot cross tenant branch boundary");
        var verified = store.ReceiveStripe(new("alpha", "a"), bytes, signature, webhookSecret);
        Check(verified.S("status") == "payment-verified-pending-branch" && store.ReceiveStripe(new("alpha", "a"), bytes, signature, webhookSecret).B("duplicate"), "provider payment webhook and retry atomically queue one verified order");
        var hubSecret = J.Id() + J.Id();
        var hubEvent = J.O("provider", "culinary-contract-v1-fixture", "eventId", "delivery-event-1", "order", J.O("name", "Synthetic hub customer", "phone", "000", "mode", "pickup", "paymentMethod", "cash", "items", new[] { J.O("productId", "same-product", "quantity", 1) }));
        var hubRaw = Encoding.UTF8.GetBytes(hubEvent.ToJsonString());
        var hubSig = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(hubSecret), Encoding.UTF8.GetBytes(now + ".").Concat(hubRaw).ToArray()));
        var hub = new DeliveryHub(commerce, "alpha-a", hubSecret);
        var hubOrder = hub.Receive(hubRaw, now, hubSig);
        Check(hub.Receive(hubRaw, now, hubSig).B("duplicate") && hubOrder.S("status") == "pending-branch", "delivery integration contract authenticates and deduplicates through commerce domain");
        Deny(() => hub.Receive(Encoding.UTF8.GetBytes("{}"), now, hubSig), "delivery hub rejects altered webhook before domain translation");
        // Deliverect: HMAC of the raw body with the location's secret; the same webhook twice is the same request.
        var dlSecret = "dl-" + J.Id();
        var dlRaw = Encoding.UTF8.GetBytes(J.O("_id", "DL-1", "status", 10, "channel", 7, "channelOrderDisplayId", "A12", "customer", J.O("name", "Laura"), "note", "Sin cebolla", "decimalDigits", 2, "payment", J.O("amount", 2000), "items", new[] { J.O("plu", "same-product", "name", "Producto", "price", 1000, "quantity", 2) }).ToJsonString());
        var dlSig = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(dlSecret), dlRaw));
        var location = new DeliverectLocation(dlSecret, new() { ["7"] = "uber" });
        var dlOrder = commerce.Deliverect("alpha-a", dlRaw, dlSig, location);
        Check(dlOrder.S("status") == "pending-branch" && commerce.Deliverect("alpha-a", dlRaw, dlSig, location).B("duplicate"), "a signed Deliverect order is queued once for the branch as an aggregator proposal");
        Deny(() => commerce.Deliverect("alpha-a", dlRaw, new string('0', 64), location), "a Deliverect webhook with a forged signature is rejected before the domain");
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(db)).Contains("Synthetic Customer"), "public order personal data is encrypted at rest");
        store.TenantLogout(token);
        Deny(() => store.AuthorizeTenant(token, "a"), "tenant session revocation enforced by backend");
        return Task.CompletedTask;

    }
}
