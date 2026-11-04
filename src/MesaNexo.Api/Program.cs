using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Api;

public static class Program {
    public static async Task<int> Main(string[] args) {

        try {

            string? Arg(string key) => args.SkipWhile(x => x != "--" + key).Skip(1).FirstOrDefault();
            if (Arg("decrypt-backup") is string legacy) { await Migration.DecryptBackup(legacy, Arg("legacy-key") ?? throw new Exception(), Arg("output") ?? throw new Exception()); Console.WriteLine("Copia descifrada y validada; protege la salida y continúa con el exportador."); return 0; }
            // Outside the branch: an export of the chain against a copy of the bucket, with no database and no keys.
            if (Arg("verify-checkpoints") is string export) { var verdict = await OfflineCheck.Run(export, Arg("checkpoints-dir") ?? throw new Exception("Especifica --checkpoints-dir"), Arg("site") ?? throw new Exception("Especifica --site")); Console.WriteLine(verdict.ToJsonString()); return verdict.B("valid") ? 0 : 4; }
            // For the root updater: the signed offer, this site and the exact package, with this binary's own root key.
            if (Arg("verify-update") is string offer) { await UpdateCheck.Run(offer, Arg("package") ?? throw new Exception("Especifica --package"), Arg("site") ?? throw new Exception("Especifica --site")); return 0; }
            var lab = args.Contains("--lab");
            var site = Arg("site") ?? (File.Exists("/etc/culinaryos/site.json") ? JsonNode.Parse(File.ReadAllText("/etc/culinaryos/site.json"))!.S("site_id") : throw new InvalidOperationException("Especifica --site"));
            var credentials = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
            // Optional one-time Setup code delivered by systemd LoadCredential on a fresh CulinaryOS.
            var setupFile = credentials is null ? null : Path.Combine(credentials, "setup-code");
            var setupCode = setupFile is not null && File.Exists(setupFile) ? (await File.ReadAllTextAsync(setupFile)).Trim() : null;
            var options = new ApiOptions(site, Port: int.Parse(Arg("port") ?? "8443"), Bind: lab ? "127.0.0.1" : Arg("bind") ?? "0.0.0.0", Socket: Arg("socket") ?? "/run/culinaryos-data/store.sock", Credentials: credentials, LabPython: Arg("python"), LabScript: Arg("lab-script"), LabDirectory: Arg("lab-directory"), ReadySocket: lab ? null : "/run/culinaryos-app/ready.sock", SetupCode: setupCode is { Length: >= 32 } ? setupCode : null);
            // The branch store is PostgreSQL: on CulinaryOS the local cluster over its Unix socket with peer authentication and the
            // keyring from systemd; in the lab a private cluster inside the lab folder.
            using var store = lab
                ? await LabPostgres.Branch(Arg("postgres-bin") ?? throw new Exception("Especifica --postgres-bin"), await LabPostgres.Cluster(Arg("postgres-bin")!, Path.Combine(options.LabDirectory ?? throw new Exception(), "postgres")), options.LabDirectory, site)
                : await PostgresBranchStore.Open(new PostgresStoreOptions(Arg("database") ?? PostgresStoreOptions.CulinaryOs, site, "/var/lib/postgresql", "/etc/culinaryos/branch.json", PgDump: "/usr/bin/pg_dump"), Keyring.Load(Path.Combine(credentials ?? throw new Exception("CREDENTIALS_DIRECTORY requerido"), "keyring")));
            // For the installer: whether this branch's database already holds a restaurant (exit 3 when it does not).
            if (args.Contains("--check-initialized")) { var meta = await store.CallAsync(J.O("op", "get", "record_id", "mn_meta_site")); Console.WriteLine(J.O("initialized", meta is not null, "meta", meta?.DeepClone()).ToJsonString()); return meta is null ? 3 : 0; }
            // One-time move off SQLite: runtime/export_store.py pipes the old store in on stdin.
            if (args.Contains("--migrate-from-export")) { Console.WriteLine((await StoreMigration.Import(store, Console.In)).ToJsonString()); if (lab) File.WriteAllText(Path.Combine(options.LabDirectory!, "initialized"), site); return 0; }
            var service = new BranchService(store, site, new Passwords());
            // For culinaryos-restore: the audit chain and the outbox of a database loaded from a snapshot (exit 4 when they fail).
            if (args.Contains("--verify-store")) { var audit = await service.AuditChain(); var outbox = await store.VerifyOutbox(); var valid = audit.B("valid") && outbox.B("valid"); Console.WriteLine(J.O("valid", valid, "audit", audit, "outbox", outbox).ToJsonString()); return valid ? 0 : 4; }
            // Support tools for the outbox: inspect, act on one event with a recorded reason, and carry pending events off a disk.
            if (args.Contains("--outbox-list")) { Console.WriteLine(J.O("status", await store.CallAsync(J.O("op", "outbox_status_v1")), "events", await store.OutboxEvents(int.Parse(Arg("limit") ?? "20")), "interventions", await store.Interventions()).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); return 0; }
            if (Arg("outbox-intervene") is string held) { Console.WriteLine((await store.Intervene(held, Arg("action") ?? throw new BusinessException(400, "Especifica --action retry|hold|release"), Arg("actor") ?? throw new BusinessException(400, "Especifica --actor"), Arg("reason") ?? throw new BusinessException(400, "Especifica --reason"))).ToJsonString()); return 0; }
            if (Arg("export-outbox") is string outboxFile) { await using var output = new FileStream(outboxFile, FileMode.CreateNew, FileAccess.Write); var count = await store.ExportPending(output); Console.WriteLine(count + " eventos pendientes exportados, sellados con el llavero de la sucursal, a " + outboxFile); return 0; }
            if (Arg("import-outbox") is string inboxFile) { using var input = File.OpenText(inboxFile); Console.WriteLine((await store.ImportPending(input)).ToJsonString()); return 0; }
            var checkpoints = ObjectBucket.From(Arg("checkpoints-config") ?? (lab ? null : Path.Combine(IntegrationConfig.Directory, "checkpoints.json")));
            var backups = ObjectBucket.From(Arg("backups-config") ?? (lab ? null : Path.Combine(IntegrationConfig.Directory, "backups.json")), 1800);
            // The password comes on stdin: an argument would stay in the process list and the shell history.
            if (Arg("reset-password") is string account) { Console.WriteLine((await service.ResetPassword(account, await Console.In.ReadLineAsync() ?? "")).ToJsonString()); return 0; }
            if (Arg("export-audit") is string chainFile) { await File.WriteAllTextAsync(chainFile, (await service.AuditRecords()).ToJsonString()); Console.WriteLine("Bitácora exportada a " + chainFile); return 0; }
            if (Arg("accept-checkpoints") is string accept) { Console.WriteLine((await service.AcceptCheckpoints(checkpoints ?? throw new BusinessException(409, "Sin destino de puntos de control"), accept)).ToJsonString()); return 0; }
            if (args.Contains("--upgrade-schema")) { Console.WriteLine((await service.UpgradeSchema()).ToJsonString()); return 0; }
            if (Arg("import-plan") is string plan) { var result = await service.ImportPlan(plan, Arg("plan-sha256") ?? throw new Exception()); Console.WriteLine(result.ToJsonString()); return 0; }
            if (Arg("initialize") is string initial) { var config = JsonNode.Parse(await File.ReadAllTextAsync(initial))!.AsObject(); await service.Initialize(config); if (lab) File.WriteAllText(Path.Combine(options.LabDirectory!, "initialized"), site); Console.WriteLine("Sucursal inicializada. Retira el archivo de aprovisionamiento que contiene credenciales."); return 0; }
            if (Arg("enrollment-file") is string enrollment) { await service.RotateEnrollment((await File.ReadAllTextAsync(enrollment)).Trim()); Console.WriteLine("Código de alta actualizado."); return 0; }
            X509Certificate2 certificate = lab ? LabCertificate.Create(options.LabDirectory!) : X509Certificate2.CreateFromPemFile(Path.Combine(options.Credentials ?? throw new Exception("CREDENTIALS_DIRECTORY requerido"), "tls-cert"), Path.Combine(options.Credentials!, "tls-key"));
            // Live integrations are optional: their credentials directory, plus the printer and PAC files from before it existed.
            // A broken file only marks its provider; the backend still starts.
            var integrations = IntegrationConfig.Load(Arg("integrations-dir") ?? (lab ? null : IntegrationConfig.Directory))
                .Adopt("printer", Arg("printer-config") ?? (lab ? null : "/etc/culinaryos/printer.json"), Arg("printer-config") is not null)
                .Adopt("pac", Arg("fiscal-config") ?? (lab ? null : "/etc/culinaryos/fiscal.json"), Arg("fiscal-config") is not null);
            var printer = integrations.Use<IPrintDriver>("printer", o => new EscPosPrintDriver(PrinterOptions.From(o)));
            var fiscal = integrations.Use("pac", FiscalOptions.From);
            // The license key lives in the backend's own state directory, outside the synchronized store.
            var licensing = new LicenseOptions(lab ? Path.Combine(options.LabDirectory!, "license") : "/var/lib/culinaryos-app/license", Arg("license-server"), Arg("hardware-file") ?? (lab ? Path.Combine(options.LabDirectory!, "hardware.json") : HardwareIdentity.DefaultFile), UseTpm: !lab, CentralCertificate: options.Credentials is null ? null : Path.Combine(options.Credentials, "central-cert"));
            using (certificate) { await using var app = ApiHost.Build(options, store, certificate, licensing, Arg("cloud-url") is string cloudUrl ? new CloudOptions(cloudUrl, options.Credentials ?? throw new Exception(), Arg("central-public-key") ?? throw new Exception()) : null, printer, fiscal, integrations, checkpoints, backups, PrivacyPurge.From(Arg("privacy-config") ?? (lab ? null : PrivacyPurge.DefaultFile))); var running = app.Services.GetRequiredService<BranchService>().Faults; AppDomain.CurrentDomain.UnhandledException += (_, e) => running.Record("api", "critical", "El servidor de la sucursal se cerró: " + (e.ExceptionObject as Exception)?.GetType().Name, "Las terminales pierden la conexión hasta que el servicio reinicia", e.ExceptionObject.ToString() ?? ""); await app.RunAsync(); }
            return 0;

        } catch (BusinessException e) { Console.Error.WriteLine(e.Message); return 1; } catch (Exception e) { Console.Error.WriteLine("No se pudo iniciar Culinary Restaurant. Comprueba identidad, configuración, credenciales y runtime."); if (args.Contains("--lab")) Console.Error.WriteLine(e); else new FaultLog(FaultLog.DefaultFile).Record("api", "critical", "El servidor de la sucursal no pudo iniciar: " + e.GetType().Name, "Ninguna terminal puede conectarse a la sucursal", e.ToString()); return 1; }

    }
}