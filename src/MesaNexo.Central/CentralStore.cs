using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using MesaNexo.Core;
namespace MesaNexo.Central;
// Tenants, their branches and the mirror of received events. Never the authority for branch operations.
// `database` is a PostgreSQL connection string; `pgBin` is where pg_dump and pg_restore live.
public sealed partial class CentralStore(string database, byte[] key, string pgBin = "/usr/bin"): IDisposable {
    readonly object gate = new();
    public void Initialize() {

        Rules.Need(key.Length == 32, "Clave central AES-256 requerida");
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS event(site_id TEXT NOT NULL,event_id TEXT NOT NULL,digest TEXT NOT NULL,value bytea NOT NULL,PRIMARY KEY(site_id,event_id));";
        cmd.ExecuteNonQuery();
        InitializeTenants(db);

    }
    NpgsqlConnection Open() { var db = new NpgsqlConnection(database); db.Open(); return db; }
    byte[] Seal(string context, string body) { var raw = Encoding.UTF8.GetBytes(body); var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[raw.Length]; var tag = new byte[16]; using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, raw, cipher, tag, Encoding.UTF8.GetBytes(context)); return nonce.Concat(tag).Concat(cipher).ToArray(); }
    string Unseal(string context, byte[] body) { var raw = new byte[body.Length - 28]; using var aes = new AesGcm(key, 16); aes.Decrypt(body.AsSpan(0, 12), body.AsSpan(28), body.AsSpan(12, 16), raw, Encoding.UTF8.GetBytes(context)); return Encoding.UTF8.GetString(raw); }
    static NpgsqlCommand Command(NpgsqlConnection db, NpgsqlTransaction? tx, string sql, params (string, object)[] parameters) { var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Item1, p.Item2); return cmd; }
    public JsonObject Receive(string site, string eventId, string digest, byte[] body) {

        lock (gate) {

            using var db = Open();
            using var tx = db.BeginTransaction();
            using var existing = Command(db, tx, "SELECT digest FROM event WHERE site_id=@s AND event_id=@e", ("@s", site), ("@e", eventId));
            var prior = existing.ExecuteScalar() as string;
            Rules.Need(prior is null || prior == digest, "Evento repetido con contenido diferente", 409);
            if (prior is null) {

                using var insert = Command(db, tx, "INSERT INTO event VALUES(@s,@e,@d,@v)", ("@s", site), ("@e", eventId), ("@d", digest), ("@v", Seal("event-" + site + "-" + eventId, Encoding.UTF8.GetString(body))));
                insert.ExecuteNonQuery();
                ProjectTenantEvent(db, tx, site, JsonNode.Parse(body)!);

            }
            tx.Commit();
            return J.O("site_id", site, "event_id", eventId, "sha256", digest, "status", "committed");

        }

    }
    // A pg_dump of the whole central; tenant data inside stays sealed under the central key.
    public string Snapshot(string destination) {

        lock (gate) {

            Rules.Need(!File.Exists(destination), "No se sobrescribe un snapshot");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            Tool("pg_dump", ["--format=custom", "--no-owner", "--file", destination], database);
            using (var file = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) file.Flush(true);
            using var input = File.OpenRead(destination);
            var digest = Convert.ToHexStringLower(SHA256.HashData(input));
            File.WriteAllText(destination + ".sha256", digest + "\n");
            return digest;

        }

    }
    // The dump is checked in a staging database first: a copy sealed under another central's key never replaces this one.
    public string Restore(string sourceFile, string expectedSha256) {

        lock (gate) {

            using (var input = File.OpenRead(sourceFile)) Rules.Need(Convert.ToHexStringLower(SHA256.HashData(input)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase), "Checksum del respaldo incorrecto");
            var staging = new NpgsqlConnectionStringBuilder(database) { Database = "central_restore_" + J.Id(), Pooling = false }.ConnectionString;
            using (var db = Open()) using (var create = Command(db, null, "CREATE DATABASE \"" + new NpgsqlConnectionStringBuilder(staging).Database + "\"")) create.ExecuteNonQuery();
            try {

                Tool("pg_restore", ["--no-owner", "--exit-on-error", sourceFile], staging);
                using (var source = new NpgsqlConnection(staging)) {

                    source.Open();
                    using var inspect = Command(source, null, "SELECT tenant_id,id,value FROM tenant_user LIMIT 1");
                    using var row = inspect.ExecuteReader();
                    if (row.Read()) { try { Unseal("tenant-user:" + row.GetString(0) + ":" + row.GetString(1), (byte[])row[2]); } catch (CryptographicException) { throw new BusinessException(400, "Copia cifrada con la clave de otra central"); } }

                }

            } finally { NpgsqlConnection.ClearAllPools(); using var db = Open(); using var drop = Command(db, null, "DROP DATABASE IF EXISTS \"" + new NpgsqlConnectionStringBuilder(staging).Database + "\" WITH (FORCE)"); drop.ExecuteNonQuery(); }
            var before = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourceFile))!, "central-before-restore-" + J.Id() + ".dump");
            Snapshot(before);
            NpgsqlConnection.ClearAllPools();
            Tool("pg_restore", ["--no-owner", "--clean", "--if-exists", "--single-transaction", "--exit-on-error", sourceFile], database);
            using (var restored = Open()) { InitializeTenants(restored); using var revoke = Command(restored, null, "DELETE FROM tenant_session"); revoke.ExecuteNonQuery(); }
            return before;

        }

    }
    void Tool(string name, string[] args, string connection) {

        var c = new NpgsqlConnectionStringBuilder(connection);
        var all = new List<string>(args) { "--dbname", c.Database! };
        if (c.Host is { } host) { all.Add("--host"); all.Add(host); }
        all.Add("--port"); all.Add(c.Port.ToString());
        if (c.Username is { } user) { all.Add("--username"); all.Add(user); }
        MesaNexo.Api.LabPostgres.Run(Path.Combine(pgBin, name), [.. all], c.Password).GetAwaiter().GetResult();

    }
    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}