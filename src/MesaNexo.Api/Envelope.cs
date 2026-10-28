using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Api;

// The branch keyring, in CulinaryOS's format: {"active": kid, "keys": {kid: base64 of 32 bytes}}. Every record and
// event is sealed with AES-256-GCM under the active key; the context (site, purpose, record, version, event) is the
// associated data, so a sealed value moved to another row or another branch no longer opens.
public sealed class Keyring: IDisposable {
    readonly Dictionary<string, byte[]> keys;
    public string Active { get; }
    public Keyring(JsonObject value) {

        Active = value.S("active");
        keys = value.Obj("keys").ToDictionary(x => x.Key, x => Convert.FromBase64String(x.Value!.GetValue<string>()));
        Rules.Need(keys.ContainsKey(Active) && keys.Count is >= 1 and <= 32 && keys.Values.All(k => k.Length == 32) && keys.Keys.All(k => k.Length is > 0 and <= 64), "Anillo de claves no válido");

    }
    public static JsonObject Generate() { var kid = Guid.NewGuid().ToString("D"); return J.O("active", kid, "keys", J.O(kid, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)))); }
    public static Keyring Load(string path) {

        var info = new FileInfo(path);
        Rules.Need(info.Exists && info.LinkTarget is null, "No se encontró el anillo de claves o es un enlace");
        if (!OperatingSystem.IsWindows()) Rules.Need((File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0, "El anillo de claves no está protegido");
        return new Keyring((JsonObject)JsonNode.Parse(File.ReadAllText(path))!);

    }
    // Layout: 1 (format) | key id length | key id | 12-byte nonce | 16-byte tag | ciphertext.
    public byte[] Seal(JsonNode? value, JsonObject context) => SealBytes(Encoding.UTF8.GetBytes(J.Canonical(value)), context);
    public JsonNode? Open(byte[] sealedValue, JsonObject context) => JsonNode.Parse(OpenBytes(sealedValue, context));
    public byte[] SealBytes(byte[] plain, JsonObject context) {

        var kid = Encoding.UTF8.GetBytes(Active);
        var sealedValue = new byte[2 + kid.Length + 28 + plain.Length];
        sealedValue[0] = 1;
        sealedValue[1] = (byte)kid.Length;
        kid.CopyTo(sealedValue, 2);
        var nonce = sealedValue.AsSpan(2 + kid.Length, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(keys[Active], 16);
        aes.Encrypt(nonce, plain, sealedValue.AsSpan(2 + kid.Length + 28), sealedValue.AsSpan(2 + kid.Length + 12, 16), Encoding.UTF8.GetBytes(J.Canonical(context)));
        return sealedValue;

    }
    public byte[] OpenBytes(byte[] sealedValue, JsonObject context) {

        if (sealedValue.Length < 30 || sealedValue[0] != 1 || sealedValue.Length < 2 + sealedValue[1] + 28) throw new StoreException("COS-DATA-INTEGRITY");
        var kid = Encoding.UTF8.GetString(sealedValue, 2, sealedValue[1]);
        if (!keys.TryGetValue(kid, out var key)) throw new StoreException("COS-DATA-INTEGRITY");
        var offset = 2 + sealedValue[1];
        var plain = new byte[sealedValue.Length - offset - 28];
        try { using var aes = new AesGcm(key, 16); aes.Decrypt(sealedValue.AsSpan(offset, 12), sealedValue.AsSpan(offset + 28), sealedValue.AsSpan(offset + 12, 16), plain, Encoding.UTF8.GetBytes(J.Canonical(context))); } catch (CryptographicException) { throw new StoreException("COS-DATA-INTEGRITY"); }
        return plain;

    }
    public void Dispose() { foreach (var key in keys.Values) CryptographicOperations.ZeroMemory(key); }
}
