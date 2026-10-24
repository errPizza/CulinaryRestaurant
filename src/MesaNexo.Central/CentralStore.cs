using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MesaNexo.Core;
namespace MesaNexo.Central;
// Provider metadata and received event mirror only. Never the authority for branch operations.
public sealed partial class CentralStore(string database, byte[] key): IDisposable {
    readonly object gate = new();
    public void Initialize() {

        Rules.Need(key.Length == 32, "Clave central AES-256 requerida");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(database))!);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS provider(id INTEGER PRIMARY KEY CHECK(id=1),value BLOB NOT NULL);CREATE TABLE IF NOT EXISTS operation(id TEXT PRIMARY KEY,fingerprint TEXT NOT NULL,actor TEXT NOT NULL,value BLOB NOT NULL);CREATE TABLE IF NOT EXISTS event(site_id TEXT NOT NULL,event_id TEXT NOT NULL,digest TEXT NOT NULL,value BLOB NOT NULL,PRIMARY KEY(site_id,event_id));";
        cmd.ExecuteNonQuery();
        InitializeTenants(db);

    }
    SqliteConnection Open() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()); db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA foreign_keys=ON;PRAGMA synchronous=FULL;PRAGMA busy_timeout=5000"; cmd.ExecuteNonQuery(); return db; }
    byte[] Seal(string context, string body) { var raw = Encoding.UTF8.GetBytes(body); var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[raw.Length]; var tag = new byte[16]; using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, raw, cipher, tag, Encoding.UTF8.GetBytes(context)); return nonce.Concat(tag).Concat(cipher).ToArray(); }
    string Unseal(string context, byte[] body) { var raw = new byte[body.Length - 28]; using var aes = new AesGcm(key, 16); aes.Decrypt(body.AsSpan(0, 12), body.AsSpan(28), body.AsSpan(12, 16), raw, Encoding.UTF8.GetBytes(context)); return Encoding.UTF8.GetString(raw); }
    static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object)[] parameters) { var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Item1, p.Item2); return cmd; }
    public JsonObject? Read() { lock (gate) { using var db = Open(); using var cmd = Command(db, null, "SELECT value FROM provider WHERE id=1"); return cmd.ExecuteScalar() is byte[] raw ? JsonNode.Parse(Unseal("provider-v1", raw))!.AsObject() : null; } }
    public T Transaction<T>(Func<JsonObject, T> action) {

        lock (gate) { using var db = Open(); using var tx = db.BeginTransaction(); using var read = Command(db, tx, "SELECT value FROM provider WHERE id=1"); var s = read.ExecuteScalar() is byte[] raw ? JsonNode.Parse(Unseal("provider-v1", raw))!.AsObject() : new JsonObject(); var result = action(s); using var save = Command(db, tx, "INSERT INTO provider VALUES(1,$value) ON CONFLICT(id) DO UPDATE SET value=excluded.value", ("$value", Seal("provider-v1", s.ToJsonString()))); save.ExecuteNonQuery(); tx.Commit(); return result; }

    }
    public JsonObject Apply(string id, string fingerprint, string actor, Func<JsonObject, JsonObject> action) {

        lock (gate) {

            using var db = Open();
            using var tx = db.BeginTransaction();
            using var prior = Command(db, tx, "SELECT fingerprint,actor,value FROM operation WHERE id=$id", ("$id", id));
            using (var reader = prior.ExecuteReader()) { if (reader.Read()) { Rules.Need(reader.GetString(0) == fingerprint && reader.GetString(1) == actor, "ID reutilizado", 409); return JsonNode.Parse(Unseal("op-" + id, (byte[])reader[2]))!.AsObject(); } }
            using var read = Command(db, tx, "SELECT value FROM provider WHERE id=1");
            var s = JsonNode.Parse(Unseal("provider-v1", (byte[])read.ExecuteScalar()!))!.AsObject();
            var result = action(s);
            using var save = Command(db, tx, "UPDATE provider SET value=$value WHERE id=1", ("$value", Seal("provider-v1", s.ToJsonString())));
            save.ExecuteNonQuery();
            using var receipt = Command(db, tx, "INSERT INTO operation VALUES($id,$f,$a,$v)", ("$id", id), ("$f", fingerprint), ("$a", actor), ("$v", Seal("op-" + id, result.ToJsonString())));
            receipt.ExecuteNonQuery();
            tx.Commit();
            return result;

        }

    }
    public JsonObject? Receipt(string id, string actor) { using var db = Open(); using var cmd = Command(db, null, "SELECT value FROM operation WHERE id=$id AND actor=$actor", ("$id", id), ("$actor", actor)); return cmd.ExecuteScalar() is byte[] raw ? JsonNode.Parse(Unseal("op-" + id, raw))!.AsObject() : null; }
    public JsonObject Receive(string site, string eventId, string digest, byte[] body) {

        lock (gate) {

            using var db = Open();
            using var tx = db.BeginTransaction();
            using var existing = Command(db, tx, "SELECT digest FROM event WHERE site_id=$s AND event_id=$e", ("$s", site), ("$e", eventId));
            var prior = existing.ExecuteScalar() as string;
            Rules.Need(prior is null || prior == digest, "Evento repetido con contenido diferente", 409);
            if (prior is null) {

                using var insert = Command(db, tx, "INSERT INTO event VALUES($s,$e,$d,$v)", ("$s", site), ("$e", eventId), ("$d", digest), ("$v", Seal("event-" + site + "-" + eventId, Encoding.UTF8.GetString(body))));
                insert.ExecuteNonQuery();
                var wire = JsonNode.Parse(body)!;
                ProjectTenantEvent(db, tx, site, wire);
                var changes = wire.Obj("data").A("changes").Objects();
                using var read = Command(db, tx, "SELECT value FROM provider WHERE id=1");
                var state = JsonNode.Parse(Unseal("provider-v1", (byte[])read.ExecuteScalar()!))!.AsObject();
                var projected = false;
                foreach (var change in changes) {

                    var row = change.Obj("data");
                    if (row.S("kind") != "ticket") continue;
                    var ticketId = row.S("id");
                    var tickets = state.A("tickets");
                    var existingTicket = tickets.Objects().FirstOrDefault(x => x.S("siteId") == site && x.S("ticketId") == ticketId);
                    if (existingTicket is not null) tickets.Remove(existingTicket);
                    var messages = row.A("messages").Objects().Select(m => J.O("id", m.S("id"), "actor", m.S("actor"), "name", m.S("name"), "message", m.S("message"), "at", m.S("at"))).ToArray();
                    tickets.Add(J.O("id", site + "_" + ticketId, "ticketId", ticketId, "siteId", site, "subject", row.S("subject"), "status", row.S("status"), "at", row.S("at"), "messages", messages));
                    var pending = state.A("supportOutbox");
                    var delivered = pending.Objects().Where(x => x.S("siteId") == site && x.S("ticketId") == ticketId && messages.Any(m => m.S("id") == x.Obj("message").S("id"))).ToArray();
                    foreach (var item in delivered) pending.Remove(item);
                    if (delivered.Length > 0) { var restaurant = state.A("restaurants").Objects().Single(x => x.S("siteId") == site); restaurant["revision"] = restaurant.L("revision") + 1; }
                    projected = true;

                }
                if (projected) { state["revision"] = state.L("revision") + 1; using var save = Command(db, tx, "UPDATE provider SET value=$v WHERE id=1", ("$v", Seal("provider-v1", state.ToJsonString()))); save.ExecuteNonQuery(); }

            }
            tx.Commit();
            return J.O("site_id", site, "event_id", eventId, "sha256", digest, "status", "committed");

        }

    }
    public string Snapshot(string destination) {

        lock (gate) {

            Rules.Need(!File.Exists(destination), "No se sobrescribe un snapshot");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            using (var source = Open()) using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString())) { copy.Open(); source.BackupDatabase(copy); using var check = copy.CreateCommand(); check.CommandText = "PRAGMA quick_check"; Rules.Need((string)check.ExecuteScalar()! == "ok", "Snapshot no íntegro"); }
            using (var file = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) file.Flush(true);
            using var input = File.OpenRead(destination);
            var digest = Convert.ToHexStringLower(SHA256.HashData(input));
            File.WriteAllText(destination + ".sha256", digest + "\n");
            return digest;

        }

    }
    public string Restore(string sourceFile, string expectedSha256, string providerId) {

        lock (gate) {

            using (var input = File.OpenRead(sourceFile)) Rules.Need(Convert.ToHexStringLower(SHA256.HashData(input)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase), "Checksum del respaldo incorrecto");
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourceFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            source.Open();
            using (var check = source.CreateCommand()) { check.CommandText = "PRAGMA quick_check"; Rules.Need((string)check.ExecuteScalar()! == "ok", "Respaldo SQLite no íntegro"); }
            using (var inspect = source.CreateCommand()) { inspect.CommandText = "SELECT value FROM provider WHERE id=1"; var s = JsonNode.Parse(Unseal("provider-v1", (byte[])inspect.ExecuteScalar()!))!; Rules.Need(s.S("providerId") == providerId, "Copia de otro proveedor"); }
            var before = database + ".before-restore-" + J.Id() + ".sqlite";
            Snapshot(before);
            using (var destination = Open()) source.BackupDatabase(destination);
            using (var restored = Open()) { InitializeTenants(restored); using var revoke = Command(restored, null, "DELETE FROM tenant_session"); revoke.ExecuteNonQuery(); }
            Transaction(s => { foreach (var session in s.A("sessions").Objects()) session["revoked"] = true; s["revision"] = s.L("revision") + 1; return true; });
            return before;

        }

    }
    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}