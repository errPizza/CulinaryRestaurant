using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Client;

// Terminal updates come from CulinaryServer through the branch server, which asks with its license. The terminal
// trusts neither of them: it checks the root-signed update itself before keeping the installer.
public static class Updates {
    public static Version Current => typeof(Updates).Assembly.GetName().Version ?? new Version(0, 0, 0);
    public static async Task<JsonObject?> Fetch(ConnectionProfile profile, JsonObject device, string platform, string directory, CancellationToken ct = default) {

        using var http = TrustedConnection.Create(profile);
        var request = (JsonObject)device.DeepClone();
        request["siteId"] = profile.SiteId;
        request["platform"] = platform;
        request["currentVersion"] = $"{Current.Major}.{Current.Minor}.{Math.Max(0, Current.Build)}";
        using var check = await http.PostAsJsonAsync("api/updates/check", request, ct);
        var reply = JsonNode.Parse(await check.Content.ReadAsStringAsync(ct)) as JsonObject ?? new();
        if (!check.IsSuccessStatusCode) throw new ApiFailure((int)check.StatusCode, reply.S("error", "No se pudo consultar la actualización"));
        if (reply["update"] is not JsonValue) return null;
        var update = Signed(reply.S("update"), profile.SiteId, platform);
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
        return update;

    }
    public static JsonObject Signed(string jws, string siteId, string platform) {

        var update = Jws.Verify(jws, Jws.Update);
        Rules.Need(update.S("siteId") == siteId && update.S("platform") == platform, "La actualización no autoriza esta sucursal o plataforma", 403);
        Rules.Need(DateTimeOffset.TryParse(update.S("exp"), out var expires) && expires > DateTimeOffset.UtcNow, "Autorización de actualización vencida", 403);
        Rules.Need(Version.TryParse(update.S("version"), out var version) && version > Current, "La versión debe ser más reciente que el cliente actual");
        return update;

    }
    public static async Task Verify(JsonObject update, string package, string platform) {

        Rules.Need(Path.GetFileName(package) == update.S("filename") && Path.GetExtension(package) == (platform == "win-x64" ? ".exe" : platform == "linux-x64" ? ".deb" : ".apk"), "Nombre o formato de paquete inválido");
        await using var stream = File.OpenRead(package);
        Rules.Need(stream.Length == update.L("bytes"), "Tamaño de paquete incorrecto");
        Rules.Need(Convert.ToHexStringLower(await SHA512.HashDataAsync(stream)) == update.S("sha512"), "Integridad del paquete inválida");

    }
}
