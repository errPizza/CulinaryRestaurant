using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using MesaNexo.Core;
namespace MesaNexo.Api;

// What went wrong here, waiting to reach Culinary Admin through CulinaryServer. A file, not the store: the store may be
// what failed, and a crash must still be told on the next start. One entry per distinct fault; repeats only count.
public sealed partial class FaultLog {
    public const string DefaultFile = "/var/lib/culinaryos-app/faults.json";
    const int MaxEntries = 200, MaxBatch = 20;
    readonly string? file;
    readonly Dictionary<string, JsonObject> entries = [];
    DateTimeOffset saved = DateTimeOffset.MinValue;
    public FaultLog(string? file) {

        this.file = file;
        try { if (file is not null && File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonArray stored) foreach (var fault in stored.Objects()) entries[fault.S("fingerprint")] = (JsonObject)fault.DeepClone(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }

    }
    public static string Version { get { var v = typeof(FaultLog).Assembly.GetName().Version ?? new Version(0, 0, 0); return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}"; } }
    public void Record(string source, string severity, string what, string affected, string detail, JsonObject? context = null, string? version = null, string? platform = null) {

        // CulinaryServer refuses a report that says nothing, and one refused report would hold back every other.
        what = what.Trim() == "" ? "Fallo sin descripción" : Clean(what, 300);
        detail = Clean(detail, 8000);
        var at = J.Now();
        lock (entries) {

            // Digits are dropped from the identity so a counter or an id in the message does not split one fault into many.
            var fingerprint = J.Hash(source + "\n" + Digits().Replace(what, "") + "\n" + Digits().Replace(detail.Length > 600 ? detail[..600] : detail, ""));
            var isNew = !entries.TryGetValue(fingerprint, out var entry);
            if (isNew && entries.Count >= MaxEntries) return;
            if (entry is null) entries[fingerprint] = entry = J.O("fingerprint", fingerprint, "source", source, "severity", severity, "what", what, "affected", Clean(affected, 300), "detail", detail, "context", Context(context), "version", Clean(version ?? Version, 32), "platform", Clean(platform ?? RuntimeInformation.RuntimeIdentifier, 32), "occurrences", 0, "firstAt", at, "lastAt", at);
            entry["occurrences"] = entry.L("occurrences") + 1;
            entry["lastAt"] = at;
            // A storm of one fault still counts, but does not rewrite the file on every repeat.
            if (isNew || DateTimeOffset.UtcNow - saved > TimeSpan.FromSeconds(5)) Save();

        }

    }
    // A request that could not finish: named by its route, so ids in the address do not make each failure look new.
    public void Request(HttpContext c, Exception e) {

        var route = (c.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? c.Request.Path.ToString();
        var action = c.Items["actionType"] as string ?? "";
        Record("api", "error", $"{c.Request.Method} {route}: {e.GetType().Name}: {e.Message}", action != "" ? $"La operación {action} no se pudo confirmar; quien la hizo debe reconciliarla" : $"La solicitud {route} no obtuvo respuesta", e.ToString(), J.O("route", route, "action", action, "operationId", c.Items["operationId"] as string ?? "", "exception", e.GetType().Name));

    }
    public JsonObject[] Pending() { lock (entries) return [.. entries.Values.Take(MaxBatch).Select(x => (JsonObject)x.DeepClone())]; }
    public void Delivered(IEnumerable<JsonObject> sent) {

        lock (entries) {

            foreach (var fault in sent) {

                // Repeats counted while the batch travelled go out on the next one.
                if (!entries.TryGetValue(fault.S("fingerprint"), out var entry)) continue;
                var left = entry.L("occurrences") - fault.L("occurrences");
                if (left <= 0) entries.Remove(fault.S("fingerprint"));
                else entry["occurrences"] = left;

            }
            Save();

        }

    }
    void Save() {

        saved = DateTimeOffset.UtcNow;
        if (file is null) return;
        try {

            var temporary = file + ".tmp";
            File.WriteAllText(temporary, new JsonArray([.. entries.Values.Select(x => (JsonNode?)x.DeepClone())]).ToJsonString());
            File.Move(temporary, file, true);

        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

    }
    static JsonObject Context(JsonObject? context) {

        var clean = new JsonObject();
        foreach (var (key, value) in (context ?? []).Take(16)) if (value is JsonValue v && v.TryGetValue<string>(out var text) && text != "") clean[Clean(key, 40)] = Clean(text, 300);
        return clean;

    }
    // Reports leave the branch. Whatever an exception happened to quote stays here: customer contact, card or phone
    // digits, credentials, and the query string or home folder of an address.
    public static string Clean(string text, int max) {

        text = Credentials().Replace(text, "$1=[oculto]");
        text = Bearer().Replace(text, "Bearer [oculto]");
        text = Email().Replace(text, "[correo]");
        text = LongNumber().Replace(text, "[número]");
        text = Query().Replace(text, "?[oculto]");
        text = Home().Replace(text, "~/");
        return text.Length > max ? text[..max] : text;

    }
    [GeneratedRegex(@"\d+")] private static partial Regex Digits();
    [GeneratedRegex(@"(?i)\b(password|passwd|contraseña|secret|deviceSecret|token|pin|cvv|clave)\b[""'\s:=]+\S+")] private static partial Regex Credentials();
    [GeneratedRegex(@"(?i)\bbearer\s+\S+")] private static partial Regex Bearer();
    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+")] private static partial Regex Email();
    [GeneratedRegex(@"(?<![\w.])\+?\d[\d -]{5,}\d(?![\w.])")] private static partial Regex LongNumber();
    [GeneratedRegex(@"\?[^\s""'<>)]+")] private static partial Regex Query();
    [GeneratedRegex(@"(/home/[^/\s]+/|[A-Za-z]:\\Users\\[^\\\s]+\\)")] private static partial Regex Home();
}

// Errors and critical logs are what the workers, the host and Kestrel already say when something breaks unattended.
public sealed class FaultLoggerProvider(FaultLog faults): ILoggerProvider {
    public ILogger CreateLogger(string category) => new FaultLogger(faults, category);
    public void Dispose() { }
    static string Area(string category) => category switch {
        var c when c.Contains("PrintWorker") => "Impresión de comandas",
        var c when c.Contains("FiscalWorker") => "Facturación",
        var c when c.Contains("Inventory") => "Inventario",
        var c when c.Contains("OutboxWorker") || c.Contains("CentralSyncWorker") => "Envío de datos a la central",
        var c when c.Contains("Checkpoint") => "Puntos de control de la bitácora",
        var c when c.Contains("OffsiteBackup") => "Respaldo fuera de la sucursal",
        var c when c.Contains("LicenseWorker") => "Licencia y soporte",
        var c when c.Contains("Hosting") => "Servidor de la sucursal: un servicio en segundo plano se detuvo",
        _ => "Servidor de la sucursal"
    };
    sealed class FaultLogger(FaultLog faults, string category): ILogger {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Error;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {

            if (!IsEnabled(level)) return;
            faults.Record("worker", level == LogLevel.Critical ? "critical" : "error", formatter(state, exception), Area(category), exception?.ToString() ?? "", J.O("category", category));

        }
    }
}
