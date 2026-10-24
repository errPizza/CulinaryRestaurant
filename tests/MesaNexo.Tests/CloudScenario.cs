using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Central;
using MesaNexo.Client;
using MesaNexo.Core;
using MesaNexo.Public;
namespace MesaNexo.Tests;

static class CloudScenario {
    static int Port() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    public static async Task Run(string root, string python, string folder, CentralStore centralStore, CentralService central, BranchService branch, string token, string credentials, int receiver, X509Certificate2 clientCert, string publicKey, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception(name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        async Task<JsonObject> Act(string type, JsonObject p) => (await branch.Action(token, J.O("operationId", J.Id(), "type", type, "payload", p))).Obj("result");
        var waiter = await Act("user.save", J.O("name", "Web service", "username", "web-service", "role", "mesero", "password", "Test-" + J.Id()));
        await Act("commerce.configure", J.O("tenantId", "restaurant-alpha", "branchId", "main", "waiterId", waiter.S("id"), "enabled", true));
        await Act("catalog.category", J.O("name", "Web food"));
        var product = await Act("catalog.product", J.O("name", "Web product", "price", 101, "category", "Web food", "station", "Cocina"));
        var local = await branch.Load();
        var changes = new[] { local.Meta, local.Get("product", product.S("id")) }.Select(d => J.O("record_id", BranchState.Key(d.S("kind"), d.S("id")), "expected_version", local.Original[BranchState.Key(d.S("kind"), d.S("id"))].Version - 1, "data", d)).ToArray();
        var eventId = J.Id();
        var wire = J.O("protocol", "culinaryos-events-v1", "site_id", "alpha-test", "event_id", eventId, "record_id", "tx_" + eventId, "version", 1, "data", J.O("contract", "mesa-nexo-transaction-v1", "changes", changes, "receipt", J.O("at", J.Now())));
        var raw = Encoding.UTF8.GetBytes(J.Canonical(wire));
        central.Receive(raw, Convert.ToHexStringLower(SHA256.HashData(raw)), clientCert);
        using var certificate = LabCertificate.Create(Path.Combine(folder, "public-tls"));
        var port = Port();
        await using var app = PublicHost.Build(new CommerceService(centralStore), certificate, port, Path.Combine(root, "src", "MesaNexo.Public", "wwwroot"));
        await app.StartAsync();
        using var http = TrustedConnection.Create(new($"https://localhost:{port}", "public", Path.Combine(folder, "public-tls", "lab-ca.crt"), certificate.GetCertHashString(HashAlgorithmName.SHA256)));
        try {

            var html = await http.GetStringAsync("s/restaurant-alpha-main");
            Check(html.Contains("Culinary Restaurant") && html.Contains("viewport"), "separate public PWA serves branded responsive shell over verified TLS");
            var privateApi = await http.GetAsync("api/state");
            Check(privateApi.StatusCode == HttpStatusCode.NotFound, "public host exposes no private operational state endpoint");
            var catalog = JsonNode.Parse(await http.GetStringAsync("public/restaurant-alpha-main/catalog"))!;
            Check(catalog.A("products").Objects().Single().L("price") == 101, "public catalog comes from authenticated branch event projection");
            var access = J.Id() + J.Id();
            var request = J.O("requestId", J.Id(), "clientToken", access, "name", "Synthetic web customer", "phone", "000", "mode", "pickup", "paymentMethod", "cash", "items", new[] { J.O("productId", product.S("id"), "quantity", 2) });
            async Task<JsonObject> Checkout() { using var response = await http.PostAsync("public/restaurant-alpha-main/orders", new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json")); response.EnsureSuccessStatusCode(); return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject(); }
            var order = await Checkout();
            var again = await Checkout();
            Check(order.S("status") == "pending-branch" && again.B("duplicate") && again.S("id") == order.S("id"), "HTTPS checkout retry persists one pending order before branch acceptance");
            var poll = new CloudPull(branch, new($"https://localhost:{receiver}", credentials, publicKey));
            Check(await poll.Refresh() == 0, "outgoing mTLS stores proposal without issuing acceptance ACK");
            var proposals = (await branch.Load()).List("cloudproposal").ToArray();
            Check(proposals.Length == 1 && proposals[0].S("status") == "pending-review" && !(await branch.Load()).List("visit").Any(x => x.S("cloudOrderId") == order.S("id")), "cloud import cannot create sale before authorized local review");
            var tampered = central.PendingCloud(clientCert);
            tampered.Obj("data")["tenantId"] = "foreign";
            try { await branch.ReceiveCloudProposals(tampered, publicKey); throw new Exception("Tampered signature accepted"); } catch (BusinessException) { Check(true, "signed cloud envelope rejects tenant tampering before domain mutation"); }
            var decision = await Act("commerce.decide", J.O("id", order.S("id"), "accept", true, "reason", "Local stock and price checked"));
            var before = (await branch.Load()).List("unit").Count();
            using (var copyRuntime = new RuntimeStore(python, Path.Combine(root, "runtime", "lab_host.py"), Path.Combine(folder, "branch-store"), "alpha-test")) {

                var restarted = new BranchService(copyRuntime, "alpha-test", new Passwords());
                TestLicense.Attach(restarted);
                var originalEnvelope = central.PendingCloud(clientCert);
                var ack = await restarted.ReceiveCloudProposals(originalEnvelope, publicKey);
                Check(ack.Single().S("status") == "accepted-local" && (await restarted.Load()).List("unit").Count() == before, "branch restart replays durable local ACK without duplicating order units");
                var resumed = new CloudPull(restarted, new($"https://localhost:{receiver}", credentials, publicKey));
                Check(await resumed.Refresh() == 1, "outgoing mTLS ACK marks central order accepted only after local commit");

            }
            var duplicate = central.AckCloud(clientCert, J.O("id", order.S("id"), "ack", decision));
            Check(duplicate.B("duplicate"), "central deduplicates acceptance ACK after lost response");
            using var status = new HttpRequestMessage(HttpMethod.Get, "public/restaurant-alpha-main/orders/" + order.S("id"));
            status.Headers.Authorization = new("Bearer", access);
            using var result = await http.SendAsync(status);
            Check(JsonNode.Parse(await result.Content.ReadAsStringAsync())!.B("acceptedLocally"), "customer status becomes locally accepted only after acknowledged durable receipt");
            using var foreign = new HttpRequestMessage(HttpMethod.Post, "public/restaurant-alpha-main/orders") { Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json") };
            foreign.Headers.Add("Origin", "https://foreign.example.invalid");
            using var rejected = await http.SendAsync(foreign);
            Check(rejected.StatusCode == HttpStatusCode.Forbidden, "public host rejects cross-origin checkout request");
            // Table QR: public page -> central -> signed pull -> waiter notice, or an order the waiter confirms.
            async Task Project(params JsonObject[] records) { var st = await branch.Load(); var rows = records.Select(d => { var cur = st.Get(d.S("kind"), d.S("id")); var key = BranchState.Key(cur.S("kind"), cur.S("id")); return J.O("record_id", key, "expected_version", st.Original[key].Version - 1, "data", cur); }).ToArray(); var ev = J.Id(); var w = J.O("protocol", "culinaryos-events-v1", "site_id", "alpha-test", "event_id", ev, "record_id", "tx_" + ev, "version", 1, "data", J.O("contract", "mesa-nexo-transaction-v1", "changes", rows, "receipt", J.O("at", J.Now()))); var bytes = Encoding.UTF8.GetBytes(J.Canonical(w)); central.Receive(bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), clientCert); }
            async Task<JsonObject> Post(string path, JsonObject body) { using var response = await http.PostAsync(path, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")); response.EnsureSuccessStatusCode(); return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject(); }
            async Task<string> Status(string id, string access) { using var ask = new HttpRequestMessage(HttpMethod.Get, "public/restaurant-alpha-main/orders/" + id); ask.Headers.Authorization = new("Bearer", access); using var reply = await http.SendAsync(ask); return JsonNode.Parse(await reply.Content.ReadAsStringAsync())!.S("status"); }
            await Act("qr.configure", J.O("url", "https://localhost/s/restaurant-alpha-main"));
            var qrTable = await Act("table.save", J.O("name", "Mesa QR", "zone", "Patio", "capacity", 4));
            var qr = await Act("table.qr", J.O("id", qrTable.S("id")));
            var tableToken = qr.S("link").Split("#mesa=")[1];
            var qrVisit = await Act("visit.open", J.O("tableId", qrTable.S("id"), "people", 2, "waiterId", waiter.S("id")));
            await Project(qrTable);
            var info = JsonNode.Parse(await http.GetStringAsync("public/restaurant-alpha-main/table/" + tableToken))!;
            Check(info.Obj("table").S("name") == "Mesa QR" && info.A("products").Count == 1, "public table page resolves the QR token and shows the published menu");
            using (var unknown = await http.GetAsync("public/restaurant-alpha-main/table/" + new string('0', 32))) Check(unknown.StatusCode == HttpStatusCode.NotFound, "unknown table token is rejected by the public page");
            var mirroredTable = centralStore.Mirror(centralStore.PublicScope("restaurant-alpha-main"), "table").Single(t => t.S("id") == qrTable.S("id"));
            Check(!mirroredTable.ContainsKey("qrToken") && mirroredTable.S("qrHash") != "", "central keeps only the hash of the table code");
            var qrPoll = new CloudPull(branch, new($"https://localhost:{receiver}", credentials, publicKey));
            var billAccess = J.Id() + J.Id();
            var bill = await Post("public/restaurant-alpha-main/table/" + tableToken, J.O("requestId", J.Id(), "clientToken", billAccess, "kind", "bill"));
            Check(await qrPoll.Refresh() == 1 && (await branch.Load()).List("notification").Any(n => n.S("visitId") == qrVisit.S("id") && n.S("message").Contains("pide la cuenta")), "QR bill request reaches the waiter and is acknowledged");
            Check(await Status(bill.S("id"), billAccess) == "accepted-local", "customer sees the waiter was notified");
            var orderAccess = J.Id() + J.Id();
            var tableOrder = await Post("public/restaurant-alpha-main/table/" + tableToken, J.O("requestId", J.Id(), "clientToken", orderAccess, "kind", "order", "items", new[] { J.O("productId", product.S("id"), "quantity", 2) }));
            Check(await qrPoll.Refresh() == 0 && (await branch.Load()).List("cloudproposal").Single(c => c.S("id") == tableOrder.S("id")).S("status") == "pending-review", "QR order waits for the waiter before reaching the kitchen");
            await Act("qr.decide", J.O("id", tableOrder.S("id"), "accept", true));
            Check((await branch.Load()).List("unit").Count(u => u.S("visitId") == qrVisit.S("id") && u.L("price") == 101) == 2, "waiter confirmation sends the QR order to the table account");
            Check(await qrPoll.Refresh() == 1 && await Status(tableOrder.S("id"), orderAccess) == "accepted-local", "confirmed QR order is acknowledged to the customer");
            await Act("table.qr", J.O("id", qrTable.S("id"), "rotate", true));
            await Project(qrTable);
            using (var old = await http.GetAsync("public/restaurant-alpha-main/table/" + tableToken)) Check(old.StatusCode == HttpStatusCode.NotFound, "rotating the table QR invalidates the printed code");

        } finally { await app.StopAsync(); }

    }
}
