using System.Buffers.Text;
using System.Diagnostics;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;

// The branch's own license key. CulinaryServer certifies its public half inside the license, so a license copied to
// another machine is useless without this private key.
public interface ILicenseKey {
    string Alg { get; }
    string Binding { get; }
    string Spki { get; }
    byte[] Sign(byte[] data);
}
public sealed record LicenseOptions(string KeyDirectory, string? ServerUrl = null, string? HardwareFile = null, bool UseTpm = true, string? CentralCertificate = null);
public static class LicenseKeys {
    // A TPM 2.0 key never leaves the chip; without one, a P-256 key file readable only by the backend's user.
    public static ILicenseKey Open(LicenseOptions options) => options.UseTpm && TpmLicenseKey.Available() ? new TpmLicenseKey(options.KeyDirectory) : FileLicenseKey.Open(options.KeyDirectory);
}
public sealed class FileLicenseKey: ILicenseKey, IDisposable {
    readonly ECDsa key;
    FileLicenseKey(ECDsa key) => this.key = key;
    public string Alg => "ES256";
    public string Binding => "fingerprint";
    public string Spki => Base64Url.EncodeToString(key.ExportSubjectPublicKeyInfo());
    public byte[] Sign(byte[] data) => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    public void Dispose() => key.Dispose();
    public static FileLicenseKey Open(string directory) {

        var file = Path.Combine(directory, "branch-license.p8");
        var key = ECDsa.Create();
        if (File.Exists(file)) { key.ImportPkcs8PrivateKey(File.ReadAllBytes(file), out _); return new(key); }
        key.GenerateKey(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(directory);
        var temporary = file + "." + J.Id();
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var output = new FileStream(temporary, options)) output.Write(key.ExportPkcs8PrivateKey());
        File.Move(temporary, file, false);
        return new(key);

    }
}
// tpm2-tools against /dev/tpmrm0: a P-256 signing key created under the owner hierarchy and kept at a persistent handle.
public sealed class TpmLicenseKey(string directory): ILicenseKey {
    const string Handle = "0x81010043";
    string? spki;
    public string Alg => "ES256";
    public string Binding => "tpm";
    public static bool Available() => OperatingSystem.IsLinux() && File.Exists("/dev/tpmrm0") && File.Exists("/usr/bin/tpm2_sign");
    public string Spki => spki ??= Load();
    string Load() {

        Directory.CreateDirectory(directory);
        var der = Path.Combine(directory, "tpm-public.der");
        if (Tool("tpm2_readpublic", "-c", Handle, "-f", "der", "-o", der) != 0) {

            var primary = Path.Combine(directory, "tpm-primary.ctx");
            var context = Path.Combine(directory, "tpm-key.ctx");
            Need(Tool("tpm2_createprimary", "-C", "o", "-g", "sha256", "-G", "ecc256", "-c", primary) == 0
                && Tool("tpm2_create", "-C", primary, "-G", "ecc256:ecdsa-sha256", "-a", "fixedtpm|fixedparent|sensitivedataorigin|userwithauth|sign", "-u", Path.Combine(directory, "tpm-key.pub"), "-r", Path.Combine(directory, "tpm-key.priv")) == 0
                && Tool("tpm2_load", "-C", primary, "-u", Path.Combine(directory, "tpm-key.pub"), "-r", Path.Combine(directory, "tpm-key.priv"), "-c", context) == 0
                && Tool("tpm2_evictcontrol", "-C", "o", "-c", context, Handle) == 0
                && Tool("tpm2_readpublic", "-c", Handle, "-f", "der", "-o", der) == 0, "No se pudo crear la llave de licencia en el TPM", 503);

        }
        return Base64Url.EncodeToString(File.ReadAllBytes(der));

    }
    public byte[] Sign(byte[] data) {

        var input = Path.Combine(directory, "tpm-input-" + J.Id());
        var output = input + ".sig";
        try {

            File.WriteAllBytes(input, data);
            Need(Tool("tpm2_sign", "-c", Handle, "-g", "sha256", "-s", "ecdsa", "-f", "plain", "-o", output, input) == 0, "El TPM no firmó la solicitud", 503);
            return P1363(File.ReadAllBytes(output));

        } finally { File.Delete(input); File.Delete(output); }

    }
    // tpm2-tools writes ECDSA "plain" signatures as DER; CulinaryServer expects r ‖ s, each padded to 32 bytes.
    static byte[] P1363(byte[] signature) {

        if (signature.Length == 64) return signature;
        var sequence = new AsnReader(signature, AsnEncodingRules.DER).ReadSequence();
        byte[] Part() { var value = sequence.ReadIntegerBytes().ToArray().SkipWhile(b => b == 0).ToArray(); Need(value.Length <= 32, "Firma del TPM inválida", 503); return new byte[32 - value.Length].Concat(value).ToArray(); }
        return Part().Concat(Part()).ToArray();

    }
    static int Tool(string name, params string[] args) {

        var start = new ProcessStartInfo("/usr/bin/" + name) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["TPM2TOOLS_TCTI"] = "device:/dev/tpmrm0";
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;

    }
}
// Machine identifiers for binding without a TPM. A root step before the backend starts writes the raw values to
// /run/culinaryos-app/hardware.json (the backend itself cannot read them); only their hashes leave the machine.
public static class HardwareIdentity {
    public const string DefaultFile = "/run/culinaryos-app/hardware.json";
    public static JsonObject Fingerprint(string? file) {

        Need(file is not null && File.Exists(file), "No se pudo leer la identidad del equipo; reinicia el servicio de Culinary Restaurant", 503);
        var raw = JsonNode.Parse(File.ReadAllText(file!))!;
        static string Part(string kind, string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("culinary-hw-v1|" + kind + "|" + value)));
        var board = raw.S("board").Trim().ToLowerInvariant();
        var disk = raw.S("disk").Trim();
        var mac = raw.S("mac").Trim().ToLowerInvariant();
        Need(board.Length > 0 && disk.Length > 0 && mac.Length > 0, "El equipo no reporta placa, disco y tarjeta de red", 503);
        return J.O("board", Part("board", board), "disk", Part("disk", disk), "mac", Part("mac", mac));

    }
}
