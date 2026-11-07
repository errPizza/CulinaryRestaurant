using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Client;

// Terminal updates come from CulinaryServer through the branch server, which asks with its license. The terminal
// trusts neither of them: it checks the signed update and the release manifest itself before keeping the installer.
public static class Updates {
    public static Version Current => typeof(Updates).Assembly.GetName().Version ?? new Version(0, 0, 0);
    // x.y.z as CulinaryServer publishes it: what the screens compare against the published version.
    public static string Running => $"{Current.Major}.{Current.Minor}.{Math.Max(0, Current.Build)}";
    // As the screens show versions: two-digit patch, 0.3.4 → 0.3.04.
    public static string Shown(string version) => Version.TryParse(version, out var v) ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build):00}" : version;
    // What to say when nothing newer is offered: up to date, newer than published, or nothing published.
    public static string Verdict(string? latest) {

        if (string.IsNullOrEmpty(latest) || !Version.TryParse(latest, out var published)) return "No hay versiones publicadas para tu licencia · tienes la " + Shown(Running) + ".";
        var running = Version.Parse(Running);
        return running > published ? $"Tu equipo tiene una versión más nueva ({Shown(Running)}) que la publicada ({Shown(latest)}). No se instalará la publicada." : $"Ya estás al día. Tienes la versión {Shown(Running)}, la más reciente publicada para tu licencia.";

    }
    public static async Task<JsonObject?> Fetch(ConnectionProfile profile, JsonObject device, string platform, string directory, CancellationToken ct = default, string? token = null, Action<string?>? published = null) {

        using var http = TrustedConnection.Create(profile);
        if (token is not null) http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var request = (JsonObject)device.DeepClone();
        request["siteId"] = profile.SiteId;
        request["platform"] = platform;
        request["currentVersion"] = Running;
        using var check = await http.PostAsJsonAsync("api/updates/check", request, ct);
        var reply = JsonNode.Parse(await check.Content.ReadAsStringAsync(ct)) as JsonObject ?? new();
        if (!check.IsSuccessStatusCode) throw new ApiFailure((int)check.StatusCode, reply.S("error", "No se pudo consultar la actualización"));
        if (reply["update"] is not JsonValue) { published?.Invoke(reply["latestVersion"] is JsonValue v ? v.GetValue<string>() : null); return null; }
        var update = Signed(reply, profile.SiteId, platform);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, Path.GetFileName(update.S("filename")));
        request.Remove("currentVersion");
        request["releaseId"] = update.S("releaseId");
        using (var download = await http.PostAsJsonAsync("api/updates/download", request, ct)) {

            if (!download.IsSuccessStatusCode) throw new ApiFailure((int)download.StatusCode, "No se pudo descargar la actualización");
            await using var input = await download.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
            await input.CopyToAsync(output, ct);
            output.Flush(true);

        }
        await Verify(update, target, platform);
        update["update"] = reply.S("update");
        update["release"] = reply.S("release");
        update["keyFloor"] = reply.S("keyFloor");
        return update;

    }
    public static JsonObject Signed(JsonObject offer, string siteId, string platform) {

        var update = Jws.VerifyUpdate(offer);
        Rules.Need(update.S("siteId") == siteId && update.S("platform") == platform, "La actualización no autoriza esta sucursal o plataforma", 403);
        Rules.Need(DateTimeOffset.TryParse(update.S("exp"), out var expires) && expires > DateTimeOffset.UtcNow, "Autorización de actualización vencida", 403);
        Rules.Need(Version.TryParse(update.S("version"), out var version) && version > Current, $"La versión {update.S("version")} no es más nueva que la instalada ({Running}); no se instala");
        return update;

    }
    public static async Task Verify(JsonObject update, string package, string platform) {

        Rules.Need(Path.GetFileName(package) == update.S("filename") && Path.GetExtension(package) == (platform == "win-x64" ? ".exe" : platform == "linux-x64" ? ".deb" : ".apk"), "Nombre o formato de paquete inválido");
        await using var stream = File.OpenRead(package);
        Rules.Need(stream.Length == update.L("bytes"), "Tamaño de paquete incorrecto");
        Rules.Need(Convert.ToHexStringLower(await SHA512.HashDataAsync(stream)) == update.S("sha512"), "Integridad del paquete inválida");

    }
}
