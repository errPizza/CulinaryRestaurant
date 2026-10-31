using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;

// An S3-compatible bucket (Cloudflare R2) reached with its own credentials, separate from CulinaryServer. R2 has no
// write-only token: what protects the objects is the bucket lock, which refuses overwriting or deleting them.
public sealed class ObjectBucket(HttpClient http, string endpoint, string bucket, string accessKey, string secretKey) {
    const string Region = "auto";
    public static ObjectBucket? From(string? file) {

        if (file is null || !File.Exists(file)) return null;
        var o = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new BusinessException(400, "checkpoints.json no es un objeto JSON");
        Need(Uri.TryCreate(o.S("endpoint"), UriKind.Absolute, out var url) && url.Scheme == "https" && o.S("bucket") != "" && o.S("accessKeyId") != "" && o.S("secretAccessKey") != "", "checkpoints.json está incompleto");
        return new(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, o.S("endpoint").TrimEnd('/'), o.S("bucket"), o.S("accessKeyId"), o.S("secretAccessKey"));

    }
    // False when the object already exists: the same checkpoint sent twice, which is not an error.
    public async Task<bool> Put(string key, byte[] body, CancellationToken ct) {

        using var reply = await Send(HttpMethod.Put, key, "", body, ct);
        if (reply.StatusCode == HttpStatusCode.PreconditionFailed) return false;
        Ensure(reply);
        return true;

    }
    public async Task<byte[]> Get(string key, CancellationToken ct) {

        using var reply = await Send(HttpMethod.Get, key, "", [], ct);
        Ensure(reply);
        return await reply.Content.ReadAsByteArrayAsync(ct);

    }
    public async Task<List<string>> List(string prefix, CancellationToken ct) {

        var keys = new List<string>();
        string? token = null;
        do {

            var query = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["list-type"] = "2", ["prefix"] = prefix };
            if (token is not null) query["continuation-token"] = token;
            using var reply = await Send(HttpMethod.Get, "", string.Join("&", query.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value))), [], ct);
            Ensure(reply);
            var xml = XDocument.Parse(await reply.Content.ReadAsStringAsync(ct));
            keys.AddRange(xml.Descendants().Where(x => x.Name.LocalName == "Key").Select(x => x.Value));
            token = xml.Descendants().FirstOrDefault(x => x.Name.LocalName == "NextContinuationToken")?.Value;

        } while (token is not null);
        return keys;

    }
    static void Ensure(HttpResponseMessage reply) { if (!reply.IsSuccessStatusCode) throw new HttpRequestException("El bucket de puntos de control respondió " + (int)reply.StatusCode); }
    async Task<HttpResponseMessage> Send(HttpMethod method, string key, string query, byte[] body, CancellationToken ct) {

        var url = new Uri(endpoint + "/" + bucket + (key == "" ? "" : "/" + key) + (query == "" ? "" : "?" + query));
        var at = DateTimeOffset.UtcNow;
        var hash = Convert.ToHexStringLower(SHA256.HashData(body));
        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = url.Authority, ["x-amz-content-sha256"] = hash, ["x-amz-date"] = at.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) };
        using var request = new HttpRequestMessage(method, url);
        if (method == HttpMethod.Put) { request.Content = new ByteArrayContent(body); request.Headers.TryAddWithoutValidation("If-None-Match", "*"); }
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", hash);
        request.Headers.TryAddWithoutValidation("x-amz-date", signed["x-amz-date"]);
        request.Headers.TryAddWithoutValidation("Authorization", Authorization(method.Method, url.AbsolutePath, query, signed, hash, at, accessKey, secretKey, Region));
        return await http.SendAsync(request, ct);

    }
    // AWS Signature Version 4 for S3, which R2 accepts with region "auto".
    public static string Authorization(string method, string path, string query, SortedDictionary<string, string> headers, string payloadHash, DateTimeOffset at, string accessKey, string secretKey, string region) {

        var day = at.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var names = string.Join(";", headers.Keys);
        var canonical = string.Join("\n", method, path, query, string.Concat(headers.Select(x => x.Key + ":" + x.Value.Trim() + "\n")), names, payloadHash);
        var scope = $"{day}/{region}/s3/aws4_request";
        var input = string.Join("\n", "AWS4-HMAC-SHA256", at.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture), scope, J.Hash(canonical));
        var key = Encoding.UTF8.GetBytes("AWS4" + secretKey);
        foreach (var part in new[] { day, region, "s3", "aws4_request" }) key = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(part));
        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope},SignedHeaders={names},Signature={Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(input)))}";

    }
}
public static class OfflineCheck {
    public static async Task<JsonObject> Run(string export, string directory, string site) {

        var audit = JsonNode.Parse(await File.ReadAllTextAsync(export))!.AsArray().Objects();
        async Task<Dictionary<string, string>> Files(string prefix) {

            var folder = Path.Combine(directory, prefix.Replace('/', Path.DirectorySeparatorChar));
            var found = new Dictionary<string, string>();
            if (Directory.Exists(folder)) foreach (var file in Directory.GetFiles(folder)) found[prefix + Path.GetFileName(file)] = await File.ReadAllTextAsync(file);
            return found;

        }
        var published = (await Files(Checkpoints.Prefix(site))).ToDictionary(x => x.Key, x => JsonNode.Parse(x.Value)!.AsObject());
        var statements = (await Files(Checkpoints.AcceptedPrefix(site))).Values.Select(x => JsonNode.Parse(x)!.AsObject()).ToArray();
        var result = Checkpoints.Compare(site, audit, published, statements.SelectMany(x => x.A("keys").Strings()).ToHashSet());
        // Listed, not hidden: an acceptance needs a real restore behind it.
        result["statements"] = new JsonArray(statements.Select(x => (JsonNode)J.O("at", x.S("at"), "reason", x.S("reason"), "keys", x.A("keys").Count)).ToArray());
        result["alerts"] = (await Files(Checkpoints.AlertsPrefix(site))).Count;
        return result;

    }
}
public sealed class CheckpointWorker(BranchService branch, ObjectBucket bucket, ILogger<CheckpointWorker> log): BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken ct) {

