using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Client;

public static class Updates {
    public static async Task<JsonObject> Verify(string bundle, string package, string trustedPublicKey, string keySha256, string siteId, string platform) {

        var keyBytes = await File.ReadAllBytesAsync(trustedPublicKey);
        Rules.Need(Convert.ToHexStringLower(SHA256.HashData(keyBytes)).Equals(keySha256.Replace(":", ""), StringComparison.OrdinalIgnoreCase), "La huella de clave del proveedor no coincide", 403);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(Encoding.UTF8.GetString(keyBytes));
        var envelope = JsonNode.Parse(await File.ReadAllTextAsync(bundle))!;
        var data = envelope.Obj("data");
        Rules.Need(rsa.VerifyData(Encoding.UTF8.GetBytes(J.Canonical(data)), Convert.FromBase64String(envelope.S("signature")), HashAlgorithmName.SHA256, RSASignaturePadding.Pss), "Firma de publicación inválida", 403);
        Rules.Need(data.S("contract") == "mesa-nexo-update-v1" && data.S("siteId") == siteId, "La publicación no autoriza esta sucursal", 403);
        Rules.Need(DateTimeOffset.Parse(data.S("expires")) > DateTimeOffset.UtcNow, "Autorización de descarga vencida", 403);
        var manifest = data.Obj("manifest");
        Rules.Need(Path.GetFileName(package) == manifest.S("filename") && Path.GetExtension(package) == (platform == "win-x64" ? ".exe" : platform == "linux-x64" ? ".deb" : ".apk"), "Nombre o formato de paquete inválido");
        Rules.Need(Version.TryParse(manifest.S("version"), out var version) && version > typeof(Updates).Assembly.GetName().Version, "La versión debe ser más reciente que el cliente actual");
        Rules.Need(manifest.S("platform") == platform && manifest.I("schema") == 1 && manifest.I("minimumApi") <= 1, "Plataforma o esquema incompatible");
        await using var stream = File.OpenRead(package);
        Rules.Need(stream.Length == manifest.L("bytes"), "Tamaño de paquete incorrecto");
        var sha = Convert.ToHexStringLower(await SHA512.HashDataAsync(stream));
        Rules.Need(sha == manifest.S("sha512"), "Integridad del paquete inválida");
        return (JsonObject)manifest.DeepClone();

    }
}