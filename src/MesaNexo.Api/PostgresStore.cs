using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MesaNexo.Core;
using Npgsql;
namespace MesaNexo.Api;

public sealed record PostgresStoreOptions(string ConnectionString, string Site, string DataDirectory, string BranchConfig, long MinFreeBytes = 128L * 1024 * 1024, string? PgDump = null) {
    // CulinaryOS: the distribution's cluster on its Unix socket; peer authentication maps the service user to its role.
    public const string CulinaryOs = "Host=/run/postgresql;Database=culinaryos;Username=culinaryos-app;Pooling=true;Maximum Pool Size=20;Timeout=10";
}

// The branch store on PostgreSQL, with the same operations and replies the CulinaryOS SQLite store and its
// mesanexo_ipc extension answered, so BranchService does not change. Every record and event is sealed with the
// branch keyring before it reaches the database: a dump or a stolen disk holds no readable restaurant data.
public sealed partial class PostgresBranchStore: IBranchStore, IDisposable {
    const int MaxTransaction = 56 * 1024, PageBytes = 100 * 1024, PageRecords = 32, MaxPageBytes = 1024 * 1024, MaxPageRecords = 4096;
    // Acknowledged events move to the archive after this long; pending ones are never dropped, whatever their age.
    static readonly TimeSpan AckedRetention = TimeSpan.FromDays(7);
    const int ArchiveBatch = 16, BaselineRecords = 512;
    // Past either mark the outbox raises an alert; nothing is dropped. Age counts only once a central is configured.
    const long AlertBytes = 2L * 1024 * 1024 * 1024;
    static readonly TimeSpan AlertAge = TimeSpan.FromDays(1);
    // Every writer takes this transaction-scoped lock first, so commits are serial as they were on SQLite.
    internal const long WriterLock = 0x4d4e5354;
    readonly NpgsqlDataSource data;
    readonly Keyring keys;
    readonly PostgresStoreOptions options;
    PostgresBranchStore(PostgresStoreOptions options, Keyring keys) {

        this.options = options;
        this.keys = keys;
        data = NpgsqlDataSource.Create(options.ConnectionString);

    }
    public string Site => options.Site;
    public static async Task<PostgresBranchStore> Open(PostgresStoreOptions options, Keyring keys, CancellationToken ct = default) {

        Rules.Need(IdentityPattern().IsMatch(options.Site), "Identificador de sucursal inválido");
        var store = new PostgresBranchStore(options, keys);
        try { await store.Initialize(ct); return store; } catch { store.Dispose(); throw; }

    }
    async Task Initialize(CancellationToken ct) {

        await using var db = await data.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await Exec(db, tx, "SELECT pg_advisory_xact_lock(@l)", ct, ("l", WriterLock));
        // Ids are compared byte by byte, as SQLite did, so the mn_<kind>_ ranges stay contiguous.
        await Exec(db, tx, """
            CREATE TABLE IF NOT EXISTS store_metadata(key text PRIMARY KEY, value text NOT NULL);
            CREATE TABLE IF NOT EXISTS records(id text COLLATE "C" PRIMARY KEY, version bigint NOT NULL, envelope bytea NOT NULL, position bigserial NOT NULL UNIQUE);
            CREATE TABLE IF NOT EXISTS events(id text COLLATE "C" PRIMARY KEY, seq bigserial NOT NULL UNIQUE, record_id text NOT NULL, version bigint NOT NULL, envelope bytea NOT NULL, digest text NOT NULL, created double precision NOT NULL, acked double precision, attempts integer NOT NULL DEFAULT 0, next_attempt double precision NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS events_pending ON events(seq) WHERE acked IS NULL;
            CREATE TABLE IF NOT EXISTS events_archive(id text COLLATE "C" PRIMARY KEY, seq bigint NOT NULL, record_id text NOT NULL, version bigint NOT NULL, envelope bytea NOT NULL, digest text NOT NULL, created double precision NOT NULL, acked double precision NOT NULL, archived double precision NOT NULL);
            ALTER TABLE events ADD COLUMN IF NOT EXISTS last_error text, ADD COLUMN IF NOT EXISTS last_error_at double precision, ADD COLUMN IF NOT EXISTS held double precision;
            CREATE TABLE IF NOT EXISTS outbox_interventions(id bigserial PRIMARY KEY, event_id text NOT NULL, action text NOT NULL, actor text NOT NULL, reason text NOT NULL, at double precision NOT NULL);
            """, ct);
        var site = await Scalar<string>(db, tx, "SELECT value FROM store_metadata WHERE key='site_id'", ct);
        var check = await Scalar<string>(db, tx, "SELECT value FROM store_metadata WHERE key='key_check'", ct);
        var context = J.O("site_id", options.Site, "purpose", "key-check-v1");
        if (site is not null) {

            if (site != options.Site) throw new StoreException("COS-DATA-SITE");
            if (check is null || J.Canonical(keys.Open(Convert.FromBase64String(check), context)) != J.Canonical(J.O("schema", 1))) throw new StoreException("COS-DATA-INTEGRITY");

        } else {

            if (await Scalar<long>(db, tx, "SELECT count(*) FROM records", ct) > 0) throw new StoreException("COS-DATA-INTEGRITY");
            await Exec(db, tx, "INSERT INTO store_metadata VALUES('key_check', @v), ('site_id', @s), ('schema', '1')", ct, ("v", Convert.ToBase64String(keys.Seal(J.O("schema", 1), context))), ("s", options.Site));

        }
        await tx.CommitAsync(ct);

    }
    public async Task<JsonNode?> CallAsync(JsonObject request, CancellationToken ct = default) {

        // Requests and replies cross as JSON text, as they did over the store socket: numbers come back as parsed
        // values and no caller shares a node with the store.
        if (Encoding.UTF8.GetByteCount(request.ToJsonString()) > 128 * 1024) throw new StoreException("COS-DATA-INVALID");
        request = (JsonObject)JsonNode.Parse(request.ToJsonString())!;
        var reply = await Dispatch(request, ct);
        return reply is null ? null : JsonNode.Parse(reply.ToJsonString());

    }
    async Task<JsonNode?> Dispatch(JsonObject request, CancellationToken ct) {

        try {

            return request.S("op") switch {
                "health" => await Health(ct),
                "get" => await Get(Identity(request.S("record_id")), ct),
                "capabilities_v1" => J.O("protocol", 1, "extensions", J.N(new[] { "transaction_v1", "scan_v1", "recent_v1", "event_status_v1", "outbox_status_v1", "outbox_maintain_v1", "snapshot_v1" }), "max_transaction", MaxTransaction),
                "transaction_v1" => await Transaction(request, ct),
                "scan_v1" => await Scan(request, ct),
                "recent_v1" => await Recent(request, ct),
                "outbox_status_v1" => await OutboxStatus(ct),
                "event_status_v1" => await EventStatus(Identity(request.S("event_id")), ct),
                "outbox_maintain_v1" => await OutboxMaintain(request, ct),
                "snapshot_v1" => await Snapshot(request, ct),
                _ => throw new StoreException("COS-DATA-INVALID")
            };

        } catch (StoreException) { throw; } catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.DeadlockDetected) { throw new StoreException("COS-DATA-CONFLICT"); } catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.DiskFull) { throw new StoreException("COS-DATA-FULL"); } catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or InvalidCastException or KeyNotFoundException or NullReferenceException) { throw new StoreException("COS-DATA-INVALID"); } catch (Exception e) when (e is NpgsqlException or IOException or TimeoutException or OperationCanceledException) { throw new StoreException("COS-DATA-UNAVAILABLE"); }

    }
    static JsonObject Context(string purpose, string recordId, long version, string? eventId = null) { var value = J.O("purpose", purpose, "record_id", recordId, "version", version); if (eventId is not null) value["event_id"] = eventId; return value; }
    JsonObject Sealed(string purpose, string recordId, long version, string? eventId = null) { var value = Context(purpose, recordId, version, eventId); value["site_id"] = options.Site; return value; }
    byte[] SealRecord(JsonNode? data, string id, long version) => keys.Seal(data, Sealed("record", id, version));
    JsonNode? OpenRecord(byte[] envelope, string id, long version) => keys.Open(envelope, Sealed("record", id, version));
    // An event keeps the exact bytes its digest covers: the sync worker sends them as they are, and events migrated from
    // the SQLite store keep the digest Python computed over its own canonical JSON.
    byte[] SealEvent(byte[] raw, string recordId, long version, string eventId) => keys.SealBytes(raw, Sealed("event", recordId, version, eventId));
    byte[] OpenEvent(byte[] envelope, string recordId, long version, string eventId) => keys.OpenBytes(envelope, Sealed("event", recordId, version, eventId));
    static byte[] Raw(JsonObject wire) => Encoding.UTF8.GetBytes(J.Canonical(wire));
    static double Epoch() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    static string Identity(string value) => IdentityPattern().IsMatch(value) ? value : throw new StoreException("COS-DATA-INVALID");
    [GeneratedRegex("^[a-zA-Z0-9_-]{1,96}$")] private static partial Regex IdentityPattern();
    static string Digest(byte[] raw) => Convert.ToHexStringLower(SHA256.HashData(raw));
    void EnsureSpace() {

        if (!Directory.Exists(options.DataDirectory)) return;
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(options.DataDirectory))!).AvailableFreeSpace < options.MinFreeBytes) throw new StoreException("COS-DATA-FULL");

    }
    async Task<JsonNode> Health(CancellationToken ct) {

        await using var db = await data.OpenConnectionAsync(ct);
        await using var cmd = Command(db, null, "SELECT count(*), count(*) FILTER (WHERE acked IS NULL), min(created) FILTER (WHERE acked IS NULL) FROM events");
        await using var row = await cmd.ExecuteReaderAsync(ct);
        await row.ReadAsync(ct);
        return J.O("ready", true, "site_id", options.Site, "events", row.GetInt64(0), "pending", row.GetInt64(1), "oldest_pending", row.IsDBNull(2) ? null : row.GetDouble(2), "synchronous", "on", "schema", 1, "engine", "postgresql");

    }
    async Task<JsonNode?> Get(string id, CancellationToken ct) {

        await using var db = await data.OpenConnectionAsync(ct);
        await using var cmd = Command(db, null, "SELECT version, envelope FROM records WHERE id=@i", ("i", id));
        await using var row = await cmd.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct)) return null;
        return J.O("record_id", id, "version", row.GetInt64(0), "data", OpenRecord((byte[])row[1], id, row.GetInt64(0)));

    }
    async Task<JsonNode> Transaction(JsonObject request, CancellationToken ct) {

        if (request["protocol"]?.GetValue<int>() != 1) throw new StoreException("COS-DATA-INVALID");
        var eventId = Identity(request.S("event_id"));
        var receipt = request["receipt"] as JsonObject ?? throw new StoreException("COS-DATA-INVALID");
        var changes = request["changes"] as JsonArray ?? throw new StoreException("COS-DATA-INVALID");
        if (changes.Count is < 1 or > 256) throw new StoreException("COS-DATA-INVALID");
        var ids = changes.Select(c => Identity(c!.S("record_id"))).ToList();
        if (ids.Distinct().Count() != ids.Count || !ids.All(x => x.StartsWith("mn_")) || !ids.Contains("mn_meta_site")) throw new StoreException("COS-DATA-INVALID");
        foreach (var change in changes) if (change!["expected_version"] is not JsonValue v || !v.TryGetValue<long>(out var expected) || expected < 0 || change["data"] is not JsonObject) throw new StoreException("COS-DATA-INVALID");
        var rid = "tx_" + eventId;
        var wire = J.O("protocol", "culinaryos-events-v1", "site_id", options.Site, "event_id", eventId, "record_id", rid, "version", 1, "data", J.O("contract", "mesa-nexo-transaction-v1", "changes", changes.DeepClone(), "receipt", receipt.DeepClone()));
        var raw = Raw(wire);
        if (raw.Length > MaxTransaction) throw new StoreException("COS-DATA-INVALID");
        await using var db = await data.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await Exec(db, tx, "SELECT pg_advisory_xact_lock(@l)", ct, ("l", WriterLock));
        await using (var prior = Command(db, tx, "SELECT envelope FROM records WHERE id=@i", ("i", rid))) {

            if (await prior.ExecuteScalarAsync(ct) is byte[] envelope) {

                var existing = OpenRecord(envelope, rid, 1)!.AsObject();
                if (existing.S("fingerprint") != receipt.S("fingerprint") || existing.S("actor") != receipt.S("actor")) throw new StoreException("COS-DATA-IDEMPOTENCY");
                return J.O("duplicate", true, "receipt", existing, "event_id", eventId);

            }

        }
        var current = new Dictionary<string, long>();
        await using (var versions = Command(db, tx, "SELECT id, version FROM records WHERE id = ANY(@ids)", ("ids", ids.ToArray()))) { await using var row = await versions.ExecuteReaderAsync(ct); while (await row.ReadAsync(ct)) current[row.GetString(0)] = row.GetInt64(1); }
        foreach (var change in changes) if (current.GetValueOrDefault(change!.S("record_id")) != change["expected_version"]!.GetValue<long>()) throw new StoreException("COS-DATA-CONFLICT");
        EnsureSpace();
        foreach (var change in changes) {

            var id = change!.S("record_id");
            var version = change["expected_version"]!.GetValue<long>() + 1;
            await Exec(db, tx, "INSERT INTO records(id, version, envelope) VALUES(@i, @v, @e) ON CONFLICT(id) DO UPDATE SET version=excluded.version, envelope=excluded.envelope", ct, ("i", id), ("v", version), ("e", SealRecord(change["data"], id, version)));

        }
        await Exec(db, tx, "INSERT INTO records(id, version, envelope) VALUES(@i, 1, @e)", ct, ("i", rid), ("e", SealRecord(receipt, rid, 1)));
        await Exec(db, tx, "INSERT INTO events(id, record_id, version, envelope, digest, created) VALUES(@i, @r, 1, @e, @d, @c)", ct, ("i", eventId), ("r", rid), ("e", SealEvent(raw, rid, 1, eventId)), ("d", Digest(raw)), ("c", Epoch()));
        await Archive(db, tx, ct);
        await tx.CommitAsync(ct);
        return J.O("duplicate", false, "receipt", receipt.DeepClone(), "event_id", eventId);

    }
    // Acknowledged events leave the hot table a few per write; they are kept in the archive, never dropped.
    static async Task Archive(NpgsqlConnection db, NpgsqlTransaction tx, CancellationToken ct) {

        await Exec(db, tx, """
            WITH moved AS (DELETE FROM events WHERE id IN (SELECT id FROM events WHERE acked < @before ORDER BY seq LIMIT @n) RETURNING *)
            INSERT INTO events_archive(id, seq, record_id, version, envelope, digest, created, acked, archived) SELECT id, seq, record_id, version, envelope, digest, created, acked, @now FROM moved ON CONFLICT(id) DO NOTHING
            """, ct, ("before", Epoch() - AckedRetention.TotalSeconds), ("n", ArchiveBatch), ("now", Epoch()));

    }
    static (int Size, int Count) Page(JsonObject request) {

        var size = request["max_bytes"]?.GetValue<int>() ?? PageBytes;
        var count = request["max_records"]?.GetValue<int>() ?? PageRecords;
        if (size is < 1024 or > MaxPageBytes || count is < 1 or > MaxPageRecords) throw new StoreException("COS-DATA-INVALID");
        return (size, count);

    }
    static string Kind(string prefix) => prefix.Length >= 5 && prefix.StartsWith("mn_") && prefix.EndsWith('_') ? Identity(prefix) : throw new StoreException("COS-DATA-INVALID");
    // Byte-bounded pages, as the SQLite store returned them.
    (JsonArray Records, bool More) Fill(List<(string Id, long Version, byte[] Envelope)> rows, int size, int count) {

        var records = new JsonArray();
        var total = 256;
        foreach (var (id, version, envelope) in rows) {

            var value = J.O("record_id", id, "version", version, "data", OpenRecord(envelope, id, version));
            var length = Encoding.UTF8.GetByteCount(J.Canonical(value)) + 1;
            if (records.Count > 0 && (total + length > size || records.Count >= count)) return (records, true);
            records.Add(value);
            total += length;

        }
        return (records, false);

    }
    static async Task<long> Revision(NpgsqlConnection db, NpgsqlTransaction tx, CancellationToken ct) => await Scalar<long?>(db, tx, "SELECT version FROM records WHERE id='mn_meta_site'", ct) ?? 0;
    async Task<JsonNode> Scan(JsonObject request, CancellationToken ct) {

        if (request["protocol"]?.GetValue<int>() != 1) throw new StoreException("COS-DATA-INVALID");
        var prefix = request.S("prefix", "mn_");
        var after = request.S("after");
        if (prefix != "mn_" || (after != "" && !after.StartsWith(prefix))) throw new StoreException("COS-DATA-INVALID");
        if (after != "") Identity(after);
        var (size, count) = Page(request);
        var exclude = request["exclude"] as JsonArray ?? [];
        if (exclude.Count > 8) throw new StoreException("COS-DATA-INVALID");
        // Excluded kinds are cut out of the id range, so their records are never read nor decrypted.
        var ranges = new List<(string Low, string High)> { (prefix, prefix + "~") };
        foreach (var kind in exclude.Select(x => Kind(x!.GetValue<string>())).Distinct().Order(StringComparer.Ordinal)) ranges = [.. ranges.SelectMany(r => new[] { (r.Low, Min(r.High, kind)), (Max(r.Low, kind + "~"), r.High) }).Where(p => string.CompareOrdinal(p.Item1, p.Item2) < 0)];
        await using var db = await data.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var revision = await Revision(db, tx, ct);
        var rows = new List<(string, long, byte[])>();
        foreach (var (low, high) in ranges) {

            if (rows.Count > count) break;
            await using var cmd = Command(db, tx, "SELECT id, version, envelope FROM records WHERE id >= @l AND id < @h AND id > @a ORDER BY id LIMIT @n", ("l", low), ("h", high), ("a", after), ("n", count + 1 - rows.Count));
            await using var row = await cmd.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct)) rows.Add((row.GetString(0), row.GetInt64(1), (byte[])row[2]));

        }
        var (records, more) = Fill(rows, size, count);
        return J.O("records", records, "revision", revision, "after", more ? records[^1]!.S("record_id") : null);
        static string Min(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? a : b;
        static string Max(string a, string b) => string.CompareOrdinal(a, b) >= 0 ? a : b;

    }
    async Task<JsonNode> Recent(JsonObject request, CancellationToken ct) {

        if (request["protocol"]?.GetValue<int>() != 1) throw new StoreException("COS-DATA-INVALID");
        var kind = Kind(request.S("prefix"));
        var (size, count) = Page(request);
        var limit = request["limit"]?.GetValue<long>() ?? 0;
        var before = request["before"]?.GetValue<long>();
        if (limit is < 1 or > 100000 || before is < 1) throw new StoreException("COS-DATA-INVALID");
        count = (int)Math.Min(count, limit);
        await using var db = await data.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var revision = await Revision(db, tx, ct);
        var rows = new List<(string, long, byte[])>();
        var positions = new List<long>();
        await using (var cmd = Command(db, tx, "SELECT id, version, envelope, position FROM records WHERE id >= @l AND id < @h AND position < @b ORDER BY position DESC LIMIT @n", ("l", kind), ("h", kind + "~"), ("b", before ?? long.MaxValue), ("n", count + 1))) {

            await using var row = await cmd.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct)) { rows.Add((row.GetString(0), row.GetInt64(1), (byte[])row[2])); positions.Add(row.GetInt64(3)); }

        }
        var (records, more) = Fill(rows, size, count);
        more = more && records.Count < limit;
        return J.O("records", records, "revision", revision, "before", more ? positions[records.Count - 1] : null);

    }
    async Task<JsonObject> OutboxStatus(CancellationToken ct) {

        await using var db = await data.OpenConnectionAsync(ct);
        JsonObject? head = null;
        await using (var first = Command(db, null, "SELECT id, attempts, last_error, last_error_at, created FROM events WHERE acked IS NULL AND held IS NULL ORDER BY seq LIMIT 1")) {

            await using var r = await first.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct) && !r.IsDBNull(2)) head = J.O("event_id", r.GetString(0), "attempts", r.GetInt32(1), "error", r.GetString(2), "error_at", r.IsDBNull(3) ? null : r.GetDouble(3), "created", r.GetDouble(4));

        }
        await using var cmd = Command(db, null, "SELECT count(*), min(created), pg_total_relation_size('events'), (SELECT value FROM store_metadata WHERE key='outbox_acked_seq'), count(held) FROM events WHERE acked IS NULL");
        await using var row = await cmd.ExecuteReaderAsync(ct);
        await row.ReadAsync(ct);
        double? oldest = row.IsDBNull(1) ? null : row.GetDouble(1);
        var bytes = row.GetInt64(2);
        var held = row.GetInt64(4);
        // The oldest event holds back every later one: a central that keeps refusing it for its content needs a person,
        // and so does a central that stopped trusting the branch. Connectivity and server errors only wait.
        var blocked = head is not null && head.L("attempts") >= BlockedAttempts && EventFault(head.S("error"));
        var alert = blocked ? "blocked" : head?.S("error") == "auth" ? "auth" : held > 0 ? "held" : bytes > AlertBytes ? "size" : oldest is { } o && Epoch() - o > AlertAge.TotalSeconds && Central() == true ? "age" : null;
        return J.O("pending", row.GetInt64(0), "oldest_pending", oldest, "bytes", bytes, "acked_seq", row.IsDBNull(3) ? 0 : long.Parse(row.GetString(3)), "held", held, "head", head, "alert", alert);

    }
    const int BlockedAttempts = 5;
    // Failures that come from the event itself, as the central classifies its answer; the rest are about the link.
    public static bool EventFault(string error) => error is "validation" or "conflict" or "incompatible" or "ack";
    // Inspection for the administrator: pending events in order, with their last failure. Their content stays sealed.
    public async Task<JsonArray> OutboxEvents(int limit, CancellationToken ct = default) {

        await using var db = await data.OpenConnectionAsync(ct);
        var list = new JsonArray();
        await using var cmd = Command(db, null, "SELECT id, seq, record_id, version, digest, created, attempts, next_attempt, last_error, last_error_at, held FROM events WHERE acked IS NULL ORDER BY seq LIMIT @n", ("n", limit));
        await using var row = await cmd.ExecuteReaderAsync(ct);
        while (await row.ReadAsync(ct)) list.Add(J.O("event_id", row.GetString(0), "seq", row.GetInt64(1), "record_id", row.GetString(2), "version", row.GetInt64(3), "sha256", row.GetString(4), "created", row.GetDouble(5), "attempts", row.GetInt32(6), "next_attempt", row.GetDouble(7), "error", row.IsDBNull(8) ? null : row.GetString(8), "error_at", row.IsDBNull(9) ? null : row.GetDouble(9), "held", row.IsDBNull(10) ? null : row.GetDouble(10)));
        return list;

    }
    // A person's decision on a pending event, recorded with who and why. `retry` sends it again now; `hold` sets it aside
    // so later events reach the central; `release` puts it back in line. None of them acknowledges or removes the event.
    public async Task<JsonObject> Intervene(string eventId, string action, string actor, string reason, CancellationToken ct = default) {

        Rules.Need(action is "retry" or "hold" or "release", "Acción de outbox desconocida");
        Rules.Need(actor.Trim().Length is >= 2 and <= 80, "Indica quién interviene");
        Rules.Need(reason.Trim().Length is >= 10 and <= 500, "Explica el motivo (10 caracteres o más)");
        await using var db = await data.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await Exec(db, tx, "SELECT pg_advisory_xact_lock(@l)", ct, ("l", WriterLock));
        var held = await Scalar<object>(db, tx, "SELECT coalesce(held, -1) FROM events WHERE id=@i AND acked IS NULL", ct, ("i", eventId));
        Rules.Need(held is double, "El evento no está pendiente", 404);
        var isHeld = (double)held! >= 0;
        Rules.Need(action != "hold" || !isHeld, "El evento ya está apartado", 409);
        Rules.Need(action != "release" || isHeld, "El evento no está apartado", 409);
        var sql = action switch { "hold" => "UPDATE events SET held=@now WHERE id=@i", "release" => "UPDATE events SET held=NULL, attempts=0, next_attempt=0 WHERE id=@i", _ => "UPDATE events SET next_attempt=0 WHERE id=@i" };
        await Exec(db, tx, sql, ct, ("now", Epoch()), ("i", eventId));
        await Exec(db, tx, "INSERT INTO outbox_interventions(event_id, action, actor, reason, at) VALUES(@i, @a, @w, @r, @now)", ct, ("i", eventId), ("a", action), ("w", actor.Trim()), ("r", reason.Trim()), ("now", Epoch()));
        await tx.CommitAsync(ct);
        return J.O("event_id", eventId, "action", action, "actor", actor.Trim());

    }
    public async Task<JsonArray> Interventions(CancellationToken ct = default) {

        await using var db = await data.OpenConnectionAsync(ct);
        var list = new JsonArray();
        await using var cmd = Command(db, null, "SELECT event_id, action, actor, reason, at FROM outbox_interventions ORDER BY id");
        await using var row = await cmd.ExecuteReaderAsync(ct);
        while (await row.ReadAsync(ct)) list.Add(J.O("event_id", row.GetString(0), "action", row.GetString(1), "actor", row.GetString(2), "reason", row.GetString(3), "at", row.GetDouble(4)));
        return list;

    }
    // Pending events as they are stored, still sealed under the keyring, to carry them off a failing disk. Importing them
    // back adds only the ones the store does not have, and only if they open under this store's keyring and site.
    public async Task<int> ExportPending(Stream output, CancellationToken ct = default) {

        await using var db = await data.OpenConnectionAsync(ct);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        var count = 0;
        await using var cmd = Command(db, null, "SELECT id, record_id, version, envelope, digest, created FROM events WHERE acked IS NULL ORDER BY seq");
        await using var row = await cmd.ExecuteReaderAsync(ct);
        while (await row.ReadAsync(ct)) { await writer.WriteLineAsync(J.O("site_id", options.Site, "id", row.GetString(0), "record_id", row.GetString(1), "version", row.GetInt64(2), "envelope", Convert.ToBase64String((byte[])row[3]), "digest", row.GetString(4), "created", row.GetDouble(5)).ToJsonString()); count++; }
        return count;

    }
    public async Task<JsonObject> ImportPending(TextReader input, CancellationToken ct = default) {

        var lines = new List<JsonObject>();
        while (await input.ReadLineAsync(ct) is { } line) if (line.Trim() != "") lines.Add((JsonObject)JsonNode.Parse(line)!);
        // Every line must open and match its digest before anything is written.
        foreach (var item in lines) {

            Rules.Need(item.S("site_id") == options.Site, "Eventos de otra sucursal");
            byte[] raw;
            try { raw = OpenEvent(Convert.FromBase64String(item.S("envelope")), item.S("record_id"), item.L("version"), item.S("id")); } catch (Exception e) when (e is StoreException or FormatException or System.Security.Cryptography.CryptographicException) { throw new StoreException("COS-DATA-INTEGRITY"); }
            if (Digest(raw) != item.S("digest")) throw new StoreException("COS-DATA-INTEGRITY");

        }
        await using var db = await data.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await Exec(db, tx, "SELECT pg_advisory_xact_lock(@l)", ct, ("l", WriterLock));
        var added = 0;
        foreach (var item in lines) {

            if (await Scalar<object>(db, tx, "SELECT 1 FROM events WHERE id=@i UNION ALL SELECT 1 FROM events_archive WHERE id=@i", ct, ("i", item.S("id"))) is not null) continue;
            await Exec(db, tx, "INSERT INTO events(id, record_id, version, envelope, digest, created) VALUES(@i, @r, @v, @e, @d, @c)", ct, ("i", item.S("id")), ("r", item.S("record_id")), ("v", item.L("version")), ("e", Convert.FromBase64String(item.S("envelope"))), ("d", item.S("digest")), ("c", item["created"]!.GetValue<double>()));
            added++;

        }
        await tx.CommitAsync(ct);
        return J.O("read", lines.Count, "added", added);

    }
    async Task<JsonNode> EventStatus(string eventId, CancellationToken ct) {

        await using var db = await data.OpenConnectionAsync(ct);
        var acked = await Scalar<object>(db, null, "SELECT coalesce(acked, -1) FROM events WHERE id=@i", ct, ("i", eventId));
        if (acked is double value) return J.O("local", "committed", "remote", value >= 0 ? "acknowledged" : "pending");
        if (await Scalar<object>(db, null, "SELECT 1 FROM events_archive WHERE id=@i", ct, ("i", eventId)) is not null) return J.O("local", "committed", "remote", "acknowledged");
        if (await Scalar<object>(db, null, "SELECT 1 FROM records WHERE id=@i", ct, ("i", "tx_" + eventId)) is not null) {

            // Only a store migrated from SQLite can miss an event it committed: the old store dropped unacknowledged ones.
            var dropped = await Scalar<string>(db, null, "SELECT value FROM store_metadata WHERE key='mesanexo_outbox_dropped'", ct) is not null;
            return J.O("local", "committed", "remote", dropped ? "untracked" : "acknowledged");

        }
        return J.O("local", "unknown", "remote", "unknown");

    }
    bool? Central() {

        try { var config = JsonNode.Parse(File.ReadAllText(options.BranchConfig)) as JsonObject; return config?["version"]?.GetValue<int>() == 1 ? config["central"] is JsonObject : null; } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { return null; }

    }
    // Without a central nothing acknowledges the outbox, and pending events are kept: a central connected later
    // receives the whole history. Only a store migrated from SQLite, which may have dropped some, queues a baseline.
    async Task<JsonNode> OutboxMaintain(JsonObject request, CancellationToken ct) {

        if (request["protocol"]?.GetValue<int>() != 1) throw new StoreException("COS-DATA-INVALID");
        var central = Central();
        if (central is null) return J.O("mode", "unknown", "more", false);
        await using var db = await data.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await Exec(db, tx, "SELECT pg_advisory_xact_lock(@l)", ct, ("l", WriterLock));
        var raw = await Scalar<string>(db, tx, "SELECT value FROM store_metadata WHERE key='mesanexo_outbox'", ct);
        var state = raw is null ? J.O("mode", "complete") : (JsonObject)JsonNode.Parse(raw)!;
        var before = J.Canonical(state);
        if (central == false) {

            if (state.S("mode") != "standalone-dropped") state = J.O("mode", "standalone");

        } else if (state.S("mode") is "standalone-dropped" or "baseline") {

            EnsureSpace();
            if (state.S("mode") == "standalone-dropped") state = J.O("mode", "baseline", "after", "", "events", 0, "started", Epoch());
            var last = await BaselineEvent(db, tx, state.S("after"), state["events"]!.GetValue<int>() + 1, ct);
            if (last is null) state = J.O("mode", "complete", "baseline_events", state["events"]!.GetValue<int>(), "baseline_completed", Epoch());
            else { state["after"] = last; state["events"] = state["events"]!.GetValue<int>() + 1; }

        } else state = J.O("mode", "complete");
        if (J.Canonical(state) != before) await Exec(db, tx, "INSERT INTO store_metadata VALUES('mesanexo_outbox', @v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ct, ("v", J.Canonical(state)));
        await tx.CommitAsync(ct);
        var status = await OutboxStatus(ct);
        return J.O("mode", state.S("mode") == "standalone-dropped" ? "standalone" : state.S("mode"), "pending", status.L("pending"), "alert", status["alert"]?.DeepClone(), "more", state.S("mode") == "baseline");

    }
    async Task<string?> BaselineEvent(NpgsqlConnection db, NpgsqlTransaction tx, string after, int part, CancellationToken ct) {

        var changes = new JsonArray();
        var size = 1024;
        await using (var cmd = Command(db, tx, "SELECT id, version, envelope FROM records WHERE id >= 'mn_' AND id < 'mn_~' AND id > @a ORDER BY id LIMIT @n", ("a", after), ("n", BaselineRecords))) {

            await using var row = await cmd.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct)) {

                var change = J.O("record_id", row.GetString(0), "expected_version", row.GetInt64(1) - 1, "data", OpenRecord((byte[])row[2], row.GetString(0), row.GetInt64(1)));
                var length = Encoding.UTF8.GetByteCount(J.Canonical(change)) + 1;
                if (changes.Count > 0 && size + length > MaxTransaction) break;
                changes.Add(change);
                size += length;

            }

        }
        if (changes.Count == 0) return null;
        var eventId = "bl" + J.Id();
        var rid = "bl_" + eventId;
        var wire = J.O("protocol", "culinaryos-events-v1", "site_id", options.Site, "event_id", eventId, "record_id", rid, "version", 1, "data", J.O("contract", "mesa-nexo-baseline-v1", "changes", changes, "receipt", J.O("actor", "outbox-baseline", "operation", "outbox.baseline", "part", part, "at", J.Now())));
        var raw = Raw(wire);
        await Exec(db, tx, "INSERT INTO events(id, record_id, version, envelope, digest, created) VALUES(@i, @r, 1, @e, @d, @c)", ct, ("i", eventId), ("r", rid), ("e", SealEvent(raw, rid, 1, eventId)), ("d", Digest(raw)), ("c", Epoch()));
        return changes[^1]!.S("record_id");

    }
    // A pg_dump of the branch database before a schema change; the data in it stays sealed under the keyring.
    async Task<JsonNode> Snapshot(JsonObject request, CancellationToken ct) {

        if (request["protocol"]?.GetValue<int>() != 1) throw new StoreException("COS-DATA-INVALID");
        var root = Path.Combine(options.DataDirectory, "schema-backups");
        Rules.Need(!Directory.Exists(root) || new DirectoryInfo(root).LinkTarget is null, "La carpeta de respaldos no puede ser un enlace");
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var file = Path.Combine(root, "schema-" + J.Id() + ".dump");
        await Dump(file, ct);
        await using var input = File.OpenRead(file);
        return J.O("backup", Path.GetFileName(file), "sha256", Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct)), "site_id", options.Site, "encrypted", true);

    }
    public async Task Dump(string file, CancellationToken ct = default) {

        var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString);
        var start = new ProcessStartInfo(options.PgDump ?? "pg_dump") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--format=custom", "--no-owner", "--file", file, "--dbname", builder.Database ?? "culinaryos" }) start.ArgumentList.Add(arg);
        if (builder.Host is { } host) { start.ArgumentList.Add("--host"); start.ArgumentList.Add(host); }
        if (builder.Port != 5432) { start.ArgumentList.Add("--port"); start.ArgumentList.Add(builder.Port.ToString()); }
        if (builder.Username is { } user) { start.ArgumentList.Add("--username"); start.ArgumentList.Add(user); }
        if (builder.Password is { } password) start.Environment["PGPASSWORD"] = password;
        using var process = Process.Start(start) ?? throw new StoreException("COS-DATA-UNAVAILABLE");
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) { if (File.Exists(file)) File.Delete(file); Console.Error.WriteLine("COS-DATA-SNAPSHOT: " + error.Trim()); throw new StoreException("COS-DATA-UNAVAILABLE"); }

    }
    // Pending events oldest first for the sync worker, and their acknowledgement; one sender keeps them in order.
    public async Task<List<(JsonObject Wire, byte[] Raw, string Digest)>> Pending(int limit, CancellationToken ct = default) {

        await using var db = await data.OpenConnectionAsync(ct);
        var now = Epoch();
        var values = new List<(JsonObject, byte[], string)>();
        await using var cmd = Command(db, null, "SELECT id, record_id, version, envelope, digest, next_attempt FROM events WHERE acked IS NULL AND held IS NULL ORDER BY seq LIMIT @n", ("n", limit));
        await using var row = await cmd.ExecuteReaderAsync(ct);
        while (await row.ReadAsync(ct)) {

            if (row.GetDouble(5) > now) break;
            var raw = OpenEvent((byte[])row[3], row.GetString(1), row.GetInt64(2), row.GetString(0));
            if (Digest(raw) != row.GetString(4)) throw new StoreException("COS-DATA-INTEGRITY");
            values.Add(((JsonObject)JsonNode.Parse(raw)!, raw, row.GetString(4)));

        }
        return values;

    }
    // A batch of acknowledgements in one transaction, which also records the last confirmed sequence. They must be the
    // oldest pending events in order: an acknowledgement that would leave an earlier event behind is refused whole.
    public async Task Acknowledge(IReadOnlyList<(string EventId, string Digest)> batch, CancellationToken ct = default) {

        if (batch.Count == 0) return;
        await using var db = await data.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var now = Epoch();
        long last = 0;
        foreach (var (eventId, digest) in batch) {

            var seq = await Scalar<long?>(db, tx, "UPDATE events SET acked=@now WHERE id=@i AND digest=@d AND acked IS NULL RETURNING seq", ct, ("now", now), ("i", eventId), ("d", digest));
            if (seq is not { } value || value <= last) throw new StoreException("COS-DATA-CONFLICT");
            last = value;

        }
        if (await Scalar<object>(db, tx, "SELECT 1 FROM events WHERE acked IS NULL AND held IS NULL AND seq < @s LIMIT 1", ct, ("s", last)) is not null) throw new StoreException("COS-DATA-CONFLICT");
        await Exec(db, tx, "INSERT INTO store_metadata VALUES('outbox_acked_seq', @v) ON CONFLICT(key) DO UPDATE SET value=greatest(store_metadata.value::bigint, excluded.value::bigint)::text", ct, ("v", last.ToString()));
        await tx.CommitAsync(ct);

    }
    public Task Acknowledge(string eventId, string digest, CancellationToken ct = default) => Acknowledge([(eventId, digest)], ct);
    // After a restore: every event opens under this keyring and matches its digest, none is in both tables, and
    // together they account for every committed transaction. A store migrated from SQLite may have dropped some.
    public async Task<JsonObject> VerifyOutbox(CancellationToken ct = default) {

        await using var db = await data.OpenConnectionAsync(ct);
        long total = 0, transactions = 0, damaged = 0, last = 0;
        foreach (var table in new[] { "events", "events_archive" }) {

            await using var cmd = Command(db, null, "SELECT id, record_id, version, envelope, digest, seq FROM " + table);
            await using var row = await cmd.ExecuteReaderAsync(ct);
            while (await row.ReadAsync(ct)) {

                total++;
                last = Math.Max(last, row.GetInt64(5));
                if (row.GetString(1).StartsWith("tx_")) transactions++;
                try { if (Digest(OpenEvent((byte[])row[3], row.GetString(1), row.GetInt64(2), row.GetString(0))) != row.GetString(4)) damaged++; } catch (StoreException) { damaged++; }

            }

        }
        var receipts = await Scalar<long>(db, null, "SELECT count(*) FROM records WHERE id LIKE 'tx\\_%'", ct);
        var duplicated = await Scalar<long>(db, null, "SELECT count(*) FROM events e JOIN events_archive a ON a.id = e.id", ct);
        var dropped = await Scalar<string>(db, null, "SELECT value FROM store_metadata WHERE key='mesanexo_outbox_dropped'", ct) is not null;
        var acked = long.Parse(await Scalar<string>(db, null, "SELECT value FROM store_metadata WHERE key='outbox_acked_seq'", ct) ?? "0");
        var pending = await Scalar<long>(db, null, "SELECT count(*) FROM events WHERE acked IS NULL", ct);
        var valid = damaged == 0 && duplicated == 0 && acked <= last && (dropped ? transactions <= receipts : transactions == receipts);
        return J.O("valid", valid, "events", total, "pending", pending, "transactions", receipts, "damaged", damaged, "duplicated", duplicated, "acked_seq", acked, "dropped_before", dropped);

    }
    // A failed delivery waits 5 s, doubling up to an hour, and keeps the class of its last failure for the alert.
    public async Task Retry(string eventId, string error = "network", CancellationToken ct = default) {

        await using var db = await data.OpenConnectionAsync(ct);
        await Exec(db, null, "UPDATE events SET attempts=attempts+1, next_attempt=@now + least(3600, 5 * power(2, least(attempts, 10))), last_error=@e, last_error_at=@now WHERE id=@i AND acked IS NULL", ct, ("now", Epoch()), ("i", eventId), ("e", error));

    }
    // Migration from SQLite: rows as they were, sealed under this store's keyring.
    internal async Task ImportRecord(NpgsqlConnection db, NpgsqlTransaction tx, string id, long version, JsonNode? value, CancellationToken ct) => await Exec(db, tx, "INSERT INTO records(id, version, envelope) VALUES(@i, @v, @e)", ct, ("i", Identity(id)), ("v", version), ("e", SealRecord(value, id, version)));
    internal async Task ImportEvent(NpgsqlConnection db, NpgsqlTransaction tx, JsonObject item, byte[] raw, CancellationToken ct) {

        var id = Identity(item.S("id"));
        var acked = item["acked"] is JsonValue a ? a.GetValue<double>() : (double?)null;
        await Exec(db, tx, "INSERT INTO events(id, record_id, version, envelope, digest, created, acked, attempts, next_attempt) VALUES(@i, @r, @v, @e, @d, @c, @a, @t, @n)", ct, ("i", id), ("r", item.S("record_id")), ("v", item.L("version")), ("e", SealEvent(raw, item.S("record_id"), item.L("version"), id)), ("d", item.S("digest")), ("c", item["created"]!.GetValue<double>()), ("a", acked), ("t", (int)item.L("attempts")), ("n", item["next_attempt"]!.GetValue<double>()));

    }
    public NpgsqlDataSource Data => data;
    public Keyring Keys => keys;
    static NpgsqlCommand Command(NpgsqlConnection db, NpgsqlTransaction? tx, string sql, params (string Name, object? Value)[] parameters) {

        var cmd = new NpgsqlCommand(sql, db, tx);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;

    }
    internal static async Task<int> Exec(NpgsqlConnection db, NpgsqlTransaction? tx, string sql, CancellationToken ct, params (string, object?)[] parameters) { await using var cmd = Command(db, tx, sql, parameters); return await cmd.ExecuteNonQueryAsync(ct); }
    internal static async Task<T?> Scalar<T>(NpgsqlConnection db, NpgsqlTransaction? tx, string sql, CancellationToken ct, params (string, object?)[] parameters) { await using var cmd = Command(db, tx, sql, parameters); var value = await cmd.ExecuteScalarAsync(ct); return value is null or DBNull ? default : (T)value; }
    public void Dispose() { data.Dispose(); keys.Dispose(); }
}
