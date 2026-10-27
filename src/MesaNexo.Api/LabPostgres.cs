using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using Npgsql;
namespace MesaNexo.Api;

// TEST AND LAB ONLY: a private PostgreSQL cluster in a lab folder, listening on loopback with trust for its own
// user. CulinaryOS runs the distribution's PostgreSQL behind a Unix socket with peer authentication instead.
public static class LabPostgres {
    static readonly SemaphoreSlim gate = new(1);
    public static string Bin(string root) => Path.Combine(root, ".tools", "postgres", "pgsql", "bin");
    // Starts the cluster in `directory` (creating it the first time) and returns a connection string to `postgres`.
    public static async Task<string> Cluster(string bin, string directory) {

        await gate.WaitAsync();
        try {

            var data = Path.Combine(directory, "data");
            var portFile = Path.Combine(directory, "port");
            if (!File.Exists(Path.Combine(data, "PG_VERSION"))) {

                if (Directory.Exists(data)) Directory.Delete(data, true);
                Directory.CreateDirectory(directory);
                await Run(Path.Combine(bin, "initdb"), ["-D", data, "-U", "culinaryos", "-A", "trust", "-E", "UTF8", "--locale=C", "--no-instructions"]);
                File.WriteAllText(Path.Combine(data, "postgresql.auto.conf"), "listen_addresses = '127.0.0.1'\nfsync = on\nsynchronous_commit = on\nmax_connections = 200\nshared_buffers = 64MB\n");

            }
            if (File.Exists(portFile) && await Reachable(int.Parse(File.ReadAllText(portFile)))) return Connection(int.Parse(File.ReadAllText(portFile)), "postgres");
            var port = Free();
            File.WriteAllText(portFile, port.ToString());
            await Run(Path.Combine(bin, "pg_ctl"), ["-D", data, "-o", "-p " + port, "-l", Path.Combine(directory, "server.log"), "-w", "-t", "60", "start"]);
            return Connection(port, "postgres");

        } finally { gate.Release(); }

    }
    public static string Connection(int port, string database) => new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = port, Username = "culinaryos", Database = database, Pooling = true, MaxPoolSize = 20, Timeout = 10 }.ConnectionString;
    // A database for one lab or test branch, named in its folder so a restart finds the same data.
    public static async Task<string> Database(string cluster, string folder) {

        Directory.CreateDirectory(folder);
        var marker = Path.Combine(folder, "postgres-database.txt");
        var name = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "b_" + J.Id();
        await using (var db = new NpgsqlConnection(cluster)) {

            await db.OpenAsync();
            await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname=@n", db);
            exists.Parameters.AddWithValue("n", name);
            if (await exists.ExecuteScalarAsync() is null) { await using var create = new NpgsqlCommand("CREATE DATABASE \"" + name + "\"", db); await create.ExecuteNonQueryAsync(); }

        }
        File.WriteAllText(marker, name);
        return new NpgsqlConnectionStringBuilder(cluster) { Database = name }.ConnectionString;

    }
    // The lab keyring, in the same file the SQLite lab store used, so a lab folder keeps one identity.
    public static Keyring Keyring(string folder) {

        var file = Path.Combine(folder, "test-only-keyring.json");
        if (!File.Exists(file)) File.WriteAllText(file, Api.Keyring.Generate().ToJsonString());
        return Api.Keyring.Load(file);

    }
    public static async Task<PostgresBranchStore> Branch(string bin, string cluster, string folder, string site) {

        var branchConfig = Path.Combine(folder, "branch.json");
        Directory.CreateDirectory(folder);
        if (!File.Exists(branchConfig)) File.WriteAllText(branchConfig, J.O("version", 1, "site_id", site).ToJsonString());
        return await PostgresBranchStore.Open(new PostgresStoreOptions(await Database(cluster, folder), site, folder, branchConfig, 1024 * 1024, Path.Combine(bin, "pg_dump")), Keyring(folder));

    }
    // Tests: one throwaway cluster per process under .lab, stopped and removed when the process exits.
    static string? shared;
    static readonly object testGate = new();
    public static PostgresBranchStore Test(string root, string folder, string site) {

        var bin = Bin(root);
        lock (testGate) {

            if (shared is null) {

                var directory = Path.Combine(root, ".lab", "pg-run-" + J.Id());
                shared = Cluster(bin, directory).GetAwaiter().GetResult();
                AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { NpgsqlConnection.ClearAllPools(); Stop(bin, directory).GetAwaiter().GetResult(); Directory.Delete(directory, true); } catch (Exception) { } };

            }

        }
        return Branch(bin, shared, folder, site).GetAwaiter().GetResult();

    }
    // Loads a pg_dump of a branch into a new database named in `folder`, the way a recovery stages a backup.
    public static async Task Restore(string bin, string cluster, string dump, string folder) {

        var connection = new NpgsqlConnectionStringBuilder(await Database(cluster, folder));
        await Run(Path.Combine(bin, "pg_restore"), ["--no-owner", "--exit-on-error", "--host", connection.Host!, "--port", connection.Port.ToString(), "--username", connection.Username!, "--dbname", connection.Database!, dump]);

    }
    public static Task RestoreTest(string root, string dump, string folder) => Restore(Bin(root), shared ?? throw new InvalidOperationException("Sin clúster de pruebas"), dump, folder);
    public static async Task Stop(string bin, string directory) { if (File.Exists(Path.Combine(directory, "data", "postmaster.pid"))) await Run(Path.Combine(bin, "pg_ctl"), ["-D", Path.Combine(directory, "data"), "-m", "fast", "-w", "stop"]); }
    static async Task<bool> Reachable(int port) { try { using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(2)); return true; } catch (Exception) { return false; } }
    static int Free() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    static async Task Run(string file, string[] args) {

        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("No se inició " + file);
        // pg_ctl start leaves the server running with these pipes inherited; read them without waiting for their end.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException(Path.GetFileName(file) + " falló: " + (await error).Trim() + " " + (await output).Trim());

    }
}