        await Task.Yield();
        var compared = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested) {

            try {

                await branch.PublishCheckpoint(bucket, false, ct);
                // Reads the whole chain, so at start and daily; publishing is what must not wait.
                if (DateTimeOffset.UtcNow - compared > TimeSpan.FromDays(1)) {

                    var result = await branch.CompareCheckpoints(bucket, ct);
                    compared = DateTimeOffset.UtcNow;
                    if (!result.B("valid")) log.LogCritical("COS-AUDIT-CHECKPOINT: la bitácora no coincide con los puntos de control publicados: {Result}", result.ToJsonString());

                }

            } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } catch (Exception e) when (e is HttpRequestException or TaskCanceledException or BusinessException or StoreException) { log.LogWarning("COS-CHECKPOINT: {Message}", e.Message); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { break; }

        }

    }
}
public sealed partial class BranchService {
    long publishedSequence; DateTimeOffset publishedAt = DateTimeOffset.MinValue;
    volatile JsonObject? auditAlert;
    // Hourly and after each cash close. A head covers everything before it, so a failed send is made up by the next one.
    public async Task<bool> PublishCheckpoint(ObjectBucket bucket, bool force = false, CancellationToken ct = default) {

        var s = await View(ct);
        var sequence = s.Meta.L("auditSequence");
        if (sequence == 0 || sequence == publishedSequence) return false;
        var closed = s.List("cash").Any(c => c.S("status") == "closed" && DateTimeOffset.TryParse(c.S("closedAt"), out var at) && at > publishedAt);
        if (!force && !closed && DateTimeOffset.UtcNow - publishedAt < TimeSpan.FromHours(1)) return false;
        var checkpoint = Checkpoints.Build(siteId, sequence, s.Meta.S("auditHead"), J.Now(), Key.Spki, Key.Sign);
        await bucket.Put(Checkpoints.Key(checkpoint), Encoding.UTF8.GetBytes(checkpoint.ToJsonString()), ct);
        publishedSequence = sequence;
        publishedAt = DateTimeOffset.UtcNow;
        return true;

    }
    async Task<Dictionary<string, JsonObject>> Published(ObjectBucket bucket, CancellationToken ct) {

        var published = new Dictionary<string, JsonObject>();
        foreach (var key in await bucket.List(Checkpoints.Prefix(siteId), ct)) published[key] = JsonNode.Parse(await bucket.Get(key, ct))!.AsObject();
        return published;

    }
    async Task<HashSet<string>> Accepted(ObjectBucket bucket, CancellationToken ct) {

        var accepted = new HashSet<string>();
        foreach (var key in await bucket.List(Checkpoints.AcceptedPrefix(siteId), ct)) accepted.UnionWith(JsonNode.Parse(await bucket.Get(key, ct))!.AsObject().A("keys").Strings());
        return accepted;

    }
    public async Task<JsonArray> AuditRecords(CancellationToken ct = default) => new(new BranchState(await Read(ct, bounded: false), true).List("audit").Where(x => x.ContainsKey("sequence")).Select(x => (JsonNode)x.DeepClone()).ToArray());
    // A mismatch stops new authorizations, tells the manager and leaves the evidence outside the machine, once.
    public async Task<JsonObject> CompareCheckpoints(ObjectBucket bucket, CancellationToken ct = default) {

        var result = Checkpoints.Compare(siteId, (await AuditRecords(ct)).Objects(), await Published(bucket, ct), await Accepted(bucket, ct));
        if (result.B("valid")) { auditAlert = null; return result; }
        if (auditAlert is null) {

            auditAlert = J.O("level", "critical", "message", "La bitácora de esta sucursal no coincide con los puntos de control externos. Las autorizaciones quedan detenidas. Llama a soporte y no apagues ni restaures el servidor.");
            await bucket.Put(Checkpoints.AlertsPrefix(siteId) + J.Id() + ".json", Encoding.UTF8.GetBytes(J.O("siteId", siteId, "at", J.Now(), "result", result.DeepClone()).ToJsonString()), ct);

        }
        return result;

    }
    // After a real restore the branch rejoins an older head, and what it published afterwards no longer matches. Someone
    // has to say so: the statement stays in the bucket, where an independent check sees it.
    public async Task<JsonObject> AcceptCheckpoints(ObjectBucket bucket, string reason, CancellationToken ct = default) {

        Need(reason.Trim().Length > 0, "Indica el motivo");
        var result = Checkpoints.Compare(siteId, (await AuditRecords(ct)).Objects(), await Published(bucket, ct), await Accepted(bucket, ct));
        Need(result.B("chain"), "La cadena local no verifica; no se acepta nada", 409);
        var keys = result.A("problems").Objects().Select(x => x.S("key")).ToArray();
        if (keys.Length > 0) await bucket.Put(Checkpoints.AcceptedPrefix(siteId) + J.Id() + ".json", Encoding.UTF8.GetBytes(J.O("siteId", siteId, "at", J.Now(), "reason", reason, "keys", keys).ToJsonString()), ct);
        return J.O("accepted", keys.Length, "reason", reason);

    }
}
