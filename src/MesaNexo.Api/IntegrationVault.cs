using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;
// Credentials a manager enters in Integraciones. They are sealed under the branch keyring, with the site and provider as
// context, in the backend's own state directory: never in the branch store, never in a client. A file an operator put in
// /etc/culinaryos/integrations wins over them, so support can always override what was typed.
public sealed class IntegrationVault(string directory, Keyring keys, string siteId) {
    public const string CulinaryOs = "/var/lib/culinaryos-app/integrations";
    JsonObject Context(string key) => J.O("purpose", "integration-credentials", "site", siteId, "provider", key);
    string PathOf(string key) => Path.Combine(directory, key + ".sealed");
    public void Save(string key, JsonObject credentials) {

        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = PathOf(key) + ".tmp";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) {

            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            output.Write(keys.Seal(credentials, Context(key)));
            output.Flush(true);

        }
        File.Move(temporary, PathOf(key), true);

    }
    public JsonObject? Load(string key) {

        var path = PathOf(key);
        return File.Exists(path) ? keys.Open(File.ReadAllBytes(path), Context(key)) as JsonObject : null;

    }
    // Fills each provider that has no file of its own; one that does not open is reported on its card.
    public IntegrationConfig Into(IntegrationConfig config) {

        foreach (var key in Restaurant.IntegrationKeys) {

            if (config.File(key) is not null || config.Status[key] != "sin credenciales") continue;
            try { if (Load(key) is { } stored) config.Fill(key, stored); } catch (Exception) { config.Status[key] = "error: no se pudieron abrir las credenciales guardadas; vuelve a capturarlas"; }

        }
        return config;

    }
    // What the card may show: the last four characters of each text value that looks secret, nothing else.
    public static JsonObject Hints(JsonObject credentials) {

        var hints = new JsonObject();
        void Walk(JsonNode? node, string path) {

            switch (node) {
                case JsonObject o: foreach (var (k, v) in o) Walk(v, path == "" ? k : path + "." + k); break;
                case JsonArray a: for (var i = 0; i < a.Count; i++) Walk(a[i], path + "." + i); break;
                case JsonValue v when v.TryGetValue<string>(out var text) && text.Length >= 8 && Secret(path): hints[path] = "••••" + text[^4..]; break;
            }

        }
        Walk(credentials, "");
        return hints;

    }
    static bool Secret(string path) { var leaf = path.Split('.')[^1].ToLowerInvariant(); return new[] { "token", "key", "secret", "password", "pass" }.Any(leaf.Contains); }
    // The same builders the live services start from: a value they reject never reaches the vault.
    static readonly Dictionary<string, Func<JsonObject, object>> Builders = new() {
        ["pac"] = o => FiscalOptions.From(o),
        ["printer"] = o => PrinterOptions.From(o),
        ["terminal"] = o => TerminalService.From(o),
        ["onlinepay"] = o => OnlinePayService.From(o),
        ["messaging"] = o => MessagingService.From(o),
        ["pms"] = o => PmsService.From(o),
        ["maps"] = o => MapsService.From(o),
        ["callerid"] = o => CallerIdService.Options(o),
        ["aggregators"] = o => AggregatorService.From(o)
    };
    public static void Validate(string key, JsonObject credentials) {

        Need(Builders.TryGetValue(key, out var build), "Integración desconocida");
        object built;
        try { built = build!(credentials); } catch (BusinessException e) { throw new BusinessException(400, "Revisa los datos: " + e.Message); } catch (Exception) { throw new BusinessException(400, "Revisa los datos: la configuración no es válida para este proveedor"); }
        (built as IDisposable)?.Dispose();

    }
}
public sealed partial class BranchService {
    public IntegrationVault? Vault { get; set; }
    // Set by the host: the live services start once with their credentials, so new ones apply after a restart.
    public Action? RestartForIntegrations { get; set; }
    async Task<JsonObject> SaveIntegrationCredentials(string token, string eventId, JsonObject p, CancellationToken ct) {

        var key = p.S("provider");
        Need(Restaurant.IntegrationKeys.Contains(key), "Integración desconocida");
        Need(p["credentials"] is JsonObject, "Faltan los datos de conexión");
        var credentials = (JsonObject)p["credentials"]!.DeepClone();
        Need(credentials.ToJsonString().Length <= 16_000, "Datos de conexión demasiado grandes");
        Need(Vault is not null, "Este servidor no puede guardar credenciales; pide a soporte que las coloque", 409);
        return await Exclusive(async () => {

            var s = await Load(ct);
            var u = Authenticate(s, token, out _);
            Need(u.S("role") == "manager", "Solo un gerente puede activar integraciones", 403);
            Need(Domain.License(s).IntegrationAllowed(key), "Tu licencia no incluye esta integración; pídela a Culinary", 403);
            IntegrationVault.Validate(key, credentials);
            var sanitized = J.O("provider", key, "hints", IntegrationVault.Hints(credentials));
            // The receipt keeps a digest of what was entered, never the values.
            var fingerprint = J.Hash(J.Canonical(J.O("actor", u.S("id"), "type", "integration.credentials", "payload", sanitized, "secret", J.Hash(J.Canonical(credentials)), "siteId", siteId)));
            Vault!.Save(key, credentials);
            var result = Domain.Act(s, u, "integration.credentials", sanitized);
            result = await Commit(s, eventId, u.S("id"), fingerprint, result, "integration.credentials", ct);
            Integrations.Fill(key, credentials);
            if (RestartForIntegrations is { } restart) { Integrations.Status[key] = "reiniciando"; _ = Task.Delay(1500).ContinueWith(_ => restart()); }
            // Without a host restart (lab, tests) the catalog changes at once; the background services wait for the next start.
            else { Integrations.Ready(key); Domain.LiveCatalog["providers"] = J.N(Integrations.ReadyKeys); }
            return J.O("operationId", eventId, "local", "committed", "duplicate", false, "result", result, "restarting", RestartForIntegrations is not null);

        }, ct);

    }
}
