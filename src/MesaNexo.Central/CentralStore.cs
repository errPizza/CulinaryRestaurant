using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MesaNexo.Core;
namespace MesaNexo.Central;
// Tenants, their branches and the mirror of received events. Never the authority for branch operations.
public sealed partial class CentralStore(string database, byte[] key): IDisposable {
    readonly object gate = new();
    public void Initialize() {

        Rules.Need(key.Length == 32, "Clave central AES-256 requerida");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(database))!);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS event(site_id TEXT NOT NULL,event_id TEXT NOT NULL,digest TEXT NOT NULL,value BLOB NOT NULL,PRIMARY KEY(site_id,event_id));";
        cmd.ExecuteNonQuery();
        InitializeTenants(db);

    }
    SqliteConnection Open() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()); db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA foreign_keys=ON;PRAGMA synchronous=FULL;PRAGMA busy_timeout=5000"; cmd.ExecuteNonQuery(); return db; }
    byte[] Seal(string context, string body) { var raw = Encoding.UTF8.GetBytes(body); var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[raw.Length]; var tag = new byte[16]; using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, raw, cipher, tag, Encoding.UTF8.GetBytes(context)); return nonce.Concat(tag).Concat(cipher).ToArray(); }
    string Unseal(string context, byte[] body) { var raw = new byte[body.Length - 28]; using var aes = new AesGcm(key, 16); aes.Decrypt(body.AsSpan(0, 12), body.AsSpan(28), body.AsSpan(12, 16), raw, Encoding.UTF8.GetBytes(context)); return Encoding.UTF8.GetString(raw); }
    static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object)[] parameters) { var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Item1, p.Item2); return cmd; }
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
                ProjectTenantEvent(db, tx, site, JsonNode.Parse(body)!);

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
    public string Restore(string sourceFile, string expectedSha256) {

        lock (gate) {

            using (var input = File.OpenRead(sourceFile)) Rules.Need(Convert.ToHexStringLower(SHA256.HashData(input)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase), "Checksum del respaldo incorrecto");
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourceFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            source.Open();
            using (var check = source.CreateCommand()) { check.CommandText = "PRAGMA quick_check"; Rules.Need((string)check.ExecuteScalar()! == "ok", "Respaldo SQLite no íntegro"); }
            // A copy sealed under another central's key would restore unreadable rows.
            using (var inspect = Command(source, null, "SELECT tenant_id,id,value FROM tenant_user LIMIT 1")) { using var row = inspect.ExecuteReader(); if (row.Read()) { try { Unseal("tenant-user:" + row.GetString(0) + ":" + row.GetString(1), (byte[])row[2]); } catch (CryptographicException) { throw new BusinessException(400, "Copia cifrada con la clave de otra central"); } } }
            var before = database + ".before-restore-" + J.Id() + ".sqlite";
            Snapshot(before);
            using (var destination = Open()) source.BackupDatabase(destination);
            using (var restored = Open()) { InitializeTenants(restored); using var revoke = Command(restored, null, "DELETE FROM tenant_session"); revoke.ExecuteNonQuery(); }
            return before;

        }

    }
    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}