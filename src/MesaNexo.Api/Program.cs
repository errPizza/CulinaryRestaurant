using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Api;

public static class Program {
    public static async Task<int> Main(string[] args) {

        try {

            string? Arg(string key) => args.SkipWhile(x => x != "--" + key).Skip(1).FirstOrDefault();
            if (Arg("decrypt-backup") is string legacy) { await Migration.DecryptBackup(legacy, Arg("legacy-key") ?? throw new Exception(), Arg("output") ?? throw new Exception()); Console.WriteLine("Copia descifrada y validada; protege la salida y continúa con el exportador."); return 0; }
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
            using (certificate) { await using var app = ApiHost.Build(options, store, certificate, licensing, Arg("cloud-url") is string cloudUrl ? new CloudOptions(cloudUrl, options.Credentials ?? throw new Exception(), Arg("central-public-key") ?? throw new Exception()) : null, printer, fiscal, integrations); await app.RunAsync(); }
            return 0;

        } catch (BusinessException e) { Console.Error.WriteLine(e.Message); return 1; } catch (Exception e) { Console.Error.WriteLine("No se pudo iniciar Culinary Restaurant. Comprueba identidad, configuración, credenciales y runtime."); if (args.Contains("--lab")) Console.Error.WriteLine(e); return 1; }

    }
}