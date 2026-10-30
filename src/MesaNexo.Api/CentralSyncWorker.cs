using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Api;

// Delivers the outbox to the central over mTLS, oldest first, as syncworker.py did: an event counts as delivered only
// when the central answers that it committed exactly that event, and nothing is ever removed on a failure or an
// ambiguous answer. One sender per branch keeps the order and keeps retries from racing acknowledgements.
// Acknowledgements are stored in batches; one lost with a crash only makes the central see an event again, which it
// answers as the same commit.
public sealed class CentralSyncWorker(PostgresBranchStore store, string branchConfig, string credentials, ILogger<CentralSyncWorker> log, HttpMessageHandler? transport = null): BackgroundService {
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(30), Budget = TimeSpan.FromSeconds(20);
    const int AckBatch = 16;
    HttpClient? http; string? url;
    protected override async Task ExecuteAsync(CancellationToken ct) {

        await Task.Yield();
        while (!ct.IsCancellationRequested) {

            try { var result = await DeliverOnce(ct); if (result.S("state") == "pending") log.LogWarning("COS-SYNC: la central no confirmó ({Reason}); se reintenta sin perder eventos", result.S("reason")); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } catch (Exception e) { log.LogWarning("COS-SYNC: {Type}", e.GetType().Name); }
            try { await Task.Delay(Interval, ct); } catch (OperationCanceledException) { break; }

        }

    }
    // One bounded delivery round, as one run of the old sync timer.
    public async Task<JsonObject> DeliverOnce(CancellationToken ct = default) {

        var central = JsonNode.Parse(await File.ReadAllTextAsync(branchConfig, ct))?["central"] as JsonObject;
        if (central is null) return J.O("state", "not_configured");
        var target = central.S("url");
        Rules.Need(Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo == "" && uri.Query == "" && uri.Fragment == "", "Central requiere URL HTTPS sin credenciales/query/fragment");
        if (http is null || url != target) { http?.Dispose(); http = Client(); url = target; }
        var sent = 0;
        var started = DateTime.UtcNow;
        var acked = new List<(string, string)>();
        async Task Flush() { await store.Acknowledge(acked, ct); sent += acked.Count; acked.Clear(); }
        foreach (var (wire, raw, digest) in await store.Pending(64, ct)) {

            if (DateTime.UtcNow - started >= Budget) break;
            var eventId = wire.S("event_id");
            string reason;
            try {

                using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(raw) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Headers.Add("Idempotency-Key", eventId);
                request.Headers.Add("X-CulinaryOs-SHA256", digest);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var response = await http.SendAsync(request, timeout.Token);
                var body = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                var ack = (int)response.StatusCode is 200 or 201 && body.Length <= 8192 ? JsonNode.Parse(body) as JsonObject : null;
                if (ack is not null && ack.S("site_id") == store.Site && ack.S("event_id") == eventId && ack.S("sha256") == digest && ack.S("status") == "committed") { acked.Add((eventId, digest)); if (acked.Count == AckBatch) await Flush(); continue; }
                reason = "ack";

            } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch (HttpRequestException e) when (e.InnerException is AuthenticationException) { reason = "tls"; } catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException) { reason = "network"; } catch (System.Text.Json.JsonException) { reason = "ack"; }
            await Flush();
            await store.Retry(eventId, ct);
            return J.O("sent", sent, "state", "pending", "reason", reason);

        }
        await Flush();
        return J.O("sent", sent, "state", sent == 0 ? "idle" : "delivered");

    }
    HttpClient Client() {

        if (transport is not null) return new HttpClient(transport, false);
        var ca = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(credentials, "central-ca")));
        // A client certificate needs an exportable key on Windows; on Linux the PEM pair loads as is.
        using var pem = X509Certificate2.CreateFromPemFile(Path.Combine(credentials, "central-cert"), Path.Combine(credentials, "central-key"));
        var client = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        var handler = new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        handler.SslOptions = new SslClientAuthenticationOptions {
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificates = [client],
            CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, CustomTrustStore = { ca }, RevocationMode = X509RevocationMode.NoCheck }
        };
        return new HttpClient(handler);

    }
    public override void Dispose() { http?.Dispose(); base.Dispose(); }
}
