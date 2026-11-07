using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;

// The kiosk user hands the package over; root believes only this check, made by the installed binary.
public static class UpdateCheck {
    public static async Task Run(string offerFile, string package, string site) {

        var update = Jws.VerifyUpdate(JsonNode.Parse(await File.ReadAllTextAsync(offerFile))!.AsObject());
        Need(update.S("siteId") == site && update.S("platform") == "linux-x64", "La actualización no autoriza esta sucursal o plataforma", 403);
        Need(DateTimeOffset.TryParse(update.S("exp"), out var expires) && expires > DateTimeOffset.UtcNow, "Autorización de actualización vencida", 403);
        Need(Version.TryParse(update.S("version"), out var version) && version > (typeof(UpdateCheck).Assembly.GetName().Version ?? new Version(0, 0, 0)), $"La versión {update.S("version")} no es más nueva que la instalada; no se instala");
        await using var stream = File.OpenRead(package);
        Need(stream.Length == update.L("bytes"), "Tamaño de paquete incorrecto");
        Need(Convert.ToHexStringLower(await SHA512.HashDataAsync(stream)) == update.S("sha512"), "Integridad del paquete inválida");

    }
}
