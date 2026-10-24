using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Api;

public static class Migration {
    public static async Task DecryptBackup(string sourceCopy, string keyFile, string output) {

        if (File.Exists(output)) throw new IOException("No se sobrescribe la salida");
        var source = Path.GetFullPath(sourceCopy);
        var original = Path.GetFullPath(@"C:\Users\kikin\Desktop\SoftwarePrototipo\data");
        if (source.StartsWith(original + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Usa una copia explícita");
        var envelope = JsonNode.Parse(await File.ReadAllTextAsync(sourceCopy))!;
        Rules.Need(envelope.S("format") == "mesa-nexo-aes-256-gcm" && envelope.I("schema") == 1, "No es un respaldo AES-GCM del prototipo");
        var key = await File.ReadAllBytesAsync(keyFile);
        Rules.Need(key.Length == 32, "Se requiere la clave original de 32 bytes");
        try { var ciphertext = Convert.FromBase64String(envelope.S("ciphertext")); Rules.Need(ciphertext.Length <= 128 * 1024 * 1024, "Respaldo demasiado grande"); var raw = new byte[ciphertext.Length]; using var aes = new AesGcm(key, 16); aes.Decrypt(Convert.FromBase64String(envelope.S("iv")), ciphertext, Convert.FromBase64String(envelope.S("tag")), raw); var state = JsonNode.Parse(raw)!; Rules.Need(state.I("schema") == 1 && state["restaurants"] is JsonArray, "Esquema incompatible"); await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None); await file.WriteAsync(raw); file.Flush(true); CryptographicOperations.ZeroMemory(raw); } finally { CryptographicOperations.ZeroMemory(key); }

    }
}